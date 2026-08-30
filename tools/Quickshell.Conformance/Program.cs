using System.Diagnostics;
using System.Globalization;
using System.Text;
using Quickshell.App;
using Quickshell.Terminal;
using Quickshell.Transport;

// Runs esctest against the headless model and writes what it found.
//
// esctest is somebody else's suite, which is the whole point of it: a test written by whoever wrote
// the parser tests that person's understanding of the specification, and that understanding is the
// thing most likely to be wrong.
//
// It runs as the pseudo-console's child and this client is the terminal it is judging. There is no
// renderer and no network in the loop - a fidelity result should not be able to fail because of a
// font.
//
// Usage:  quickshell-conformance [include-regex]
//
// The suite is POSIX and this client is Windows, so it runs under WSL. Setup is in the report this
// writes, under "Reproducing this".

string include = args.Length > 0 ? args[0] : ".*";
string suite = Environment.GetEnvironmentVariable("QUICKSHELL_ESCTEST") ?? "/home/ubuntu/esctest";
string log = "/tmp/quickshell-esctest.log";

string command =
    $"wsl.exe -- bash -c \"cd {suite} && python3 esctest.py --expected-terminal=xterm "
    + $"--logfile={log} --include='{include}' --timeout=1\"";

Console.WriteLine($"esctest: include={include}, suite={suite}");

Stopwatch clock = Stopwatch.StartNew();
Emulator emulator = new(80, 25);
PtyExit exit;
long parsed;

await using (ConPtyChannel channel = await ConPtyChannel.StartAsync(command, 80, 25))
await using (SessionPipeline pipeline = SessionPipeline.Start(channel, emulator))
{
    Task<PtyExit> closed = channel.Closed;

    while (!closed.IsCompleted && clock.Elapsed < TimeSpan.FromMinutes(20))
    {
        await Task.Delay(500);
    }

    if (!closed.IsCompleted)
    {
        await Console.Error.WriteLineAsync("esctest did not finish within twenty minutes.");

        return 1;
    }

    exit = await closed;
    parsed = pipeline.Work.Bytes;
}

clock.Stop();

string[] lines = Read(log);

if (lines.Length == 0)
{
    await Console.Error.WriteLineAsync($"no log at {log}: did esctest run?");

    return 1;
}

Tally tally = Tally.Of(lines);

await Console.Out.WriteLineAsync(
    $"{tally.Passed} passed, {tally.KnownBugs} known bugs, {tally.Failed} failed "
    + $"in {clock.Elapsed.TotalSeconds:F0}s ({parsed} bytes, exit {exit.Code})");

string report = Path.Combine(Root(), "docs", "measurements", $"esctest-{Machine()}.md");

await File.WriteAllTextAsync(report, Report(tally, clock.Elapsed, parsed), new UTF8Encoding(false));

Console.WriteLine($"-> {report}");

return 0;

static string[] Read(string path)
{
    ProcessStartInfo start = new("wsl.exe", $"-- cat {path}")
    {
        RedirectStandardOutput = true,
        StandardOutputEncoding = Encoding.UTF8,
        UseShellExecute = false,
    };

    using Process? reading = Process.Start(start);

    if (reading is null)
    {
        return [];
    }

    string text = reading.StandardOutput.ReadToEnd();
    reading.WaitForExit();

    return text.Split('\n');
}

static string Root()
{
    DirectoryInfo? directory = new(AppContext.BaseDirectory);

    while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Quickshell.sln")))
    {
        directory = directory.Parent;
    }

    return directory?.FullName ?? Environment.CurrentDirectory;
}

static string Machine() => Environment.MachineName.ToLowerInvariant();

static string Report(Tally tally, TimeSpan took, long parsed)
{
    StringBuilder report = new();

    report.AppendLine("# esctest");
    report.AppendLine();
    report.AppendLine(string.Format(
        CultureInfo.InvariantCulture,
        "`esctest` from the terminal working group, run against the headless model on {0}, "
        + "{1:yyyy-MM-dd}. No renderer and no network: the suite runs as the pseudo-console's child "
        + "and this client is the terminal it judges. {2:F0} s, {3:N0} bytes parsed.",
        Machine(),
        DateTime.Now,
        took.TotalSeconds,
        parsed));
    report.AppendLine();
    report.AppendLine("| | tests | of total |");
    report.AppendLine("|---|---:|---:|");

    int total = tally.Passed + tally.KnownBugs + tally.Failed;

    report.AppendLine(Row("passed", tally.Passed, total));
    report.AppendLine(Row("known bugs in xterm itself", tally.KnownBugs, total));
    report.AppendLine(Row("failed", tally.Failed, total));
    report.AppendLine();
    report.AppendLine("## Why the failures fail");
    report.AppendLine();
    report.AppendLine("Grouped by what the traceback says, because 'three hundred failures' is not a");
    report.AppendLine("finding and 'one missing sequence and seventy real gaps' is.");
    report.AppendLine();
    report.AppendLine("| cause | tests |");
    report.AppendLine("|---|---:|");
    report.AppendLine($"| the screen could not be read back at all | {tally.Checksum} |");
    report.AppendLine($"| the suite declined the test itself | {tally.Internal} |");
    report.AppendLine($"| a real difference in behaviour | {tally.Other} |");
    report.AppendLine();
    report.AppendLine("## The failing tests");
    report.AppendLine();
    report.AppendLine("Every one, by class, so a change that improves one area while quietly breaking");
    report.AppendLine("another shows up as a number rather than as a feeling.");
    report.AppendLine();
    report.AppendLine("| class | failing |");
    report.AppendLine("|---|---:|");

    foreach ((string name, int count) in tally.ByClass)
    {
        report.AppendLine($"| `{name}` | {count} |");
    }

    report.AppendLine();
    report.AppendLine("## Reproducing this");
    report.AppendLine();
    report.AppendLine("```");
    report.AppendLine("# once: the suite is POSIX, so it lives in WSL");
    report.AppendLine("curl -L -o esctest.zip \\");
    report.AppendLine("  https://codeload.github.com/ThomasDickey/esctest2/zip/refs/heads/master");
    report.AppendLine("unzip -q esctest.zip && mv esctest2-master/esctest ~/esctest");
    report.AppendLine();
    report.AppendLine("# then, from the repository root");
    report.AppendLine("dotnet run --project tools/Quickshell.Conformance -c Release");
    report.AppendLine("```");
    report.AppendLine();
    report.AppendLine("`QUICKSHELL_ESCTEST` overrides where the suite lives. An argument is a regular");
    report.AppendLine("expression over test names, so `... -- CUP` runs one section.");

    return report.ToString();

    static string Row(string what, int count, int total) =>
        string.Format(
            CultureInfo.InvariantCulture,
            "| {0} | {1} | {2:F1}% |",
            what,
            count,
            total == 0 ? 0 : count * 100.0 / total);
}

