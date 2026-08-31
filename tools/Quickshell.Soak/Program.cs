using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Quickshell.Soak;

/// <summary>
/// Seventy-two hours, and what is watched over them.
///
/// <para><b>A terminal client stays open for weeks, so every defect that scales with time is
/// invisible to every test that has to finish.</b> This runs the arrangement the design asks for —
/// twenty sessions in six roles against the docker fixture — and watches the counters that would
/// rise.</para>
///
/// <para><b>The verdict is a slope, not a bound.</b> A counter that stays under a limit for three
/// days while rising is a leak that reaches the limit in three weeks, and three weeks is ordinary
/// uptime here. <see cref="Trend"/> is where that reasoning lives.</para>
///
/// <para><b>The scrollback ring is the deliberate counter-example.</b> It is supposed to grow to its
/// configured capacity and then stop, so it is watched like the others and its stopping is part of
/// the result rather than an exception to it.</para>
/// </summary>
public static class Soak
{
    private static readonly TimeSpan Sample = TimeSpan.FromSeconds(30);

    /// <summary>Runs the soak and prints the report.</summary>
    public static async Task<int> Main(string[] arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        if (Argument(arguments, "--help") is not null)
        {
            Console.WriteLine(
                """
                Quickshell.Soak — what days of uptime cost.

                  --hours <n>       how long to run (default 72)
                  --sessions <n>    how many sessions (default 20)
                  --warmup <min>    minutes to discard before the trend starts (default 10)
                  --host/--port/--user/--key   the fixture (defaults: 127.0.0.1 2222 probe)
                  --no-parse        read host output and drop it, so a counter that rises anyway
                                    belongs to the transport rather than to the parser
                  --raw-read        read the channel directly instead of through SessionPipeline,
                                    which is the arrangement no real client uses (QS139)
                  --out <file>      write the report here as well as printing it

                Needs the fixture up: prototypes/SshProbe/fixture/up.sh
                """);

            return 0;
        }

        double hours = Number(arguments, "--hours", 72);
        int count = (int)Number(arguments, "--sessions", 20);
        double warmup = Number(arguments, "--warmup", 10);
        string host = Argument(arguments, "--host") is { Length: > 0 } named ? named : "127.0.0.1";
        int port = (int)Number(arguments, "--port", 2222);
        string user = Argument(arguments, "--user") is { Length: > 0 } who ? who : "probe";
        string key = Argument(arguments, "--key") is { Length: > 0 } path ? path : Fixture();

        if (!File.Exists(key))
        {
            await Console.Error.WriteLineAsync(
                $"no key at {key} — run prototypes/SshProbe/fixture/up.sh").ConfigureAwait(false);

            return 2;
        }

        // Refused before a single session is started, because a soak against nothing is a soak that
        // reports flat counters for three days and proves that the process was idle. The first run
        // of this tool did exactly that: the docker fixture had stopped, and 824 connection failures
        // were reported as "swallowed" beside memory figures nobody should have read.
        if (!await ListeningAsync(host, port).ConfigureAwait(false))
        {
            await Console.Error.WriteLineAsync(
                $"nothing is listening on {host}:{port.ToString(CultureInfo.InvariantCulture)} — "
                + "run prototypes/SshProbe/fixture/up.sh, and note that Docker Desktop's resource "
                + "saver stops these containers on its own").ConfigureAwait(false);

            return 3;
        }

        // Off isolates a layer: the bytes still cross the network and the channel and nothing above
        // the transport sees them, so a counter that rises either way is not the parser's.
        bool parse = Argument(arguments, "--no-parse") is null;

        // The pipeline is how a real client reads: a bounded queue, and a reader that waits. Off
        // reads the channel directly, which is what this harness did when it produced the numbers
        // QS139 was first filed on — kept so the two arrangements can be compared rather than
        // argued about.
        bool pipeline = Argument(arguments, "--raw-read") is null;

        List<Soaked> sessions = [.. Enumerable.Range(0, count)
                                              .Select(at => new Soaked(Roles(at), at, host, port,
                                                                       user, key, parse, pipeline))];

        foreach (Soaked session in sessions)
        {
            session.Start();

            // Staggered, so twenty simultaneous handshakes are not what the first sample measures.
            await Task.Delay(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);
        }

        string report = await WatchAsync(sessions, hours, warmup).ConfigureAwait(false);

        Console.WriteLine(report);

        if (Argument(arguments, "--out") is { Length: > 0 } into)
        {
            await File.WriteAllTextAsync(into, report).ConfigureAwait(false);
        }

        foreach (Soaked session in sessions)
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }

