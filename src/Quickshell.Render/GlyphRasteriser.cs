using SharpGen.Runtime;
using Vortice;
using Vortice.DCommon;
using Vortice.DirectWrite;
using Vortice.Mathematics;

namespace Quickshell.Render;

/// <summary>
/// Which face draws a codepoint and at what size, once fallback and fitting have been decided.
/// </summary>
/// <param name="Family">The family that actually has the character, which may not be the one asked for.</param>
/// <param name="Glyph">The glyph index within that family.</param>
/// <param name="SizeInPixels">The em size to rasterise at, reduced where the face would not fit.</param>
/// <param name="Substituted">Whether this is a fallback rather than the preferred family.</param>
public readonly record struct GlyphResolution(string Family, ushort Glyph, float SizeInPixels, bool Substituted);

/// <summary>What a rasterised glyph is made of, which decides what kind of atlas page holds it.</summary>
public enum GlyphKind
{
    /// <summary>One byte of coverage per pixel, tinted by the cell's foreground colour.</summary>
    Coverage,

    /// <summary>Four bytes per pixel, its own colour. An emoji is not a shape somebody tints.</summary>
    Colour,

    /// <summary>
    /// Three bytes per pixel: one coverage for each of the display's colour stripes, which is what
    /// ClearType is. Tinted like <see cref="Coverage"/>, but per channel, so the glyph's edges take
    /// colour and a stem reads as heavier than the same stem antialiased in grey.
    /// </summary>
    ClearType,
}

/// <summary>
/// One rasterised glyph: its pixels, and where they sit relative to the pen.
///
/// <para><see cref="Left"/> and <see cref="Top"/> are DirectWrite's own bounds, so they are signed
/// and usually negative on the vertical: a glyph rises above the baseline the run was stated at.</para>
///
/// <para>A colour glyph carries four bytes per pixel — straight, not premultiplied, sRGB-encoded
/// RGB with coverage in the alpha. That is deliberately the same shape as the grayscale case with
/// the tint already applied, so the shader's blend is one expression for both.</para>
/// </summary>
public sealed class GlyphBitmap
{
    private readonly byte[] _pixels;

    internal GlyphBitmap(int width, int height, int left, int top, byte[] pixels,
                         GlyphKind kind = GlyphKind.Coverage)
    {
        Width = width;
        Height = height;
        Left = left;
        Top = top;
        Kind = kind;
        _pixels = pixels;
    }

    /// <summary>A glyph that marks no pixels: a space, or a control character with no shape.</summary>
    public static GlyphBitmap Empty { get; } = new(0, 0, 0, 0, []);

    /// <summary>The bitmap's width in pixels.</summary>
    public int Width { get; }

    /// <summary>The bitmap's height in pixels.</summary>
    public int Height { get; }

    /// <summary>The left edge relative to the pen position the run was rasterised at.</summary>
    public int Left { get; }

    /// <summary>The top edge relative to the baseline, negative for anything that rises above it.</summary>
    public int Top { get; }

    /// <summary>Coverage or colour, which is which kind of page it lands on.</summary>
    public GlyphKind Kind { get; }

    /// <summary>Bytes per pixel: one for coverage, three for ClearType, four for colour.</summary>
    public int BytesPerPixel => Kind switch
    {
        GlyphKind.Colour => 4,
        GlyphKind.ClearType => 3,
        _ => 1,
    };

    /// <summary>Whether this glyph marks no pixels at all.</summary>
    public bool IsEmpty => Width <= 0 || Height <= 0;

    /// <summary>The pixels, row-major, <see cref="Width"/> times <see cref="BytesPerPixel"/> to a row.</summary>
    public ReadOnlySpan<byte> Coverage => _pixels;
}

