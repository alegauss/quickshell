using System.Runtime.InteropServices;
using Quickshell.Render;
using Quickshell.Terminal;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Xunit;

namespace Quickshell.Render.Tests;

/// <summary>
/// The decorations, read back off the glass. Each is meant to be arithmetic in the pixel shader
/// rather than geometry, so what these check is that one draw call still produced all of them.
/// </summary>
public sealed class DecorationTests
{
    private const uint Width = 320;
    private const uint Height = 120;

    // ---- The blink clock, which is the one thing that may wake an idle window ----

    [Fact]
    public void TheCursorIsOnForHalfTheIntervalAndOffForTheOther()
    {
        CursorBlink blink = new() { Interval = TimeSpan.FromMilliseconds(500) };

        Assert.True(blink.IsShowingAt(TimeSpan.Zero));
        Assert.True(blink.IsShowingAt(TimeSpan.FromMilliseconds(499)));
        Assert.False(blink.IsShowingAt(TimeSpan.FromMilliseconds(500)));
        Assert.False(blink.IsShowingAt(TimeSpan.FromMilliseconds(999)));
        Assert.True(blink.IsShowingAt(TimeSpan.FromMilliseconds(1000)));
    }

    /// <summary>
    /// The claim the block's "an idle window issues no draw calls" criterion rests on: with
    /// blinking off there is nothing left to wake for, and this has to answer null rather than a
    /// long interval.
    /// </summary>
    [Fact]
    public void BlinkingOffLeavesNothingToWakeFor()
    {
        CursorBlink blink = new() { Interval = TimeSpan.FromMilliseconds(500) };

        Assert.NotNull(blink.NextChangeAfter(TimeSpan.FromMilliseconds(120)));
        Assert.Equal(TimeSpan.FromMilliseconds(380), blink.NextChangeAfter(TimeSpan.FromMilliseconds(120)));

        blink.Enabled = false;

        Assert.Null(blink.NextChangeAfter(TimeSpan.FromMilliseconds(120)));
        Assert.True(blink.IsShowingAt(TimeSpan.FromMilliseconds(120)),
                    "a cursor that does not blink still has to be visible");
    }

    [Fact]
    public void ARendererWithBlinkingOffSchedulesNoWake()
    {
        using Harness harness = new();

        Assert.NotNull(harness.Renderer.NextCursorWake());

        harness.Renderer.Blink.Enabled = false;

        Assert.Null(harness.Renderer.NextCursorWake());
        Assert.True(harness.Renderer.CursorShowing);
    }

    // ---- The rules, placed by the font rather than by a fraction of the cell ----

    [Fact]
    public void TheRulesComeFromTheFontAndNotFromAGuess()
    {
        using GlyphRasteriser rasteriser = new();
        CellMetrics measured = rasteriser.Measure(new FontSettings("Consolas", 20f, 96f));

        Assert.True(measured.UnderlineY > measured.Baseline,
                    "the underline is at or above the baseline, so it will cut through the text");
        Assert.True(measured.UnderlineY < measured.Height,
                    "the underline is below the bottom of the cell");
        Assert.True(measured.StrikeY < measured.Baseline,
                    "the strikethrough is below the baseline, which is not through the text");
        Assert.True(measured.UnderlineThickness >= 1);
        Assert.True(measured.StrikeThickness >= 1);
    }

    /// <summary>Metrics built by hand have no face to ask, so they fall back rather than read zero.</summary>
    [Fact]
    public void MetricsWithNoFontBehindThemStillPlaceTheirRules()
    {
        CellMetrics byHand = new(10, 24, 18);

        Assert.True(byHand.UnderlineY > byHand.Baseline);
        Assert.True(byHand.UnderlineY < byHand.Height);
        Assert.True(byHand.UnderlineThickness >= 1);
        Assert.True(byHand.StrikeY is > 0 and < 18);
    }

