using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace Quickshell.Gate;

/// <summary>
/// The performance gate, run where the figures are defined: <c>run-perf-gate.cmd</c> by hand, and
/// <c>release.cmd</c> before it makes an archive. QS79.
///
/// <para><b>Two modes.</b> <c>--baseline</c> samples every figure seven times over (eleven starts
/// for the start figure, the first of them dropped as the cold one), derives each threshold from
/// how far those samples spread, and writes <c>benchmarks\gate\&lt;machine&gt;.json</c> — committed,
/// because a baseline is a claim about a desk that the next check is read against. Without it, a
/// check samples fewer times, takes the medians, and judges each against the baseline.</para>
///
/// <para><b>Four answers, each its own exit code.</b> 0: every figure held. 1: a figure is worse
/// than its threshold and nothing said it was meant. 2: a figure is worse and a commit since the
/// baseline said it was meant, with a <c>Performance-Moved</c> trailer — which lets the change through
/// and asks for a new baseline before the next release, because a trailer left in range would go on
/// excusing that figure by any amount. 3: nothing was judged, and the sentence says why.</para>
///
/// <para><b>Every judged run is recorded</b>, baseline or check, in
/// <c>benchmarks\results\gate-&lt;machine&gt;.md</c>: the commit, the three medians and the verdict.
/// A threshold catches a step; only that file shows one per cent a month.</para>
///
/// <para><b>Per machine, and never across two.</b> A baseline is read only on the machine that took
/// it, and a machine without one is refused rather than judged against somebody else's desk.</para>
/// </summary>
public static class Program
{
    /// <summary>Every figure held.</summary>
    public const int Held = 0;

    /// <summary>A figure is worse than its threshold, and nothing said it was meant.</summary>
    public const int Worse = 1;

    /// <summary>A figure is worse, a commit said it was meant, and a new baseline is owed.</summary>
    public const int Accepted = 2;

    /// <summary>Nothing was judged.</summary>
    public const int NotJudged = 3;

    private static readonly JsonSerializerOptions Written = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>Runs the gate.</summary>
    /// <returns>One of <see cref="Held"/>, <see cref="Worse"/>, <see cref="Accepted"/> and <see cref="NotJudged"/>.</returns>
    public static int Main(string[] arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        if (arguments.Contains("--help", StringComparer.Ordinal))
        {
            Console.WriteLine(
                """
                Quickshell.Gate - fails when a performance figure worsens past what its own noise explains.

                  (no flag)        check this tree against this machine's baseline
                  --baseline       take this machine's baseline instead, and write it for committing
                  --client <exe>   the client whose start to time; without it, a release publish is made

                Exit: 0 held, 1 worse, 2 worse but a commit said it was meant (take a new baseline before
                the next release), 3 not judged (no baseline, or a measurement failed).
                """);

            return 0;
        }

        try
        {
            return Run(arguments.Contains("--baseline", StringComparer.Ordinal), Argument(arguments, "--client"));
        }
        catch (Exception refused) when (refused is GateRefusal or IOException or UnauthorizedAccessException
                                            or JsonException or Win32Exception)
        {
            // Whatever stopped it, the answer is that nothing was judged - never a verdict nobody
            // measured, and never a crash a release script would have to read as one.
            Console.Error.WriteLine($"gate: not judged - {refused.Message}");

            return NotJudged;
        }
    }

