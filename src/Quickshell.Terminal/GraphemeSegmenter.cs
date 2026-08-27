using System.Globalization;

namespace Quickshell.Terminal;

/// <summary>
/// Text into the units a cell actually holds: grapheme clusters, by UAX #29.
///
/// <para><b>Below this boundary a cell would hold a codepoint, and every one of those is a cell too
/// many.</b> A base with its combining marks is one cell. A regional indicator pair — the two
/// letters a flag is made of — is one cell. An emoji ZWJ sequence with its variation selectors is
/// one cell, however many codepoints deep it goes.</para>
///
/// <para><b>Segmentation is incremental too</b>, because a cluster can straddle reads just as a
/// character can. The rule is that a cluster is only known to be finished once something after it
/// has started a new one, so the last cluster of every feed is held back until the next feed says
/// what follows it — or until the flush says nothing does.</para>
///
/// <para><b>It allocates nothing, and the shape of the API is why.</b> Clusters come back as spans
/// into a buffer this class reuses, one at a time, rather than as a list of strings — a screen of
/// text is one string per character otherwise, which measured out at fifty-five kilobytes of garbage
/// per megabyte of stream and a collection pause landing in the middle of somebody's <c>vim</c>
/// session. The cost of that shape is a contract: <b>a cluster is only valid until the next call</b>,
/// because <see cref="Append"/> may move the buffer underneath it.</para>
///
/// <para><b>The buffer is bounded, which is a security property and not a tidiness one.</b> A stream
/// with no cluster boundary in it — a base character and then combining marks for ever, which is a
/// thing a host can send — would otherwise be a remote machine deciding how much memory this process
/// holds, and would make each read cost more than the last. Past <see cref="MaximumCluster"/> the
/// boundary rules are overruled and a cluster is emitted anyway.</para>
///
/// <para>The boundary rules themselves come from <see cref="StringInfo"/>, which is the runtime's
/// UAX #29 implementation over its own Unicode data. Hand-rolling them would mean carrying a second
/// copy of a table that already ships, and being wrong about emoji sequences in a different way
/// from everything else on the machine.</para>
/// </summary>
public sealed class GraphemeSegmenter
{
    /// <summary>
    /// The longest cluster this will assemble, in UTF-16 units.
    ///
    /// <para>A family emoji with skin tones is about eleven; sixty-four is far above anything text
    /// contains and far below anything a buffer notices. Past it, the input is a host generating
    /// characters rather than writing words.</para>
    /// </summary>
    public const int MaximumCluster = 64;

    private char[] _pending = new char[256];
    private int _start;
    private int _end;

    /// <summary>Characters held back because their cluster may not have finished.</summary>
    public int Pending => _end - _start;

    /// <summary>
    /// How many times the length cap has overruled the boundary rules.
    ///
    /// <para>Zero on every stream that is text. Non-zero says a host sent something that has no
    /// cluster boundary in it, which is worth being able to see rather than absorbing quietly.</para>
    /// </summary>
    public long Forced { get; private set; }

    /// <summary>
    /// Adds a read's worth of text. <b>Invalidates every cluster previously handed out.</b>
    /// </summary>
    public void Append(ReadOnlySpan<char> text)
    {
        if (text.IsEmpty)
        {
            return;
        }

        // Compacting before growing is what keeps this one allocation for the life of the session:
        // whatever was consumed is at the front, and after a drain there is at most one part-built
        // cluster left to move.
        if (_start > 0)
        {
            _pending.AsSpan(_start, _end - _start).CopyTo(_pending);
            _end -= _start;
            _start = 0;
        }

        if (_end + text.Length > _pending.Length)
        {
            Array.Resize(ref _pending, Math.Max(_pending.Length * 2, _end + text.Length));
        }

        text.CopyTo(_pending.AsSpan(_end));
        _end += text.Length;
    }

