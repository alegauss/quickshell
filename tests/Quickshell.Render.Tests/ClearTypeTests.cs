using System.Runtime.InteropServices;
using Quickshell.Render;
using Quickshell.Terminal;
using Vortice.Direct3D11;
using Vortice.DirectWrite;
using Vortice.DXGI;
using Xunit;

namespace Quickshell.Render.Tests;

/// <summary>
/// Text rasterised against the display's colour stripes rather than against its pixels.
///
/// <para><b>The panel geometry is injected in every test here, and never read from the machine.</b>
/// A suite that took this display's answer would assert one thing on the developer's monitor and
/// another on the build agent, and the channel swap — the half most likely to be wrong — would be
/// exercised on neither. What is read from the machine in production is covered by one test of its
/// own, which asserts only that an answer arrives.</para>
/// </summary>
public sealed class ClearTypeTests
{
    private const uint Width = 320;
    private const uint Height = 120;

    /// <summary>Large, so a glyph has enough edge pixels for a fringe to be measurable.</summary>
    private static readonly FontSettings Grey = new("Consolas", 20f, 96f);

    /// <summary>The same font asking for ClearType, which is the only difference between them.</summary>
    private static readonly FontSettings Striped = Grey with { ClearType = true };

    /// <summary>A letter with vertical stems, which is where subpixel coverage has most to say.</summary>
    private const char Stems = 'H';

    // ---- The coverage: three numbers where there was one ----

    /// <summary>
    /// The symptom's cause, addressed: a ClearType glyph carries a separate coverage per colour
    /// stripe, and they are genuinely different numbers rather than one repeated three times.
    /// </summary>
    [Fact]
    public void AClearTypeGlyphCarriesThreeDifferentCoveragesPerPixel()
    {
        using GlyphRasteriser rasteriser = new(PixelGeometry.Rgb);

        GlyphBitmap striped = rasteriser.Rasterise(Key(rasteriser, Striped, Stems));

        Assert.Equal(GlyphKind.ClearType, striped.Kind);
        Assert.Equal(3, striped.BytesPerPixel);
        Assert.Equal(striped.Width * striped.Height * 3, striped.Coverage.Length);

        Assert.True(Fringed(striped),
                    "every pixel's three coverages were equal, so this is grayscale in three copies");
    }

    /// <summary>And the same face without it is still one channel, unchanged by any of this.</summary>
    [Fact]
    public void AGrayscaleGlyphIsStillOneCoveragePerPixel()
    {
        using GlyphRasteriser rasteriser = new(PixelGeometry.Rgb);

        GlyphBitmap grey = rasteriser.Rasterise(Key(rasteriser, Grey, Stems));

        Assert.Equal(GlyphKind.Coverage, grey.Kind);
        Assert.Equal(1, grey.BytesPerPixel);
        Assert.Equal(grey.Width * grey.Height, grey.Coverage.Length);
    }

    /// <summary>
    /// A panel whose stripes run blue to red gets its coverages reversed, once, as the glyph is
    /// rasterised. Getting this backwards is not subtle — every letter is fringed on the wrong side
    /// — and it is not something the machine running this suite can be asked to demonstrate, so the
    /// geometry is given rather than read.
    /// </summary>
    [Fact]
    public void ABlueToRedPanelGetsItsStripesTheOtherWayRound()
    {
        using GlyphRasteriser forwards = new(PixelGeometry.Rgb);
        using GlyphRasteriser backwards = new(PixelGeometry.Bgr);

        GlyphBitmap red = forwards.Rasterise(Key(forwards, Striped, Stems));
        GlyphBitmap blue = backwards.Rasterise(Key(backwards, Striped, Stems));

        Assert.Equal(red.Coverage.Length, blue.Coverage.Length);

        for (int pixel = 0; pixel * 3 < red.Coverage.Length; pixel++)
        {
            Assert.Equal(red.Coverage[pixel * 3], blue.Coverage[(pixel * 3) + 2]);
            Assert.Equal(red.Coverage[(pixel * 3) + 1], blue.Coverage[(pixel * 3) + 1]);
            Assert.Equal(red.Coverage[(pixel * 3) + 2], blue.Coverage[pixel * 3]);
        }
    }