        // Non-zero for a rising counter, and also for a run too short to have an opinion: a soak
        // that exits 0 without judging anything is a green nobody should be able to bank.
        return report.Contains("**RISING**", StringComparison.Ordinal)
               || report.Contains("Too short to judge", StringComparison.Ordinal)
               || report.Contains("Nothing connected", StringComparison.Ordinal)
            ? 1
            : 0;
    }

    /// <summary>
    /// The roles, spread over the sessions so every one of them is represented.
    ///
    /// <para>Idle is the majority because that is what twenty open sessions mostly are, and because
    /// an idle session is the one a leak is least excused in.</para>
    /// </summary>
    private static Role Roles(int at) =>
        (at % 10) switch
        {
            0 => Role.Printing,
            1 => Role.Churning,
            2 => Role.Forwarding,
            3 => Role.Flapping,
            4 => Role.FullScreen,
            _ => Role.Idle,
        };

    private static async Task<string> WatchAsync(List<Soaked> sessions, double hours, double warmup)
    {
        Process self = Process.GetCurrentProcess();

        Trend working = new("private memory (MB)", "MB", tolerance: 25);
        Trend managed = new("managed heap (MB)", "MB", tolerance: 15);
        Trend handles = new("handles", "handles", tolerance: 200);
        Trend threads = new("threads", "threads", tolerance: 10);
        Trend gen2 = new("gen2 heap (MB)", "MB", tolerance: 15);

        // The large and pinned object heaps, because a gen2 of half a megabyte beside a total heap of
        // a gigabyte says the memory is somewhere this was not looking. Anything 85 KB or over lands
        // in the large one, and a pinned buffer handed to native code lands in the other — which for
        // a client whose whole job is moving buffers is exactly where to look first.
        Trend loh = new("large object heap (MB)", "MB", tolerance: 15);
        Trend poh = new("pinned object heap (MB)", "MB", tolerance: 15);

        Trend scrollback = new("scrollback lines (one model)", "lines", tolerance: 0);

        Trend[] watched = [working, managed, handles, threads, gen2, loh, poh, scrollback];

        Stopwatch clock = Stopwatch.StartNew();
        TimeSpan warm = TimeSpan.FromMinutes(warmup);
        TimeSpan whole = TimeSpan.FromHours(hours);
        int discarded = 0;

        await Console.Error.WriteLineAsync(
            $"soaking {sessions.Count.ToString(CultureInfo.InvariantCulture)} sessions for "
            + $"{hours.ToString("F2", CultureInfo.InvariantCulture)}h, "
            + $"discarding the first {warmup.ToString("F0", CultureInfo.InvariantCulture)} min")
            .ConfigureAwait(false);

        while (clock.Elapsed < whole)
        {
            await Task.Delay(Sample).ConfigureAwait(false);

            self.Refresh();

            GCMemoryInfo gc = GC.GetGCMemoryInfo();

            if (clock.Elapsed < warm)
            {
                // Caches filling and code being jitted is a rise that is not a leak. Counting it
                // would fail every run and teach everybody to ignore the verdict.
                discarded++;

                continue;
            }

            TimeSpan since = clock.Elapsed - warm;

            working.Took(since, self.PrivateMemorySize64 / (1024.0 * 1024.0));
            managed.Took(since, GC.GetTotalMemory(forceFullCollection: false) / (1024.0 * 1024.0));
            handles.Took(since, self.HandleCount);
            threads.Took(since, self.Threads.Count);
            gen2.Took(since, Generation(gc, 2));
            loh.Took(since, Generation(gc, 3));
            poh.Took(since, Generation(gc, 4));

            // The counter-example: it is meant to reach capacity and stop. A tolerance of zero says
            // so, and a ring that kept growing would be the one failure this row can report.
            scrollback.Took(since, Deepest(sessions));

            await Console.Error.WriteLineAsync(
                $"{since.TotalMinutes.ToString("F0", CultureInfo.InvariantCulture)} min: "
                + $"{working.Last.ToString("F1", CultureInfo.InvariantCulture)} MB, "
                + $"{handles.Last.ToString("F0", CultureInfo.InvariantCulture)} handles, "
                + $"{Bytes(sessions).ToString("F1", CultureInfo.InvariantCulture)} GB parsed")
                .ConfigureAwait(false);
        }

        // One forced collection, after the measured interval and never during it: it tells garbage
        // apart from retention, which is the first question any rising heap raises and the one a
        // sampled series cannot answer. Doing it mid-run would be measuring this tool's schedule
        // instead of the client's behaviour.
        long beforeCollecting = GC.GetTotalMemory(forceFullCollection: false);
        long afterCollecting = GC.GetTotalMemory(forceFullCollection: true);

        self.Refresh();

        long privateAfter = self.PrivateMemorySize64;

        return Compose(sessions, watched, clock.Elapsed, warm, discarded,
                       beforeCollecting / (1024.0 * 1024.0), afterCollecting / (1024.0 * 1024.0),
                       privateAfter / (1024.0 * 1024.0));
    }

    /// <summary>
    /// One generation's size after the last collection, in megabytes, or zero where this runtime
    /// does not report it.
    /// </summary>
    private static double Generation(GCMemoryInfo gc, int which) =>
        gc.GenerationInfo.Length > which
            ? gc.GenerationInfo[which].SizeAfterBytes / (1024.0 * 1024.0)
            : 0;

    /// <summary>The deepest scrollback any model has reached.</summary>
    private static double Deepest(List<Soaked> sessions) =>
        sessions.Select(session => session.Emulator?.Screens.Primary.ScrollbackLines ?? 0)
                .DefaultIfEmpty(0)
                .Max();

    private static double Bytes(List<Soaked> sessions) =>
        sessions.Sum(session => session.Bytes) / (1024.0 * 1024.0 * 1024.0);

    private static string Compose(List<Soaked> sessions, Trend[] watched, TimeSpan ran,
                                  TimeSpan warm, int discarded, double heapBefore, double heapAfter,
                                  double privateAfter)
    {
        StringBuilder report = new();

        report.AppendLine(CultureInfo.InvariantCulture,
                          $"# Soak — {ran.TotalHours:F2} h, {sessions.Count} sessions")
              .AppendLine()
              .AppendLine(CultureInfo.InvariantCulture,
                          $"Run {DateTimeOffset.Now:yyyy-MM-dd HH:mm} local. "
                          + $"{watched[0].Samples} samples at {Sample.TotalSeconds:F0} s, after "
                          + $"{warm.TotalMinutes:F0} min of warm-up ({discarded} samples discarded).")
              .AppendLine()
              .AppendLine(CultureInfo.InvariantCulture,
                          $"{Bytes(sessions):F2} GB of host output parsed, "
                          + $"{sessions.Sum(session => session.Connections)} connections made, "
                          + $"{sessions.Sum(session => session.Failures)} failures swallowed.")
              .AppendLine()
              .AppendLine("## What was watched")
              .AppendLine()
              .AppendLine("Flat is the criterion, not bounded: the slope is extrapolated over "
                          + "twenty-one days, which is ordinary uptime for this client.")
              .AppendLine()
              .AppendLine("| counter | first | last | peak | per hour | over 21 days | tolerance | verdict |")
              .AppendLine("|---|---|---|---|---|---|---|---|");

        foreach (Trend trend in watched)
        {
            report.AppendLine(trend.Row());
        }

        report.AppendLine()
              .AppendLine("## Garbage or retention")
              .AppendLine()
              .AppendLine("One forced full collection, after the measured interval. What survives it "
                          + "is held; what does not was the collector's schedule rather than this "
                          + "client's behaviour.")
              .AppendLine()
              .AppendLine(CultureInfo.InvariantCulture,
                          $"| managed heap before | {heapBefore:F1} MB |")
              .AppendLine("|---|---|")
              .AppendLine(CultureInfo.InvariantCulture,
                          $"| managed heap after | {heapAfter:F1} MB |")
              .AppendLine(CultureInfo.InvariantCulture,
                          $"| collected | {heapBefore - heapAfter:F1} MB |")
              .AppendLine(CultureInfo.InvariantCulture,
                          $"| private memory after | {privateAfter:F1} MB |")
              .AppendLine()
              .AppendLine("## The sessions")
              .AppendLine()
              .AppendLine("| # | role | connections | MB from host | failures | last failure |")
              .AppendLine("|---|---|---|---|---|---|");

        foreach (Soaked session in sessions)
        {
            string why = session.LastFailure.Length == 0 ? "—" : session.LastFailure;

            report.AppendLine(CultureInfo.InvariantCulture,
                              $"| {session.Number} | {session.Role} | {session.Connections} | "
                              + $"{session.Bytes / (1024.0 * 1024.0):F1} | {session.Failures} | "
                              + $"{why} |");
        }

        bool judgeable = watched.All(trend => trend.Judgeable);
        bool rising = watched.Any(trend => trend.Judgeable && !trend.Flat);
        long connections = sessions.Sum(session => session.Connections);

        report.AppendLine();

        if (connections == 0)
        {
            // Said before anything else, because every counter above is then a measurement of an
            // idle process and none of it is about this client.
            report.AppendLine("**Nothing connected.** Every counter above is what a process that "
                              + "did no work costs, and none of it says anything about this client. "
                              + "The failure reasons are in the table.");

            return report.ToString();
        }

        if (!judgeable)
        {
            string tooShort = string.Create(
                CultureInfo.InvariantCulture,
                $"**Too short to judge.** A verdict needs at least {Trend.MinimumSpan.TotalHours:F0} h of span and {Trend.MinimumSamples} samples; this run has {watched[0].Span.TotalHours:F2} h and {watched[0].Samples}. The numbers above are what they are — a slope fitted to a warm-up and extrapolated over three weeks is arithmetic, not evidence.");

            report.AppendLine(tooShort);
        }
        else
        {
            report.AppendLine(rising
                                  ? "**A watched counter is rising.** Each one is a defect with a "
                                    + "line of its own, found here rather than by a user three "
                                    + "weeks in."
                                  : "**Every watched counter is flat** over the measured interval.");
        }

        return report.ToString();
    }

    /// <summary>
    /// Whether anything answers there at all.
    ///
    /// <para>Checked once, before any session exists. The fixture stopping mid-run is a different
    /// problem and shows up as failures with a reason attached; this is the case where it was never
    /// up, which no amount of watching counters can distinguish from a healthy idle process.</para>
    /// </summary>
    private static async Task<bool> ListeningAsync(string host, int port)
    {
        try
        {
            using System.Net.Sockets.TcpClient probe = new();

            using CancellationTokenSource giveUp = new(TimeSpan.FromSeconds(3));

            await probe.ConnectAsync(host, port, giveUp.Token).ConfigureAwait(false);

            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>The fixture's key, found from wherever this was started.</summary>
    private static string Fixture()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null
               && !File.Exists(Path.Combine(directory.FullName, "Quickshell.sln")))
        {
            directory = directory.Parent;
        }

        return Path.Combine(directory?.FullName ?? ".", "prototypes", "SshProbe", "fixture", "keys",
                            "probe_ed25519");
    }

    private static string? Argument(string[] arguments, string name)
    {
        int at = Array.FindIndex(arguments,
                                 argument => string.Equals(argument, name, StringComparison.Ordinal));

        if (at < 0)
        {
            return null;
        }

        return at + 1 < arguments.Length
               && !arguments[at + 1].StartsWith("--", StringComparison.Ordinal)
            ? arguments[at + 1]
            : string.Empty;
    }

    private static double Number(string[] arguments, string name, double fallback) =>
        Argument(arguments, name) is { Length: > 0 } given
        && double.TryParse(given, CultureInfo.InvariantCulture, out double parsed)
            ? parsed
            : fallback;
}
