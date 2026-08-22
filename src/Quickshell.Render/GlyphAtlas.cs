using Vortice.Direct3D11;
using Vortice.DirectWrite;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace Quickshell.Render;

/// <summary>Where one glyph's pixels landed, and where they sit relative to the pen.</summary>
/// <param name="Page">Which page holds it; an index into the pages of its own kind.</param>
/// <param name="X">The left edge within that page.</param>
/// <param name="Y">The top edge within that page.</param>
/// <param name="Width">The width in pixels.</param>
/// <param name="Height">The height in pixels.</param>
/// <param name="Left">The left edge relative to the pen position.</param>
/// <param name="Top">The top edge relative to the baseline, negative above it.</param>
/// <param name="IsColour">Whether this sits on a colour page, which the shader samples differently.</param>
public readonly record struct GlyphPlacement(int Page, int X, int Y, int Width, int Height,
                                             int Left, int Top, bool IsColour = false)
{
    /// <summary>A glyph that marks no pixels. It is cached like any other: a space is a lookup too.</summary>
    public static GlyphPlacement Empty => default;

    /// <summary>Whether there is nothing to sample. A caller draws no quad for this.</summary>
    public bool IsEmpty => Width <= 0 || Height <= 0;
}

/// <summary>
/// The glyph cache: every distinct shape rasterised once and sampled thereafter.
///
/// <para>A terminal redraws the same few hundred shapes forever, which is the whole reason this
/// renderer can be cheap. What makes that true is that <see cref="Cache(in GlyphKey)"/> is the only
/// door and it is keyed on <see cref="GlyphKey"/> — everything that changes pixels and nothing
/// that does not.</para>
///
/// <para>Packing is a skyline allocator over <see cref="PageSize"/>-square pages, pages added on
/// demand up to a ceiling. <b>Eviction takes a whole page</b>, least recently used: reclaiming a
/// hole inside a page costs more bookkeeping than the memory is worth.</para>
///
/// <para><b>There are two kinds of page.</b> Coverage pages hold one byte per pixel and are tinted
/// by the cell's foreground; colour pages hold four and are not, because an emoji is painted rather
/// than tinted. They cannot share a page — one channel and four are different textures — so they
/// are packed, counted and evicted apart, and a placement says which kind it is on.</para>
///
/// <para><b>A font change rebuilds rather than evicts.</b> Every entry is invalid at once, so
/// dropping the lot and resetting the packers is both simpler than a sweep and easier to prove
/// correct.</para>
///
/// <para><b>The atlas is GPU state, so device loss discards it.</b> It holds nothing that outlives
/// the device: after a loss it is empty and rebuilds itself from nothing but
/// <see cref="Font"/>, one glyph at a time, as the frames ask for them.</para>
/// </summary>
public sealed class GlyphAtlas : IDeviceResource, IDisposable
{
    /// <summary>The side of one atlas page in pixels. Every feature level 11_0 device allows it.</summary>
    public const int PageSize = 2048;

    private readonly Dictionary<GlyphKey, GlyphPlacement> _entries = [];
    private readonly List<Page> _coverage = [];
    private readonly List<Page> _colour = [];
    private readonly GraphicsDevice _graphics;
    private readonly GlyphRasteriser _rasteriser;
    private readonly bool _ownsRasteriser;
    private readonly int _maximumPages;

    private ID3D11Device? _device;
    private long _clock;

    private GlyphAtlas(GraphicsDevice graphics, FontSettings font, int maximumPages,
                       GlyphRasteriser? rasteriser)
    {
        _graphics = graphics;
        _maximumPages = maximumPages;
        _ownsRasteriser = rasteriser is null;
        _rasteriser = rasteriser ?? new GlyphRasteriser();
        Font = font;
    }

    /// <summary>One page and the profile of what has been packed into it.</summary>
    private sealed class Page
    {
        public required ID3D11Texture2D Texture { get; init; }

        public required ID3D11ShaderResourceView View { get; init; }

        public required GlyphKind Kind { get; init; }

        public SkylinePacker Packer { get; } = new(PageSize, PageSize);

        /// <summary>The clock reading at the last hit. Lowest is what eviction takes.</summary>
        public long LastUsed { get; set; }
    }

    /// <summary>
    /// Opens an atlas and registers it with the device that owns it, so a device loss releases and
    /// rebuilds it without the caller doing either.
    /// </summary>
    /// <param name="graphics">The device this atlas lives on.</param>
    /// <param name="font">The font every key is rasterised at; <see cref="FontSettings.Default"/> when null.</param>
    /// <param name="maximumPages">The ceiling on pages of each kind. Beyond it a whole page is evicted.</param>
    /// <param name="rasteriser">A rasteriser to share; one is opened and owned when null.</param>
    public static GlyphAtlas For(GraphicsDevice graphics, FontSettings? font = null,
                                 int maximumPages = 4, GlyphRasteriser? rasteriser = null)
    {
        ArgumentNullException.ThrowIfNull(graphics);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumPages, 1);

