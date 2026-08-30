// The whole terminal grid, in one draw.
//
// A four-vertex unit quad is issued once per frame through DrawInstanced, one instance per visible
// cell. The cell's position is not in the instance: it is derived here from SV_InstanceID and the
// column count, which is what keeps the per-cell cost at twenty bytes.
//
// The part that is easy to get subtly wrong is the blend. Coverage is not colour: it says how much
// of a pixel the glyph covers, and covering half a pixel means half the light, not half the number
// stored in an sRGB byte. So both colours are taken to linear, mixed there, and returned. Blending
// coverage directly in sRGB is why light-on-dark terminal text so often looks too thin and
// dark-on-light too heavy, and it is these three functions rather than a font problem.

cbuffer Frame : register(b0)
{
    float2 CellSize;            // one cell in pixels, whole numbers
    float2 ViewportSize;        // the back buffer in pixels
    uint   Columns;             // cells per row, which turns an instance id into a position
    float  Baseline;            // pixels from the top of a cell down to the baseline
    float  UnderlineY;          // the font's own underline, not a fraction of the cell
    float  UnderlineThickness;
    float  StrikeY;             // the font's own strikethrough
    float  StrikeThickness;
    float  CursorShowing;       // the blink phase: 1 while the cursor is on, 0 while it is not
    float  ClearType;           // 1 while the coverage pages carry one alpha per colour stripe
    float3 CursorColour;
    float  Reserved2;
    float3 SelectionColour;
    float  Reserved3;
};

// One per atlas page. D3D feature level 11_0 cannot index a texture array dynamically, so the page
// is a branch rather than a subscript; a page nothing bound reads as zero coverage, which is the
// right answer for a glyph that is not there.
//
// Declared float4 and not float because the page is R8_UNorm for grayscale and R8G8B8A8_UNorm for
// ClearType, and one declaration has to read both. A one-channel format answers (r, 0, 0, 1), so the
// grayscale case takes .r and spreads it; the ClearType case takes .rgb, which is three coverages.
Texture2D<float4> Atlas0 : register(t0);
Texture2D<float4> Atlas1 : register(t1);
Texture2D<float4> Atlas2 : register(t2);
Texture2D<float4> Atlas3 : register(t3);

// The colour pages, for glyphs that are painted rather than tinted. An emoji carries its own
// colours, so the cell's foreground means nothing for one and is ignored below.
Texture2D<float4> Colour0 : register(t4);
Texture2D<float4> Colour1 : register(t5);
Texture2D<float4> Colour2 : register(t6);
Texture2D<float4> Colour3 : register(t7);

struct Instance
{
    uint Foreground   : FOREGROUND;
    uint Background   : BACKGROUND;
    uint GlyphOrigin  : GLYPHORIGIN;
    uint GlyphSize    : GLYPHSIZE;
    uint GlyphBearing : GLYPHBEARING;
    uint Index        : SV_InstanceID;
    uint Vertex       : SV_VertexID;
};

struct Fragment
{
    float4 Position : SV_Position;
    float2 CellPixel : CELLPIXEL;
    nointerpolation float3 Foreground : COLOR0;
    nointerpolation float3 Background : COLOR1;
    nointerpolation int4 Glyph : GLYPH;      // origin x, origin y, width, height
    nointerpolation int2 Bearing : BEARING;  // left of the pen, top of the baseline
    nointerpolation uint Page : PAGE;
    nointerpolation uint IsColour : ISCOLOUR;
    nointerpolation uint Attributes : ATTRIBUTES;  // the foreground's own top byte
    nointerpolation uint Underline : UNDERLINE;    // the style, 0 for none
    nointerpolation uint Cursor : CURSOR;          // the shape, 0 for none
};

/// One inside a rule of the given thickness centred on `centre`, zero outside, with a pixel of
/// softness at each edge. Antialiasing the rule matters more than it sounds: a one-pixel line
/// snapped to an integer is a line that appears and disappears as the window moves between
/// monitors of different scale.
float Rule(float y, float centre, float thickness)
{
    float half = max(0.5, thickness * 0.5);
    return 1.0 - smoothstep(half - 0.5, half + 0.5, abs(y - centre));
}

float3 Unpack(uint packed)
{
    return float3((packed >> 16) & 0xFFu, (packed >> 8) & 0xFFu, packed & 0xFFu) / 255.0;
}