/// <summary>
/// DirectWrite, reduced to the one question the atlas asks it: what pixels does this glyph cover.
///
/// <para><c>IDWriteGlyphRunAnalysis::CreateAlphaTexture</c> is the specific door, because it
/// produces exactly the coverage bitmap Windows itself would produce. Text here then matches text
/// everywhere else on the machine instead of being subtly this project's own.</para>
///
/// <para>Two coverages come out of that door. <c>DWRITE_TEXTURE_ALIASED_1x1</c> is one channel, and
/// is what a face is drawn with unless it asks otherwise. <c>DWRITE_TEXTURE_CLEARTYPE_3x1</c> is
/// three, one per colour stripe of the display, and is what the rest of Windows draws text with —
/// see <see cref="Geometry"/> for the condition that makes it wrong rather than merely different.</para>
///
/// <para>This holds no GPU state, so it is deliberately not an <see cref="IDeviceResource"/>: a
/// device loss costs the atlas its pixels and costs this nothing.</para>
/// </summary>
public sealed class GlyphRasteriser : IDisposable
{
    /// <summary>
    /// The faces Windows itself falls back to, asked in this order. Emoji first, because a colour
    /// glyph found in a symbol font is a monochrome outline of something the user expected to be an
    /// emoji, and that is a worse answer than the one below it.
    /// </summary>
    private static readonly string[] Fallbacks =
    [
        "Segoe UI Emoji",
        "Segoe UI Symbol",
        "Segoe UI Historic",
        "Microsoft YaHei",      // Simplified Chinese
        "Microsoft JhengHei",   // Traditional Chinese
        "Yu Gothic",            // Japanese
        "Malgun Gothic",        // Korean
        "Nirmala UI",           // the Indic scripts
        "Segoe UI",
    ];

    private readonly Dictionary<(string Family, FontWeight Weight, FontStyle Slant), IDWriteFontFace> _faces = [];
    private readonly Dictionary<(string Family, FontWeight Weight, FontStyle Slant, int Codepoint), string> _resolved = [];
    private readonly IDWriteFactory2 _factory;
    private readonly IDWriteFontCollection _installed;

    // DirectWrite's character-map call is a batch API and the atlas asks it one character at a
    // time, so the batch of one is kept rather than allocated on every cache miss.
    private readonly uint[] _codepoint = new uint[1];
    private readonly ushort[] _glyph = new ushort[1];

    /// <summary>Opens the shared DirectWrite factory and reads the system font collection once.</summary>
    /// <param name="geometry">
    /// The stripe order to rasterise ClearType for, or null to ask this display. Passed only by a
    /// test: the panel's answer is not something a machine running the suite can be told to change,
    /// and a channel swap nobody can exercise is a channel swap nobody has checked.
    /// </param>
    public GlyphRasteriser(PixelGeometry? geometry = null)
    {
        // IDWriteFactory2 and not IDWriteFactory, for one argument: its CreateGlyphRunAnalysis is
        // the only one that takes an antialias mode, and grayscale coverage in a one-channel texture
        // is what asking the older factory for a 1x1 texture silently answers empty to.
        _factory = DWrite.DWriteCreateFactory<IDWriteFactory2>(FactoryType.Shared);
        _installed = _factory.GetSystemFontCollection(false);
        Geometry = geometry ?? Panel(_factory);
    }

    /// <summary>
    /// How this display lays its colour stripes out, which is the whole of whether ClearType is
    /// allowed and which way round its channels go.
    ///
    /// <para><b>Read from the machine rather than assumed.</b> ClearType is a bet that a pixel is
    /// three stripes side by side in a known order. On a panel where that is false — a rotated
    /// display, most projectors, a good many laptop screens — the bet loses and the result is not
    /// slightly-off text, it is coloured fringes on every letter. Windows already knows the answer
    /// and DirectWrite will say it, so there is no reason to guess.</para>
    ///
    /// <para><see cref="PixelGeometry.Flat"/> means there is no horizontal stripe order to exploit,
    /// and <see cref="CanClearType"/> is false there. <see cref="PixelGeometry.Bgr"/> means there is
    /// one and it runs the other way, which the rasteriser handles by reversing each pixel's three
    /// coverages as it caches the glyph.</para>
    /// </summary>
    public PixelGeometry Geometry { get; }

