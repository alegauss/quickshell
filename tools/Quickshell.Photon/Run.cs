using System.Diagnostics;
using Quickshell.Render;
using Quickshell.Terminal;
using Vortice.DXGI;

namespace Quickshell.Photon;

/// <summary>Which swapchain a run presents through.</summary>
/// <param name="Name">What the report calls it.</param>
/// <param name="Latency">The maximum frame latency.</param>
/// <param name="Waitable">Whether the swapchain has its waitable object and the loop waits on it.</param>
/// <param name="Buffers">The swapchain's buffer count.</param>
/// <param name="WaitFirst">Whether the loop waits before reading the model rather than after.</param>
internal sealed record Arm(string Name, uint Latency, bool Waitable, uint Buffers, bool WaitFirst = false)
{
    /// <summary>
    /// The client's own path: two buffers, latency one, and the wait after the frame is built, which
    /// is where <c>TerminalView.DrawIfNeeded</c> has it.
    /// </summary>
    internal static Arm Client { get; } = new("client", 1, true, 2);

    /// <summary>
    /// The client's swapchain with the wait moved before the model is read, which is the order the
    /// waitable object is documented for: the frame is built from what arrived during the wait.
    /// </summary>
    internal static Arm Early { get; } = new("wait-first", 1, true, 2, WaitFirst: true);

    /// <summary>
    /// The client's two buffers without the flags: latency three and Present blocking. The difference
    /// from <see cref="Client"/> is the flags and nothing else.
    /// </summary>
    internal static Arm Unbought { get; } = new("unbought", 3, false, 2);

    /// <summary>
    /// The same, with a third buffer: the chain a renderer gets when it asks for one more buffer to
    /// stop Present blocking, and the one place a queue has room to form.
    /// </summary>
    internal static Arm Deep { get; } = new("unbought-3", 3, false, 3);
}

/// <summary>What the host is doing while the echoes arrive.</summary>
/// <param name="Name">What the report calls it.</param>
/// <param name="Streaming">Whether output streams every frame, or frames happen only on an echo.</param>
internal sealed record Workload(string Name, bool Streaming)
{
    /// <summary>A shell at a prompt: nothing on screen changes except what is typed.</summary>
    internal static Workload Typing { get; } = new("typing", false);

    /// <summary>Typing while a host streams output, so every frame is drawn and the queue can fill.</summary>
    internal static Workload Busy { get; } = new("busy", true);
}

/// <summary>
/// One timed run: a full grid on a real swapchain, echoes arriving at moments the loop does not
/// choose, and DXGI asked when each frame carrying one reached the glass.
///
/// <para><b>The input end is the echo's arrival, not the frame's start.</b> A keystroke echoed by a
/// local shell arrives when it arrives — while the loop is parked on the swapchain as often as not —
/// so each echo is scheduled on the clock and stamped with the instant it was due. A loop that is
/// blocked when it falls due picks it up late, and that lateness is exactly what the flags were bought
/// to remove; stamping the frame's start instead would measure it away.</para>
///
/// <para><b>The photon end is DXGI's own vblank.</b> <see cref="PresentSurface.OnGlass"/> gives the
/// latest present that was shown and the QPC instant of the vblank it was shown at, which is the same
/// clock <see cref="Stopwatch"/> reads. That is the instrumented path figure 1 allows; it stops at the
/// compositor's account of the glass, and the panel's own response time is not in it.</para>
///
/// <para><b>The loop is <c>TerminalView.DrawIfNeeded</c>'s order</b>: paint from the model, then wait
/// on the swapchain, then draw and present. An echo that lands during the wait misses the frame it
/// was waiting for, and is in the next one — which is the client's real behaviour and is measured as
/// such.</para>
/// </summary>
internal static class Run
{
    /// <summary>The figure-3 grid: two hundred columns by fifty rows.</summary>
    private const int Columns = 200;

    private const int Rows = 50;

    /// <summary>How much of the stream a busy frame consumes.</summary>
    private const int BytesPerFrame = 4 * 1024;