int Signed16(uint half16)
{
    int value = (int)(half16 & 0xFFFFu);
    return value >= 32768 ? value - 65536 : value;
}

float3 ToLinear(float3 encoded)
{
    return encoded <= 0.04045 ? encoded / 12.92 : pow((encoded + 0.055) / 1.055, 2.4);
}

float3 ToEncoded(float3 linearLight)
{
    return linearLight <= 0.0031308
        ? linearLight * 12.92
        : (1.055 * pow(linearLight, 1.0 / 2.4)) - 0.055;
}

// Three coverages, whatever the page holds: one per colour stripe under ClearType, and the same
// number three times otherwise. Returning float3 in both cases is what lets the blend below be one
// expression rather than two, and a lerp by (c, c, c) is exactly the grayscale blend it replaces.
float3 SampleAtlas(uint page, int2 texel)
{
    float4 stored;

    if (page == 1u)
    {
        stored = Atlas1.Load(int3(texel, 0));
    }
    else if (page == 2u)
    {
        stored = Atlas2.Load(int3(texel, 0));
    }
    else if (page == 3u)
    {
        stored = Atlas3.Load(int3(texel, 0));
    }
    else
    {
        stored = Atlas0.Load(int3(texel, 0));
    }

    return ClearType > 0.5 ? stored.rgb : stored.rrr;
}

float4 SampleColour(uint page, int2 texel)
{
    if (page == 1u)
    {
        return Colour1.Load(int3(texel, 0));
    }

    if (page == 2u)
    {
        return Colour2.Load(int3(texel, 0));
    }

    if (page == 3u)
    {
        return Colour3.Load(int3(texel, 0));
    }

    return Colour0.Load(int3(texel, 0));
}

Fragment VertexMain(Instance input)
{
    // 0,0  1,0  0,1  1,1 - a triangle strip, so no index buffer and no vertex buffer either.
    float2 corner = float2(input.Vertex & 1u, (input.Vertex >> 1) & 1u);

    uint attributes = input.Foreground >> 24;

    // How many cells this one occupies, from the model. Two widens the quad so a wide character is
    // drawn across both of its cells; zero collapses it, which is the trailing cell of such a pair
    // saying it has nothing of its own to draw.
    uint span = attributes >> 6;
    float2 extent = float2(CellSize.x * span, CellSize.y);

    float2 cell = float2(input.Index % Columns, input.Index / Columns);
    float2 pixel = (cell * CellSize) + (corner * extent);

    uint decoration = input.Background >> 24;
    uint cursor = decoration >> 6;

    float3 foreground = Unpack(input.Foreground);
    float3 background = Unpack(input.Background);

    // The order these are applied in is the order a user expects to see them win. Inverse is what
    // the host asked for, so it goes first; selection is what the user did with the mouse, so it
    // overrides that; the cursor is where they are about to type, so it overrides everything.
    if ((attributes & 4u) != 0u)
    {
        float3 swapped = foreground;
        foreground = background;
        background = swapped;
    }

    if ((attributes & 32u) != 0u)
    {
        background = SelectionColour;
    }

    // A block cursor inverts the glyph against the cursor colour rather than being drawn over it.
    // Overdrawn, the character under the cursor is invisible on any theme where the cursor is
    // opaque, which is every theme.
    if (cursor == 1u && CursorShowing > 0.5)
    {
        foreground = background;
        background = CursorColour;
    }

    Fragment output;
    output.Position = float4(((pixel.x / ViewportSize.x) * 2.0) - 1.0,
                             1.0 - ((pixel.y / ViewportSize.y) * 2.0),
                             0.0, 1.0);
    output.CellPixel = corner * extent;
    output.Foreground = foreground;
    output.Background = background;
    output.Glyph = int4((int)(input.GlyphOrigin & 0xFFFFu), (int)(input.GlyphOrigin >> 16),
                        (int)(input.GlyphSize & 0xFFFFu), (int)(input.GlyphSize >> 16));
    output.Bearing = int2(Signed16(input.GlyphBearing), Signed16(input.GlyphBearing >> 16));
    output.Page = decoration & 3u;
    output.IsColour = decoration & 4u;
    output.Attributes = attributes;
    output.Underline = (decoration >> 3) & 7u;
    output.Cursor = cursor;
    return output;
}