    private static int Run(bool baseline, string? client)
    {
        string root = Root();
        string machine = Environment.MachineName.ToLowerInvariant();
        string baselineFile = Path.Combine(root, "benchmarks", "gate", $"{machine}.json");
        string history = Path.Combine(root, "benchmarks", "results", $"gate-{machine}.md");

        // Read only by a check: taking a baseline replaces this file, and one that no longer parses is
        // exactly the one somebody takes a new baseline to be rid of.
        BaselineFile? standing = baseline ? null : ReadBaseline(baselineFile, machine);

        string commit = Git(root, "rev-parse", "HEAD").Trim();

        // What the gate writes itself is not a change to what it measures, or every run after the first
        // would call a clean commit dirty.
        bool dirty = Git(root, "status", "--porcelain", "--", ".", ":(exclude)benchmarks/gate",
                         ":(exclude)benchmarks/results/gate-*.md").Trim().Length > 0;

        // Asked before an hour of measuring rather than after it: trailers are read from the baseline's
        // commit to here, and a commit rewritten since - amended, rebased - is not a range at all.
        if (standing is not null && ExitOf(root, "git", "merge-base", "--is-ancestor", standing.Commit, "HEAD") != 0)
        {
            throw new GateRefusal($"the baseline for {machine} was taken at {standing.Commit[..7]}, which is not in this "
                                  + "history any more - amended or rebased away - so the trailers since it cannot be read. "
                                  + "Take a new baseline.");
        }

        Console.WriteLine($"gate  {machine}, {(baseline ? "taking a baseline" : "checking")} at {commit[..7]}"
                          + (dirty ? " with uncommitted changes" : string.Empty));

        Say("building the harnesses");
        Exec(root, "dotnet", "build", Path.Combine("benchmarks", "Quickshell.Replay", "Quickshell.Replay.csproj"),
             "-c", "Release", "--nologo", "-v", "quiet");
        Exec(root, "dotnet", "build", Path.Combine("tools", "Quickshell.Startup", "Quickshell.Startup.csproj"),
             "-c", "Release", "--nologo", "-v", "quiet");

        client ??= Publish(root);

        if (!File.Exists(client))
        {
            throw new GateRefusal($"there is no client at {client} to time");
        }

        Dictionary<string, List<double>> samples =
            Sample(root, Path.GetFullPath(client), replays: baseline ? 7 : 3, starts: baseline ? 11 : 7);

        return standing is null
            ? Baseline(samples, baselineFile, history, machine, commit, dirty)
            : Check(samples, standing, root, history, machine, commit, dirty);
    }

    /// <summary>The baseline a check is read against, or a refusal saying why there is none to read.</summary>
    private static BaselineFile ReadBaseline(string file, string machine)
    {
        if (!File.Exists(file))
        {
            throw new GateRefusal($"there is no baseline for {machine}, so nothing here can be judged. Take one on this "
                                  + "machine with run-perf-gate.cmd --baseline and commit what it writes.");
        }

        try
        {
            return JsonSerializer.Deserialize<BaselineFile>(File.ReadAllText(file), Written)
                   ?? throw new JsonException("it holds nothing");
        }
        catch (JsonException unreadable)
        {
            throw new GateRefusal($"{file} is not a baseline this gate can read ({unreadable.Message}). "
                                  + "run-perf-gate.cmd --baseline writes a new one over it.", unreadable);
        }
    }

    /// <summary>Writes the standings a later check is read against, and says what each may move by.</summary>
    private static int Baseline(Dictionary<string, List<double>> samples, string baselineFile, string history,
                                string machine, string commit, bool dirty)
    {
        Dictionary<string, Standing> figures = samples.ToDictionary(
            pair => pair.Key,
            pair => new Standing(Gate.Median(pair.Value), Gate.Threshold(pair.Value), pair.Value),
            StringComparer.Ordinal);

        Directory.CreateDirectory(Path.GetDirectoryName(baselineFile)!);
        File.WriteAllText(baselineFile,
                          JsonSerializer.Serialize(new BaselineFile(machine, commit, DateTimeOffset.Now, dirty, figures), Written));

        Console.WriteLine();

        foreach (Figure figure in Gate.Figures)
        {
            Standing taken = figures[figure.Name];

            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  {figure.Name,-8} {Gate.Show(taken.Median, figure),12}   may worsen by {taken.Threshold:P1}, from {taken.Samples.Count} samples"));
        }

        Record(history, machine, commit, dirty, figures.ToDictionary(pair => pair.Key, pair => pair.Value.Median), "baseline");

        Console.WriteLine();
        Console.WriteLine($"gate: baseline written to {baselineFile}. Commit it: it is what the next check is read against.");

        return Held;
    }

