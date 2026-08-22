using Vortice;
using Vortice.DCommon;
using Vortice.DirectWrite;

namespace Quickshell.Render;

/// <summary>
/// One rasterised glyph: its coverage, and where that coverage sits relative to the pen.
///
/// <para><see cref="Left"/> and <see cref="Top"/> are DirectWrite's own bounds, so they are signed
/// and usually negative on the vertical: a glyph rises above the baseline the run was stated at.</para>
/// </summary>
public sealed class GlyphBitmap
{
    private readonly byte[] _coverage;

    internal GlyphBitmap(int width, int height, int left, int top, byte[] coverage)
    {
        Width = width;
        Height = height;
        Left = left;
        Top = top;
        _coverage = coverage;
    }

    /// <summary>A glyph that marks no pixels: a space, or a control character with no shape.</summary>
    public static GlyphBitmap Empty { get; } = new(0, 0, 0, 0, []);

    /// <summary>The coverage bitmap's width in pixels.</summary>
    public int Width { get; }

    /// <summary>The coverage bitmap's height in pixels.</summary>
    public int Height { get; }

    /// <summary>The left edge relative to the pen position the run was rasterised at.</summary>
    public int Left { get; }

    /// <summary>The top edge relative to the baseline, negative for anything that rises above it.</summary>
    public int Top { get; }

    /// <summary>Whether this glyph marks no pixels at all.</summary>
    public bool IsEmpty => Width <= 0 || Height <= 0;

    /// <summary>One byte of coverage per pixel, row-major, <see cref="Width"/> bytes to a row.</summary>
    public ReadOnlySpan<byte> Coverage => _coverage;
}

/// <summary>
/// DirectWrite, reduced to the one question the atlas asks it: what pixels does this glyph cover.
///
/// <para><c>IDWriteGlyphRunAnalysis::CreateAlphaTexture</c> is the specific door, because it
/// produces exactly the coverage bitmap Windows itself would produce. Text here then matches text
/// everywhere else on the machine instead of being subtly this project's own.</para>
///
/// <para>Grayscale coverage only — <c>DWRITE_TEXTURE_ALIASED_1x1</c>, one channel. Subpixel coverage
/// is a genuinely different pipeline and a later line; shipping grayscale first means the renderer
/// is correct before it is pretty.</para>
///
/// <para>This holds no GPU state, so it is deliberately not an <see cref="IDeviceResource"/>: a
/// device loss costs the atlas its pixels and costs this nothing.</para>
/// </summary>
public sealed class GlyphRasteriser : IDisposable
{
    private readonly Dictionary<(string Family, FontWeight Weight, FontStyle Slant), IDWriteFontFace> _faces = [];
    private readonly IDWriteFactory2 _factory;
    private readonly IDWriteFontCollection _installed;

    // DirectWrite's character-map call is a batch API and the atlas asks it one character at a
    // time, so the batch of one is kept rather than allocated on every cache miss.
    private readonly uint[] _codepoint = new uint[1];
    private readonly ushort[] _glyph = new ushort[1];

    /// <summary>Opens the shared DirectWrite factory and reads the system font collection once.</summary>
    public GlyphRasteriser()
    {
        // IDWriteFactory2 and not IDWriteFactory, for one argument: its CreateGlyphRunAnalysis is
        // the only one that takes an antialias mode, and grayscale coverage in a one-channel texture
        // is what asking the older factory for a 1x1 texture silently answers empty to.
        _factory = DWrite.DWriteCreateFactory<IDWriteFactory2>(FactoryType.Shared);
        _installed = _factory.GetSystemFontCollection(false);
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

        return new CellMetrics(
            Math.Max(1, (int)MathF.Ceiling(reference[0].AdvanceWidth * scale)),
            Math.Max(1, (int)MathF.Ceiling((metrics.Ascent + metrics.Descent + metrics.LineGap) * scale)),
            (int)MathF.Round(metrics.Ascent * scale));
    }

    /// <summary>
    /// Rasterises one glyph at one subpixel offset. The em size is taken as physical pixels and
    /// DirectWrite is told one pixel per DIP, so the key's size field and the bitmap's size are the
    /// same number rather than two that have to be kept in step.
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

        using IDWriteGlyphRunAnalysis analysis = _factory.CreateGlyphRunAnalysis(
            run,
            null,
            RenderingMode.NaturalSymmetric,
            MeasuringMode.Natural,
            GridFitMode.Default,
            TextAntialiasMode.Grayscale,
            key.SubpixelOffsetInPixels,
            0f);

        Rasterisations++;

        RawRect bounds = analysis.GetAlphaTextureBounds(TextureType.Aliased1x1);
        int width = bounds.Right - bounds.Left;
        int height = bounds.Bottom - bounds.Top;

        if (width <= 0 || height <= 0)
        {
            return GlyphBitmap.Empty;
        }

        byte[] coverage = new byte[width * height];
        analysis.CreateAlphaTexture(TextureType.Aliased1x1, bounds, coverage, (uint)coverage.Length);

        return new GlyphBitmap(width, height, bounds.Left, bounds.Top, coverage);
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
