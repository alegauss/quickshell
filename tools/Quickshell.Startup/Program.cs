using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Quickshell.Startup;

/// <summary>
/// How long the client takes to reach a prompt — figure 5 of the performance budget. QS75.
///
/// <para><b>The client times itself, and this only starts it and reads what it says.</b> Each start
/// is given <c>--startup-report</c>, and the client writes when each milestone happened, measured
/// from its own creation by Windows: so no two clocks are compared, and the harness's own overhead
/// in starting a process is not in the figure.</para>
///
/// <para><b>Many starts, and the first reported apart.</b> The first start after a build is the
/// nearest this desk comes to a cold file cache without flushing the system's standby list, which
/// needs tools and rights a harness should not assume; every later start is warm. Both are
/// reported, each labelled for what it is, because a single number that does not say which it was
/// is not a measurement.</para>
///
/// <para><b>Each start is ended with everything it started</b> — the shell and its console host
/// included — and waited out before the next, so no start inherits a warm process from the one
/// before it.</para>
/// </summary>
public static class Startup
{
    /// <summary>The milestones a start reports, in the order they happen.</summary>
    private static readonly string[] Milestones = ["main", "application", "constructed", "shown", "shell", "device", "frame", "interactive"];

    /// <summary>Runs the starts and prints the report.</summary>
    public static int Main(string[] arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        if (arguments.Length == 0 || arguments.Contains("--help", StringComparer.Ordinal))
        {
            Console.WriteLine(
                """
                Quickshell.Startup — how long the client takes to reach a prompt.

                  --launch <exe>   the client to start
                  --runs <n>       how many starts (default 10)
                  --label <name>   what to call this build in the report
                  --out <file>     append the report here as well as printing it
                  --json <file>    write every start's milestones here, for a program to read

                Each start is timed by the client itself, from its creation to the first frame that
                carries the shell's own output.
                """);

            return 0;
        }

        string? launch = Argument(arguments, "--launch");

        if (launch is null || !File.Exists(launch))
        {
            Console.Error.WriteLine("give --launch <exe>, a client that exists");

            return 2;
        }

        // Whole, because a process is created from a path and not from a working directory's idea
        // of one.
        launch = Path.GetFullPath(launch);

        int runs = int.TryParse(Argument(arguments, "--runs"), NumberStyles.None,
                                CultureInfo.InvariantCulture, out int asked) && asked > 0 ? asked : 10;
        string label = Argument(arguments, "--label") ?? Path.GetFileNameWithoutExtension(launch);

        List<Dictionary<string, double>> starts = [];

        for (int run = 0; run < runs; run++)
        {
            Dictionary<string, double>? timed = Once(launch);

            if (timed is null)
            {
                Console.Error.WriteLine($"start {run + 1} reported nothing within thirty seconds");

                return 3;
            }

            starts.Add(timed);

            Console.Error.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"start {run + 1}: interactive at {timed["interactive"]:0.0} ms"));
        }

        string report = Report(label, launch, starts);

        Console.WriteLine(report);

        if (Argument(arguments, "--out") is { } file)
        {
            File.AppendAllText(file, report + Environment.NewLine);
        }

        // Every start and not the summary, first included and in order: the performance gate
        // (QS79) derives its threshold from how far the warm starts spread, which a median hides.
        if (Argument(arguments, "--json") is { } json)
        {
            File.WriteAllText(json, JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["label"] = label,
                ["launch"] = launch,
                ["starts"] = starts,
            }));
        }

        return 0;
    }

    /// <summary>One start, timed by the client, and everything it started ended afterwards.</summary>
    private static Dictionary<string, double>? Once(string launch)
    {
        string report = Path.Combine(Path.GetTempPath(), $"quickshell-startup-{Guid.NewGuid():N}.json");

        using Process client = Process.Start(new ProcessStartInfo(launch)
        {
            ArgumentList = { "--startup-report", report },
            UseShellExecute = false,
        })!;

        try
        {
            Stopwatch waited = Stopwatch.StartNew();

            while (!File.Exists(report))
            {
                if (waited.Elapsed > TimeSpan.FromSeconds(30) || client.HasExited)
                {
                    return null;
                }

                Thread.Sleep(10);
            }

            // Read as soon as it can be, which is not always as soon as it exists: the client moves it
            // into place whole, and a virus scanner opening a file that has just appeared is enough
            // to refuse the first read of it. Found by the performance gate's first failing run.
            while (true)
            {
                try
                {
                    return JsonSerializer.Deserialize<Dictionary<string, double>>(File.ReadAllText(report));
                }
                catch (IOException)
                {
                    // Past the same thirty seconds a report that never appears gets, it is a start that
                    // reported nothing, and the caller says so rather than this dying on the read.
                    if (waited.Elapsed > TimeSpan.FromSeconds(30))
                    {
                        return null;
                    }

                    Thread.Sleep(10);
                }
            }
        }
        finally
        {
            // The shell and its console host with it, or the next start shares a desk with them.
            try
            {
                client.Kill(entireProcessTree: true);
                client.WaitForExit(10_000);
            }
            catch (InvalidOperationException)
            {
                // Already gone.
            }

            Gone(report);

            // Long enough for the console host the shell ran under to go too.
            Thread.Sleep(1_000);
        }
    }

    /// <summary>
    /// Deletes a start's report, waiting out whatever is still reading it, and leaves it in the
    /// temporary folder rather than fail a run of starts over one file.
    /// </summary>
    private static void Gone(string report)
    {
        for (int attempt = 0; attempt < 50; attempt++)
        {
            try
            {
                File.Delete(report);

                return;
            }
            catch (IOException)
            {
                Thread.Sleep(100);
            }
        }
    }

    /// <summary>The first start, then the median and spread of the rest, per milestone.</summary>
    private static string Report(string label, string launch, List<Dictionary<string, double>> starts)
    {
        StringBuilder text = new();

        text.AppendLine(CultureInfo.InvariantCulture,
                        $"## {label} — {starts.Count} starts, {DateTime.Now:yyyy-MM-dd}")
            .AppendLine()
            .AppendLine(CultureInfo.InvariantCulture, $"`{launch}`")
            .AppendLine()
            .AppendLine("| milestone | first start | warm median | warm min | warm max |")
            .AppendLine("|---|---|---|---|---|");

        List<Dictionary<string, double>> warm = starts.Count > 1 ? starts[1..] : starts;

        foreach (string milestone in Milestones)
        {
            double[] times = [.. warm.Where(start => start.ContainsKey(milestone))
                                     .Select(start => start[milestone])
                                     .Order()];

            if (times.Length == 0)
            {
                continue;
            }

            string first = starts[0].TryGetValue(milestone, out double at) ? Ms(at) : "—";

            text.AppendLine(CultureInfo.InvariantCulture,
                            $"| {milestone} | {first} | {Ms(Median(times))} | {Ms(times[0])} | {Ms(times[^1])} |");
        }

        return text.ToString();
    }

    private static double Median(double[] sorted) =>
        sorted.Length % 2 == 1
            ? sorted[sorted.Length / 2]
            : (sorted[(sorted.Length / 2) - 1] + sorted[sorted.Length / 2]) / 2;

    private static string Ms(double value) =>
        value.ToString("0", CultureInfo.InvariantCulture) + " ms";

    private static string? Argument(string[] arguments, string flag)
    {
        int at = Array.IndexOf(arguments, flag);

        return at >= 0 && at + 1 < arguments.Length ? arguments[at + 1] : null;
    }
}
