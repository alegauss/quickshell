namespace Quickshell.Terminal;

/// <summary>
/// How many cells a codepoint occupies: zero, one or two.
///
/// <para><b>This lives in the model, and the renderer is told.</b> A renderer that decided width for
/// itself would eventually disagree with the model about which column the cursor is in, and that
/// disagreement is invisible until somebody is editing a filename in a remote shell — at which
/// point it is not a rendering bug any more, it is the client typing into the wrong place.</para>
///
/// <para><b>The tables are generated, not written.</b> They come from the Unicode Character
/// Database by <c>tools/generate-width-table.py</c>, and <see cref="UnicodeVersion"/> says which
/// release. A hand-written range list is one nobody will ever diff against a new Unicode; a
/// generated one is a file anybody can rebuild and compare, which is the whole difference when
/// this decides which column the cursor is in.</para>
/// </summary>
public static partial class CharacterWidth
{
    /// <summary>
    /// The lowest codepoint that either range table can contain, so everything printable below it
    /// is one column and needs no search.
    ///
    /// <para><b>This constant is a claim about the generated tables and it is checked</b> —
    /// <c>CharacterWidthTests</c> walks every codepoint beneath it against the tables themselves,
    /// so a regeneration that added a lower range fails rather than silently making this wrong.</para>
    /// </summary>
    private const int FirstNarrowExit = 0x0300;

    /// <summary>
    /// How many cells <paramref name="codepoint"/> occupies.
    ///
    /// <para>Zero for a combining mark, which attaches to the cell before it; zero for a control
    /// character and for the format characters that mark direction and joining, none of which the
    /// host advances its own cursor for. Two for East Asian wide and fullwidth forms and for the
    /// emoji Unicode gives emoji presentation. One for everything else.</para>
    /// </summary>
    public static int Of(int codepoint)
    {
        if (codepoint < 0 || codepoint > 0x10FFFF)
        {
            return 1;
        }

        // C0, DEL and C1. The parser consumes these rather than printing them, so a width of one
        // here would be a column the host never advanced.
        if (codepoint < 0x20 || (codepoint >= 0x7F && codepoint < 0xA0))
        {
            return 0;
        }

        // Below the first codepoint either table can hold, so both searches below are guaranteed to
        // miss and neither is worth making. This is provable from the generated tables rather than
        // assumed: ZeroRanges begins at U+0300 and WideRanges at U+1100, and the explicit joiners
        // and direction marks IsZeroWidth names outside the table all start at U+200B.
        //
        // It is worth a comment this long because it is 22% of what placing a character costs.
        // Measured: the lookup was 14.3 ns of a 63.6 ns path, and every ASCII character in every
        // corpus stream paid two binary searches over Unicode range tables to be told it was one
        // column wide. QS146 is where that was separated out.
        if (codepoint < FirstNarrowExit)
        {
            return 1;
        }

        if (IsZeroWidth(codepoint))
        {
            return 0;
        }

        return IsWide(codepoint) ? 2 : 1;
    }

    /// <summary>Whether this codepoint attaches to the cell before it rather than taking its own.</summary>
    public static bool IsZeroWidth(int codepoint)
    {
        // The joiners and the direction marks. These are format characters rather than marks, so
        // they are not in the generated table, and the host does not advance for them either.
        if (codepoint is 0x200B or 0x200C or 0x200D or 0x200E or 0x200F or 0xFEFF
            or (>= 0x2028 and <= 0x202E) or (>= 0x2060 and <= 0x2064))
        {
            return true;
        }

        return Contains(ZeroRanges, codepoint);
    }

    /// <summary>Whether this codepoint takes two cells.</summary>
    public static bool IsWide(int codepoint) => Contains(WideRanges, codepoint);

    /// <summary>
    /// How many cells a string occupies, which is what the cursor column is a running total of.
    /// Surrogate pairs count once, as the codepoint they encode.
    /// </summary>
    public static int Of(ReadOnlySpan<char> text)
    {
        int total = 0;

        for (int index = 0; index < text.Length; index++)
        {
            int codepoint = text[index];

            if (char.IsHighSurrogate(text[index]) && index + 1 < text.Length
                && char.IsLowSurrogate(text[index + 1]))
            {
                codepoint = char.ConvertToUtf32(text[index], text[index + 1]);
                index++;
            }

            total += Of(codepoint);
        }

        return total;
    }

    /// <summary>
    /// How many cells a grapheme cluster occupies. The cluster's width is its base character's:
    /// everything after the base is a mark, a joiner or a variation selector, and none of those
    /// moves the cursor.
    /// </summary>
    public static int OfCluster(ReadOnlySpan<char> cluster)
    {
        if (cluster.IsEmpty)
        {
            return 0;
        }

        int codepoint = char.IsHighSurrogate(cluster[0]) && cluster.Length > 1
            ? char.ConvertToUtf32(cluster[0], cluster[1])
            : cluster[0];

        // A cluster whose base takes no cell still takes one: a lone combining mark arriving with
        // nothing to attach to is drawn on its own, and the host advanced for it.
        return Math.Max(Of(codepoint), IsZeroWidth(codepoint) || codepoint < 0x20 ? 1 : 0);
    }

    /// <summary>Binary search over a flat array of sorted, non-overlapping first/last pairs.</summary>
    private static bool Contains(int[] ranges, int codepoint)
    {
        int low = 0;
        int high = (ranges.Length / 2) - 1;

        while (low <= high)
        {
            int middle = (low + high) / 2;

            if (codepoint < ranges[middle * 2])
            {
                high = middle - 1;
            }
            else if (codepoint > ranges[(middle * 2) + 1])
            {
                low = middle + 1;
            }
            else
            {
                return true;
            }
        }

        return false;
    }
}