    [Fact]
    public void AnUnderlineDrawsBelowTheBaselineAndNowhereElse()
    {
        using Harness harness = new();
        CellMetrics metrics = harness.Metrics;

        CellInstance[] cells =
        [
            CellInstance.For(GlyphPlacement.Empty, Rgb.White, Rgb.Black),
            CellInstance.For(GlyphPlacement.Empty, Rgb.White, Rgb.Black, underline: UnderlineStyle.Single),
        ];

        harness.Renderer.Draw(harness.Surface, cells, 2);
        byte[] frame = harness.ReadBack();

        Assert.Equal(0, Ink(frame, metrics, 0));
        Assert.True(Ink(frame, metrics, 1) > 0, "an underlined cell drew no rule");

        // Every inked row is at or below the font's underline position, give or take the pixel of
        // softness the rule is antialiased with.
        for (int y = 0; y < metrics.Height; y++)
        {
            if (RowInk(frame, metrics, 1, y) > 0)
            {
                Assert.True(y >= metrics.UnderlineY - metrics.UnderlineThickness - 1,
                            $"the underline reached row {y}, above the font's own {metrics.UnderlineY}");
            }
        }
    }

    [Fact]
    public void DoubleUnderlineDrawsTwoRulesAndSingleDrawsOne()
    {
        using Harness harness = new();
        CellMetrics metrics = harness.Metrics;

        CellInstance[] cells =
        [
            CellInstance.For(GlyphPlacement.Empty, Rgb.White, Rgb.Black, underline: UnderlineStyle.Single),
            CellInstance.For(GlyphPlacement.Empty, Rgb.White, Rgb.Black, underline: UnderlineStyle.Double),
        ];

        harness.Renderer.Draw(harness.Surface, cells, 2);
        byte[] frame = harness.ReadBack();

        Assert.Equal(1, InkedBands(frame, metrics, 0));
        Assert.Equal(2, InkedBands(frame, metrics, 1));
    }

    [Fact]
    public void OverlineAndStrikeDrawAboveAndThroughTheText()
    {
        using Harness harness = new();
        CellMetrics metrics = harness.Metrics;

        CellInstance[] cells =
        [
            CellInstance.For(GlyphPlacement.Empty, Rgb.White, Rgb.Black, CellFlags.Overline),
            CellInstance.For(GlyphPlacement.Empty, Rgb.White, Rgb.Black, CellFlags.Strike),
        ];

        harness.Renderer.Draw(harness.Surface, cells, 2);
        byte[] frame = harness.ReadBack();

        Assert.True(Ink(frame, metrics, 0) > 0, "an overlined cell drew nothing");
        Assert.True(Ink(frame, metrics, 1) > 0, "a struck cell drew nothing");

        Assert.True(TopmostInkedRow(frame, metrics, 0) < metrics.Baseline / 2,
                    "the overline is not above the text");
        Assert.InRange(TopmostInkedRow(frame, metrics, 1), 1, metrics.Baseline);
    }

    /// <summary>
    /// The falsification this design names. The curl's phase is a function of the pixel's position
    /// in the window, so a wave running under a word crosses every cell boundary without a step. If
    /// it were a function of the position within the cell, each cell would restart at the same
    /// phase and every boundary would show a discontinuity.
    /// </summary>
    [Fact]
    public void AnUndercurlJoinsAcrossTheCellBoundary()
    {
        using Harness harness = new();
        CellMetrics metrics = harness.Metrics;

        CellInstance[] cells = new CellInstance[4];
        Array.Fill(cells, CellInstance.For(GlyphPlacement.Empty, Rgb.White, Rgb.Black,
                                           underline: UnderlineStyle.Curly));

        harness.Renderer.Draw(harness.Surface, cells, 4);
        byte[] frame = harness.ReadBack();

        // The curl's height at each column of pixels, across the two cells either side of a
        // boundary. Measured in tenths of a pixel, because the rule is antialiased and its centre
        // of mass moves smoothly rather than in whole pixels.
        int boundary = metrics.Width;
        int first = boundary - metrics.Width;
        int last = boundary + metrics.Width;

        int?[] centres = new int?[last - first];

        for (int x = first; x < last; x++)
        {
            centres[x - first] = InkCentre(frame, metrics, x);
        }

        // The step the cell boundary itself introduces, against the largest step anywhere else.
        // Comparing the two is what makes this independent of how deep the wave happens to be: a
        // curl whose phase restarts at the boundary jumps further there than it ever does inside a
        // cell, and one that carries its phase across does not.
        int atBoundary = Step(centres, boundary - first - 1);
        int elsewhere = 0;
        int moved = 0;

        for (int index = 1; index < centres.Length; index++)
        {
            int step = Step(centres, index - 1);
            moved = Math.Max(moved, step);

            if (index != boundary - first)
            {
                elsewhere = Math.Max(elsewhere, step);
            }
        }

        Assert.True(moved > 3,
            $"the curl only moves {moved / 10.0:F1} pixels across a whole period, which is a " +
            "straight line with a wobble rather than an undercurl");

        Assert.True(atBoundary <= elsewhere,
            $"the curl steps {atBoundary / 10.0:F1} pixels across the cell boundary and at most " +
            $"{elsewhere / 10.0:F1} pixels anywhere else, so its phase restarts at each cell");
    }