    /// <summary>
    /// Whether this display can be drawn ClearType at all. False on a panel with no horizontal
    /// stripe order, where a font asking for it is quietly given grayscale instead — which looks
    /// slightly thin, where honouring the request would look broken.
    /// </summary>
    public bool CanClearType => Geometry != PixelGeometry.Flat;

    /// <summary>The stripe order the system's rendering parameters report, defaulting to flat.</summary>
    private static PixelGeometry Panel(IDWriteFactory factory)
    {
        try
        {
            using IDWriteRenderingParams parameters = factory.CreateRenderingParams();

            return parameters.PixelGeometry;
        }
        catch (SharpGenException)
        {
            // No parameters to read is not a reason to refuse to draw. Flat is the answer that
            // costs a slightly thin letter rather than a fringed one.
            return PixelGeometry.Flat;
        }
    }

    /// <summary>
    /// How many glyphs have actually been rasterised. The atlas exists to keep this number close to
    /// the number of distinct shapes on screen rather than to the number of cells drawn, so a test
    /// that asserts caching asserts on this.
    /// </summary>
    public int Rasterisations { get; private set; }

    /// <summary>
    /// The glyph index a codepoint maps to in a face. This is the whole of the shaping this class
    /// does: a real shaper is a later line, and a monospaced grid gets the right answer from the
    /// character map for everything that is one character wide.
    /// </summary>
    public ushort GlyphIndex(string family, FontWeight weight, FontStyle slant, int codepoint)
    {
        _codepoint[0] = (uint)codepoint;
        Face(family, weight, slant).GetGlyphIndices(_codepoint, _glyph);

        return _glyph[0];
    }

    /// <summary>
    /// The face a family, weight and slant resolve to, for a caller that shapes rather than
    /// rasterises.
    ///
    /// <para>Handed out rather than looked up a second time, because shaping and rasterising
    /// disagreeing about which face a family means is a bug whose symptom is a glyph index from one
    /// face drawn out of another — which is not a missing character, it is the wrong one.</para>
    ///
    /// <para>The face is owned here and is released with this rasteriser. A caller does not dispose it.</para>
    /// </summary>
    public IDWriteFontFace FaceFor(string family, FontWeight weight, FontStyle slant) =>
        Face(family, weight, slant);

    /// <summary>
    /// A text analyzer over the same factory, for the same reason: one DirectWrite, one answer.
    /// The caller owns what this returns.
    /// </summary>
    public IDWriteTextAnalyzer CreateAnalyzer() => _factory.CreateTextAnalyzer();

    /// <summary>
    /// The grid geometry a font implies: one cell's advance, the line height, and where the
    /// baseline falls inside it.
    ///
    /// <para>The advance is read off a reference glyph rather than averaged, because the fonts a
    /// terminal is set in are monospaced and the average of a monospaced face is its advance. A
    /// proportional face measured this way gives a grid that is wrong in a way the user can see at
    /// once, which is better than one that is wrong subtly.</para>
    /// </summary>
    public CellMetrics Measure(FontSettings font, FontWeight weight = FontWeight.Normal,
                               FontStyle slant = FontStyle.Normal)
    {
        IDWriteFontFace face = Face(font.Family, weight, slant);
        FontMetrics metrics = face.Metrics;
        float scale = font.SizeInPixels / metrics.DesignUnitsPerEm;

        _glyph[0] = GlyphIndex(font.Family, weight, slant, 'M');
        GlyphMetrics[] reference = new GlyphMetrics[1];
        face.GetDesignGlyphMetrics(_glyph, reference, false);

        int baseline = (int)MathF.Round(metrics.Ascent * scale);

        return new CellMetrics(
            Math.Max(1, (int)MathF.Ceiling(reference[0].AdvanceWidth * scale)),
            Math.Max(1, (int)MathF.Ceiling((metrics.Ascent + metrics.Descent + metrics.LineGap) * scale)),
            baseline)
        {
            // DirectWrite states both of these relative to the baseline and positive upwards, so a
            // negative underline position is one below the baseline - which is where every
            // underline goes. The cell's own coordinates run downwards from its top, hence the sign.
            UnderlineY = baseline - (int)MathF.Round(metrics.UnderlinePosition * scale),
            UnderlineThickness = Math.Max(1, (int)MathF.Round(metrics.UnderlineThickness * scale)),
            StrikeY = baseline - (int)MathF.Round(metrics.StrikethroughPosition * scale),
            StrikeThickness = Math.Max(1, (int)MathF.Round(metrics.StrikethroughThickness * scale)),
        };
    }