    /// <summary>Judges this tree against the baseline, prints why, records the run, and says how it went.</summary>
    private static int Check(Dictionary<string, List<double>> samples, BaselineFile standing, string root,
                             string history, string machine, string commit, bool dirty)
    {
        string[] unjudged = [.. Gate.Figures.Select(figure => figure.Name).Where(name => !standing.Figures.ContainsKey(name))];

        if (unjudged.Length > 0)
        {
            throw new GateRefusal($"the baseline for {machine} has no standing for {string.Join(", ", unjudged)}, "
                                  + "so those would pass unjudged. Take a new baseline.");
        }

        Dictionary<string, double> now = samples.ToDictionary(pair => pair.Key, pair => Gate.Median(pair.Value), StringComparer.Ordinal);

        IReadOnlyList<Move> moves = Gate.Moves(Git(root, "log", "--format=%H%x00%B%x01", $"{standing.Commit}..HEAD"));
        IReadOnlyList<Judgement> judged = Gate.Judge(standing.Figures, now, moves);

        Console.WriteLine();
        Console.WriteLine($"  {"figure",-8} {"baseline",12} {"now",12} {"worse by",9} {"allowed",8}   verdict");

        foreach (Judgement judgement in judged)
        {
            string verdict = judgement.Verdict switch
            {
                Verdict.Accepted => $"accepted: {judgement.Because!.Commit[..7]} says {judgement.Because.Why}",
                Verdict.Better => "better - worth a new baseline",
                Verdict.Worse => "WORSE",
                _ => "held",
            };

            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  {judgement.Figure.Name,-8} {Gate.Show(judgement.Was, judgement.Figure),12} {Gate.Show(judgement.Now, judgement.Figure),12} {judgement.Worse,9:P1} {judgement.Threshold,8:P1}   {verdict}"));
        }

        Judgement[] worse = [.. judged.Where(judgement => judgement.Verdict == Verdict.Worse)];
        Judgement[] accepted = [.. judged.Where(judgement => judgement.Verdict == Verdict.Accepted)];

        string summary = "held";

        if (worse.Length > 0)
        {
            summary = "worse: " + string.Join(", ", worse.Select(judgement => judgement.Figure.Name));
        }
        else if (accepted.Length > 0)
        {
            summary = "accepted: " + string.Join(", ", accepted.Select(judgement => $"{judgement.Figure.Name} ({judgement.Because!.Commit[..7]})"));
        }

        Record(history, machine, commit, dirty, now, summary);

        Console.WriteLine();

        if (worse.Length > 0)
        {
            Console.WriteLine($"gate: FAILED - {summary}. A trade that was meant is said in the commit that made it, as a "
                              + "trailer: Performance-Moved: <figure> - <what it bought>.");

            return Worse;
        }

        if (accepted.Length > 0)
        {
            Console.WriteLine($"gate: {summary}. Take a new baseline at this commit before the next release: until one is "
                              + "taken, that trailer would go on excusing the figure by any amount.");

            return Accepted;
        }

        Console.WriteLine($"gate: {summary}");

        return Held;
    }

