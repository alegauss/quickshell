using System.Runtime.InteropServices;
using Quickshell.Render;
using Quickshell.Terminal;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Xunit;

namespace Quickshell.Render.Tests;

/// <summary>
/// The grid, drawn for real and read back off the back buffer. A renderer that compiles and binds
/// everything correctly can still put the wrong colour on the glass, and nothing but the pixels
/// says which happened.
/// </summary>
public sealed class CellRendererTests
{
    private const uint Width = 320;
    private const uint Height = 200;

    [Fact]
    public void TheInstanceIsTwentyBytes()
    {
        // The stride the vertex buffer is bound with, against the struct the GPU actually reads.
        Assert.Equal(CellInstance.Stride, Marshal.SizeOf<CellInstance>());
    }

    [Fact]
    public void AFontImpliesAWholePixelGrid()
    {
        using GlyphRasteriser rasteriser = new();

        CellMetrics metrics = rasteriser.Measure(FontSettings.Default);

        Assert.True(metrics.Width > 0, "a cell with no width is not a grid");
        Assert.True(metrics.Height > metrics.Baseline, "the baseline is below the bottom of the cell");
        Assert.True(metrics.Baseline > 0, "the baseline is above the top of the cell");
        Assert.Equal((32, 10), new CellMetrics(10, 20, 16).GridFor(320, 200));
    }

    [Fact]
    public void OneDrawPutsEveryCellsBackgroundOnTheBackBuffer()
    {
        using Harness harness = new();

        Rgb blue = new(0, 0, 200);
        Rgb red = new(200, 0, 0);
        int columns = 8;
        int rows = 4;

        CellInstance[] cells = new CellInstance[columns * rows];

        for (int cell = 0; cell < cells.Length; cell++)
        {
            cells[cell] = CellInstance.For(GlyphPlacement.Empty, Rgb.White, cell % 2 == 0 ? blue : red);
        }

        harness.Renderer.Draw(harness.Surface, cells, columns);

        Assert.Equal(1, harness.Renderer.Draws);

        byte[] frame = harness.ReadBack();
        CellMetrics metrics = harness.Metrics;

        // The middle of each cell, so a one-pixel error in placement is not what this reads.
        for (int row = 0; row < rows; row++)
        {
            for (int column = 0; column < columns; column++)
            {
                Rgb expected = ((row * columns) + column) % 2 == 0 ? blue : red;
                Rgb actual = Pixel(frame,
                                   (column * metrics.Width) + (metrics.Width / 2),
                                   (row * metrics.Height) + (metrics.Height / 2));

                Assert.Equal(expected, actual);
            }
        }
    }

    [Fact]
    public void ACellsBackgroundStopsAtItsOwnEdge()
    {
        using Harness harness = new();

        Rgb green = new(0, 180, 0);
        CellInstance[] cells =
        [
            CellInstance.For(GlyphPlacement.Empty, Rgb.White, green),
            CellInstance.For(GlyphPlacement.Empty, Rgb.White, Rgb.Black),
        ];

        harness.Renderer.Draw(harness.Surface, cells, 2);

        byte[] frame = harness.ReadBack();
        int edge = harness.Metrics.Width;

        Assert.Equal(green, Pixel(frame, edge - 1, 0));
        Assert.Equal(Rgb.Black, Pixel(frame, edge, 0));
    }

    [Fact]
    public void InverseSwapsTheTwoColours()
    {
        using Harness harness = new();

        Rgb amber = new(220, 140, 0);
        CellInstance[] cells =
        [
            CellInstance.For(GlyphPlacement.Empty, amber, Rgb.Black),
            CellInstance.For(GlyphPlacement.Empty, amber, Rgb.Black, CellFlags.Inverse),
        ];

        harness.Renderer.Draw(harness.Surface, cells, 2);

        byte[] frame = harness.ReadBack();

        Assert.Equal(Rgb.Black, Pixel(frame, 1, 1));
        Assert.Equal(amber, Pixel(frame, harness.Metrics.Width + 1, 1));
    }

    /// <summary>
    /// The falsification the design names. Coverage says how much of a pixel the glyph covers, so
    /// half coverage is half the <em>light</em> - and mixing it in sRGB instead of linear makes
    /// light-on-dark text thin and dark-on-light text heavy by the same mistake in opposite
    /// directions. Measured as ink, the two must agree.
    /// </summary>
    [Fact]
    public void TheSameCharacterLightOnDarkAndDarkOnLightHasMatchingWeight()
    {
        using Harness harness = new();

        GlyphPlacement glyph = harness.Atlas.Cache('B');
        Assert.False(glyph.IsEmpty);

        CellInstance[] cells =
        [
            CellInstance.For(glyph, Rgb.White, Rgb.Black),
            CellInstance.For(glyph, Rgb.Black, Rgb.White),
        ];

        harness.Renderer.Draw(harness.Surface, cells, 2);

        byte[] frame = harness.ReadBack();
        CellMetrics metrics = harness.Metrics;

        double lightOnDark = 0;
        double darkOnLight = 0;

        for (int y = 0; y < metrics.Height; y++)
        {
            for (int x = 0; x < metrics.Width; x++)
            {
                lightOnDark += Linear(Pixel(frame, x, y).Green);
                darkOnLight += 1.0 - Linear(Pixel(frame, metrics.Width + x, y).Green);
            }
        }

        Assert.True(lightOnDark > 1.0, $"no ink was drawn: the light-on-dark cell measured {lightOnDark:F2}");

        double difference = Math.Abs(lightOnDark - darkOnLight) / Math.Max(lightOnDark, darkOnLight);

        Assert.True(difference < 0.05,
            $"the same character weighs {lightOnDark:F2} light-on-dark and {darkOnLight:F2} " +
            $"dark-on-light, a difference of {difference:P0} - the blend is not happening in linear light");
    }