    /// <summary>
    /// Which face actually draws this codepoint, and at what em size it fits the space allowed.
    ///
    /// <para><b>No monospaced font covers Unicode.</b> When the preferred family has no glyph, the
    /// families Windows itself falls back to are asked in turn, and then anything installed that
    /// has the character. The answer is a family name, which is a field the cache key already
    /// carries — so a fallback glyph caches like any other and this probe runs once per character
    /// rather than once per cell.</para>
    ///
    /// <para><b>The substitute's metrics are not trusted.</b> A face chosen for coverage has its own
    /// idea of an advance, and drawn at the primary's em size it would spill into the cells beside
    /// it. <paramref name="maximumAdvance"/> is the room there is; a face wider than that is
    /// rasterised smaller so it fits, which is what "fitted to the cell" means in pixels.</para>
    /// </summary>
    public GlyphResolution Resolve(FontSettings font, FontWeight weight, FontStyle slant,
                                   int codepoint, float maximumAdvance = 0f)
    {
        (string Family, FontWeight Weight, FontStyle Slant, int Codepoint) question =
            (font.Family, weight, slant, codepoint);

        if (!_resolved.TryGetValue(question, out string? family))
        {
            family = FamilyFor(font.Family, weight, slant, codepoint);
            _resolved[question] = family;
        }

        ushort glyph = GlyphIndex(family, weight, slant, codepoint);
        float size = font.SizeInPixels;

        if (maximumAdvance > 0f)
        {
            float advance = AdvanceOf(family, weight, slant, glyph, size);

            if (advance > maximumAdvance)
            {
                size *= maximumAdvance / advance;
            }
        }

        return new GlyphResolution(family, glyph, size, !string.Equals(family, font.Family, StringComparison.Ordinal));
    }

    /// <summary>
    /// Rasterises one glyph at one subpixel offset. The em size is taken as physical pixels and
    /// DirectWrite is told one pixel per DIP, so the key's size field and the bitmap's size are the
    /// same number rather than two that have to be kept in step.
    ///
    /// <para>A glyph with colour layers comes back as <see cref="GlyphKind.Colour"/>: emoji are
    /// painted, not tinted, so there is nothing a foreground colour could mean for one.</para>
    /// </summary>
    public GlyphBitmap Rasterise(in GlyphKey key)
    {
        GlyphRun run = new()
        {
            FontFace = Face(key.Family, key.Weight, key.Slant),
            FontEmSize = key.SizeInPixels,
            Indices = [key.Glyph],
            Advances = [0f],
            BidiLevel = 0,
            IsSideways = false,
        };

        Rasterisations++;

        // A colour glyph is asked about first and is never ClearType: an emoji carries its own
        // pixels, so there is no coverage for a display's stripes to be measured against.
        GlyphBitmap? colour = RasteriseColour(run, key.SubpixelOffsetInPixels);

        return colour ?? RasteriseCoverage(run, key.SubpixelOffsetInPixels, key.ClearType);
    }