    /// <summary>Ignored at the start of a run, while the statistics and the queue settle.</summary>
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(1);

    /// <summary>Times one arm under one workload.</summary>
    /// <param name="arm">The swapchain.</param>
    /// <param name="workload">What the host is doing.</param>
    /// <param name="stream">Captured output, to fill the screen and to stream while busy.</param>
    /// <param name="length">How long to run.</param>
    /// <param name="seed">For the echo intervals, so two arms see the same rhythm.</param>
    /// <param name="host">Makes the window to present into, at the size asked for in pixels.</param>
    internal static Outcome Time(Arm arm, Workload workload, byte[] stream, TimeSpan length, int seed,
                                 Func<int, int, IPhotonHost> host)
    {
        using GlyphRasteriser rasteriser = new();
        CellMetrics metrics = rasteriser.Measure(FontSettings.Default);

        using IPhotonHost window = host(metrics.Width * Columns, metrics.Height * Rows);
        using GraphicsDevice device = GraphicsDevice.Open(outputWindow: window.Handle);

        // The window's own size and not the one asked for, which a host that lays out in its own
        // units may not have honoured to the pixel.
        using PresentSurface surface = PresentSurface.For(device, window.Handle, window.Width, window.Height,
                                                          arm.Latency, arm.Waitable, arm.Buffers);
        using GlyphAtlas atlas = GlyphAtlas.For(device, FontSettings.Default, rasteriser: rasteriser);
        using CellRenderer renderer = CellRenderer.For(device, atlas, metrics);

        Emulator emulator = new(Columns, Rows);
        GridPainter painter = new(atlas, new Palette());
        CellInstance[] cells = new CellInstance[Columns * Rows];

        // A screen full of what a host really sends, so every frame is a whole grid of glyphs and
        // not a clear: an empty frame is the workload that told QS7 nothing.
        int offset = 0;

        for (int fed = 0; fed < 64 * 1024; fed += BytesPerFrame)
        {
            offset = Next(emulator, stream, offset);
        }

        Random rhythm = new(seed);
        List<(long Present, long Arrived)> pending = [];
        List<double> latencies = [];
        Dictionary<string, int> modes = new(StringComparer.Ordinal);
        int unresolved = 0;
        long frames = 0;

        long started = Stopwatch.GetTimestamp();
        long settled = started + Ticks(Settle);
        long end = settled + Ticks(length);
        long due = settled + Interval(rhythm, workload);

        while (Stopwatch.GetTimestamp() < end)
        {
            // Waiting first only where there is a frame to draw: at a prompt the loop has nothing to
            // do until an echo is due, and parking it on the swapchain then would be the idle cost
            // figure 4 forbids.
            if (arm.WaitFirst && (workload.Streaming || Stopwatch.GetTimestamp() >= due))
            {
                surface.WaitForNextFrame();
            }

            long now = Stopwatch.GetTimestamp();
            bool echo = now >= due;
            long arrived = due;

            if (workload.Streaming)
            {
                offset = Next(emulator, stream, offset);
            }

            if (echo)
            {
                emulator.Feed("x"u8);
                due += Interval(rhythm, workload);

                // A loop that fell more than an interval behind would otherwise owe a burst of echoes
                // all at once, which is a backlog and not typing.
                due = Math.Max(due, now);
            }

            if (workload.Streaming || echo)
            {
                Damage damage = emulator.Damage;

                painter.Paint(emulator.Buffer, cells, damage.CursorVisible ? damage.CursorRow : -1,
                              damage.CursorColumn, CursorShape.Block, metrics);

                if (!arm.WaitFirst)
                {
                    surface.WaitForNextFrame();
                }

                renderer.Draw(surface, cells.AsSpan(0, painter.Painted), Columns);
                surface.Present();
                frames++;

                if (echo)
                {
                    pending.Add((surface.LastPresentId, arrived));
                }
            }
            else
            {
                Thread.Sleep(1);
            }

            unresolved += Resolve(surface, pending, latencies, modes);
        }

        // The last echoes are still in the queue: give them the time a frame takes to land.
        long drain = Stopwatch.GetTimestamp() + Ticks(TimeSpan.FromMilliseconds(200));

        while (pending.Count > 0 && Stopwatch.GetTimestamp() < drain)
        {
            Thread.Sleep(1);
            unresolved += Resolve(surface, pending, latencies, modes);
        }

        return new Outcome(arm, workload, latencies, unresolved + pending.Count, frames,
                           surface.Occlusions, modes);
    }