    [Fact]
    public void AGlyphLandsInsideItsOwnCellAndNotTheOneBeside()
    {
        using Harness harness = new();

        GlyphPlacement glyph = harness.Atlas.Cache('W');
        CellInstance[] cells =
        [
            CellInstance.For(GlyphPlacement.Empty, Rgb.White, Rgb.Black),
            CellInstance.For(glyph, Rgb.White, Rgb.Black),
        ];

        harness.Renderer.Draw(harness.Surface, cells, 2);

        byte[] frame = harness.ReadBack();
        CellMetrics metrics = harness.Metrics;

        int leftInk = 0;
        int rightInk = 0;

        for (int y = 0; y < metrics.Height; y++)
        {
            for (int x = 0; x < metrics.Width; x++)
            {
                leftInk += Pixel(frame, x, y).Green > 0 ? 1 : 0;
                rightInk += Pixel(frame, metrics.Width + x, y).Green > 0 ? 1 : 0;
            }
        }

        Assert.Equal(0, leftInk);
        Assert.True(rightInk > 0, "the cell that was given a glyph drew nothing");
    }

    [Fact]
    public void MoreCellsThanTheBufferHoldsGrowsItRatherThanRefusing()
    {
        using Harness harness = new();

        int capacity = harness.Renderer.Capacity;
        CellInstance[] cells = new CellInstance[capacity + 1];
        Array.Fill(cells, CellInstance.For(GlyphPlacement.Empty, Rgb.White, Rgb.Black));

        harness.Renderer.Draw(harness.Surface, cells, 80);

        Assert.True(harness.Renderer.Capacity > capacity);
        Assert.Equal(1, harness.Renderer.Draws);
    }

    [Fact]
    public void DeviceLossRebuildsTheShadersAndTheNextFrameDraws()
    {
        using Harness harness = new();

        CellInstance[] cells = [CellInstance.For(GlyphPlacement.Empty, Rgb.White, new Rgb(10, 20, 30))];

        harness.Renderer.Draw(harness.Surface, cells, 1);
        harness.Device.Recover();

        Assert.Equal(1, harness.Device.Recoveries);

        harness.Renderer.Draw(harness.Surface, cells, 1);

        Assert.Equal(new Rgb(10, 20, 30), Pixel(harness.ReadBack(), 1, 1));
    }

    [Fact]
    public void AnAtlasWithMorePagesThanTheShaderReachesIsRefused()
    {
        using GraphicsDevice device = GraphicsDevice.Open();
        using GlyphAtlas atlas = GlyphAtlas.For(device, maximumPages: CellRenderer.AtlasSlots + 1);

        ArgumentException refused = Assert.Throws<ArgumentException>(
            () => CellRenderer.For(device, atlas, new CellMetrics(8, 16, 12)));

        Assert.Contains("this shader reaches", refused.Message, StringComparison.Ordinal);
    }

    private static Rgb Pixel(byte[] frame, int x, int y)
    {
        // The back buffer is BGRA, so blue is the first byte and alpha is the one nobody reads.
        int offset = ((y * (int)Width) + x) * 4;
        return new Rgb(frame[offset + 2], frame[offset + 1], frame[offset]);
    }

    private static double Linear(byte encoded)
    {
        double value = encoded / 255.0;
        return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
    }

    /// <summary>A window, a device, an atlas and a renderer, plus the readback they are judged by.</summary>
    private sealed class Harness : IDisposable
    {
        private readonly TestWindow _window = new((int)Width, (int)Height);

        public Harness()
        {
            Device = GraphicsDevice.Open(outputWindow: _window.Handle);
            Surface = PresentSurface.For(Device, _window.Handle, Width, Height);
            Rasteriser = new GlyphRasteriser();
            Atlas = GlyphAtlas.For(Device, FontSettings.Default, rasteriser: Rasteriser);
            Metrics = Rasteriser.Measure(FontSettings.Default);
            Renderer = CellRenderer.For(Device, Atlas, Metrics);
        }

        public GraphicsDevice Device { get; }

        public PresentSurface Surface { get; }

        public GlyphRasteriser Rasteriser { get; }

        public GlyphAtlas Atlas { get; }

        public CellRenderer Renderer { get; }

        public CellMetrics Metrics { get; }

        /// <summary>
        /// The back buffer as BGRA bytes, tightly packed. Read without presenting, so an occluded
        /// or hidden window is not a reason for the picture to be missing.
        /// </summary>
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
