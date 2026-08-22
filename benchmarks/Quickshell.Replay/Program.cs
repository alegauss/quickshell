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
IStreamConsumer[] consumers = [new EscapeScanConsumer(), new ParseConsumer(), renderConsumer];

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
report.AppendLine("Each stream is replayed through three consumers. `escape-scan` is the floor: it touches every");
report.AppendLine("byte and does the cheapest thing a parser must also do, so no parser can beat it. `parse` is the");
report.AppendLine("Williams table with a handler that only counts. `render` is the whole path - parse, decode,");
report.AppendLine("segment into grapheme clusters, resolve each through the glyph atlas, fill instances, and one");
report.AppendLine("draw call per 16 KB of stream.");
report.AppendLine();
report.AppendLine("**The gap between `parse` and `render` is the figure this harness exists for.** Today it is");
report.AppendLine("dominated by allocation rather than by drawing: the render arm allocates tens of megabytes per");
report.AppendLine("megabyte of stream, and the cause is `GraphemeSegmenter.Feed` returning a `List<string>` - one");
report.AppendLine("string per cluster, which for a screen of text is one per character.");
report.AppendLine();
report.AppendLine("**Read the allocation column against `escape-scan`, not against zero.** That consumer allocates");
report.AppendLine("nothing whatsoever, so whatever it reports is the harness's own fixed cost divided by the size");
report.AppendLine("of the stream - which is why a 0.02 MB stream shows tens of KB per MB and the 32 MB one shows");
report.AppendLine("zero. `parse` reports the same figure as `escape-scan` on every stream, to the decimal, which");
report.AppendLine("is what says the parser itself allocates nothing. `render` does not.");
report.AppendLine();
report.AppendLine("The render arm also stands in for a terminal buffer that does not exist yet: cursor, wrap,");
report.AppendLine("carriage return, line feed and erase-display, and nothing else. What it measures is the volume");
report.AppendLine("of glyph and instance work a stream implies, which is the part a real buffer would not change.");
report.AppendLine("It never presents - a vsync-locked present would cap the replay at the display's refresh rate");
report.AppendLine("rather than measure the renderer.");

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
