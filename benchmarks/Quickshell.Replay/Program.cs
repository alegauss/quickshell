using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Quickshell.Replay;

// Replays every captured stream through every consumer that exists, and writes the numbers to a
// file the repository keeps, so two runs months apart on the same machine are comparable.
//
// It is not BenchmarkDotNet and deliberately not: a one-shot stream of this size does not fit that
// iteration model. The microbenchmarks live next door in Quickshell.Benchmarks.
//
//   Quickshell.Replay [corpus] [--only cat-log:parse,cat-log:emulate] [--json <file>]
//
// `--only` replays just the pairs named, and then the results file is left alone: a table of two
// rows written over the table of thirty-six would be a report that shrank because somebody asked a
// narrower question. `--json` writes what was measured where a program can read it, which is how
// the performance gate (QS79) reads it.

string? only = null;
string? json = null;
string? corpusArgument = null;

// Every argument accounted for. A flag this harness does not know, or one given no value, is refused
// rather than ignored: ignored, it would fall through to a full run and write over the results file
// that `--only` exists to leave alone.
for (int at = 0; at < args.Length; at++)
{
    switch (args[at])
    {
        case "--only" when at + 1 < args.Length:
            only = args[++at];
            break;

        case "--json" when at + 1 < args.Length:
            json = args[++at];
            break;

        case string flag when flag.StartsWith("--", StringComparison.Ordinal):
            Console.Error.WriteLine($"{flag} is not a flag this harness takes, or it was given no value. "
                                    + "It takes a corpus folder, --only <stream:consumer,...> and --json <file>.");
            return 2;

        case string folder when corpusArgument is null:
            corpusArgument = folder;
            break;

        default:
            Console.Error.WriteLine($"two corpus folders were given, {corpusArgument} and {args[at]}; a run replays one.");
            return 2;
    }
}

string corpusDirectory = corpusArgument ?? Corpus.Find();
IReadOnlyList<Corpus> streams = Corpus.Load(corpusDirectory);

if (streams.Count == 0)
{
    Console.Error.WriteLine($"no corpus in {corpusDirectory}");
    return 1;
}

HashSet<string>? wanted = only is null
    ? null
    : new HashSet<string>(only.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                          StringComparer.Ordinal);

// Built only where it is asked for: it opens a device and a window, which a question about the
// parser has no business paying for.
using RenderConsumer? renderConsumer =
    wanted is null || wanted.Any(pair => pair.EndsWith(":render", StringComparison.Ordinal))
        ? new RenderConsumer()
        : null;

// In order of how much of a terminal each one is: the floor, the state machine, the real terminal,
// and the glyph work. `emulate` sits between parse and render on purpose - it is what a session
// costs, and until QS141 nothing here measured it.
List<IStreamConsumer> consumers =
[
    new EscapeScanConsumer(), new ParseConsumer(), new DecodeConsumer(), new SegmentConsumer(),
    new EmulateConsumer(),
];

if (renderConsumer is not null)
{
    consumers.Add(renderConsumer);
}

List<Dictionary<string, object>> measured = [];

const int ChunkSize = 64 * 1024;
const int Warmups = 1;
const int Runs = 5;

List<string> notes = [];

StringBuilder report = new();
report.AppendLine("# Replay results");
report.AppendLine();
report.AppendLine(CultureInfo.InvariantCulture,
    $"Captured streams replayed through every consumer that exists. Run on {Environment.MachineName}, " +
    $".NET {Environment.Version}, {Environment.ProcessorCount} logical cores, " +
    $"{DateTimeOffset.Now:yyyy-MM-dd}. Best of {Runs} after {Warmups} warmup, {ChunkSize / 1024} KB chunks.");
report.AppendLine();
report.AppendLine("| stream | MB | consumer | MB/s | alloc KB/MB | gen0 |");
report.AppendLine("|---|---|---|---|---|---|");