    // ---- The cursor ----

    [Fact]
    public void ABlockCursorInvertsTheGlyphRatherThanCoveringIt()
    {
        using Harness harness = new();
        CellMetrics metrics = harness.Metrics;

        GlyphPlacement glyph = harness.Atlas.Cache('H');
        harness.Renderer.CursorColour = new Rgb(255, 0, 0);

        CellInstance[] cells =
        [
            CellInstance.For(glyph, Rgb.White, Rgb.Black),
            CellInstance.For(glyph, Rgb.White, Rgb.Black, cursor: CursorShape.Block),
        ];

        harness.Renderer.Draw(harness.Surface, cells, 2);
        byte[] frame = harness.ReadBack();

        int red = 0;
        int black = 0;

        for (int y = 0; y < metrics.Height; y++)
        {
            for (int x = 0; x < metrics.Width; x++)
            {
                Rgb pixel = Pixel(frame, metrics.Width + x, y);
                red += pixel.Red > 128 && pixel.Green < 64 ? 1 : 0;
                black += pixel is { Red: < 32, Green: < 32, Blue: < 32 } ? 1 : 0;
            }
        }

        Assert.True(red > 0, "the block cursor did not fill its cell with the cursor colour");
        Assert.True(black > 0,
            "nothing in the cursor cell took the cell's background, so the glyph was covered " +
            "rather than inverted and the character under the cursor is invisible");
    }

    [Fact]
    public void ABarCursorDrawsAtTheLeftEdgeOnly()
    {
        using Harness harness = new();
        CellMetrics metrics = harness.Metrics;

        harness.Renderer.CursorColour = new Rgb(255, 0, 0);

        CellInstance[] cells =
        [
            CellInstance.For(GlyphPlacement.Empty, Rgb.White, Rgb.Black, cursor: CursorShape.Bar),
        ];

        harness.Renderer.Draw(harness.Surface, cells, 1);
        byte[] frame = harness.ReadBack();

        Assert.True(Pixel(frame, 0, metrics.Height / 2).Red > 128, "the bar cursor drew nothing");
        Assert.True(Pixel(frame, metrics.Width - 1, metrics.Height / 2).Red < 64,
                    "the bar cursor filled the whole cell");
    }

    [Fact]
    public void ACursorInItsOffPhaseDrawsNothing()
    {
        using Harness harness = new();
        CellMetrics metrics = harness.Metrics;

        harness.Renderer.CursorColour = new Rgb(255, 0, 0);
        harness.Renderer.Elapsed = harness.Renderer.Blink.Interval;
        Assert.False(harness.Renderer.CursorShowing);

        CellInstance[] cells =
        [
            CellInstance.For(GlyphPlacement.Empty, Rgb.White, Rgb.Black, cursor: CursorShape.Block),
        ];

        harness.Renderer.Draw(harness.Surface, cells, 1);
        byte[] frame = harness.ReadBack();

        Assert.Equal(Rgb.Black, Pixel(frame, metrics.Width / 2, metrics.Height / 2));
    }

    // ---- Selection composes with the rest rather than covering it ----

