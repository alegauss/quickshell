using System.Globalization;
using Quickshell.Terminal;

namespace Quickshell.Replay;

/// <summary>
/// The arm that measures what a session actually costs: the parser <em>and</em> the terminal it
/// writes into.
///
/// <para><b>Why this had to exist.</b> Figure 2 of the performance budget asks for 400 MB/s of
/// sustained parse throughput, and the `parse` arm reported 977 MB/s — the Williams table with a
/// handler that only counts. Nothing here measured <see cref="Emulator.Feed"/>, which is the call a
/// real session makes for every byte a host sends, and it turns out to be two orders of magnitude
/// slower. A budget figure nothing measures against the real path is a figure that can be met while
/// the thing it is about is slow, which is what QS141 found.</para>
///
/// <para><b>It is not the `render` arm either.</b> That one stands in for glyph and instance work and
/// deliberately keeps only a stub of a buffer — cursor, wrap, carriage return, line feed, erase.
/// This one is the real <see cref="Emulator"/>: every escape sequence it implements, real cells, real
/// scrollback, real reflow. The gap between `parse` and this is the cost of being a terminal rather
/// than a byte scanner.</para>
///
/// <para>The size is the one a session uses rather than a benchmark's convenience — 200x50 with
/// 2,000 lines behind it, which is what the corpus was captured at.</para>
/// </summary>
public sealed class EmulateConsumer : IStreamConsumer
{
    private Emulator _emulator = Make();

    /// <inheritdoc/>
    public string Name => "emulate";

    /// <inheritdoc/>
    public string What =>
        "the parser and the real terminal it writes into - cells, scrollback and reflow, which is "
        + "the call a session makes for every byte a host sends";

    /// <summary>
    /// The buffer's own generation, which moves with every write and cannot be folded away.
    /// </summary>
    public long Result => _emulator.Buffer.Generation;

    /// <summary>
    /// What the buffer did, read off its own counters — which is what separates "writing cells is
    /// expensive" from "scrolling is expensive and happens once a line".
    ///
    /// <para>`CellsWrittenByScrolling` against the cells the stream actually printed is the whole
    /// question: a ring that rotates moves no cells at all, and one that copies moves a screenful
    /// every line.</para>
    /// </summary>
    public string Note
    {
        get
        {
            TerminalBuffer buffer = _emulator.Screens.Primary;

            return string.Create(
                CultureInfo.InvariantCulture,
                $"{buffer.Scrolls} scrolls, {buffer.CellsWrittenByScrolling} cells moved by "
                + $"scrolling, {buffer.ClusterCount} clusters interned");
        }
    }

    /// <inheritdoc/>
    public void Reset() => _emulator = Make();

    /// <inheritdoc/>
    public void Feed(ReadOnlySpan<byte> chunk)
    {
        _emulator.Feed(chunk);

        // Drained, because a host that asks questions would otherwise have its answers accumulate
        // and this would be measuring a buffer nobody read. It is what a session does.
        if (!_emulator.Reply.IsEmpty)
        {
            _emulator.ClearReply();
        }
    }

    private static Emulator Make() => new(200, 50, scrollback: 2_000);
}