foreach (Corpus stream in streams)
{
    foreach (IStreamConsumer consumer in consumers)
    {
        if (wanted is not null && !wanted.Contains($"{stream.Name}:{consumer.Name}"))
        {
            continue;
        }

        double best = 0;
        long bestAllocated = 0;
        int bestGen0 = 0;

        for (int run = 0; run < Warmups + Runs; run++)
        {
            consumer.Reset();

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            int gen0Before = GC.CollectionCount(0);
            long allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
            Stopwatch clock = Stopwatch.StartNew();

            for (int offset = 0; offset < stream.Bytes.Length; offset += ChunkSize)
            {
                int length = Math.Min(ChunkSize, stream.Bytes.Length - offset);
                consumer.Feed(stream.Bytes.AsSpan(offset, length));
            }

            clock.Stop();
            long allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
            int gen0 = GC.CollectionCount(0) - gen0Before;

            // Read the result so the loop above cannot be optimised away.
            if (consumer.Result < 0)
            {
                throw new InvalidOperationException("unreachable");
            }

            if (run < Warmups)
            {
                continue;
            }

            double megabytesPerSecond = stream.Megabytes / clock.Elapsed.TotalSeconds;

            if (megabytesPerSecond > best)
            {
                best = megabytesPerSecond;
                bestAllocated = allocated;
                bestGen0 = gen0;
            }
        }

        report.AppendLine(CultureInfo.InvariantCulture,
            $"| `{stream.Name}` | {stream.Megabytes:F2} | {consumer.Name} | {best:F0} | " +
            $"{bestAllocated / Math.Max(0.001, stream.Megabytes) / 1024.0:F1} | {bestGen0} |");

        measured.Add(new Dictionary<string, object>
        {
            ["stream"] = stream.Name,
            ["consumer"] = consumer.Name,
            ["megabytesPerSecond"] = best,
            ["allocatedKilobytesPerMegabyte"] = bestAllocated / Math.Max(0.001, stream.Megabytes) / 1024.0,
            ["gen0"] = bestGen0,
        });

        Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
            "{0,-16} {1,8:F2} MB  {2,-14} {3,8:F0} MB/s  {4,7:F1} KB/MB",
            stream.Name, stream.Megabytes, consumer.Name, best,
            bestAllocated / Math.Max(0.001, stream.Megabytes) / 1024.0));

        // What the arm noticed about its own subsystem, where it has one to notice with. Read from
        // the last run rather than the fastest, which is the same shape of work either way.
        if (consumer.Note is { Length: > 0 } note)
        {
            notes.Add(string.Create(CultureInfo.InvariantCulture,
                                    $"| `{stream.Name}` | `{consumer.Name}` | {note} |"));

            Console.WriteLine($"{string.Empty,-16} {string.Empty,8}     {string.Empty,-14} {note}");
        }
    }
}

if (notes.Count > 0)
{
    report.AppendLine();
    report.AppendLine("## What the arms noticed");
    report.AppendLine();
    report.AppendLine("Counters read off the subsystem itself after the run, which a throughput");
    report.AppendLine("figure cannot report. `cells moved by scrolling` against the cells a stream");
    report.AppendLine("printed is what says whether the terminal's cost is writing or shifting.");
    report.AppendLine();
    report.AppendLine("| stream | consumer | what it counted |");
    report.AppendLine("|---|---|---|");

    foreach (string note in notes)
    {
        report.AppendLine(note);
    }
}