    /// <summary>
    /// The falsification the design names, turned into a refusal. A panel with no horizontal stripe
    /// order cannot be drawn ClearType, so a font that asks for it is given grayscale instead —
    /// which is slightly thin, where honouring the request would be coloured fringes on every
    /// letter of a rotated screen.
    /// </summary>
    [Fact]
    public void APanelWithNoStripeOrderRefusesClearTypeHoweverTheFontAsks()
    {
        using GraphicsDevice device = GraphicsDevice.Open();
        using GlyphRasteriser flat = new(PixelGeometry.Flat);
        using GlyphAtlas atlas = GlyphAtlas.For(device, Striped, rasteriser: flat);

        Assert.False(flat.CanClearType);
        Assert.True(atlas.Font.ClearType, "the font did not actually ask, so nothing was refused");
        Assert.False(atlas.IsClearType);

        // And what it cached is grayscale, not an unread ClearType tile.
        atlas.Cache(Stems);

        Assert.Equal(GlyphKind.Coverage, flat.Rasterise(Key(flat, Striped, Stems)).Kind);
    }

    /// <summary>Whatever this display is, DirectWrite answered rather than leaving it unknown.</summary>
    [Fact]
    public void ThisDisplaysStripeOrderIsReadFromTheMachine()
    {
        using GlyphRasteriser rasteriser = new();

        Assert.Contains(rasteriser.Geometry,
                        new[] { PixelGeometry.Flat, PixelGeometry.Rgb, PixelGeometry.Bgr });
        Assert.Equal(rasteriser.Geometry != PixelGeometry.Flat, rasteriser.CanClearType);
    }

    // ---- The cache: two bitmaps, and it can tell them apart ----

    /// <summary>
    /// The same character at the same size in the same face is two different sets of pixels, so it
    /// is two entries. A key that could not tell them apart would hand a grayscale tile to a
    /// ClearType frame the first time a setting changed.
    /// </summary>
    [Fact]
    public void GrayscaleAndClearTypeAreTwoEntriesForOneCharacter()
    {
        using GraphicsDevice device = GraphicsDevice.Open();
        using GlyphRasteriser rasteriser = new(PixelGeometry.Rgb);
        using GlyphAtlas atlas = GlyphAtlas.For(device, Grey, rasteriser: rasteriser);

        atlas.Cache(Key(rasteriser, Grey, Stems));
        atlas.Cache(Key(rasteriser, Striped, Stems));

        Assert.Equal(2, atlas.CachedGlyphs);
        Assert.Equal(2, atlas.Rasterisations);
    }

    /// <summary>
    /// Turning it on remakes the coverage pages rather than reusing them. It is the one font change
    /// that cannot be answered by resetting a packer: a texture's format is fixed when it is made,
    /// and a one-channel page cannot hold three.
    /// </summary>
    [Fact]
    public void TurningItOnRemakesThePagesRatherThanResettingThem()
    {
        using GraphicsDevice device = GraphicsDevice.Open();
        using GlyphRasteriser rasteriser = new(PixelGeometry.Rgb);
        using GlyphAtlas atlas = GlyphAtlas.For(device, Grey, rasteriser: rasteriser);

        atlas.Cache(Stems);

        Assert.False(atlas.IsClearType);
        Assert.Equal(1, atlas.PageCount);

        atlas.UseFont(Striped);

        Assert.True(atlas.IsClearType);
        Assert.Equal(1, atlas.Rebuilds);
        Assert.Equal(0, atlas.PageCount);

        // And the next glyph opens a page of the new format and lands on it.
        GlyphPlacement placed = atlas.Cache(Stems);

        Assert.False(placed.IsEmpty);
        Assert.Equal(1, atlas.PageCount);
    }

    /// <summary>A font change that leaves the setting alone still only resets the packers.</summary>
    [Fact]
    public void AFontChangeThatKeepsTheSettingKeepsThePages()
    {
        using GraphicsDevice device = GraphicsDevice.Open();
        using GlyphRasteriser rasteriser = new(PixelGeometry.Rgb);
        using GlyphAtlas atlas = GlyphAtlas.For(device, Striped, rasteriser: rasteriser);

        atlas.Cache(Stems);
        atlas.UseFont(Striped with { SizeInPoints = 24f });

        Assert.True(atlas.IsClearType);
        Assert.Equal(1, atlas.PageCount);
        Assert.Equal(0, atlas.CachedGlyphs);
    }

    // ---- The picture: what it looks like on the glass ----

    /// <summary>
    /// The whole path, end to end and judged by pixels: white text on black is grey everywhere when
    /// the coverage is one channel, and takes colour at the stems when it is three.
    ///
    /// <para>This is the one that would catch the blend being wrong. The design expected the cost
    /// here to be a second pass or dual-source blending, because standard alpha blending carries one
    /// alpha and this needs three — but this renderer never used the output merger's blend at all.
    /// Every cell paints its own opaque background and the pixel shader does the mixing itself, so a
    /// three-channel coverage is a <c>lerp</c> by a <c>float3</c> and costs nothing.</para>
    /// </summary>
    [Fact]
    public void ClearTypeTakesColourAtTheStemsAndGrayscaleDoesNot()
    {
        using Harness grey = new(Grey);
        using Harness striped = new(Striped);

        Assert.False(grey.Atlas.IsClearType);
        Assert.True(striped.Atlas.IsClearType, "the ClearType harness is drawing grayscale");

        int greyFringes = Fringes(grey);
        int stripedFringes = Fringes(striped);

        Assert.Equal(0, greyFringes);
        Assert.True(stripedFringes > 0,
                    "no pixel of the ClearType frame differed between channels, so the three "
                    + "coverages never reached the blend");
    }