    /// <summary>
    /// Takes the next cluster that is certainly complete, holding the last one back.
    /// </summary>
    /// <returns>False where nothing is certain yet, which is where a caller stops until the next
    /// read.</returns>
    public bool TryNext(out ReadOnlySpan<char> cluster)
    {
        cluster = default;

        int available = _end - _start;

        if (available == 0)
        {
            return false;
        }

        // A read that ended between the halves of a surrogate pair has no last character yet, and
        // segmenting the half would be worse than not segmenting at all: the runtime reads a lone
        // high surrogate as a complete cluster of its own, which makes the cluster before it look
        // finished and releases it. That is how the woman-technologist emoji comes out as two cells
        // when the read happens to split its second half.
        int usable = char.IsHighSurrogate(_pending[_end - 1]) ? available - 1 : available;

        if (usable <= 0)
        {
            return false;
        }

        int length = Measure(usable, out int window);

        if (length >= window && window == usable)
        {
            // The cluster reaches the end of what has arrived. What follows it has not, and a
            // combining mark or a joiner in the next read would have belonged to it — so it waits,
            // unless it has already grown past anything a cluster can be.
            if (available < MaximumCluster)
            {
                return false;
            }

            length = MaximumCluster;
            Forced++;
        }
        else if (length > MaximumCluster)
        {
            length = MaximumCluster;
            Forced++;
        }

        cluster = Take(length);

        return true;
    }

    /// <summary>
    /// Takes the next cluster with nothing held back, because nothing more is coming.
    ///
    /// <para>Called at the end of every read rather than only at the end of a stream, which is a
    /// deliberate departure from what incremental segmentation does on its own: holding a cluster
    /// back is correct for a stream and wrong for a terminal, where it means the last character a
    /// user typed does not appear until they type another.</para>
    /// </summary>
    public bool TryFlush(out ReadOnlySpan<char> cluster)
    {
        cluster = default;

        if (_end == _start)
        {
            return false;
        }

        int length = Measure(_end - _start, out _);

        if (length > MaximumCluster)
        {
            length = MaximumCluster;
            Forced++;
        }

        cluster = Take(length);

        return true;
    }

    /// <summary>Forgets what was held back, which is what a reconnect is.</summary>
    public void Reset()
    {
        _start = 0;
        _end = 0;
    }

    /// <summary>Every extended grapheme cluster in a complete string, in order.</summary>
    public static IEnumerable<string> Clusters(string text)
    {
        TextElementEnumerator enumerator = StringInfo.GetTextElementEnumerator(text);

        while (enumerator.MoveNext())
        {
            yield return (string)enumerator.Current;
        }
    }

    /// <summary>
    /// How long the next cluster is, looking at no more than the cap plus one character.
    ///
    /// <para><b>The window is what keeps this linear.</b> Asking the boundary rules about a whole
    /// sixty-four kilobyte read of combining marks scans all of it, then emits one cluster, then
    /// scans nearly all of it again — the quadratic cost is the hang the fuzz seeds are looking for.
    /// One character past the cap is enough to tell "complete" from "longer than we will allow".</para>
    /// </summary>
    private int Measure(int usable, out int window)
    {
        window = Math.Min(usable, MaximumCluster + 1);

        // Never cut the window between the halves of a pair: the rules would read the lone high
        // surrogate as a boundary and report a cluster that is not one.
        if (window < usable && char.IsHighSurrogate(_pending[_start + window - 1]))
        {
            window++;
        }

        return StringInfo.GetNextTextElementLength(_pending.AsSpan(_start, window));
    }

    /// <summary>Hands out a cluster of a given length and steps past it, never splitting a pair.</summary>
    private ReadOnlySpan<char> Take(int length)
    {
        if (length > 1 && char.IsHighSurrogate(_pending[_start + length - 1]))
        {
            length--;
        }

        ReadOnlySpan<char> cluster = _pending.AsSpan(_start, Math.Max(1, length));
        _start += cluster.Length;

        if (_start == _end)
        {
            _start = 0;
            _end = 0;
        }

        return cluster;
    }
}
