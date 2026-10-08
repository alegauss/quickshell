using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace Quickshell.Photon;

/// <summary>
/// Input to photon: how long an echoed keystroke takes to reach the glass.
///
/// <para><b>Figure 1 of the performance budget, and the first one nothing measured.</b> QS7 bought
/// the present path three flags to bound it and could not show what they were worth, because one
/// clear per frame can never get ahead of the display. This draws the figure-3 grid from the atlas
/// every frame, so there is something to queue, and times the client's path against the one a
/// renderer that never bought the flags would be on.</para>
///
/// <para><b>The display bounds the absolute answer.</b> On the 60 Hz reference panel a frame is
/// 16.7 ms, so what this settles is the shape — one frame against several — and not the 8.3 ms a
/// 120 Hz panel would allow.</para>
/// </summary>
public static class Photon
{
    /// <summary>Runs every arm under every workload and prints the report.</summary>
    public static int Main(string[] arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        if (Argument(arguments, "--help") is not null)
        {
            Console.WriteLine(
                """
                Quickshell.Photon — how long an echoed keystroke takes to reach the glass.

                  --seconds <n>   how long each run times (default 20)
                  --passes <n>    how many times each arm runs under each workload (default 3)
                  --out <file>    append the report here as well as printing it

                It puts a large topmost window on this desk for every run and never activates it.
                Nothing may cover it: an occluded frame has no photon end, and a run that saw one
                is reported as not measured.
                """);

            return 0;
        }

        TimeSpan length = TimeSpan.FromSeconds(Number(arguments, "--seconds", 20));
        int passes = Number(arguments, "--passes", 3);
        byte[] stream = Stream("cat-log");

        Arm[] arms = [Arm.Client, Arm.Early, Arm.Unbought, Arm.Deep];
        Workload[] workloads = [Workload.Typing, Workload.Busy];
        List<Outcome> outcomes = [];

        for (int pass = 0; pass < passes; pass++)
        {
            foreach (Workload workload in workloads)
            {
                // The order swaps every pass, so a desk that got busier halfway through a pass is not
                // charged to one arm.
                IEnumerable<Arm> order = pass % 2 == 0 ? arms : arms.Reverse();

                foreach (Arm arm in order)
                {
                    Console.Error.WriteLine(string.Create(CultureInfo.InvariantCulture,
                        $"pass {pass + 1}/{passes}: {arm.Name} while {workload.Name}…"));

                    // The same seed for both arms of a pass, so they see the same rhythm of echoes.
                    outcomes.Add(Run.Time(arm, workload, stream, length, seed: 86 + pass));
                }
            }
        }

        string report = Report(outcomes, length, passes);

        Console.WriteLine(report);

        if (Argument(arguments, "--out") is { Length: > 0 } into)
        {
            File.AppendAllText(into, report + Environment.NewLine);
        }

        return outcomes.Any(each => each.Occlusions > 0 || each.Latencies.Count == 0) ? 1 : 0;
    }

    private static string Report(List<Outcome> outcomes, TimeSpan length, int passes)
    {
        StringBuilder text = new();

        text.AppendLine(CultureInfo.InvariantCulture,
                        $"## Input to photon — {passes} passes of {length.TotalSeconds:F0} s per arm")
            .AppendLine()
            .AppendLine(CultureInfo.InvariantCulture,
                        $"measured {DateTimeOffset.Now:yyyy-MM-dd HH:mm} local on {Environment.MachineName}, echo due to the vblank DXGI reports it shown at, in milliseconds")
            .AppendLine()
            .AppendLine("| workload | arm | echoes | unresolved | frames | occluded | shown as | min | p10 | median | p90 | max |")
            .AppendLine("|---|---|---|---|---|---|---|---|---|---|---|---|");

        foreach (IGrouping<(string, string), Outcome> group in outcomes.GroupBy(
                     each => (each.Workload.Name, each.Arm.Name)))
        {
            List<double> pooled = [.. group.SelectMany(each => each.Latencies).Order()];
            Outcome first = group.First();

            string how = (first.Arm.Waitable ? first.Arm.WaitFirst ? "waited before reading" : "waited after painting"
                                             : "Present blocks")
                         + $", {first.Arm.Buffers.ToString(CultureInfo.InvariantCulture)} buffers";

            int unresolved = group.Sum(each => each.Unresolved);
            long frames = group.Sum(each => each.Frames);
            long occluded = group.Sum(each => each.Occlusions);

            // Every way the arm's echoes reached the glass, most first: a median is one cost only
            // when this says one mode.
            string shown = string.Join(", ", group.SelectMany(each => each.Modes)
                                                  .GroupBy(pair => pair.Key, StringComparer.Ordinal)
                                                  .Select(mode => (Mode: mode.Key, Count: mode.Sum(pair => pair.Value)))
                                                  .OrderByDescending(mode => mode.Count)
                                                  .Select(mode => string.Create(CultureInfo.InvariantCulture, $"{mode.Mode} {mode.Count}")));

            text.AppendLine(CultureInfo.InvariantCulture,
                $"| {first.Workload.Name} | {first.Arm.Name} (latency {first.Arm.Latency}, {how}) | {pooled.Count} | {unresolved} | {frames} | {occluded} | {shown} | {Quantile(pooled, 0):F1} | {Quantile(pooled, 0.10):F1} | {Quantile(pooled, 0.50):F1} | {Quantile(pooled, 0.90):F1} | {Quantile(pooled, 1):F1} |");
        }

        text.AppendLine()
            .AppendLine("Per pass, median:")
            .AppendLine();

        foreach (IGrouping<(string, string), Outcome> group in outcomes.GroupBy(
                     each => (each.Workload.Name, each.Arm.Name)))
        {
            string medians = string.Join(", ", group.Select(
                each => Quantile([.. each.Latencies.Order()], 0.5).ToString("F1", CultureInfo.InvariantCulture)));

            text.AppendLine(CultureInfo.InvariantCulture, $"- {group.Key.Item1}, {group.Key.Item2}: {medians}");
        }

        return text.ToString();
    }

    /// <summary>A nearest-rank quantile of an ordered list, or NaN for an empty one.</summary>
    private static double Quantile(List<double> ordered, double q) =>
        ordered.Count == 0 ? double.NaN
                           : ordered[(int)Math.Clamp(Math.Ceiling(q * ordered.Count) - 1, 0, ordered.Count - 1)];

    /// <summary>One captured stream from the corpus, found from wherever this was started.</summary>
    private static byte[] Stream(string name)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Quickshell.sln")))
        {
            directory = directory.Parent;
        }

        if (directory is null)
        {
            throw new DirectoryNotFoundException("the repository root is not above this executable");
        }

        string path = Path.Combine(directory.FullName, "benchmarks", "corpus", "streams", name + ".raw.gz");

        using FileStream file = File.OpenRead(path);
        using GZipStream unzip = new(file, CompressionMode.Decompress);
        using MemoryStream memory = new();
        unzip.CopyTo(memory);

        return memory.ToArray();
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
}
