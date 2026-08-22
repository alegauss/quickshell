using System.Globalization;
using System.Text;

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
/// what follows it — or until <see cref="Flush"/> says nothing does.</para>
///
/// <para>The boundary rules themselves come from <see cref="StringInfo"/>, which is the runtime's
/// UAX #29 implementation over its own Unicode data. Hand-rolling them would mean carrying a second
/// copy of a table that already ships, and being wrong about emoji sequences in a different way
/// from everything else on the machine.</para>
/// </summary>
public sealed class GraphemeSegmenter
{
    private readonly StringBuilder _pending = new();

    /// <summary>Characters held back because their cluster may not have finished.</summary>
    public int Pending => _pending.Length;

    /// <summary>
    /// Feeds text and returns the clusters that are certainly complete. The tail is kept: a cluster
    /// at the end of a feed may still grow when the next one arrives.
    /// </summary>
    public List<string> Feed(ReadOnlySpan<char> text)
    {
        _pending.Append(text);

        List<string> clusters = [];

        if (_pending.Length == 0)
        {
            return clusters;
        }

        string buffered = _pending.ToString();
        int usable = buffered.Length;

        // A feed that ended between the halves of a surrogate pair has no last character yet, and
        // segmenting the half would be worse than not segmenting at all: the runtime reads a lone
        // high surrogate as a complete cluster of its own, which makes the cluster before it look
        // finished and releases it. That is how the woman-technologist emoji comes out as two cells
        // when the read happens to split its second half.
        if (usable > 0 && char.IsHighSurrogate(buffered[usable - 1]))
        {
            usable--;
        }

        if (usable == 0)
        {
            return clusters;
        }

        int consumed = 0;

        foreach (string cluster in Clusters(buffered[..usable]))
        {
            // The last cluster is not emitted: what follows it has not arrived, and a combining
            // mark or a joiner in the next read would have belonged to it.
            if (consumed + cluster.Length >= usable)
            {
                break;
            }

            clusters.Add(cluster);
            consumed += cluster.Length;
        }

        _pending.Remove(0, consumed);
        return clusters;
    }

    /// <summary>
    /// Ends the stream: whatever is held back is complete now, because nothing more is coming.
    /// </summary>
    public List<string> Flush()
    {
        List<string> clusters = _pending.Length == 0 ? [] : Clusters(_pending.ToString()).ToList();
        _pending.Clear();

        return clusters;
    }

    /// <summary>Forgets what was held back, which is what a reconnect is.</summary>
    public void Reset() => _pending.Clear();

    /// <summary>Every extended grapheme cluster in a complete string, in order.</summary>
    public static IEnumerable<string> Clusters(string text)
    {
        TextElementEnumerator enumerator = StringInfo.GetTextElementEnumerator(text);

        while (enumerator.MoveNext())
        {
            yield return (string)enumerator.Current;
        }
    }
}