report.AppendLine();
report.AppendLine("## Reading these numbers");
report.AppendLine();
report.AppendLine("Each stream is replayed through six consumers, in order of how much of a terminal each one is,");
report.AppendLine("so that consecutive arms differ by one stage. `escape-scan` is the floor: it touches every byte");
report.AppendLine("and does the cheapest thing a parser must also do, so no parser can beat it. `parse` is the");
report.AppendLine("Williams table with a handler that only counts. `decode` adds UTF-8 decoding. `segment` adds");
report.AppendLine("grapheme clustering - everything done to printed text short of writing a cell. `emulate` is the");
report.AppendLine("real `Emulator` - cells, scrollback, reflow, every sequence it implements - which is the call a");
report.AppendLine("session makes for every byte a host sends. `render` is the glyph path: atlas lookups, instances,");
report.AppendLine("and one draw call per 16 KB of stream.");
report.AppendLine();
report.AppendLine("**Read figure 2 of the budget against `parse` and nothing else.** It asks for 400 MB/s of");
report.AppendLine("sustained parse throughput, and `parse` is the arm it governs - the state machine, with a handler");
report.AppendLine("that builds nothing. `emulate` is one to two orders of magnitude below it, and `emulate` is what");
report.AppendLine("a session costs. Until QS141 nothing here measured that at all, so the figure could be met while");
report.AppendLine("the thing it is about was slow. It also only meets 400 on the 32 MB stream: the smaller captures");
report.AppendLine("are dominated by fixed cost, which the allocation note below applies to throughput too.");
report.AppendLine();
report.AppendLine("**The gap between `parse` and `render` is the figure this harness exists for.** It used to be");
report.AppendLine("dominated by allocation rather than by drawing: the render arm allocated tens of megabytes per");
report.AppendLine("megabyte of stream, because `GraphemeSegmenter` handed back a `List<string>` - one string per");
report.AppendLine("cluster, which for a screen of text is one per character. QS24 replaced that with spans into a");
report.AppendLine("reused buffer, so what is left in the gap is glyph and instance work.");
report.AppendLine();
report.AppendLine("**Read the allocation column against `escape-scan`, not against zero.** That consumer allocates");
report.AppendLine("nothing whatsoever, so whatever it reports is the harness's own fixed cost divided by the size");
report.AppendLine("of the stream - which is why a 0.02 MB stream shows tens of KB per MB and the 32 MB one shows");
report.AppendLine("zero. `parse` reports the same figure as `escape-scan` on every stream, to the decimal, which");
report.AppendLine("is what says the parser itself allocates nothing.");
report.AppendLine();
report.AppendLine("**Since QS94, `render` reports that same figure too.** The 32 MB replay used to allocate");
report.AppendLine("54,227 KB per MB and take 102 gen-0 collections; it now allocates at the floor and takes none.");
report.AppendLine("Throughput did not move, and was never the point: allocation on this path bought a collection");
report.AppendLine("pause during somebody's `vim` session, not megabytes per second.");
report.AppendLine();
report.AppendLine("The render arm keeps a stub of a buffer rather than the real one - cursor, wrap, carriage return,");
report.AppendLine("line feed and erase-display, and nothing else. That was once because no terminal buffer existed;");
report.AppendLine("now one does, and the stub is kept on purpose so this arm measures the volume of glyph and");
report.AppendLine("instance work a stream implies without the emulator's cost folded in. `emulate` is where the");
report.AppendLine("real buffer is measured. It never presents - a vsync-locked present would cap the replay at the");
report.AppendLine("display's refresh rate rather than measure the renderer.");
report.AppendLine();
report.AppendLine("**Where the hundredfold goes.** Read the arms as a ladder and convert to time per megabyte,");
report.AppendLine("on the 32 MB stream where fixed cost is noise. Each rung adds one stage of what `Emulator.Feed`");
report.AppendLine("does, so the difference between two rungs is that stage's cost and nothing else:");
report.AppendLine();
report.AppendLine("| rung | ms/MB | added by this stage |");
report.AppendLine("|---|---|---|");
report.AppendLine("| `escape-scan` | 0.34 | the floor - touching every byte |");
report.AppendLine("| `parse` | 0.85 | +0.51, the state machine |");
report.AppendLine("| `decode` | 1.91 | +1.06, UTF-8 decoding |");
report.AppendLine("| `segment` | 16.4 | **+14.5, grapheme clustering** |");
report.AppendLine("| `emulate` | 76.9 | **+60.5, writing cells** |");
report.AppendLine("| `render` | 200 | +123, glyph and instance work |");
report.AppendLine();
report.AppendLine("It is not one thing. Grapheme clustering costs about nine times what reaches it, and writing");
report.AppendLine("cells about five times again; multiplied, that is the hundredfold. Cell writing is the larger");
report.AppendLine("absolute cost and clustering the larger multiplier, so either is worth attacking and neither");
report.AppendLine("alone is the answer.");
report.AppendLine();
report.AppendLine("**Allocation above the floor starts at `decode`, not at the terminal.** Block C asks the parse");
report.AppendLine("path to allocate zero in steady state; `parse` reports the floor, `decode` 1.9 KB/MB, `segment`");
report.AppendLine("3.9, and `emulate` adds none of its own. So the cells are free and the text handling is not,");
report.AppendLine("which is the opposite of where one would look first.");

foreach (IStreamConsumer consumer in consumers)
{
    report.AppendLine();
    report.AppendLine(CultureInfo.InvariantCulture, $"- `{consumer.Name}` - {consumer.What}");
}

if (json is not null)
{
    File.WriteAllText(json, JsonSerializer.Serialize(measured));
}

if (wanted is not null)
{
    // Every pair asked for, or a refusal naming the ones that were not there: a list with a misspelt
    // pair in it is a question half answered, and exiting 0 on it would pass the half off as whole.
    string[] missed = [.. wanted.Where(pair => !measured.Any(one => $"{one["stream"]}:{one["consumer"]}" == pair))
                                .Order(StringComparer.Ordinal)];

    if (missed.Length > 0)
    {
        Console.Error.WriteLine($"--only named what this corpus and these consumers do not have: {string.Join(", ", missed)}");
        return 1;
    }

    return 0;
}

string resultsDirectory = Path.Combine(Path.GetDirectoryName(corpusDirectory)!, "..", "results");
Directory.CreateDirectory(resultsDirectory);
string resultsPath = Path.GetFullPath(Path.Combine(resultsDirectory, $"replay-{Environment.MachineName.ToLowerInvariant()}.md"));

File.WriteAllText(resultsPath, report.ToString());
Console.WriteLine($"\n-> {resultsPath}");
return 0;
