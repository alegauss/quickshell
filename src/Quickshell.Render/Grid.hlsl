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
    float2 CellSize;       // one cell in pixels, whole numbers
    float2 ViewportSize;   // the back buffer in pixels
    uint   Columns;        // cells per row, which is what turns an instance id into a position
    float  Baseline;       // pixels from the top of a cell down to the baseline
    float  AtlasPageSize;  // unused by Load, kept so the layout matches a sampled variant
    float  Reserved;
};

// One per atlas page. D3D feature level 11_0 cannot index a texture array dynamically, so the page
// is a branch rather than a subscript; a page nothing bound reads as zero coverage, which is the
// right answer for a glyph that is not there.
Texture2D<float> Atlas0 : register(t0);
Texture2D<float> Atlas1 : register(t1);
Texture2D<float> Atlas2 : register(t2);
Texture2D<float> Atlas3 : register(t3);

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
};

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

float SampleAtlas(uint page, int2 texel)
{
    if (page == 1u)
    {
        return Atlas1.Load(int3(texel, 0));
    }

    if (page == 2u)
    {
        return Atlas2.Load(int3(texel, 0));
    }

    if (page == 3u)
    {
        return Atlas3.Load(int3(texel, 0));
    }

    return Atlas0.Load(int3(texel, 0));
}

Fragment VertexMain(Instance input)
{
    // 0,0  1,0  0,1  1,1 - a triangle strip, so no index buffer and no vertex buffer either.
    float2 corner = float2(input.Vertex & 1u, (input.Vertex >> 1) & 1u);

    float2 cell = float2(input.Index % Columns, input.Index / Columns);
    float2 pixel = (cell + corner) * CellSize;

    uint flags = input.Foreground >> 24;
    bool inverse = (flags & 4u) != 0u;
    float3 foreground = Unpack(input.Foreground);
    float3 background = Unpack(input.Background);

    Fragment output;
    output.Position = float4(((pixel.x / ViewportSize.x) * 2.0) - 1.0,
                             1.0 - ((pixel.y / ViewportSize.y) * 2.0),
                             0.0, 1.0);
    output.CellPixel = corner * CellSize;
    output.Foreground = inverse ? background : foreground;
    output.Background = inverse ? foreground : background;
    output.Glyph = int4((int)(input.GlyphOrigin & 0xFFFFu), (int)(input.GlyphOrigin >> 16),
                        (int)(input.GlyphSize & 0xFFFFu), (int)(input.GlyphSize >> 16));
    output.Bearing = int2(Signed16(input.GlyphBearing), Signed16(input.GlyphBearing >> 16));
    output.Page = input.Background >> 24;
    return output;
}

float4 PixelMain(Fragment input) : SV_Target
{
    // One texel of the atlas to one pixel of the window, by construction: the glyph was rasterised
    // at this display's scale, so Load rather than Sample - a filtered read here would be a blur
    // applied to a bitmap that is already the right size.
    int2 cellPixel = int2(floor(input.CellPixel));
    int2 inGlyph = cellPixel - int2(input.Bearing.x, (int)Baseline + input.Bearing.y);

    float coverage = 0.0;

    if (input.Glyph.z > 0 && input.Glyph.w > 0 &&
        inGlyph.x >= 0 && inGlyph.y >= 0 && inGlyph.x < input.Glyph.z && inGlyph.y < input.Glyph.w)
    {
        coverage = SampleAtlas(input.Page, input.Glyph.xy + inGlyph);
    }

    float3 blended = lerp(ToLinear(input.Background), ToLinear(input.Foreground), coverage);
    return float4(ToEncoded(blended), 1.0);
}