    /// <summary>Every figure's samples: the replay harness run so many times, and the client started so many.</summary>
    private static Dictionary<string, List<double>> Sample(string root, string client, int replays, int starts)
    {
        string replay = Path.Combine(root, "benchmarks", "Quickshell.Replay", "bin", "Release", "net10.0-windows", "Quickshell.Replay.exe");
        string startup = Path.Combine(root, "tools", "Quickshell.Startup", "bin", "Release", "net10.0-windows", "Quickshell.Startup.exe");

        List<double> parse = [];
        List<double> emulate = [];

        for (int run = 1; run <= replays; run++)
        {
            Say($"replaying cat-log, {run} of {replays}");

            using JsonDocument read = Measured(root, replay, "--only", "cat-log:parse,cat-log:emulate");

            foreach (JsonElement arm in read.RootElement.EnumerateArray())
            {
                double rate = arm.GetProperty("megabytesPerSecond").GetDouble();

                (arm.GetProperty("consumer").GetString() == "parse" ? parse : emulate).Add(rate);
            }
        }

        Say($"starting the client {starts} times");

        using JsonDocument timed = Measured(root, startup, "--launch", client,
                                           "--runs", starts.ToString(CultureInfo.InvariantCulture), "--label", "gate");

        // The first start is the nearest thing to a cold one this desk offers, and apart from the rest
        // for that reason: the figure gated is the warm one.
        List<double> start = [.. timed.RootElement.GetProperty("starts").EnumerateArray().Skip(1)
                                      .Select(one => one.GetProperty("interactive").GetDouble())];

        if (parse.Count != replays || emulate.Count != replays || start.Count < 2)
        {
            throw new GateRefusal("a measurement came back short, so there is nothing whole to judge");
        }

        return new(StringComparer.Ordinal) { ["parse"] = parse, ["emulate"] = emulate, ["start"] = start };
    }

    /// <summary>
    /// Runs a harness with <c>--json</c> pointed at a file of its own, and reads what it wrote.
    ///
    /// <para>A fresh file every time, and read and removed patiently: a file that has just appeared is
    /// one a virus scanner opens at once, which refuses the first read of it — the startup harness met
    /// exactly that, and the gate reads what that harness and the replay write.</para>
    /// </summary>
    private static JsonDocument Measured(string root, string harness, params string[] arguments)
    {
        string scratch = Path.Combine(Path.GetTempPath(), $"quickshell-gate-{Guid.NewGuid():N}.json");

        try
        {
            Exec(root, harness, [.. arguments, "--json", scratch]);

            return JsonDocument.Parse(Patiently(() => File.ReadAllText(scratch)));
        }
        finally
        {
            try
            {
                Patiently(() =>
                {
                    File.Delete(scratch);

                    return true;
                });
            }
            catch (IOException)
            {
                // Left in the temporary folder, which is not a reason to lose a measurement.
            }
        }
    }

    /// <summary>Tries something for five seconds while another process has the file it needs open.</summary>
    private static T Patiently<T>(Func<T> attempt)
    {
        Stopwatch waited = Stopwatch.StartNew();

        while (true)
        {
            try
            {
                return attempt();
            }
            catch (IOException) when (waited.Elapsed < TimeSpan.FromSeconds(5))
            {
                Thread.Sleep(20);
            }
        }
    }

    /// <summary>
    /// A client published the way release.cmd publishes one, so the start the gate times is the start
    /// of the thing people download.
    /// </summary>
    private static string Publish(string root)
    {
        string output = Path.Combine(root, "artifacts", "gate", "quickshell");

        if (Directory.Exists(output))
        {
            Directory.Delete(output, recursive: true);
        }

        Say("publishing the client the way a release does");
        Exec(root, "dotnet", "publish", Path.Combine("src", "Quickshell.App", "Quickshell.App.csproj"), "--configuration", "Release",
             "--runtime", "win-x64", "--self-contained", "true", "-p:PublishReadyToRun=true", "--output", output,
             "--nologo", "-v", "quiet");

        return Path.Combine(output, "quickshell.exe");
    }

