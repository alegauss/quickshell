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

IStreamConsumer[] consumers = [new EscapeScanConsumer()];

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
report.AppendLine("## The arms that are empty");
report.AppendLine();
report.AppendLine("Each stream is meant to be replayed twice: headless, which measures the parser alone, and");
report.AppendLine("through the whole pipeline with a renderer, which measures what coalescing saves. Neither");
report.AppendLine("consumer exists yet - there is no parser and no renderer - so the table above has one arm,");
report.AppendLine("and it is the floor rather than either of them. A number here is a ceiling: whatever the");
report.AppendLine("parser costs is on top of touching the bytes at all.");

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