    /// <summary>
    /// Rasterises a run's coverage, in one channel or in three.
    ///
    /// <para>The layer path calls this with <paramref name="clearType"/> false whatever the face
    /// asked for, which is deliberate: a colour glyph's layers are composited here into a picture,
    /// and three coverages per layer would be three pictures with nothing to choose between them.</para>
    /// </summary>
    private GlyphBitmap RasteriseCoverage(GlyphRun run, float subpixelOffset, bool clearType)
    {
        TextureType texture = clearType ? TextureType.Cleartype3x1 : TextureType.Aliased1x1;

        using IDWriteGlyphRunAnalysis analysis = _factory.CreateGlyphRunAnalysis(
            run,
            null,
            clearType ? RenderingMode.CleartypeNaturalSymmetric : RenderingMode.NaturalSymmetric,
            MeasuringMode.Natural,
            GridFitMode.Default,
            clearType ? TextAntialiasMode.Cleartype : TextAntialiasMode.Grayscale,
            subpixelOffset,
            0f);

        RawRect bounds = analysis.GetAlphaTextureBounds(texture);
        int width = bounds.Right - bounds.Left;
        int height = bounds.Bottom - bounds.Top;

        if (width <= 0 || height <= 0)
        {
            return GlyphBitmap.Empty;
        }

        // The bounds are in pixels either way; only the bytes behind each pixel differ, which is
        // why the size of this buffer and not the size of the rectangle is what carries the three.
        GlyphKind kind = clearType ? GlyphKind.ClearType : GlyphKind.Coverage;
        int stride = clearType ? 3 : 1;
        byte[] coverage = new byte[width * height * stride];

        analysis.CreateAlphaTexture(texture, bounds, coverage, (uint)coverage.Length);

        if (clearType && Geometry == PixelGeometry.Bgr)
        {
            Reverse(coverage);
        }

        return new GlyphBitmap(width, height, bounds.Left, bounds.Top, coverage, kind);
    }

    /// <summary>
    /// Puts the stripes in the panel's order, once, at rasterisation time.
    ///
    /// <para>DirectWrite always hands back the coverages red, green, blue. A panel whose stripes run
    /// the other way needs them blue, green, red, and doing it here rather than in the shader costs
    /// one pass over a glyph that is about to be cached instead of a branch on every pixel of every
    /// frame. Getting it wrong is not subtle: every letter is fringed on the wrong side.</para>
    /// </summary>
    private static void Reverse(Span<byte> coverage)
    {
        for (int pixel = 0; pixel + 2 < coverage.Length; pixel += 3)
        {
            (coverage[pixel], coverage[pixel + 2]) = (coverage[pixel + 2], coverage[pixel]);
        }
    }

    /// <summary>
    /// Composites a colour glyph's layers, or answers null when the glyph has none and the
    /// grayscale path is the right one.
    ///
    /// <para>Each layer is an ordinary coverage run with a colour attached, so the layers are
    /// rasterised through the same door as everything else and mixed here. The mixing is done in
    /// linear light and encoded back on the way out, for the same reason the shader does: coverage
    /// is light, and averaging it in sRGB is what makes the edges of an emoji look dirty.</para>
    /// </summary>
    private GlyphBitmap? RasteriseColour(GlyphRun run, float subpixelOffset)
    {
        IDWriteColorGlyphRunEnumerator? layers = TranslateColour(run, subpixelOffset);

        if (layers is null)
        {
            return null;
        }

        using (layers)
        {
            List<(GlyphBitmap Bitmap, Color4 Colour)> painted = [];
            int left = int.MaxValue;
            int top = int.MaxValue;
            int right = int.MinValue;
            int bottom = int.MinValue;

            while (layers.MoveNext())
            {
                ColorGlyphRun layer = layers.CurrentRun;
                GlyphBitmap bitmap = RasteriseCoverage(layer.GlyphRun, subpixelOffset, false);

                if (bitmap.IsEmpty)
                {
                    continue;
                }

                painted.Add((bitmap, layer.RunColor));
                left = Math.Min(left, bitmap.Left);
                top = Math.Min(top, bitmap.Top);
                right = Math.Max(right, bitmap.Left + bitmap.Width);
                bottom = Math.Max(bottom, bitmap.Top + bitmap.Height);
            }

            if (painted.Count == 0)
            {
                return GlyphBitmap.Empty;
            }

            return Composite(painted, left, top, right - left, bottom - top);
        }
    }

