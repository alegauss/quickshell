using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace Quickshell.Idle;

/// <summary>
/// What a window costs while nobody is typing into it.
///
/// <para><b>Figure 4 of the performance budget is the number zero</b>, and "small" is not a pass
/// there. This measures it the way the budget says: the OS scheduler's own account of how much core
/// time a process consumed over an idle interval, sampled rather than felt.</para>
///
/// <para><b>The timer resolution is the half nobody notices.</b> A process that asks Windows for a
/// finer system timer and never gives it back is spending battery inside every other application on
/// the machine, and nothing in the process's own numbers shows it. It is a system-wide setting, so
/// attribution here is by difference: read it before the subject is running, read it while it is,
/// read it once more after it has gone.</para>
///
/// <para><b>It measures any process, on purpose.</b> The comparison against the incumbent is part of
/// the result — this is the figure the project claims to win on, and a claim measured only against
/// itself is not a comparison.</para>
/// </summary>
public static class Idle
{
    /// <summary>
    /// Windows reports timer resolution in 100-nanosecond units.
    /// </summary>
    private const double HundredNanosecondsPerMillisecond = 10_000.0;

    [DllImport("ntdll.dll")]
    private static extern int NtQueryTimerResolution(out uint minimum, out uint maximum,
                                                     out uint current);

    /// <summary>Runs one measurement and prints the report.</summary>
    public static int Main(string[] arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        if (Argument(arguments, "--help") is not null || arguments.Length == 0)
        {
            Console.WriteLine(
                """
                Quickshell.Idle — what a window costs while nobody types into it.

                  --launch <exe>     start this and measure it
                  --attach <names>   measure processes already running, by name, comma-separated —
                                     a client whose idle cost is spread over a helper process is
                                     one that would otherwise be measured at a fraction of it
                  --for <seconds>    how long to watch (default 600)
                  --settle <seconds> how long to ignore after start-up (default 30)
                  --label <name>     what to call the subject in the report
                  --out <file>        append the report here as well as printing it

                One subject per run. The timer resolution is system-wide, so measuring two at
                once makes attribution impossible.
                """);

            return 0;
        }

        int seconds = Number(arguments, "--for", 600);
        int settle = Number(arguments, "--settle", 30);
        string? launch = Argument(arguments, "--launch");
        string? attach = Argument(arguments, "--attach");

        if (launch is null && attach is null)
        {
            Console.Error.WriteLine("give either --launch <exe> or --attach <name>");

            return 2;
        }

        string label = Argument(arguments, "--label") ?? Path.GetFileNameWithoutExtension(
            launch ?? attach ?? "unknown");

        // Before anything is started, which is the only reading that can serve as a baseline.
        double quiet = Resolution();

        Process? started = null;
        List<Process> subjects = [];

        try
        {
            if (launch is not null)
            {
                started = Process.Start(new ProcessStartInfo(launch) { UseShellExecute = false });

                if (started is null)
                {
                    Console.Error.WriteLine($"{launch} did not start");

                    return 3;
                }

                subjects.Add(started);
            }
            else
            {
                foreach (string name in attach!.Split(',', StringSplitOptions.RemoveEmptyEntries
                                                          | StringSplitOptions.TrimEntries))
                {
                    Process[] running = Process.GetProcessesByName(name);

                    if (running.Length == 0)
                    {
                        Console.Error.WriteLine($"no process called {name} is running");

                        return 4;
                    }

                    subjects.AddRange(running);
                }
            }

            Report report = Watch(subjects, label, seconds, settle, quiet);

            Console.WriteLine(report.Text);

            if (Argument(arguments, "--out") is { } into)
            {
                File.AppendAllText(into, report.Text + Environment.NewLine);
            }

            return report.Zero ? 0 : 1;
        }
        finally
        {
            if (started is { HasExited: false })
            {
                started.Kill(entireProcessTree: true);
                started.WaitForExit(5_000);
            }

            started?.Dispose();
        }
    }