    [Fact]
    public void ASelectedCellTakesTheSelectionColourAndKeepsItsDecorations()
    {
        using Harness harness = new();
        CellMetrics metrics = harness.Metrics;

        harness.Renderer.SelectionColour = new Rgb(0, 0, 200);

        CellInstance[] cells =
        [
            CellInstance.For(GlyphPlacement.Empty, Rgb.White, Rgb.Black, CellFlags.Selected),
            CellInstance.For(GlyphPlacement.Empty, Rgb.White, Rgb.Black, CellFlags.Selected,
                             underline: UnderlineStyle.Single),
        ];

        harness.Renderer.Draw(harness.Surface, cells, 2);
        byte[] frame = harness.ReadBack();

        Assert.Equal(new Rgb(0, 0, 200), Pixel(frame, metrics.Width / 2, 1));
        Assert.Equal(new Rgb(0, 0, 200), Pixel(frame, metrics.Width + (metrics.Width / 2), 1));

        // The underline is still there, drawn in the foreground over the selection's ground.
        Assert.True(Pixel(frame, metrics.Width + (metrics.Width / 2), metrics.UnderlineY).Red > 128,
                    "selection covered the underline instead of composing with it");
    }

    [Fact]
    public void EveryDecorationIsStillOneDrawCall()
    {
        using Harness harness = new();

        CellInstance[] cells =
        [
            CellInstance.For(GlyphPlacement.Empty, Rgb.White, Rgb.Black, CellFlags.Overline | CellFlags.Strike,
                             underline: UnderlineStyle.Curly, cursor: CursorShape.Block),
            CellInstance.For(GlyphPlacement.Empty, Rgb.White, Rgb.Black, CellFlags.Selected,
                             underline: UnderlineStyle.Dotted),
        ];

        harness.Renderer.Draw(harness.Surface, cells, 2);

        Assert.Equal(1, harness.Renderer.Draws);
        Assert.Equal(CellInstance.Stride, Marshal.SizeOf<CellInstance>());
    }

    private static int Ink(byte[] frame, CellMetrics metrics, int column)
    {
        int ink = 0;

        for (int y = 0; y < metrics.Height; y++)
        {
            ink += RowInk(frame, metrics, column, y);
        }

        return ink;
    }

    private static int RowInk(byte[] frame, CellMetrics metrics, int column, int y)
    {
        int ink = 0;

        for (int x = 0; x < metrics.Width; x++)
        {
            ink += Pixel(frame, (column * metrics.Width) + x, y).Green > 32 ? 1 : 0;
        }

        return ink;
    }

    private static int TopmostInkedRow(byte[] frame, CellMetrics metrics, int column)
    {
        for (int y = 0; y < metrics.Height; y++)
        {
            if (RowInk(frame, metrics, column, y) > 0)
            {
                return y;
            }
        }

        return metrics.Height;
    }

    /// <summary>How many separate horizontal bands of ink a cell has, which is what tells one rule from two.</summary>
    private static int InkedBands(byte[] frame, CellMetrics metrics, int column)
    {
        int bands = 0;
        bool inside = false;

        for (int y = 0; y < metrics.Height; y++)
        {
            bool inked = RowInk(frame, metrics, column, y) > metrics.Width / 2;

            if (inked && !inside)
            {
                bands++;
            }

            inside = inked;
        }

        return bands;
    }

    /// <summary>
    /// The vertical centre of the ink in one column of pixels, in tenths of a pixel, or null where
    /// there is none. Tenths because the rule is antialiased: its centre of mass moves a fraction
    /// of a pixel per column, and rounding that to whole pixels throws away the wave.
    /// </summary>
    private static int? InkCentre(byte[] frame, CellMetrics metrics, int x)
    {
        long weighted = 0;
        long total = 0;

        for (int y = 0; y < metrics.Height; y++)
        {
            int value = Pixel(frame, x, y).Green;
            weighted += (long)value * y * 10;
            total += value;
        }

        return total > 0 ? (int)(weighted / total) : null;
    }

    /// <summary>How far the curl moved between two neighbouring columns, or zero where either is blank.</summary>
    private static int Step(int?[] centres, int index)
    {
        return centres[index] is int before && centres[index + 1] is int after
            ? Math.Abs(after - before)
            : 0;
    }

    private static Rgb Pixel(byte[] frame, int x, int y)
    {
        int offset = ((y * (int)Width) + x) * 4;
        return new Rgb(frame[offset + 2], frame[offset + 1], frame[offset]);
    }

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
