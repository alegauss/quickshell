using System.Runtime.InteropServices;
using Quickshell.Render;
using Quickshell.Terminal;
using Vortice.Direct3D11;
using Vortice.DirectWrite;
using Vortice.DXGI;
using Xunit;

namespace Quickshell.Render.Tests;

/// <summary>
/// The three places the monospaced grid stops being true: a character no monospaced face has, a
/// glyph that is painted rather than tinted, and a character that is not one cell wide.
/// </summary>
public sealed class WideAndColourTests
{
    private const uint Width = 320;
    private const uint Height = 120;

    private const int Grinning = 0x1F600;   // an emoji, so a colour glyph
    private const int Middle = 0x4E2D;      // CJK, so wide and almost certainly a fallback face
    private const int Acute = 0x0301;       // a combining mark, so no cell of its own

    // ---- Width: the model's answer, which the renderer is told ----

    [Theory]
    [InlineData('A', 1)]
    [InlineData(' ', 1)]
    [InlineData(Middle, 2)]
    [InlineData(Grinning, 2)]
    [InlineData(0xFF21, 2)]      // fullwidth A
    [InlineData(0xAC00, 2)]      // Hangul syllable
    [InlineData(Acute, 0)]
    [InlineData(0x200D, 0)]      // zero-width joiner
    [InlineData(0xFE0F, 0)]      // variation selector 16
    [InlineData('\n', 0)]
    [InlineData(0x0416, 1)]      // Cyrillic Zhe, narrow
    public void WidthIsWhatTheHostWillAlsoHaveDecided(int codepoint, int cells)
    {
        Assert.Equal(cells, CharacterWidth.Of(codepoint));
    }

    /// <summary>
    /// The criterion this line is read against: a mixed line has to leave the cursor where the host
    /// puts it, and the cursor column is a running total of exactly this.
    /// </summary>
    [Fact]
    public void AMixedLineTotalsTheColumnsTheHostWouldHave()
    {
        // "ab" + CJK + combining acute on the previous cell + emoji.
        string line = "ab中́\U0001F600";

        Assert.Equal(1 + 1 + 2 + 0 + 2, CharacterWidth.Of(line));
    }

    [Fact]
    public void ASurrogatePairCountsOnceAsTheCodepointItEncodes()
    {
        Assert.Equal(2, CharacterWidth.Of("\U0001F600"));
        Assert.Equal(2, "\U0001F600".Length);
    }

    // ---- Fallback: a face that has the character, cached like any other ----

    [Fact]
    public void ACharacterConsolasLacksResolvesToAFaceThatHasIt()
    {
        using GlyphRasteriser rasteriser = new();
        FontSettings font = new("Consolas", 20f, 96f);

        GlyphResolution latin = rasteriser.Resolve(font, FontWeight.Normal, FontStyle.Normal, 'A');
        GlyphResolution cjk = rasteriser.Resolve(font, FontWeight.Normal, FontStyle.Normal, Middle);

        Assert.False(latin.Substituted);
        Assert.Equal("Consolas", latin.Family);

        Assert.True(cjk.Substituted, "Consolas was reported as covering a CJK ideograph");
        Assert.NotEqual("Consolas", cjk.Family);
        Assert.NotEqual(0, cjk.Glyph);
    }

    [Fact]
    public void AnEmojiResolvesToTheColourFaceAndRasterisesAsColour()
    {
        using GlyphRasteriser rasteriser = new();
        FontSettings font = new("Consolas", 20f, 96f);

        GlyphResolution resolved = rasteriser.Resolve(font, FontWeight.Normal, FontStyle.Normal, Grinning);
        GlyphBitmap bitmap = rasteriser.Rasterise(new GlyphKey(
            resolved.Family, FontWeight.Normal, FontStyle.Normal, resolved.SizeInPixels, resolved.Glyph, 0));

        Assert.True(resolved.Substituted);
        Assert.Equal(GlyphKind.Colour, bitmap.Kind);
        Assert.Equal(4, bitmap.BytesPerPixel);
        Assert.False(bitmap.IsEmpty);

        // Not one flat colour: an emoji that composited to a single tone is one layer, which means
        // the layer walk stopped early.
        ReadOnlySpan<byte> pixels = bitmap.Coverage;
        int distinct = 0;
        uint first = 0;

        for (int pixel = 0; pixel < bitmap.Width * bitmap.Height; pixel++)
        {
            if (pixels[(pixel * 4) + 3] == 0)
            {
                continue;
            }

            uint colour = ((uint)pixels[pixel * 4] << 16) | ((uint)pixels[(pixel * 4) + 1] << 8) | pixels[(pixel * 4) + 2];

            if (distinct == 0)
            {
                first = colour;
                distinct = 1;
            }
            else if (colour != first)
            {
                distinct = 2;
                break;
            }
        }

        Assert.Equal(2, distinct);
    }