        GlyphAtlas atlas = new(graphics, font ?? FontSettings.Default, maximumPages, rasteriser);
        graphics.Register(atlas);
        return atlas;
    }

    /// <summary>The font every character handed to <see cref="Cache(int, FontWeight, FontStyle, float, float)"/> is resolved against.</summary>
    public FontSettings Font { get; private set; }

    /// <summary>How many coverage pages are allocated. Never above <see cref="MaximumPages"/>.</summary>
    public int PageCount => _coverage.Count;

    /// <summary>How many colour pages are allocated. Never above <see cref="MaximumPages"/>.</summary>
    public int ColourPageCount => _colour.Count;

    /// <summary>The ceiling on pages of each kind. Beyond it a whole page is evicted.</summary>
    public int MaximumPages => _maximumPages;

    /// <summary>How many distinct glyphs the cache is currently holding.</summary>
    public int CachedGlyphs => _entries.Count;

    /// <summary>How many whole pages have been evicted since the last device loss.</summary>
    public int Evictions { get; private set; }

    /// <summary>How many times a font change has thrown the whole cache away.</summary>
    public int Rebuilds { get; private set; }

    /// <summary>Rasterisations the atlas has caused. It exists to keep this far below the cells drawn.</summary>
    public int Rasterisations => _rasteriser.Rasterisations;

    /// <summary>The view a shader samples one coverage page through.</summary>
    public ID3D11ShaderResourceView PageView(int page) => _coverage[page].View;

    /// <summary>The view a shader samples one colour page through.</summary>
    public ID3D11ShaderResourceView ColourPageView(int page) => _colour[page].View;

    /// <summary>
    /// Caches one character at one pen position, falling back to a face that has it when the
    /// configured family does not.
    /// </summary>
    /// <param name="codepoint">The character, as a Unicode codepoint rather than a UTF-16 unit.</param>
    /// <param name="weight">The weight to match a face at.</param>
    /// <param name="slant">Upright, italic or oblique.</param>
    /// <param name="penX">The column's real horizontal position; only its fraction matters.</param>
    /// <param name="maximumAdvance">
    /// The room the glyph has, in pixels — one cell, or two for a wide character. A substituted face
    /// wider than this is rasterised smaller so it fits, because a fallback's metrics are its own
    /// and a glyph that spills into the next cell is a glyph drawn over somebody else's character.
    /// Zero leaves the size alone.
    /// </param>
    public GlyphPlacement Cache(int codepoint, FontWeight weight = FontWeight.Normal,
                                FontStyle slant = FontStyle.Normal, float penX = 0f,
                                float maximumAdvance = 0f)
    {
        GlyphResolution resolved = _rasteriser.Resolve(Font, weight, slant, codepoint, maximumAdvance);
        GlyphKey key = new(resolved.Family, weight, slant, resolved.SizeInPixels, resolved.Glyph,
                           GlyphKey.Quantise(penX));

        return Cache(key);
    }

    /// <summary>
    /// Caches one glyph, rasterising it only if this exact key has not been seen since the last
    /// rebuild, eviction or device loss.
    /// </summary>
    public GlyphPlacement Cache(in GlyphKey key)
    {
        if (_entries.TryGetValue(key, out GlyphPlacement hit))
        {
            if (!hit.IsEmpty)
            {
                PagesFor(hit.IsColour ? GlyphKind.Colour : GlyphKind.Coverage)[hit.Page].LastUsed = ++_clock;
            }

            return hit;
        }

        GlyphPlacement placement = Place(_rasteriser.Rasterise(key));
        _entries[key] = placement;
        return placement;
    }

    /// <summary>
    /// Points the atlas at a different font. Every entry is invalid at once, so this rebuilds: the
    /// cache is emptied and the pages are handed back to their packers rather than swept for what
    /// happens to still be valid.
    /// </summary>
    public void UseFont(FontSettings font)
    {
        if (font == Font)
        {
            return;
        }

        Font = font;
        _entries.Clear();

        foreach (Page page in _coverage.Concat(_colour))
        {
            page.Packer.Reset();
        }

        Rebuilds++;
    }

    void IDeviceResource.Create(ID3D11Device device)
    {
        // Nothing is built here. Every page is GPU memory for glyphs nobody has asked for yet, and
        // what the atlas held before the loss is exactly what the next frames will ask for again.
        _device = device;
        _entries.Clear();
        _coverage.Clear();
        _colour.Clear();
        Evictions = 0;
    }

    void IDeviceResource.Release()
    {
        foreach (Page page in _coverage.Concat(_colour))
        {
            page.View.Dispose();
            page.Texture.Dispose();
        }

        _coverage.Clear();
        _colour.Clear();
        _entries.Clear();
        _device = null;
    }

    /// <summary>Releases the pages, and the rasteriser if this atlas opened one.</summary>
    public void Dispose()
    {
        ((IDeviceResource)this).Release();

        if (_ownsRasteriser)
        {
            _rasteriser.Dispose();
        }
    }

    private List<Page> PagesFor(GlyphKind kind) => kind == GlyphKind.Colour ? _colour : _coverage;

    private GlyphPlacement Place(GlyphBitmap bitmap)
    {
        if (bitmap.IsEmpty)
        {
            return GlyphPlacement.Empty;
        }

        if (bitmap.Width > PageSize || bitmap.Height > PageSize)
        {
            throw new InvalidOperationException(
                $"a {bitmap.Width}x{bitmap.Height} glyph does not fit a {PageSize}-square atlas page");
        }

        List<Page> pages = PagesFor(bitmap.Kind);

        // Two passes at most: the second runs against a page that is either brand new or just
        // evicted, and an empty page fits anything the check above let through.
        for (int attempt = 0; attempt < 2; attempt++)
        {
            for (int page = 0; page < pages.Count; page++)
            {
                if (pages[page].Packer.TryAllocate(bitmap.Width, bitmap.Height, out int x, out int y))
                {
                    return Upload(pages, page, x, y, bitmap);
                }
            }

            if (pages.Count < _maximumPages)
            {
                AddPage(bitmap.Kind);
            }
            else
            {
                EvictLeastRecentlyUsed(bitmap.Kind);
            }
        }

        throw new InvalidOperationException("no atlas page accepted a glyph an empty page would fit");
    }

    private GlyphPlacement Upload(List<Page> pages, int page, int x, int y, GlyphBitmap bitmap)
    {
        Page target = pages[page];

        // The row pitch is the glyph's own width in bytes, one for coverage and four for colour.
        // The box confines the write to the rectangle the packer handed out, so no other glyph's
        // pixels are touched.
        _graphics.Context.UpdateSubresource(
            bitmap.Coverage,
            target.Texture,
            0,
            (uint)(bitmap.Width * bitmap.BytesPerPixel),
            0,
            new Box(x, y, 0, x + bitmap.Width, y + bitmap.Height, 1));

        target.LastUsed = ++_clock;
        return new GlyphPlacement(page, x, y, bitmap.Width, bitmap.Height, bitmap.Left, bitmap.Top,
                                  bitmap.Kind == GlyphKind.Colour);
    }

    private void AddPage(GlyphKind kind)
    {
        ID3D11Device device = _device ?? throw new InvalidOperationException("the atlas has no device");

        Texture2DDescription description = new()
        {
            Width = PageSize,
            Height = PageSize,
            MipLevels = 1,
            ArraySize = 1,

            // One channel for coverage, because the coverage is one channel: a four-channel page
            // would cost four times the memory to carry three copies of the same byte. Colour
            // glyphs are the case where all four are carrying something.
            Format = kind == GlyphKind.Colour ? Format.R8G8B8A8_UNorm : Format.R8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.None,
        };

        ID3D11Texture2D texture = device.CreateTexture2D(description);

        PagesFor(kind).Add(new Page
        {
            Texture = texture,
            View = device.CreateShaderResourceView(texture),
            Kind = kind,
            LastUsed = ++_clock,
        });
    }

    private void EvictLeastRecentlyUsed(GlyphKind kind)
    {
        List<Page> pages = PagesFor(kind);
        bool colour = kind == GlyphKind.Colour;
        int victim = 0;

        for (int page = 1; page < pages.Count; page++)
        {
            if (pages[page].LastUsed < pages[victim].LastUsed)
            {
                victim = page;
            }
        }

        // An empty placement carries page zero and points at nothing, so it is not evidence that
        // page zero is still holding a glyph. Only real placements on this kind of page are dropped.
        foreach (GlyphKey key in _entries.Where(entry => !entry.Value.IsEmpty
                                                         && entry.Value.IsColour == colour
                                                         && entry.Value.Page == victim)
                                         .Select(entry => entry.Key)
                                         .ToList())
        {
            _entries.Remove(key);
        }

        pages[victim].Packer.Reset();
        pages[victim].LastUsed = ++_clock;
        Evictions++;
    }
}
