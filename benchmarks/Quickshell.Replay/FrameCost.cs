using System.Diagnostics;
using Quickshell.Render;
using Quickshell.Terminal;
using Vortice.Direct3D11;

namespace Quickshell.Replay;

/// <summary>
/// Figure 3 of the budget: one full grid, drawn again and again, timed on the CPU and the GPU
/// separately (QS196).
///
/// <para><b>Not a throughput.</b> The stream arms report megabytes a second with parsing folded in.
/// Figure 3 asks what one steady-state frame costs: a 200 by 50 grid, already filled, redrawn with
/// nothing new arriving. So the grid is filled once from a real stream through the emulator, and
/// then the same frame is drawn a few thousand times.</para>
///
/// <para><b>Two clocks, read separately.</b> The CPU's is a stopwatch around each draw call: building
/// and uploading instances and recording the draw. The GPU's is a pair of timestamp queries around the
/// same work, read once the frame has finished, inside a disjoint query that says whether the
/// timestamps can be trusted at all. It never presents, as the render arm never does: a vsync-locked
/// present measures the display.</para>
///
/// <para><b>The median and the 99th percentile</b>, because a frame budget is broken by the slow
/// frame and not the average one.</para>
/// </summary>
public sealed class FrameCost : IDisposable
{
    /// <summary>The grid figure 3 is stated for.</summary>
    public const int Columns = 200;

    /// <summary>Its rows.</summary>
    public const int Rows = 50;

    private readonly ReplayWindow _window;
    private readonly GraphicsDevice _device;
    private readonly PresentSurface _surface;
    private readonly GlyphRasteriser _rasteriser;
    private readonly GlyphAtlas _atlas;
    private readonly CellRenderer _renderer;

    /// <summary>A device, an atlas and a target sized for exactly the grid the figure names.</summary>
    public FrameCost()
    {
        FontSettings font = new("Consolas", 11f, 96f);

        _rasteriser = new GlyphRasteriser();

        CellMetrics metrics = _rasteriser.Measure(font);
        uint width = (uint)Math.Ceiling((double)metrics.Width * Columns);
        uint height = (uint)Math.Ceiling((double)metrics.Height * Rows);

        _window = new ReplayWindow((int)width, (int)height);
        _device = GraphicsDevice.Open(outputWindow: _window.Handle);
        _surface = PresentSurface.For(_device, _window.Handle, width, height);
        _atlas = GlyphAtlas.For(_device, font, rasteriser: _rasteriser);
        _renderer = CellRenderer.For(_device, _atlas, metrics);
    }

    /// <summary>What the device opened on, for the report: a figure from WARP is not a GPU's.</summary>
    public string Adapter => _device.Adapter.ToString();

    /// <summary>Fills the grid from a stream, then draws it <paramref name="frames"/> times.</summary>
    /// <returns>Each frame's CPU and GPU time in milliseconds; the GPU's is empty where the timestamps were disjoint.</returns>
    public (double[] Cpu, double[] Gpu) Measure(ReadOnlySpan<byte> stream, int frames)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(frames, 1);

        CellInstance[] cells = Filled(stream);
        ID3D11Device device = _device.Device;
        ID3D11DeviceContext context = _device.Context;

        using ID3D11Query disjoint = device.CreateQuery(new QueryDescription(QueryType.TimestampDisjoint));
        using ID3D11Query begin = device.CreateQuery(new QueryDescription(QueryType.Timestamp));
        using ID3D11Query end = device.CreateQuery(new QueryDescription(QueryType.Timestamp));

        // A few frames first: the first draw creates the instance buffer and the atlas texture's
        // contents, and figure 3 is explicitly not the first frame.
        for (int warm = 0; warm < 16; warm++)
        {
            _renderer.Draw(_surface, cells, Columns);
        }

        Settle(context);

        double[] cpu = new double[frames];
        List<double> gpu = new(frames);

        for (int frame = 0; frame < frames; frame++)
        {
            context.Begin(disjoint);
            context.End(begin);

            long started = Stopwatch.GetTimestamp();

            _renderer.Draw(_surface, cells, Columns);

            cpu[frame] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;

            context.End(end);
            context.End(disjoint);

            // Waited for here, outside both clocks: read later, a frame's timestamps would sit in a
            // queue other frames are also in, and the GPU's figure would include its neighbours.
            QueryDataTimestampDisjoint when = Read<QueryDataTimestampDisjoint>(context, disjoint);
            ulong from = Read<ulong>(context, begin);
            ulong to = Read<ulong>(context, end);

            if (!when.Disjoint && when.Frequency > 0 && to >= from)
            {
                gpu.Add((to - from) * 1000.0 / when.Frequency);
            }
        }

        return (cpu, [.. gpu]);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _renderer.Dispose();
        _atlas.Dispose();
        _rasteriser.Dispose();
        _surface.Dispose();
        _device.Dispose();
        _window.Dispose();
    }

    /// <summary>The value at a fraction of the way through the sorted samples, nearest rank.</summary>
    public static double Percentile(double[] samples, double fraction)
    {
        ArgumentNullException.ThrowIfNull(samples);

        if (samples.Length == 0)
        {
            return double.NaN;
        }

        double[] sorted = [.. samples.Order()];
        int rank = (int)Math.Ceiling(fraction * sorted.Length) - 1;

        return sorted[Math.Clamp(rank, 0, sorted.Length - 1)];
    }

    /// <summary>
    /// The grid a real stream leaves behind, through the real emulator, as the instances one frame
    /// draws — every cell filled, where the stream filled it.
    /// </summary>
    private CellInstance[] Filled(ReadOnlySpan<byte> stream)
    {
        Emulator emulator = new(Columns, Rows);

        emulator.Feed(stream);

        CellInstance[] cells = new CellInstance[Columns * Rows];
        Rgb ground = new(16, 18, 24);
        Span<char> text = stackalloc char[8];

        for (int row = 0; row < Rows; row++)
        {
            ReadOnlySpan<Cell> line = emulator.Buffer.Screen(row);

            for (int column = 0; column < Columns; column++)
            {
                int length = column < line.Length ? emulator.Buffer.TextOf(line[column], text) : 0;
                int codepoint = length switch
                {
                    0 => ' ',
                    >= 2 when char.IsSurrogatePair(text[0], text[1]) => char.ConvertToUtf32(text[0], text[1]),
                    _ => text[0],
                };

                GlyphPlacement glyph = codepoint == ' ' ? GlyphPlacement.Empty : _atlas.Cache(codepoint);

                cells[(row * Columns) + column] = CellInstance.For(glyph, new Rgb(214, 219, 228), ground);
            }
        }

        return cells;
    }

    /// <summary>Waits until the GPU has done everything submitted so far.</summary>
    private void Settle(ID3D11DeviceContext context)
    {
        using ID3D11Query done = _device.Device.CreateQuery(new QueryDescription(QueryType.Event));

        context.End(done);
        Read<int>(context, done);
    }

    /// <summary>Spins until a query has its answer, which a flush makes arrive.</summary>
    private static T Read<T>(ID3D11DeviceContext context, ID3D11Query query)
        where T : unmanaged
    {
        T value;

        while (!context.GetData(query, out value))
        {
            Thread.SpinWait(64);
        }

        return value;
    }
}