float4 PixelMain(Fragment input) : SV_Target
{
    // One texel of the atlas to one pixel of the window, by construction: the glyph was rasterised
    // at this display's scale, so Load rather than Sample - a filtered read here would be a blur
    // applied to a bitmap that is already the right size.
    int2 cellPixel = int2(floor(input.CellPixel));
    int2 inGlyph = cellPixel - int2(input.Bearing.x, (int)Baseline + input.Bearing.y);

    bool inside = input.Glyph.z > 0 && input.Glyph.w > 0 &&
                  inGlyph.x >= 0 && inGlyph.y >= 0 &&
                  inGlyph.x < input.Glyph.z && inGlyph.y < input.Glyph.w;

    // A colour glyph carries its own colours, so the foreground is not consulted at all: what the
    // atlas holds is already the picture, and the alpha is the only thing the cell contributes to.
    float3 ink = input.Foreground;
    float3 coverage = 0.0;

    if (inside)
    {
        if (input.IsColour != 0u)
        {
            float4 painted = SampleColour(input.Page, input.Glyph.xy + inGlyph);
            ink = painted.rgb;
            coverage = painted.aaa;
        }
        else
        {
            coverage = SampleAtlas(input.Page, input.Glyph.xy + inGlyph);
        }
    }

    float3 blended = lerp(ToLinear(input.Background), ToLinear(ink), coverage);

    // Everything below is derived from the cell's own coordinates. No decoration adds a draw, a
    // vertex or a byte of upload; each is arithmetic on numbers the instance already carried.
    //
    // The horizontal argument is the pixel's position in the *window*, not in the cell. That is the
    // whole reason an undercurl running under a word joins at every cell boundary instead of
    // restarting its phase at each one - which is the discontinuity this design is falsified by.
    float alongRow = input.Position.x;
    float y = input.CellPixel.y;
    float rule = 0.0;

    if (input.Underline == 1u)
    {
        rule = Rule(y, UnderlineY, UnderlineThickness);
    }
    else if (input.Underline == 2u)
    {
        float gap = max(2.0, UnderlineThickness * 2.0);
        rule = max(Rule(y, UnderlineY - (gap * 0.5), UnderlineThickness),
                   Rule(y, UnderlineY + (gap * 0.5), UnderlineThickness));
    }
    else if (input.Underline == 3u)
    {
        // A sine, so it is procedural: as geometry this would be a mesh per cell, and as a glyph it
        // would not join across cells at all.
        float amplitude = max(1.5, UnderlineThickness * 1.5);
        float period = max(5.0, CellSize.x * 0.75);
        rule = Rule(y, UnderlineY + (sin(alongRow * 6.28318530718 / period) * amplitude),
                    UnderlineThickness);
    }
    else if (input.Underline == 4u)
    {
        float dot = max(1.0, UnderlineThickness);
        rule = Rule(y, UnderlineY, UnderlineThickness) * step(fmod(floor(alongRow / dot), 2.0), 0.5);
    }
    else if (input.Underline == 5u)
    {
        float dash = max(2.0, UnderlineThickness * 3.0);
        rule = Rule(y, UnderlineY, UnderlineThickness) * step(fmod(floor(alongRow / dash), 2.0), 0.5);
    }

    if ((input.Attributes & 8u) != 0u)
    {
        rule = max(rule, Rule(y, UnderlineThickness, UnderlineThickness));
    }

    if ((input.Attributes & 16u) != 0u)
    {
        rule = max(rule, Rule(y, StrikeY, StrikeThickness));
    }

    // The rules are drawn in the text's own colour, so they follow inverse, selection and the block
    // cursor's swap without any of them needing to know that a rule exists.
    blended = lerp(blended, ToLinear(input.Foreground), saturate(rule));

    if (CursorShowing > 0.5 && input.Cursor >= 2u)
    {
        float bar = input.Cursor == 2u
            ? 1.0 - smoothstep(max(1.0, CellSize.x * 0.12), max(1.0, CellSize.x * 0.12) + 1.0, input.CellPixel.x)
            : Rule(y, CellSize.y - max(1.0, UnderlineThickness), max(2.0, UnderlineThickness * 2.0));

        blended = lerp(blended, ToLinear(CursorColour), saturate(bar));
    }

    return float4(ToEncoded(blended), 1.0);
}