    /// <summary>How many pixels of a drawn letter differ between their red and blue channels.</summary>
    private static int Fringes(Harness harness)
    {
        CellMetrics metrics = harness.Metrics;

        GlyphPlacement glyph = harness.Atlas.Cache(Stems);
        Assert.False(glyph.IsEmpty, "the letter rasterised to nothing, so this counted an empty cell");

        CellInstance[] cells = [CellInstance.For(glyph, Rgb.White, Rgb.Black)];

        harness.Renderer.Draw(harness.Surface, cells, 1);

        byte[] frame = harness.ReadBack();
        int fringes = 0;

        for (int y = 0; y < metrics.Height; y++)
        {
            for (int x = 0; x < metrics.Width; x++)
            {
                int offset = ((y * (int)Width) + x) * 4;

                // One level is enough: the shader's own arithmetic cannot separate the channels of a
                // grey coverage at all, so anything above zero is the atlas carrying three numbers.
                if (Math.Abs(frame[offset] - frame[offset + 2]) > 0)
                {
                    fringes++;
                }
            }
        }

        return fringes;
    }

    /// <summary>Whether any pixel's three coverages differ, which is what makes it subpixel.</summary>
    private static bool Fringed(GlyphBitmap bitmap)
    {
        ReadOnlySpan<byte> pixels = bitmap.Coverage;

        for (int pixel = 0; (pixel * 3) + 2 < pixels.Length; pixel++)
        {
            if (pixels[pixel * 3] != pixels[(pixel * 3) + 2])
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The key for one character in one font, resolved the way the atlas resolves it.</summary>
    private static GlyphKey Key(GlyphRasteriser rasteriser, FontSettings font, char character)
    {
        GlyphResolution resolved =
            rasteriser.Resolve(font, FontWeight.Normal, FontStyle.Normal, character);

        return new GlyphKey(resolved.Family, FontWeight.Normal, FontStyle.Normal,
                            resolved.SizeInPixels, resolved.Glyph, 0)
        {
            ClearType = font.ClearType && rasteriser.CanClearType,
        };
    }

    /// <summary>A window, a device, an atlas and a renderer, plus the readback they are judged by.</summary>
    private sealed class Harness : IDisposable
    {
        private readonly TestWindow _window = new((int)Width, (int)Height);

        public Harness(FontSettings font)
        {
            Device = GraphicsDevice.Open(outputWindow: _window.Handle);
            Surface = PresentSurface.For(Device, _window.Handle, Width, Height);
            Rasteriser = new GlyphRasteriser(PixelGeometry.Rgb);
            Atlas = GlyphAtlas.For(Device, font, rasteriser: Rasteriser);
            Metrics = Rasteriser.Measure(font);
            Renderer = CellRenderer.For(Device, Atlas, Metrics);
        }

        public GraphicsDevice Device { get; }

        public PresentSurface Surface { get; }

        public GlyphRasteriser Rasteriser { get; }

        public GlyphAtlas Atlas { get; }

        public CellRenderer Renderer { get; }

        public CellMetrics Metrics { get; }

        public byte[] ReadBack()
        {
            using ID3D11Resource resource = Surface.View.Resource;
            using ID3D11Texture2D back = resource.QueryInterface<ID3D11Texture2D>();
            using ID3D11Texture2D staging = Device.Device.CreateTexture2D(new Texture2DDescription
            {
                Width = Width,
                Height = Height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Staging,
                BindFlags = BindFlags.None,
                CPUAccessFlags = CpuAccessFlags.Read,
            });

            Device.Context.CopyResource(staging, back);

            MappedSubresource mapped = Device.Context.Map(staging, 0, MapMode.Read);
            byte[] frame = new byte[Width * Height * 4];

            try
            {
                for (int row = 0; row < Height; row++)
                {
                    Marshal.Copy(mapped.DataPointer + (row * (int)mapped.RowPitch),
                                 frame, row * (int)Width * 4, (int)Width * 4);
                }
            }
            finally
            {
                Device.Context.Unmap(staging, 0);
            }

            return frame;
        }

        public void Dispose()
        {
            Renderer.Dispose();
            Atlas.Dispose();
            Rasteriser.Dispose();
            Surface.Dispose();
            Device.Dispose();
            _window.Dispose();
        }
    }
}