/// <summary>What the suite's own log says, which is the only thing a pass rate may come from.</summary>
internal sealed record Tally(
    int Passed,
    int KnownBugs,
    int Failed,
    int Checksum,
    int Internal,
    int Other,
    IReadOnlyList<(string Name, int Count)> ByClass)
{
    /// <summary>Reads the numbers out of esctest's own log rather than out of anybody's memory.</summary>
    public static Tally Of(string[] lines)
    {
        int passed = 0;
        int known = 0;
        int failed = 0;

        foreach (string line in lines)
        {
            if (!line.StartsWith("*** ", StringComparison.Ordinal) || !line.Contains(" passed,", StringComparison.Ordinal))
            {
                continue;
            }

            string[] parts = line.Split(' ');

            for (int index = 0; index + 1 < parts.Length; index++)
            {
                if (!int.TryParse(parts[index], NumberStyles.None, CultureInfo.InvariantCulture, out int count))
                {
                    continue;
                }

                if (parts[index + 1].StartsWith("test", StringComparison.OrdinalIgnoreCase)
                    && index + 2 < parts.Length
                    && parts[index + 2].Contains("FAILED", StringComparison.OrdinalIgnoreCase))
                {
                    failed = count;
                }
                else if (parts[index + 1].StartsWith("known", StringComparison.OrdinalIgnoreCase))
                {
                    known = count;
                }
                else if (parts[index + 1].StartsWith("test", StringComparison.OrdinalIgnoreCase))
                {
                    passed = passed == 0 ? count : passed;
                }
            }
        }

        HashSet<string> failing = Failing(lines);
        (int checksum, int internals, int other, List<(string, int)> byClass) = Causes(lines, failing);

        // The summary line spells the last number as "N tests failed" too, so the count of names
        // under "Failing tests:" is the one to trust.
        return new Tally(passed, known, failing.Count, checksum, internals, other, byClass);
    }

    private static HashSet<string> Failing(string[] lines)
    {
        HashSet<string> failing = new(StringComparer.Ordinal);
        bool listing = false;

        foreach (string line in lines)
        {
            if (line.StartsWith("Failing tests:", StringComparison.Ordinal))
            {
                listing = true;
                continue;
            }

            if (listing && line.Trim().Length > 0)
            {
                failing.Add(line.Trim());
            }
        }

        return failing;
    }

    private static (int Checksum, int Internal, int Other, List<(string, int)> ByClass) Causes(
        string[] lines,
        HashSet<string> failing)
    {
        Dictionary<string, string> cause = new(StringComparer.Ordinal);
        string? current = null;
        StringBuilder body = new();

        foreach (string line in lines)
        {
            if (line.StartsWith("Run test: ", StringComparison.Ordinal))
            {
                Classify(cause, failing, current, body.ToString());
                current = line["Run test: ".Length..].Trim();
                body.Clear();
            }
            else
            {
                body.AppendLine(line);
            }
        }

        Classify(cause, failing, current, body.ToString());

        Dictionary<string, int> classes = new(StringComparer.Ordinal);

        foreach (string name in failing)
        {
            string owner = name.Split('.')[0];
            classes[owner] = classes.GetValueOrDefault(owner) + 1;
        }

        return (
            cause.Values.Count(v => v == "checksum"),
            cause.Values.Count(v => v == "internal"),
            cause.Values.Count(v => v == "other"),
            [.. classes.OrderByDescending(entry => entry.Value)
                .ThenBy(entry => entry.Key, StringComparer.Ordinal)
                .Select(entry => (entry.Key, entry.Value))]);
    }

    private static void Classify(
        Dictionary<string, string> cause,
        HashSet<string> failing,
        string? name,
        string body)
    {
        if (name is null || !failing.Contains(name))
        {
            return;
        }

        cause[name] = body.Contains("ChecksumException", StringComparison.Ordinal)
            ? "checksum"
            : body.Contains("InternalError", StringComparison.Ordinal) ? "internal" : "other";
    }
}