    /// <summary>
    /// Matches what DXGI says is on the glass against the echoes waiting for it.
    ///
    /// <para>Only an exact match is timed. A present DXGI has already gone past was shown at some
    /// vblank this poll did not see, and the nearest one would flatter it by up to a frame; it is
    /// counted as unresolved instead, and the report says how many.</para>
    /// </summary>
    /// <returns>How many echoes went past unseen.</returns>
    private static int Resolve(PresentSurface surface, List<(long Present, long Arrived)> pending,
                               List<double> latencies, Dictionary<string, int> modes)
    {
        if (pending.Count == 0 || !surface.OnGlass(out long shown, out long vblank))
        {
            return 0;
        }

        int missed = 0;

        for (int at = pending.Count - 1; at >= 0; at--)
        {
            (long present, long arrived) = pending[at];

            if (present > shown)
            {
                continue;
            }

            if (present == shown)
            {
                latencies.Add(Stopwatch.GetElapsedTime(arrived, vblank).TotalMilliseconds);

                // Asked of the same frame the latency was, so the two can be read side by side: a
                // composed echo and an overlaid one are two different costs, and a pooled median
                // over both would describe neither (QS201).
                string mode = Mode(surface.PresentationMode());
                modes[mode] = modes.GetValueOrDefault(mode) + 1;
            }
            else
            {
                missed++;
            }

            pending.RemoveAt(at);
        }

        return missed;
    }

    /// <summary>What the report calls each way a frame reaches the glass.</summary>
    private static string Mode(FramePresentationMode? mode) => mode switch
    {
        FramePresentationMode.Composed => "composed",
        FramePresentationMode.Overlay => "overlay",
        FramePresentationMode.None => "independent flip",
        FramePresentationMode.CompositionFailure => "composition failed",
        _ => "not reported",
    };

    /// <summary>Feeds the next frame's worth of stream, wrapping at the end.</summary>
    private static int Next(Emulator emulator, byte[] stream, int offset)
    {
        if (offset + BytesPerFrame > stream.Length)
        {
            offset = 0;
        }

        emulator.Feed(stream.AsSpan(offset, Math.Min(BytesPerFrame, stream.Length)));
        return offset + BytesPerFrame;
    }

    /// <summary>
    /// The gap to the next echo. Between 100 and 200 ms at a prompt, about a fast typist's rate,
    /// and between 50 and 150 ms while busy. Never a whole multiple of the refresh, so the echoes
    /// land at every phase of the frame rather than one.
    /// </summary>
    private static long Interval(Random rhythm, Workload workload)
    {
        double milliseconds = workload.Streaming ? 50 + (rhythm.NextDouble() * 100)
                                                 : 100 + (rhythm.NextDouble() * 100);

        return Ticks(TimeSpan.FromMilliseconds(milliseconds));
    }

    private static long Ticks(TimeSpan span) => (long)(span.TotalSeconds * Stopwatch.Frequency);
}

/// <summary>What one run found.</summary>
/// <param name="Arm">The swapchain it presented through.</param>
/// <param name="Workload">What the host was doing.</param>
/// <param name="Latencies">Milliseconds from each timed echo being due to the vblank it was shown at.</param>
/// <param name="Unresolved">Echoes whose vblank went past unseen.</param>
/// <param name="Frames">Frames presented.</param>
/// <param name="Occlusions">Presents that went nowhere because the window was covered.</param>
/// <param name="Modes">How each timed echo reached the glass, counted by the report's word for it.</param>
internal sealed record Outcome(Arm Arm, Workload Workload, List<double> Latencies, int Unresolved,
                               long Frames, long Occlusions, Dictionary<string, int> Modes);
