using System.Globalization;

namespace Quickshell.Terminal;

/// <summary>
/// How many cells a codepoint occupies: zero, one or two.
///
/// <para><b>This lives in the model, and the renderer is told.</b> A renderer that decided width for
/// itself would eventually disagree with the model about which column the cursor is in, and that
/// disagreement is invisible until somebody is editing a filename in a remote shell — at which
/// point it is not a rendering bug any more, it is the client typing into the wrong place.</para>
///
/// <para>The table is East Asian Width's <c>W</c> and <c>F</c> classes plus the emoji that Unicode
/// gives emoji presentation by default. It is deliberately the same rule every <c>wcwidth</c> on
/// the other side of the connection is trying to implement, because agreeing with the host matters
/// far more than being right in the abstract.</para>
/// </summary>
public static class CharacterWidth
{
    /// <summary>
    /// Ranges of codepoints that occupy two cells, sorted and non-overlapping. Stored as pairs so
    /// the lookup is a binary search over a flat array rather than a walk over objects.
    /// </summary>
    private static readonly int[] Wide =
    [
        0x1100, 0x115F,     // Hangul Jamo initial consonants
        0x2329, 0x232A,     // angle brackets
        0x2E80, 0x303E,     // CJK radicals, Kangxi, CJK symbols
        0x3041, 0x33FF,     // kana, Hangul compatibility jamo, CJK compatibility
        0x3400, 0x4DBF,     // CJK unified ideographs extension A
        0x4E00, 0x9FFF,     // CJK unified ideographs
        0xA000, 0xA4CF,     // Yi
        0xA960, 0xA97F,     // Hangul jamo extended A
        0xAC00, 0xD7A3,     // Hangul syllables
        0xF900, 0xFAFF,     // CJK compatibility ideographs
        0xFE10, 0xFE19,     // vertical forms
        0xFE30, 0xFE6F,     // CJK compatibility forms, small form variants
        0xFF00, 0xFF60,     // fullwidth forms
        0xFFE0, 0xFFE6,     // fullwidth signs
        0x1B000, 0x1B001,   // kana supplement
        0x1F004, 0x1F004,   // mahjong red dragon
        0x1F0CF, 0x1F0CF,   // playing card black joker
        0x1F18E, 0x1F18E,   // negative squared AB
        0x1F191, 0x1F19A,   // squared CL through squared VS
        0x1F200, 0x1F320,   // enclosed ideographic supplement into miscellaneous symbols
        0x1F32D, 0x1F335,
        0x1F337, 0x1F37C,
        0x1F37E, 0x1F393,
        0x1F3A0, 0x1F3CA,
        0x1F3CF, 0x1F3D3,
        0x1F3E0, 0x1F3F0,
        0x1F3F4, 0x1F3F4,
        0x1F3F8, 0x1F43E,
        0x1F440, 0x1F440,
        0x1F442, 0x1F4FC,
        0x1F4FF, 0x1F53D,
        0x1F54B, 0x1F54E,
        0x1F550, 0x1F567,
        0x1F57A, 0x1F57A,
        0x1F595, 0x1F596,
        0x1F5A4, 0x1F5A4,
        0x1F5FB, 0x1F64F,
        0x1F680, 0x1F6C5,
        0x1F6CC, 0x1F6CC,
        0x1F6D0, 0x1F6D2,
        0x1F6D5, 0x1F6D7,
        0x1F6EB, 0x1F6EC,
        0x1F6F4, 0x1F6FC,
        0x1F7E0, 0x1F7EB,
        0x1F90C, 0x1F93A,
        0x1F93C, 0x1F945,
        0x1F947, 0x1F978,
        0x1F97A, 0x1F9CB,
        0x1F9CD, 0x1F9FF,
        0x1FA70, 0x1FA74,
        0x1FA78, 0x1FA7A,
        0x1FA80, 0x1FA86,
        0x1FA90, 0x1FAA8,
        0x1FAB0, 0x1FAB6,
        0x1FAC0, 0x1FAC2,
        0x1FAD0, 0x1FAD6,
        0x20000, 0x2FFFD,   // CJK extension B and beyond
        0x30000, 0x3FFFD,
    ];

    /// <summary>
    /// How many cells <paramref name="codepoint"/> occupies.
    ///
    /// <para>Zero for a combining mark, which attaches to the cell before it; zero for a control
    /// character and for the format characters that mark direction and joining, none of which the
    /// host advances its own cursor for. Two for East Asian wide and fullwidth forms and for emoji.
    /// One for everything else.</para>
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

        if (IsZeroWidth(codepoint))
        {
            return 0;
        }

        return IsWide(codepoint) ? 2 : 1;
    }

    /// <summary>Whether this codepoint attaches to the cell before it rather than taking its own.</summary>
    public static bool IsZeroWidth(int codepoint)
    {
        // The joiners and the direction marks: invisible, and the host does not advance for them.
        if (codepoint is 0x200B or 0x200C or 0x200D or 0x200E or 0x200F or 0xFEFF
            or (>= 0x2028 and <= 0x202E) or (>= 0x2060 and <= 0x2064))
        {
            return true;
        }

        // A variation selector chooses a presentation; it never occupies a cell of its own.
        if (codepoint is (>= 0xFE00 and <= 0xFE0F) or (>= 0xE0100 and <= 0xE01EF))
        {
            return true;
        }

        UnicodeCategory category = codepoint <= 0xFFFF
            ? CharUnicodeInfo.GetUnicodeCategory((char)codepoint)
            : CharUnicodeInfo.GetUnicodeCategory(char.ConvertFromUtf32(codepoint), 0);

        return category is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark;
    }

    /// <summary>Whether this codepoint takes two cells.</summary>
    public static bool IsWide(int codepoint)
    {
        int low = 0;
        int high = (Wide.Length / 2) - 1;

        while (low <= high)
        {
            int middle = (low + high) / 2;

            if (codepoint < Wide[middle * 2])
            {
                high = middle - 1;
            }
            else if (codepoint > Wide[(middle * 2) + 1])
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
}