    /// <summary>One row of the history, which is the only place drift shows.</summary>
    private static void Record(string history, string machine, string commit, bool dirty,
                               Dictionary<string, double> figures, string verdict)
    {
        if (!File.Exists(history))
        {
            File.WriteAllText(history,
                $"""
                # Performance gate - {machine}

                Every judged run of the gate on this machine, oldest first: `run-perf-gate.cmd` by hand, and
                `release.cmd` before it makes an archive. The figures are that run's medians; a baseline row
                is where the thresholds in `benchmarks/gate/{machine}.json` were taken. A commit marked `+`
                was measured with uncommitted changes on top of it, the gate's own files aside.

                | when | commit | parse MB/s | emulate MB/s | start ms | verdict |
                |---|---|---|---|---|---|

                """);
        }

        File.AppendAllText(history, string.Create(CultureInfo.InvariantCulture,
            $"| {DateTime.Now:yyyy-MM-dd HH:mm} | {commit[..7]}{(dirty ? "+" : string.Empty)} | {figures["parse"]:0} | {figures["emulate"]:0.0} | {figures["start"]:0} | {verdict} |{Environment.NewLine}"));
    }

    private static string Git(string root, params string[] arguments) => Exec(root, "git", arguments);

    /// <summary>Runs a program to the end and returns what it printed; one that fails is a refusal.</summary>
    private static string Exec(string root, string program, params string[] arguments)
    {
        (int exit, string output, string error) = Started(root, program, arguments);

        if (exit != 0)
        {
            throw new GateRefusal($"{Path.GetFileName(program)} {string.Join(' ', arguments)} exited {exit}: "
                                  + $"{error.Trim()} {output.Trim()}".Trim());
        }

        return output;
    }

    /// <summary>Runs a program to the end and returns only how it exited, for a question its exit code answers.</summary>
    private static int ExitOf(string root, string program, params string[] arguments) =>
        Started(root, program, arguments).Exit;

    private static (int Exit, string Output, string Error) Started(string root, string program, string[] arguments)
    {
        ProcessStartInfo start = new(program)
        {
            WorkingDirectory = root,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(start) ?? throw new GateRefusal($"{program} did not start");

        Task<string> error = process.StandardError.ReadToEndAsync();
        string output = process.StandardOutput.ReadToEnd();

        process.WaitForExit();

        return (process.ExitCode, output, error.GetAwaiter().GetResult());
    }

    private static void Say(string doing) => Console.WriteLine($"  {doing}");

    /// <summary>
    /// The repository this runs in, found by its solution.
    ///
    /// <para>From where this program is before where it was started from: <c>dotnet run</c> keeps the
    /// caller's working directory, and a release script run from one checkout while the shell stood in
    /// another would otherwise build, judge and record against the wrong tree.</para>
    /// </summary>
    private static string Root()
    {
        foreach (string start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            for (DirectoryInfo? directory = new(start); directory is not null; directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(directory.FullName, "Quickshell.sln")))
                {
                    return directory.FullName;
                }
            }
        }

        throw new GateRefusal("this is not inside the quickshell repository, so there is nothing to build or record");
    }

    private static string? Argument(string[] arguments, string flag)
    {
        int at = Array.IndexOf(arguments, flag);

        return at >= 0 && at + 1 < arguments.Length ? arguments[at + 1] : null;
    }
}

/// <summary>What a baseline file holds: whose desk, which commit, and every figure's standing.</summary>
/// <param name="Machine">The machine it was taken on, the only one it is read on.</param>
/// <param name="Commit">The commit it was taken at, which is where a check starts reading trailers.</param>
/// <param name="Taken">When.</param>
/// <param name="Dirty">Whether the tree had uncommitted changes, said rather than hidden.</param>
/// <param name="Figures">Each figure's median, threshold and samples.</param>
public sealed record BaselineFile(string Machine, string Commit, DateTimeOffset Taken, bool Dirty,
                                  Dictionary<string, Standing> Figures);

/// <summary>A run that could not judge anything, and the sentence that says why.</summary>
public sealed class GateRefusal : Exception
{
    /// <summary>A refusal saying why.</summary>
    public GateRefusal(string message)
        : base(message)
    {
    }

    /// <summary>A refusal saying why, with what caused it.</summary>
    public GateRefusal(string message, Exception inner)
        : base(message, inner)
    {
    }

    /// <summary>A refusal with no sentence, which the analyzers ask every exception to have.</summary>
    public GateRefusal()
    {
    }
}
