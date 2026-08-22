using BenchmarkDotNet.Attributes;
using Quickshell.Replay;

namespace Quickshell.Benchmarks;

/// <summary>
/// The microbenchmarks, on the corpus and not on generated input.
///
/// What is measured here today is the floor: scanning captured bytes for the one thing a parser
/// cannot avoid noticing. That is worth having before the parser exists, because it is the ceiling
/// the parser will be read against - a parser at 300 MB/s on a stream whose floor is 1500 MB/s has
/// spent four fifths of the budget on itself, and without this number nobody could say that.
///
/// Allocation is a first-class column here, never a footnote: a run that got faster while
/// allocating more has not got faster, it has borrowed from a collection that will happen during
/// somebody's vim session.
/// </summary>
[MemoryDiagnoser]
public class ByteScanBenchmarks
{
    private readonly Dictionary<string, byte[]> _streams = [];

    [Params("cat-log", "vim-scroll", "ls-color-r", "htop", "tmux-resize", "dmesg")]
    public string Stream { get; set; } = "cat-log";

    [GlobalSetup]
    public void Load()
    {
        foreach (Corpus corpus in Corpus.Load(Corpus.Find()))
        {
            _streams[corpus.Name] = corpus.Bytes;
        }
    }

    [Benchmark]
    public long CountEscapes()
    {
        byte[] bytes = _streams[Stream];
        long escapes = 0;

        foreach (byte value in bytes)
        {
            if (value == 0x1B)
            {
                escapes++;
            }
        }

        return escapes;
    }
}
