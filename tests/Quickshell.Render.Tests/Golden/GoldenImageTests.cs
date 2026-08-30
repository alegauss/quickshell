using System.IO;
using System.Runtime.InteropServices;
using Quickshell.Render;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Xunit;

namespace Quickshell.Render.Tests.Golden;

/// <summary>
/// Rendering is judged by looking, so this looks. Each scene is rendered offscreen, read back and
/// compared against a committed reference image.
///
/// <para><b>The tolerance is a property of the scene, and it is measured.</b> A scene with text in
/// it is comparing two things at once: this renderer's arithmetic, which is deterministic, and
/// DirectWrite's glyph coverage, which is not ours and differs between machines. So text scenes
/// allow what that difference was measured at and no more, and <c>no-glyphs</c> — which contains
/// nothing DirectWrite drew — is held to a single level. See <see cref="GoldenScenes.TextTolerance"/>
/// for the arithmetic that separated the two.</para>
///
/// <para>Both bounds are always checked: how far a pixel may drift, and how many may drift at all. A
/// shape in the wrong place fails on either — it moves hundreds of levels and thousands of pixels,
/// which is what keeps a loosened level bound from loosening the test.</para>
///
/// <para><b>A reference is never regenerated to make a test pass.</b> Nothing here writes one: the
/// only door is <c>QUICKSHELL_GOLDEN=write</c> in the environment, which is a deliberate act
/// somebody has to argue for in the commit that changes the picture. A missing reference is a
/// failure, not an invitation.</para>
///
/// <para><b>One machine is not the matrix.</b> Every scene runs on this machine's own adapter and
/// again on WARP, which is a completely separate rasteriser rather than a second driver for the
/// same silicon — so a scene that agrees on both has survived two implementations. That is two of
/// the five environments the design names; the other three are not reachable from here and are
/// filed rather than implied.</para>
/// </summary>
public sealed class GoldenImageTests
{
    /// <summary>
    /// What share of the pixels may differ at all. The worst scene measured 0.27 per cent, so half
    /// a per cent is the same reasoning: a shape in the wrong place moves far more of the picture
    /// than this, which is what keeps the bound from hiding one.
    /// </summary>
    private const double DriftTolerance = 0.005;

    public static TheoryData<string, bool> Scenes
    {
        get
        {
            TheoryData<string, bool> data = [];

            foreach (GoldenScenes.Scene scene in GoldenScenes.All)
            {
                data.Add(scene.Name, false);   // this machine's own adapter
                data.Add(scene.Name, true);    // WARP, a separate rasteriser entirely
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Scenes))]
    public void TheSceneMatchesItsReference(string name, bool warp)
    {
        GoldenScenes.Scene scene = GoldenScenes.All.Single(candidate => candidate.Name == name);
        byte[] actual = Render(scene, warp);

        if (Environment.GetEnvironmentVariable("QUICKSHELL_GOLDEN") == "write")
        {
            // The one door, and it is never taken by a test run that was only meant to check.
            if (!warp)
            {
                Directory.CreateDirectory(GoldenDirectory());
                File.WriteAllBytes(ReferencePath(name),
                                   Png.Encode(actual, (int)GoldenScenes.Width, (int)GoldenScenes.Height));
            }

            return;
        }

        Assert.True(File.Exists(ReferencePath(name)),
            $"no reference image for '{name}'. Look at the scene, decide it is right, and then run " +
            "the suite once with QUICKSHELL_GOLDEN=write. A missing reference is not a pass.");

        byte[] reference = Png.Decode(File.ReadAllBytes(ReferencePath(name)), out int width, out int height);

        Assert.Equal((int)GoldenScenes.Width, width);
        Assert.Equal((int)GoldenScenes.Height, height);

        Comparison difference = Compare(reference, actual, width, height);
        double mean = difference.Mean(width, height);

        if (difference.Worst <= scene.Ceiling
            && mean <= scene.MeanTolerance
            && difference.Drifted <= DriftTolerance * width * height)
        {
            return;
        }

        string written = WriteFailureImages(name, warp, reference, actual, width, height);

        Assert.Fail(
            $"'{name}' on {(warp ? "WARP" : "this machine's adapter")} differs from its reference: " +
            $"{difference.Drifted} of {width * height} pixels drifted, averaging {mean:F4} levels " +
            $"across the whole picture and worst at {difference.Worst} levels " +
            $"({difference.WorstX},{difference.WorstY}). This scene allows a mean of " +
            $"{scene.MeanTolerance} and a ceiling of {scene.Ceiling}. " +
            $"The reference, what was drawn and the difference are in {written}.");
    }