    [Fact]
    public void ALetterIsStillCoverageAndNotColour()
    {
        using GlyphRasteriser rasteriser = new();
        FontSettings font = new("Consolas", 20f, 96f);

        GlyphResolution resolved = rasteriser.Resolve(font, FontWeight.Normal, FontStyle.Normal, 'A');
        GlyphBitmap bitmap = rasteriser.Rasterise(new GlyphKey(
            resolved.Family, FontWeight.Normal, FontStyle.Normal, resolved.SizeInPixels, resolved.Glyph, 0));

        Assert.Equal(GlyphKind.Coverage, bitmap.Kind);
        Assert.Equal(1, bitmap.BytesPerPixel);
    }

    /// <summary>
    /// A fallback face has its own idea of an advance, and drawn at the primary's em size it spills
    /// into the cells beside it. The room it is given is what it is fitted to.
    /// </summary>
    [Fact]
    public void ASubstitutedFaceIsFittedToTheRoomItIsGiven()
    {
        using GlyphRasteriser rasteriser = new();
        FontSettings font = new("Consolas", 20f, 96f);
        CellMetrics metrics = rasteriser.Measure(font);

        GlyphResolution unfitted = rasteriser.Resolve(font, FontWeight.Normal, FontStyle.Normal, Middle);
        GlyphResolution fitted = rasteriser.Resolve(font, FontWeight.Normal, FontStyle.Normal, Middle,
                                                    metrics.Width * 2f);

        Assert.Equal(font.SizeInPixels, unfitted.SizeInPixels, 3);
        Assert.True(fitted.SizeInPixels <= unfitted.SizeInPixels,
                    "fitting a glyph to its cells made it larger");

        GlyphBitmap bitmap = rasteriser.Rasterise(new GlyphKey(
            fitted.Family, FontWeight.Normal, FontStyle.Normal, fitted.SizeInPixels, fitted.Glyph, 0));

        Assert.True(bitmap.Width <= metrics.Width * 2,
                    $"a fitted glyph is {bitmap.Width}px wide in {metrics.Width * 2}px of cell");
    }

    [Fact]
    public void ColourAndCoverageGlyphsLandOnDifferentPages()
    {
        using GraphicsDevice device = GraphicsDevice.Open();
        using GlyphAtlas atlas = GlyphAtlas.For(device, new FontSettings("Consolas", 20f, 96f));

        GlyphPlacement letter = atlas.Cache('A');
        GlyphPlacement emoji = atlas.Cache(Grinning);

        Assert.False(letter.IsColour);
        Assert.True(emoji.IsColour, "an emoji was cached as coverage, so it will be drawn as a silhouette");
        Assert.Equal(1, atlas.PageCount);
        Assert.Equal(1, atlas.ColourPageCount);
        Assert.Equal(2, atlas.CachedGlyphs);
    }

    // ---- The picture: what all three look like on the glass ----

    /// <summary>
    /// A wide character is drawn across both of its cells. Ink in the trailing cell is what says the
    /// span reached the shader; without it the right half of every CJK character is missing.
    /// </summary>
    [Fact]
    public void AWideCharacterIsDrawnAcrossBothOfItsCells()
    {
        using Harness harness = new();
        CellMetrics metrics = harness.Metrics;

        GlyphPlacement wide = harness.Atlas.Cache(Middle, maximumAdvance: metrics.Width * 2f);
        CellInstance[] cells =
        [
            CellInstance.For(wide, Rgb.White, Rgb.Black, span: 2),
            CellInstance.For(GlyphPlacement.Empty, Rgb.White, Rgb.Black, span: 0),
            CellInstance.For(GlyphPlacement.Empty, Rgb.White, Rgb.Black),
        ];

        harness.Renderer.Draw(harness.Surface, cells, 3);
        byte[] frame = harness.ReadBack();

        Assert.True(InkIn(frame, metrics, 0) > 0, "the leading cell of a wide pair drew nothing");
        Assert.True(InkIn(frame, metrics, 1) > 0,
                    "the trailing cell of a wide pair is blank, so the character was clipped in half");
    }

