namespace Quickshell.Replay;

/// <summary>
/// What a replayed stream is fed into.
///
/// The design this harness comes from asks each stream to be replayed twice - headless, which
/// measures the parser alone, and through the whole pipeline with a renderer, which measures what
/// coalescing saves - and says the gap between those two numbers is the most informative figure
/// this project will produce. Neither of those consumers exists yet: there is no parser and no
/// renderer. So the seam is here, one consumer is implemented, and the results file names the
/// arms that are empty rather than quietly reporting one number as if it were the pair.
/// </summary>
public interface IStreamConsumer
{
    string Name { get; }

    /// <summary>What this consumer is measuring, in the results file's own words.</summary>
    string What { get; }

    void Reset();

    void Feed(ReadOnlySpan<byte> chunk);

    /// <summary>Something derived from the whole stream, so the work cannot be optimised away.</summary>
    long Result { get; }
}

/// <summary>
/// The floor: it touches every byte and does the cheapest thing a parser must also do, which is
/// notice where an escape sequence starts. No parser can be faster than this on this machine, so
/// it is the ceiling every later number is read against.
/// </summary>
public sealed class EscapeScanConsumer : IStreamConsumer
{
    private long _escapes;

    public string Name => "escape-scan";

    public string What => "touches every byte and counts ESC - the ceiling a parser is measured against";

    public long Result => _escapes;

    public void Reset() => _escapes = 0;

    public void Feed(ReadOnlySpan<byte> chunk)
    {
        long escapes = 0;

        foreach (byte value in chunk)
        {
            if (value == 0x1B)
            {
                escapes++;
            }
        }

        _escapes += escapes;
    }
}