    /// <summary>
    /// The suite is worth nothing if a reference can be regenerated by accident, so the door is
    /// checked as well as used: a run that was not told to write must not write.
    /// </summary>
    [Fact]
    public void NothingWritesAReferenceUnlessItWasAskedTo()
    {
        Assert.SkipWhen(Environment.GetEnvironmentVariable("QUICKSHELL_GOLDEN") == "write",
            "this run was deliberately told to write references, which is the case this guards against");

        string[] before = Directory.Exists(GoldenDirectory())
            ? Directory.GetFiles(GoldenDirectory(), "*.png").Select(File.GetLastWriteTimeUtc)
                       .Select(time => time.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture))
                       .ToArray()
            : [];

        GoldenScenes.Scene scene = GoldenScenes.All[0];
        Render(scene, warp: false);

        string[] after = Directory.Exists(GoldenDirectory())
            ? Directory.GetFiles(GoldenDirectory(), "*.png").Select(File.GetLastWriteTimeUtc)
                       .Select(time => time.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture))
                       .ToArray()
            : [];

        Assert.Equal(before, after);
    }

    [Fact]
    public void EverySceneHasAReferenceCommitted()
    {
        string[] missing = GoldenScenes.All.Select(scene => scene.Name)
                                           .Where(name => !File.Exists(ReferencePath(name)))
                                           .ToArray();

        Assert.True(missing.Length == 0,
            $"these scenes have no committed reference: {string.Join(", ", missing)}");
    }

    /// <summary>A reference that decodes to something other than what was encoded is not a reference.</summary>
    [Fact]
    public void ThePngCodecRoundTripsWhatItWasGiven()
    {
        byte[] pixels = new byte[64 * 48 * 4];

        for (int index = 0; index < 64 * 48; index++)
        {
            pixels[index * 4] = (byte)(index % 251);
            pixels[(index * 4) + 1] = (byte)(index % 199);
            pixels[(index * 4) + 2] = (byte)(index % 173);
            pixels[(index * 4) + 3] = 255;
        }

        byte[] decoded = Png.Decode(Png.Encode(pixels, 64, 48), out int width, out int height);

        Assert.Equal(64, width);
        Assert.Equal(48, height);
        Assert.Equal(pixels, decoded);
    }

    /// <summary>
    /// QS109's own claim, run rather than argued: the criterion admits a different rasteriser and
    /// still refuses a regression.
    ///
    /// <para><b>A loosened bound that only ever passes is not evidence of anything</b>, so this
    /// takes a committed reference and damages it twice — once the way the CI runner differs, and
    /// once the way a renderer that broke would differ — and asserts which one gets through.</para>
    ///
    /// <para>The rasteriser case is not invented: 228 pixels at 11 levels is exactly what the runner
    /// reported on every red build, against a scene of 123,200 pixels.</para>
    /// </summary>
    [Fact]
    public void TheCriterionAdmitsADifferentRasteriserAndRefusesARegression()
    {
        byte[] reference = Png.Decode(File.ReadAllBytes(ReferencePath("text-small")),
                                      out int width, out int height);

        Comparison rasteriser = Compare(reference, Scattered(reference, width, height, 228, 11),
                                        width, height);

        Assert.True(Admits(rasteriser, width, height),
                    $"the criterion refuses what the CI runner actually produced: "
                    + $"{rasteriser.Drifted} pixels, mean {rasteriser.Mean(width, height):F4}, "
                    + $"worst {rasteriser.Worst}");

        // A regression: everything a row lower. It is the shape of the failure QS96 measured at 204
        // levels when an underline moved by one pixel, and it moves the picture rather than a corner
        // of it — which is precisely what the mean is there to see.
        Comparison moved = Compare(reference, ShiftedDown(reference, width, height), width, height);

        Assert.False(Admits(moved, width, height),
                     $"the criterion admits a picture shifted a whole row: {moved.Drifted} pixels, "
                     + $"mean {moved.Mean(width, height):F4}, worst {moved.Worst}");

        // And the two are not close: the point is a wide gap, not a lucky threshold.
        Assert.True(moved.Mean(width, height) > rasteriser.Mean(width, height) * 50,
                    $"a regression averaged {moved.Mean(width, height):F4} levels against a "
                    + $"rasteriser's {rasteriser.Mean(width, height):F4}, which is too close to tell "
                    + "apart by a threshold");
    }

    /// <summary>Whether a difference is one a text scene lets through.</summary>
    private static bool Admits(Comparison difference, int width, int height) =>
        difference.Worst <= GoldenScenes.TextCeiling
        && difference.Mean(width, height) <= GoldenScenes.TextMean
        && difference.Drifted <= DriftTolerance * width * height;

    /// <summary>A copy with this many pixels moved by this many levels, spread across the picture.</summary>
    private static byte[] Scattered(byte[] source, int width, int height, int pixels, int levels)
    {
        byte[] damaged = (byte[])source.Clone();
        int step = width * height / pixels;

        for (int pixel = 0; pixel < pixels; pixel++)
        {
            int offset = pixel * step * 4;

            for (int channel = 0; channel < 3; channel++)
            {
                damaged[offset + channel] = (byte)Math.Clamp(source[offset + channel] + levels, 0, 255);
            }
        }

        return damaged;
    }

    /// <summary>A copy with every row one lower, which is what a shape in the wrong place looks like.</summary>
    private static byte[] ShiftedDown(byte[] source, int width, int height)
    {
        byte[] moved = new byte[source.Length];

        Array.Copy(source, 0, moved, width * 4, (height - 1) * width * 4);
        Array.Copy(source, 0, moved, 0, width * 4);

        return moved;
    }

    /// <summary>
    /// What two pictures differ by, in the three ways that mean different things.
    /// </summary>
    /// <param name="Drifted">How many pixels moved at all.</param>
    /// <param name="Worst">The largest single-channel difference anywhere.</param>
    /// <param name="WorstX">Where that was.</param>
    /// <param name="WorstY">Where that was.</param>
    /// <param name="Total">Every pixel's difference added up, which is what <see cref="Mean"/> divides.</param>
    private readonly record struct Comparison(int Drifted, int Worst, int WorstX, int WorstY, long Total)
    {
        /// <summary>
        /// The average difference across the whole picture, in levels.
        ///
        /// <para><b>This is the statistic that separates a rasteriser from a regression, and the
        /// maximum is not.</b> A machine whose DirectWrite antialiases differently moves a scattering
        /// of edge pixels by a few levels each: the maximum jumps and the mean barely moves. A shape
        /// drawn in the wrong place moves thousands of pixels by hundreds of levels: the mean moves
        /// enormously. Judging by the maximum is judging by the single noisiest pixel on the
        /// machine, which is why it does not survive a change of machine.</para>
        /// </summary>
        public double Mean(int width, int height) => (double)Total / (width * height);
    }

    private static Comparison Compare(byte[] reference, byte[] actual, int width, int height)
    {
        int drifted = 0;
        int worst = 0;
        int worstX = 0;
        int worstY = 0;
        long total = 0;

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int offset = ((y * width) + x) * 4;
                int channel = Math.Max(
                    Math.Abs(reference[offset] - actual[offset]),
                    Math.Max(Math.Abs(reference[offset + 1] - actual[offset + 1]),
                             Math.Abs(reference[offset + 2] - actual[offset + 2])));

                if (channel == 0)
                {
                    continue;
                }

                drifted++;
                total += channel;

                if (channel > worst)
                {
                    worst = channel;
                    worstX = x;
                    worstY = y;
                }
            }
        }

        return new Comparison(drifted, worst, worstX, worstY, total);
    }

    /// <summary>
    /// Writes the reference, what was drawn and a difference image beside each other. The difference
    /// is amplified: a four-level drift is invisible at its own scale and obvious at eight times it.
    /// </summary>
    private static string WriteFailureImages(string name, bool warp, byte[] reference, byte[] actual,
                                             int width, int height)
    {
        string directory = Path.Combine(RepositoryRoot(), "TestResults", "golden");
        Directory.CreateDirectory(directory);

        string adapter = warp ? "warp" : "adapter";
        byte[] difference = new byte[width * height * 4];

        for (int index = 0; index < width * height; index++)
        {
            for (int channel = 0; channel < 3; channel++)
            {
                int offset = (index * 4) + channel;
                difference[offset] = (byte)Math.Min(255, Math.Abs(reference[offset] - actual[offset]) * 8);
            }

            difference[(index * 4) + 3] = 255;
        }

        File.WriteAllBytes(Path.Combine(directory, $"{name}.{adapter}.reference.png"),
                           Png.Encode(reference, width, height));
        File.WriteAllBytes(Path.Combine(directory, $"{name}.{adapter}.actual.png"),
                           Png.Encode(actual, width, height));
        File.WriteAllBytes(Path.Combine(directory, $"{name}.{adapter}.difference.png"),
                           Png.Encode(difference, width, height));

        return directory;
    }

    private static byte[] Render(GoldenScenes.Scene scene, bool warp)
    {
        using TestWindow window = new((int)GoldenScenes.Width, (int)GoldenScenes.Height);
        using GraphicsDevice device = warp
            ? GraphicsDevice.Open(new WarpOnlyProbe())
            : GraphicsDevice.Open(outputWindow: window.Handle);
        using PresentSurface surface = PresentSurface.For(device, window.Handle,
                                                          GoldenScenes.Width, GoldenScenes.Height);
        using GlyphRasteriser rasteriser = new();
        using GlyphAtlas atlas = GlyphAtlas.For(device, scene.Font, rasteriser: rasteriser);

        CellMetrics metrics = rasteriser.Measure(scene.Font);
        using CellRenderer renderer = CellRenderer.For(device, atlas, metrics);

        // The blink phase is a clock, and a clock in a reference image is a test that fails at
        // random. Pinned to zero, which is the phase a cursor is showing in.
        renderer.Elapsed = TimeSpan.Zero;

        (int columns, int rows) = metrics.GridFor(GoldenScenes.Width, GoldenScenes.Height);
        CellInstance[] cells = new CellInstance[columns * rows];

        scene.Paint(new GoldenScenes.Painter(cells, atlas, metrics, columns, rows));
        renderer.Draw(surface, cells, columns);

        return ReadBack(device, surface);
    }

    private static byte[] ReadBack(GraphicsDevice device, PresentSurface surface)
    {
        using ID3D11Resource resource = surface.View.Resource;
        using ID3D11Texture2D back = resource.QueryInterface<ID3D11Texture2D>();
        using ID3D11Texture2D staging = device.Device.CreateTexture2D(new Texture2DDescription
        {
            Width = GoldenScenes.Width,
            Height = GoldenScenes.Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read,
        });

        device.Context.CopyResource(staging, back);

        MappedSubresource mapped = device.Context.Map(staging, 0, MapMode.Read);
        byte[] frame = new byte[GoldenScenes.Width * GoldenScenes.Height * 4];

        try
        {
            for (int row = 0; row < GoldenScenes.Height; row++)
            {
                Marshal.Copy(mapped.DataPointer + (row * (int)mapped.RowPitch), frame,
                             row * (int)GoldenScenes.Width * 4, (int)GoldenScenes.Width * 4);
            }
        }
        finally
        {
            device.Context.Unmap(staging, 0);
        }

        return frame;
    }

    /// <summary>Makes the adapter chain end at WARP, which is how the second environment is reached.</summary>
    private sealed class WarpOnlyProbe : IAdapterProbe
    {
        public AdapterInfo? ForOutputWindow(nint outputWindow) => null;

        public AdapterInfo? DefaultHardware() => null;

        public AdapterInfo Warp() => new("WARP software rasteriser", 0);
    }

    private static string ReferencePath(string name) => Path.Combine(GoldenDirectory(), name + ".png");

    /// <summary>
    /// Where the committed references live. Deliberately not <c>golden</c>: this tree already has a
    /// <c>Golden</c> source folder, Windows does not tell the two apart, and the first write put
    /// seven PNGs in among the C# files.
    /// </summary>
    private static string GoldenDirectory() =>
        Path.Combine(RepositoryRoot(), "tests", "Quickshell.Render.Tests", "references");

    private static string RepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Quickshell.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory!.FullName;
    }
}
