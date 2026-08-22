using Vortice.Direct3D11;
using Vortice.DirectWrite;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace Quickshell.Render;

/// <summary>Where one glyph's coverage landed, and where it sits relative to the pen.</summary>
/// <param name="Page">Which atlas page holds it; index into <see cref="GlyphAtlas.PageView"/>.</param>
/// <param name="X">The left edge within that page.</param>
/// <param name="Y">The top edge within that page.</param>
/// <param name="Width">The coverage width in pixels.</param>
/// <param name="Height">The coverage height in pixels.</param>
/// <param name="Left">The left edge relative to the pen position.</param>
/// <param name="Top">The top edge relative to the baseline, negative above it.</param>
public readonly record struct GlyphPlacement(int Page, int X, int Y, int Width, int Height, int Left, int Top)
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
    private readonly List<Page> _pages = [];
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
    /// <param name="maximumPages">The page ceiling. Beyond it a whole page is evicted.</param>
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

    /// <summary>The font every key handed to <see cref="Cache(int, FontWeight, FontStyle, float)"/> is rasterised at.</summary>
    public FontSettings Font { get; private set; }

    /// <summary>How many pages are currently allocated. Never above the ceiling given to <see cref="For"/>.</summary>
    public int PageCount => _pages.Count;

    /// <summary>How many distinct glyphs the cache is currently holding.</summary>
    public int CachedGlyphs => _entries.Count;

    /// <summary>How many whole pages have been evicted since the last device loss.</summary>
    public int Evictions { get; private set; }

    /// <summary>How many times a font change has thrown the whole cache away.</summary>
    public int Rebuilds { get; private set; }

    /// <summary>Rasterisations the atlas has caused. It exists to keep this far below the cells drawn.</summary>
    public int Rasterisations => _rasteriser.Rasterisations;

    /// <summary>The view a shader samples one page through.</summary>
    public ID3D11ShaderResourceView PageView(int page) => _pages[page].View;

    /// <summary>
    /// Caches one character at one pen position, shaping it through the face's character map.
    /// <paramref name="penX"/> is the column's real horizontal position: only its fraction matters,
    /// and it is quantised to <see cref="GlyphKey.SubpixelPositions"/>.
    /// </summary>
    public GlyphPlacement Cache(int codepoint, FontWeight weight = FontWeight.Normal,
                                FontStyle slant = FontStyle.Normal, float penX = 0f)
    {
        ushort glyph = _rasteriser.GlyphIndex(Font.Family, weight, slant, codepoint);
        GlyphKey key = new(Font.Family, weight, slant, Font.SizeInPixels, glyph, GlyphKey.Quantise(penX));

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
                _pages[hit.Page].LastUsed = ++_clock;
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

        foreach (Page page in _pages)
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
        _pages.Clear();
        Evictions = 0;
    }

    void IDeviceResource.Release()
    {
        foreach (Page page in _pages)
        {
            page.View.Dispose();
            page.Texture.Dispose();
        }

        _pages.Clear();
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

        // Two passes at most: the second runs against a page that is either brand new or just
        // evicted, and an empty page fits anything the check above let through.
        for (int attempt = 0; attempt < 2; attempt++)
        {
            for (int page = 0; page < _pages.Count; page++)
            {
                if (_pages[page].Packer.TryAllocate(bitmap.Width, bitmap.Height, out int x, out int y))
                {
                    return Upload(page, x, y, bitmap);
                }
            }

            if (_pages.Count < _maximumPages)
            {
                AddPage();
            }
            else
            {
                EvictLeastRecentlyUsed();
            }
        }

        throw new InvalidOperationException("no atlas page accepted a glyph an empty page would fit");
    }

    private GlyphPlacement Upload(int page, int x, int y, GlyphBitmap bitmap)
    {
        Page target = _pages[page];

        // One byte per pixel, so the row pitch is the glyph's own width: the box confines the write
        // to the rectangle the packer handed out, and no other glyph's pixels are touched.
        _graphics.Context.UpdateSubresource(
            bitmap.Coverage,
            target.Texture,
            0,
            (uint)bitmap.Width,
            0,
            new Box(x, y, 0, x + bitmap.Width, y + bitmap.Height, 1));

        target.LastUsed = ++_clock;
        return new GlyphPlacement(page, x, y, bitmap.Width, bitmap.Height, bitmap.Left, bitmap.Top);
    }

    private void AddPage()
    {
        ID3D11Device device = _device ?? throw new InvalidOperationException("the atlas has no device");

        Texture2DDescription description = new()
        {
            Width = PageSize,
            Height = PageSize,
            MipLevels = 1,
            ArraySize = 1,

            // One channel, because the coverage is one channel. A four-channel page would cost four
            // times the memory to carry three copies of the same byte.
            Format = Format.R8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.None,
        };

        ID3D11Texture2D texture = device.CreateTexture2D(description);

        _pages.Add(new Page
        {
            Texture = texture,
            View = device.CreateShaderResourceView(texture),
            LastUsed = ++_clock,
        });
    }

    private void EvictLeastRecentlyUsed()
    {
        int victim = 0;

        for (int page = 1; page < _pages.Count; page++)
        {
            if (_pages[page].LastUsed < _pages[victim].LastUsed)
            {
                victim = page;
            }
        }

        // An empty placement carries page zero and points at nothing, so it is not evidence that
        // page zero is still holding a glyph. Only real placements are dropped.
        foreach (GlyphKey key in _entries.Where(entry => !entry.Value.IsEmpty && entry.Value.Page == victim)
                                         .Select(entry => entry.Key)
                                         .ToList())
        {
            _entries.Remove(key);
        }

        _pages[victim].Packer.Reset();
        _pages[victim].LastUsed = ++_clock;
        Evictions++;
    }
}