    private static GlyphBitmap Composite(List<(GlyphBitmap Bitmap, Color4 Colour)> layers,
                                         int left, int top, int width, int height)
    {
        // Linear premultiplied while compositing, straight sRGB on the way out: the shader reads
        // this the same way it reads a foreground colour, so the tile is what a tinted coverage
        // bitmap would have been had the tint varied per pixel.
        float[] accumulated = new float[width * height * 4];

        foreach ((GlyphBitmap bitmap, Color4 colour) in layers)
        {
            ReadOnlySpan<byte> pixels = bitmap.Coverage;
            float red = ToLinear(colour.R);
            float green = ToLinear(colour.G);
            float blue = ToLinear(colour.B);

            for (int row = 0; row < bitmap.Height; row++)
            {
                for (int column = 0; column < bitmap.Width; column++)
                {
                    float alpha = pixels[(row * bitmap.Width) + column] / 255f * colour.A;

                    if (alpha <= 0f)
                    {
                        continue;
                    }

                    int target = ((((bitmap.Top - top + row) * width) + bitmap.Left - left + column) * 4);
                    float behind = 1f - alpha;

                    accumulated[target] = (red * alpha) + (accumulated[target] * behind);
                    accumulated[target + 1] = (green * alpha) + (accumulated[target + 1] * behind);
                    accumulated[target + 2] = (blue * alpha) + (accumulated[target + 2] * behind);
                    accumulated[target + 3] = alpha + (accumulated[target + 3] * behind);
                }
            }
        }

        byte[] tile = new byte[width * height * 4];

        for (int pixel = 0; pixel < width * height; pixel++)
        {
            float alpha = accumulated[(pixel * 4) + 3];

            // Back to straight alpha, because a premultiplied tile would need a second blend mode
            // in the shader for no picture anybody could tell apart.
            tile[(pixel * 4)] = Encode(alpha > 0f ? accumulated[pixel * 4] / alpha : 0f);
            tile[(pixel * 4) + 1] = Encode(alpha > 0f ? accumulated[(pixel * 4) + 1] / alpha : 0f);
            tile[(pixel * 4) + 2] = Encode(alpha > 0f ? accumulated[(pixel * 4) + 2] / alpha : 0f);
            tile[(pixel * 4) + 3] = (byte)Math.Clamp((int)MathF.Round(alpha * 255f), 0, 255);
        }

        return new GlyphBitmap(width, height, left, top, tile, GlyphKind.Colour);
    }

    private static float ToLinear(float encoded) =>
        encoded <= 0.04045f ? encoded / 12.92f : MathF.Pow((encoded + 0.055f) / 1.055f, 2.4f);

    private static byte Encode(float linearLight)
    {
        float encoded = linearLight <= 0.0031308f
            ? linearLight * 12.92f
            : (1.055f * MathF.Pow(linearLight, 1f / 2.4f)) - 0.055f;

        return (byte)Math.Clamp((int)MathF.Round(encoded * 255f), 0, 255);
    }

    /// <summary>Releases the cached faces, the font collection and the factory.</summary>
    public void Dispose()
    {
        foreach (IDWriteFontFace face in _faces.Values)
        {
            face.Dispose();
        }

        _faces.Clear();
        _installed.Dispose();
        _factory.Dispose();
    }

    /// <summary>
    /// Asks DirectWrite for the colour layers of a run, or answers null when it has none.
    ///
    /// <para><c>DWRITE_E_NOCOLOR</c> is the ordinary answer for the overwhelming majority of
    /// characters, so it is a state and not an error: every letter on screen arrives here first.</para>
    /// </summary>
    private IDWriteColorGlyphRunEnumerator? TranslateColour(GlyphRun run, float subpixelOffset)
    {
        try
        {
            Result result = _factory.TranslateColorGlyphRun(
                subpixelOffset, 0f, run, null, MeasuringMode.Natural, null, 0,
                out IDWriteColorGlyphRunEnumerator? layers);

            return result.Success ? layers : null;
        }
        catch (SharpGenException)
        {
            return null;
        }
    }

