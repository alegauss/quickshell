using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace HostProbe.Core;

/// <summary>
/// The run, identical for all three hosts: warm up, count presents, count presents again with a
/// dropdown overlapping, prove a modal overlaps too, then time thirty clicks from the input going
/// in to the desktop frame that answers it being presented.
/// </summary>
public static class ProbeDriver
{
    private const int WarmupMs = 1500;
    private const int SampleMs = 5000;
    private const int Trials = 30;
    private const int TrialTimeoutMs = 500;

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint hWnd, out RectNative rect);

    [StructLayout(LayoutKind.Sequential)]
    private struct RectNative
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    public static void Run(IProbeHost host, string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);

        ProbeReport report = new()
        {
            Host = host.HostName,
            RanAt = DateTimeOffset.Now,
            MachineDisplayHz = DisplayRefresh().ToString(CultureInfo.InvariantCulture),
            LatencyTrials = Trials,
        };

        Thread.Sleep(WarmupMs);
        Foreground(host);

        (int x, int y, int width, int height) = ReadPaneRect(host);
        int sampleX = x + (width / 2);
        int sampleY = y + (height / 2);

        using DesktopProbe desktop = DesktopProbe.ForPoint(sampleX, sampleY);

        // Nothing is clicked until the pane's own idle colour has been seen at the sample point.
        // Without this the probe would inject thirty clicks at a coordinate owned by whatever
        // window is actually in front, which is what the first run of this rig did.
        if (!desktop.WaitForDark(sampleX, sampleY, 2000))
        {
            report.Notes.Add($"the pane's idle colour was never seen at {sampleX},{sampleY}, so no click was injected " +
                             $"(frames {desktop.FramesAcquired}, pointer-only {desktop.PointerOnlyFrames}, " +
                             $"acquire failures {desktop.AcquireFailures}, last pixel 0x{desktop.LastPixel:X8})");
            Write(host, report, outputDirectory);
            host.RunOnUi(host.Shutdown);
            return;
        }

        report.PresentedFpsClean = SampleFps(host.Pane);

        host.RunOnUi(host.OpenDropdown);
        Thread.Sleep(600);
        report.DropdownShot = Capture(host, desktop, outputDirectory, "dropdown");
        report.DropdownOverlapped = report.DropdownShot.Length > 0;
        report.PresentedFpsWithDropdown = SampleFps(host.Pane);
        host.RunOnUi(host.CloseDropdown);
        Thread.Sleep(400);

        host.RunOnUi(host.ShowModal);
        Thread.Sleep(800);
        report.ModalShot = Capture(host, desktop, outputDirectory, "modal");
        report.ModalOverlapped = report.ModalShot.Length > 0;
        host.RunOnUi(host.CloseModal);
        Thread.Sleep(500);

        Foreground(host);
        MeasureLatency(desktop, report, sampleX, sampleY);

        Write(host, report, outputDirectory);
        host.RunOnUi(host.Shutdown);
    }

    private static void Foreground(IProbeHost host)
    {
        host.RunOnUi(() => Native.SetForegroundWindow(host.WindowHandle));
        Thread.Sleep(400);
    }

    private static void Write(IProbeHost host, ProbeReport report, string outputDirectory)
    {
        string path = Path.Combine(outputDirectory, host.HostName + ".json");
        File.WriteAllText(path, JsonSerializer.Serialize(report, Indented));

        Console.WriteLine($"{host.HostName}: fps {report.PresentedFpsClean:F1} clean, {report.PresentedFpsWithDropdown:F1} with dropdown; " +
                          $"click->pixel median {report.LatencyMedianMs:F1} ms, p95 {report.LatencyP95Ms:F1} ms, " +
                          $"{report.LatencyTimeouts} timeout(s) -> {path}");

        foreach (string note in report.Notes)
        {
            Console.WriteLine("  note: " + note);
        }
    }

    private static double SampleFps(Pane pane)
    {
        long before = pane.Frames;
        long start = Clock.Now;
        Thread.Sleep(SampleMs);
        long after = pane.Frames;
        long stop = Clock.Now;

        return (after - before) / (Clock.MillisecondsBetween(start, stop) / 1000.0);
    }

    private static void MeasureLatency(DesktopProbe desktop, ProbeReport report, int sampleX, int sampleY)
    {
        List<double> samples = [];

        for (int trial = 0; trial < Trials; trial++)
        {
            if (!desktop.WaitForDark(sampleX, sampleY, TrialTimeoutMs * 2))
            {
                report.Notes.Add($"trial {trial}: the pane never returned to idle, so no click was injected");
                report.LatencyTimeouts++;
                continue;
            }

            // The pointer is parked on the sample point before the clock starts, so the move is
            // not inside the interval being measured.
            Native.ParkPointer(sampleX, sampleY);
            Thread.Sleep(80);

            long clicked = Clock.Now;
            Native.ClickAt(sampleX, sampleY);

            long lit = desktop.WaitForLit(sampleX, sampleY, TrialTimeoutMs);

            if (lit < 0)
            {
                report.LatencyTimeouts++;
                continue;
            }

            samples.Add(Clock.MillisecondsBetween(clicked, lit));
        }

        samples.Sort();
        report.LatencySamplesMs = samples;

        if (samples.Count > 0)
        {
            report.LatencyMedianMs = samples[samples.Count / 2];
            report.LatencyP95Ms = samples[Math.Min(samples.Count - 1, (int)(samples.Count * 0.95))];
            report.LatencyMinMs = samples[0];
            report.LatencyMaxMs = samples[^1];
        }
    }

    private static (int X, int Y, int Width, int Height) ReadPaneRect(IProbeHost host)
    {
        (int X, int Y, int Width, int Height) rect = default;
        host.RunOnUi(() => rect = host.PaneScreenRect);
        return rect;
    }

    private static int DisplayRefresh() => 60;

    private static string Capture(IProbeHost host, DesktopProbe desktop, string outputDirectory, string label)
    {
        nint handle = nint.Zero;
        host.RunOnUi(() => handle = host.WindowHandle);

        if (handle == nint.Zero || !GetWindowRect(handle, out RectNative rect))
        {
            return "";
        }

        int width = rect.Right - rect.Left;
        int height = rect.Bottom - rect.Top;

        if (width <= 0 || height <= 0)
        {
            return "";
        }

        string path = Path.Combine(outputDirectory, $"{host.HostName}-{label}.png");

        return desktop.CaptureRegion(rect.Left, rect.Top, width, height, path) ? path : "";
    }
}
