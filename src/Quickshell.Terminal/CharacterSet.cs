namespace Quickshell.Terminal;

/// <summary>Which set a designated slot holds. Only the three a host actually sends.</summary>
public enum CharacterSet : byte
{
    /// <summary>US ASCII: every byte means itself.</summary>
    Ascii = 0,

    /// <summary>
    /// DEC Special Graphics, and the reason this whole file exists. A program drawing a box sends
    /// <c>ESC ( 0</c> and then ASCII letters, so a terminal that ignores the designation draws
    /// <c>lqqqk</c> where the user expects a corner.
    /// </summary>
    DecSpecialGraphics = 1,

    /// <summary>UK national: one codepoint differs, and it is the pound sign.</summary>
    UnitedKingdom = 2,
}

/// <summary>
/// The fixed tables a designated character set is.
///
/// <para>Nothing here is a guess or a heuristic: DEC published these mappings and every terminal
/// carries the same ones, which is what makes a box drawn by a program forty years old still join
/// up today.</para>
/// </summary>
public static class CharacterSets
{
    /// <summary>
    /// DEC Special Graphics over 0x5F to 0x7E, in order. Outside that range the set is ASCII, which
    /// is why a program can switch to it and still send spaces and digits.
    /// </summary>
    private static readonly int[] SpecialGraphics =
    [
        0x00A0, // _  no-break space
        0x25C6, // `  black diamond
        0x2592, // a  medium shade
        0x2409, // b  symbol for horizontal tab
        0x240C, // c  symbol for form feed
        0x240D, // d  symbol for carriage return
        0x240A, // e  symbol for line feed
        0x00B0, // f  degree sign
        0x00B1, // g  plus-minus
        0x2424, // h  symbol for newline
        0x240B, // i  symbol for vertical tab
        0x2518, // j  box drawings up and left
        0x2510, // k  box drawings down and left
        0x250C, // l  box drawings down and right
        0x2514, // m  box drawings up and right
        0x253C, // n  box drawings vertical and horizontal
        0x23BA, // o  horizontal scan line 1
        0x23BB, // p  horizontal scan line 3
        0x2500, // q  box drawings horizontal
        0x23BC, // r  horizontal scan line 7
        0x23BD, // s  horizontal scan line 9
        0x251C, // t  box drawings vertical and right
        0x2524, // u  box drawings vertical and left
        0x2534, // v  box drawings up and horizontal
        0x252C, // w  box drawings down and horizontal
        0x2502, // x  box drawings vertical
        0x2264, // y  less-than or equal
        0x2265, // z  greater-than or equal
        0x03C0, // {  greek small pi
        0x2260, // |  not equal
        0x00A3, // }  pound sign
        0x00B7, // ~  middle dot
    ];

    /// <summary>Which set a designation byte names, or null where nothing here knows it.</summary>
    public static CharacterSet? Designated(byte final) => final switch
    {
        (byte)'B' => CharacterSet.Ascii,
        (byte)'0' or (byte)'2' => CharacterSet.DecSpecialGraphics,
        (byte)'A' => CharacterSet.UnitedKingdom,
        _ => null,
    };

    /// <summary>
    /// What a codepoint becomes under a designated set. Anything the set does not remap comes back
    /// unchanged, so a program switching sets can still send text that means itself.
    /// </summary>
    public static int Map(CharacterSet set, int codepoint) => set switch
    {
        CharacterSet.DecSpecialGraphics when codepoint is >= 0x5F and <= 0x7E =>
            SpecialGraphics[codepoint - 0x5F],

        // The UK set differs from ASCII in exactly one place, which is the whole of its definition.
        CharacterSet.UnitedKingdom when codepoint == '#' => 0x00A3,

        _ => codepoint,
    };
}