    /// <summary>
    /// The trailing cell draws nothing of its own. If it painted its own background it would erase
    /// the half of the character the leading cell had just drawn there.
    /// </summary>
    [Fact]
    public void TheTrailingCellOfAWidePairPaintsNothingOverIt()
    {
        using Harness harness = new();
        CellMetrics metrics = harness.Metrics;

        GlyphPlacement wide = harness.Atlas.Cache(Middle, maximumAdvance: metrics.Width * 2f);

        // The trailing cell is given a colour that could not be mistaken for the leading cell's, so
        // a quad it should not have drawn is obvious rather than subtle.
        CellInstance[] painted =
        [
            CellInstance.For(wide, Rgb.White, Rgb.Black, span: 2),
            CellInstance.For(GlyphPlacement.Empty, Rgb.White, new Rgb(255, 0, 255), span: 0),
        ];

        harness.Renderer.Draw(harness.Surface, painted, 2);
        byte[] frame = harness.ReadBack();

        for (int x = metrics.Width; x < metrics.Width * 2; x++)
        {
            Assert.NotEqual(new Rgb(255, 0, 255), Pixel(frame, x, metrics.Height / 2));
        }
    }

    /// <summary>
    /// An emoji ignores the cell's foreground entirely. Drawn twice with two different foregrounds,
    /// the pixels must be identical — if they are not, it is being tinted like coverage.
    /// </summary>
    [Fact]
    public void AColourGlyphIgnoresTheCellsForeground()
    {
        using Harness harness = new();
        CellMetrics metrics = harness.Metrics;

        GlyphPlacement emoji = harness.Atlas.Cache(Grinning, maximumAdvance: metrics.Width * 2f);
        Assert.True(emoji.IsColour);

        CellInstance[] cells =
        [
            CellInstance.For(emoji, Rgb.White, Rgb.Black, span: 2),
            CellInstance.For(GlyphPlacement.Empty, Rgb.White, Rgb.Black, span: 0),
            CellInstance.For(emoji, new Rgb(0, 255, 0), Rgb.Black, span: 2),
            CellInstance.For(GlyphPlacement.Empty, Rgb.White, Rgb.Black, span: 0),
        ];

        harness.Renderer.Draw(harness.Surface, cells, 4);
        byte[] frame = harness.ReadBack();

        int ink = 0;

        for (int y = 0; y < metrics.Height; y++)
        {
            for (int x = 0; x < metrics.Width * 2; x++)
            {
                Rgb white = Pixel(frame, x, y);
                Rgb green = Pixel(frame, (metrics.Width * 2) + x, y);

                Assert.Equal(white, green);
                ink += white == Rgb.Black ? 0 : 1;
            }
        }

        Assert.True(ink > 0, "the emoji drew no pixels at all, so this compared two empty cells");
    }

    private static int InkIn(byte[] frame, CellMetrics metrics, int column)
    {
        int ink = 0;

        for (int y = 0; y < metrics.Height; y++)
        {
            for (int x = 0; x < metrics.Width; x++)
            {
                ink += Pixel(frame, (column * metrics.Width) + x, y).Green > 0 ? 1 : 0;
            }
        }

        return ink;
    }

    private static Rgb Pixel(byte[] frame, int x, int y)
    {
        int offset = ((y * (int)Width) + x) * 4;
        return new Rgb(frame[offset + 2], frame[offset + 1], frame[offset]);
    }

    /// <summary>A window, a device, an atlas and a renderer, plus the readback they are judged by.</summary>
    private sealed class Harness : IDisposable
    {
        private static readonly FontSettings Font = new("Consolas", 20f, 96f);

        private readonly TestWindow _window = new((int)Width, (int)Height);

        public Harness()
        {
            Device = GraphicsDevice.Open(outputWindow: _window.Handle);
            Surface = PresentSurface.For(Device, _window.Handle, Width, Height);
            Rasteriser = new GlyphRasteriser();
            Atlas = GlyphAtlas.For(Device, Font, rasteriser: Rasteriser);
            Metrics = Rasteriser.Measure(Font);
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
