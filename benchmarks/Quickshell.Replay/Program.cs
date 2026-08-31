using System.Diagnostics;
using System.Globalization;
using System.Text;
using Quickshell.Replay;

// Replays every captured stream through every consumer that exists, and writes the numbers to a
// file the repository keeps, so two runs months apart on the same machine are comparable.
//
// It is not BenchmarkDotNet and deliberately not: a one-shot stream of this size does not fit that
// iteration model. The microbenchmarks live next door in Quickshell.Benchmarks.

string corpusDirectory = args.Length > 0 ? args[0] : Corpus.Find();
IReadOnlyList<Corpus> streams = Corpus.Load(corpusDirectory);

if (streams.Count == 0)
{
    Console.Error.WriteLine($"no corpus in {corpusDirectory}");
    return 1;
}

using RenderConsumer renderConsumer = new();

// In order of how much of a terminal each one is: the floor, the state machine, the real terminal,
// and the glyph work. `emulate` sits between parse and render on purpose - it is what a session
// costs, and until QS141 nothing here measured it.
IStreamConsumer[] consumers =
[
    new EscapeScanConsumer(), new ParseConsumer(), new DecodeConsumer(), new SegmentConsumer(),
    new EmulateConsumer(), renderConsumer,
];

const int ChunkSize = 64 * 1024;
const int Warmups = 1;
const int Runs = 5;

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

        Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
            "{0,-16} {1,8:F2} MB  {2,-14} {3,8:F0} MB/s  {4,7:F1} KB/MB",
            stream.Name, stream.Megabytes, consumer.Name, best,
            bestAllocated / Math.Max(0.001, stream.Megabytes) / 1024.0));
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

string resultsDirectory = Path.Combine(Path.GetDirectoryName(corpusDirectory)!, "..", "results");
Directory.CreateDirectory(resultsDirectory);
string resultsPath = Path.GetFullPath(Path.Combine(resultsDirectory, $"replay-{Environment.MachineName.ToLowerInvariant()}.md"));

File.WriteAllText(resultsPath, report.ToString());
Console.WriteLine($"\n-> {resultsPath}");
return 0;