    private float AdvanceOf(string family, FontWeight weight, FontStyle slant, ushort glyph, float size)
    {
        IDWriteFontFace face = Face(family, weight, slant);
        GlyphMetrics[] metrics = new GlyphMetrics[1];

        _glyph[0] = glyph;
        face.GetDesignGlyphMetrics(_glyph, metrics, false);

        return metrics[0].AdvanceWidth * size / face.Metrics.DesignUnitsPerEm;
    }

    /// <summary>
    /// The first family that actually has this character: the preferred one, then the faces Windows
    /// itself reaches for, then anything installed.
    ///
    /// <para>The ordered list is what keeps the answer stable and recognisable. A bare scan of the
    /// collection finds <em>a</em> face with the character, and which one depends on what somebody
    /// installed last — so the same text renders in a different font on two machines, which is worse
    /// than not rendering at all because nobody files it as a bug.</para>
    /// </summary>
    private string FamilyFor(string preferred, FontWeight weight, FontStyle slant, int codepoint)
    {
        if (!_installed.FindFamilyName(preferred, out _))
        {
            // A family nobody has is a configuration error and not a fallback: the user asked for a
            // font, and quietly drawing in a different one is how somebody spends an afternoon
            // wondering why their terminal ignores its own settings. Fallback is for a character
            // the chosen face lacks, which is a different thing entirely.
            throw new InvalidOperationException($"no font family named '{preferred}' is installed");
        }

        if (HasCharacter(preferred, codepoint))
        {
            return preferred;
        }

        foreach (string family in Fallbacks)
        {
            if (HasCharacter(family, codepoint))
            {
                return family;
            }
        }

        for (uint index = 0; index < _installed.FontFamilyCount; index++)
        {
            using IDWriteFontFamily family = _installed.GetFontFamily(index);
            using IDWriteFont font = family.GetFirstMatchingFont(weight, FontStretch.Normal, slant);

            if (font.HasCharacter((uint)codepoint))
            {
                using IDWriteLocalizedStrings names = family.FamilyNames;
                return NameOf(names) ?? preferred;
            }
        }

        // Nothing has it. The preferred face answers with .notdef, which is a box the user can see
        // and report, and is the honest picture of a character this machine cannot draw.
        return preferred;
    }

    private bool HasCharacter(string family, int codepoint)
    {
        if (!_installed.FindFamilyName(family, out uint index))
        {
            return false;
        }

        using IDWriteFontFamily matched = _installed.GetFontFamily(index);
        using IDWriteFont font = matched.GetFirstMatchingFont(FontWeight.Normal, FontStretch.Normal, FontStyle.Normal);

        return font.HasCharacter((uint)codepoint);
    }

    private static string? NameOf(IDWriteLocalizedStrings names)
    {
        return names.Count == 0 ? null : names.GetString(0);
    }

    private IDWriteFontFace Face(string family, FontWeight weight, FontStyle slant)
    {
        if (_faces.TryGetValue((family, weight, slant), out IDWriteFontFace? cached))
        {
            return cached;
        }

        if (!_installed.FindFamilyName(family, out uint index))
        {
            // The message names the family because that string came from a settings file somebody
            // typed, and "font not found" without it is a bug report nobody can act on.
            throw new InvalidOperationException($"no font family named '{family}' is installed");
        }

        using IDWriteFontFamily matched = _installed.GetFontFamily(index);
        using IDWriteFont font = matched.GetFirstMatchingFont(weight, FontStretch.Normal, slant);

        IDWriteFontFace face = font.CreateFontFace();
        _faces[(family, weight, slant)] = face;
        return face;
    }
}