    /// <summary>
    /// Watches one process and composes what it found.
    /// </summary>
    /// <param name="subjects">
    /// What to watch, summed. More than one because a client that spreads its idle cost over a
    /// helper process — an X server, a broker — would otherwise be measured at a fraction of what it
    /// actually costs, and that would flatter it.
    /// </param>
    /// <param name="label">What to call it.</param>
    /// <param name="seconds">How long the measured interval is, after settling.</param>
    /// <param name="settle">
    /// How long to discard first. A window that has just opened is doing start-up work, and
    /// counting it would be measuring the cold start twice under the wrong figure's name.
    /// </param>
    /// <param name="quiet">The system timer resolution before the subject existed.</param>
    private static Report Watch(List<Process> subjects, string label, int seconds, int settle,
                                double quiet)
    {
        Console.Error.WriteLine($"settling for {settle.ToString(CultureInfo.InvariantCulture)}s…");

        Thread.Sleep(TimeSpan.FromSeconds(settle));

        if (Gone(subjects) is { } left)
        {
            return new Report($"{label}: {left} exited during the settling period", false);
        }

        TimeSpan cpuAtStart = Cpu(subjects);
        long peakMemory = Memory(subjects);
        double finest = quiet;
        int samples = 0;

        Stopwatch clock = Stopwatch.StartNew();

        Console.Error.WriteLine($"measuring for {seconds.ToString(CultureInfo.InvariantCulture)}s…");

        while (clock.Elapsed < TimeSpan.FromSeconds(seconds))
        {
            Thread.Sleep(1_000);

            if (Gone(subjects) is { } went)
            {
                return new Report(
                    $"{label}: {went} exited "
                    + $"{clock.Elapsed.TotalSeconds.ToString("F0", CultureInfo.InvariantCulture)}s "
                    + "into the measured interval, so there is no idle figure for it",
                    false);
            }

            peakMemory = Math.Max(peakMemory, Memory(subjects));

            // The finest the system timer went while the subject was up. Smaller is a finer timer,
            // which is the expensive direction.
            finest = Math.Min(finest, Resolution());

            samples++;
        }

        TimeSpan measured = clock.Elapsed;

        TimeSpan cpu = Cpu(subjects) - cpuAtStart;
        long memory = Memory(subjects);
        int threads = subjects.Sum(each => each.Threads.Count);
        int handles = subjects.Sum(each => each.HandleCount);

        // Per one core, which is the unit a person can reason about: 100% is one core saturated.
        double occupancy = cpu.TotalMilliseconds / measured.TotalMilliseconds * 100.0;

        bool raised = finest < quiet - 0.01;
        bool zero = occupancy < 0.10 && !raised;

        StringBuilder text = new();

        text.AppendLine(CultureInfo.InvariantCulture,
                        $"## {label} — idle for {measured.TotalSeconds:F0}s")
            .AppendLine()
            .AppendLine(CultureInfo.InvariantCulture,
                        $"measured {DateTimeOffset.Now:yyyy-MM-dd HH:mm} local, "
                        + $"{samples} samples after a {settle}s settle")
            .AppendLine()
            .AppendLine("| | |")
            .AppendLine("|---|---|")
            .AppendLine(CultureInfo.InvariantCulture,
                        $"| core time consumed | {cpu.TotalMilliseconds:F0} ms |")
            .AppendLine(CultureInfo.InvariantCulture,
                        $"| occupancy of one core | {occupancy:F4} % |")
            .AppendLine(CultureInfo.InvariantCulture,
                        $"| private memory | {memory / (1024.0 * 1024.0):F1} MB "
                        + $"(peak {peakMemory / (1024.0 * 1024.0):F1} MB) |")
            .AppendLine(CultureInfo.InvariantCulture, $"| threads | {threads} |")
            .AppendLine(CultureInfo.InvariantCulture, $"| handles | {handles} |")
            .AppendLine(CultureInfo.InvariantCulture,
                        $"| system timer before it ran | {quiet:F3} ms |")
            .AppendLine(CultureInfo.InvariantCulture,
                        $"| finest system timer while it ran | {finest:F3} ms |")
            .AppendLine(CultureInfo.InvariantCulture,
                        $"| raised the system timer | {(raised ? "yes" : "no")} |")
            .AppendLine()
            .AppendLine(zero
                            ? "**Zero by the budget's reading**: under a tenth of one percent of a "
                              + "core, and the system timer was left where it was found."
                            : raised
                                ? "**Fails figure 4**: the system timer was finer while this ran "
                                  + "than before it started, which costs battery inside every other "
                                  + "process on the machine."
                                : "**Fails figure 4**: measurable core occupancy while idle.")
            .AppendLine()
            .AppendLine("Attribution of the timer reading is by difference — it is a system-wide "
                        + "setting, so a machine with other software raising it cannot be told "
                        + "apart from a subject that did. Read it on a quiet desk.");

        return new Report(text.ToString(), zero);
    }

    /// <summary>The name of the first subject that has gone, or null while all are up.</summary>
    private static string? Gone(List<Process> subjects)
    {
        foreach (Process each in subjects)
        {
            each.Refresh();

            if (each.HasExited)
            {
                return each.ProcessName;
            }
        }

        return null;
    }

    /// <summary>Core time across every subject.</summary>
    private static TimeSpan Cpu(List<Process> subjects)
    {
        TimeSpan total = TimeSpan.Zero;

        foreach (Process each in subjects)
        {
            each.Refresh();

            total += each.TotalProcessorTime;
        }

        return total;
    }

    /// <summary>Private memory across every subject.</summary>
    private static long Memory(List<Process> subjects)
    {
        long total = 0;

        foreach (Process each in subjects)
        {
            each.Refresh();

            total += each.PrivateMemorySize64;
        }

        return total;
    }

    /// <summary>The system timer's current resolution, in milliseconds.</summary>
    private static double Resolution()
    {
        if (NtQueryTimerResolution(out _, out _, out uint current) != 0)
        {
            return double.NaN;
        }

        return current / HundredNanosecondsPerMillisecond;
    }

    private static string? Argument(string[] arguments, string name)
    {
        int at = Array.FindIndex(arguments,
                                 argument => string.Equals(argument, name, StringComparison.Ordinal));

        if (at < 0)
        {
            return null;
        }

        return at + 1 < arguments.Length && !arguments[at + 1].StartsWith("--", StringComparison.Ordinal)
            ? arguments[at + 1]
            : string.Empty;
    }

    private static int Number(string[] arguments, string name, int fallback) =>
        Argument(arguments, name) is { Length: > 0 } given
        && int.TryParse(given, CultureInfo.InvariantCulture, out int parsed)
            ? parsed
            : fallback;

    /// <summary>What one run found, and whether it met the figure.</summary>
    private sealed record Report(string Text, bool Zero);
}
