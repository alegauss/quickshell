namespace Quickshell.Terminal;

/// <summary>
/// One colour, as the terminal model produced it.
///
/// <para>It lives here and not in the renderer because the palette is terminal state: OSC 4 can
/// change what index 9 means at any moment, and a renderer that looked colours up would be holding
/// a copy of something the model already owns. What reaches the GPU is the resolved colour.</para>
/// </summary>
/// <param name="Red">The red channel, 0 to 255.</param>
/// <param name="Green">The green channel, 0 to 255.</param>
/// <param name="Blue">The blue channel, 0 to 255.</param>
public readonly record struct Rgb(byte Red, byte Green, byte Blue)
{
    /// <summary>Black.</summary>
    public static Rgb Black => new(0, 0, 0);

    /// <summary>White.</summary>
    public static Rgb White => new(255, 255, 255);

    /// <summary>The three channels packed into the low 24 bits, red highest.</summary>
    public uint Packed => ((uint)Red << 16) | ((uint)Green << 8) | Blue;
}

/// <summary>
/// What a cell is, beyond its character, its two colours and the two things that have their own
/// enumerations because they are more than on or off.
///
/// <para>Bold and slant are here because they are what the model was told, not what the renderer
/// does: both are resolved when the glyph is cached, by picking a different face. They ride along
/// so a cell round-trips through the renderer without losing what the host said.</para>
/// </summary>
[Flags]
public enum CellFlags : byte
{
    /// <summary>Ordinary text.</summary>
    None = 0,

    /// <summary>The host asked for bold. Resolved at rasterisation by matching a heavier face.</summary>
    Bold = 1,

    /// <summary>The host asked for italic. Resolved at rasterisation by matching a slanted face.</summary>
    Slant = 2,

    /// <summary>Foreground and background are swapped when the cell is drawn.</summary>
    Inverse = 4,

    /// <summary>A rule above the text, at the same thickness the underline uses.</summary>
    Overline = 8,

    /// <summary>A rule through the text, at the font's own strikethrough position.</summary>
    Strike = 16,

    /// <summary>The cell is inside the selection.</summary>
    Selected = 32,
}

/// <summary>
/// How a cell's underline is drawn. A style rather than a flag, because SGR 4 has had five of them
/// since ECMA-48 and compilers have been colouring errors with the curly one for a decade.
/// </summary>
public enum UnderlineStyle : byte
{
    /// <summary>No underline.</summary>
    None = 0,

    /// <summary>One rule at the font's own underline position.</summary>
    Single = 1,

    /// <summary>Two rules, the second below the first.</summary>
    Double = 2,

    /// <summary>A sine wave. What a compiler underlines a mistake with.</summary>
    Curly = 3,

    /// <summary>A dotted rule.</summary>
    Dotted = 4,

    /// <summary>A dashed rule.</summary>
    Dashed = 5,
}

/// <summary>
/// What the cursor looks like on the cell it is on.
///
/// <para>The shape is the model's, because a host can ask for it with DECSCUSR and a user can set
/// it; whether it is <em>visible</em> at this instant is the renderer's, because that is a clock.</para>
/// </summary>
public enum CursorShape : byte
{
    /// <summary>The cursor is not on this cell.</summary>
    None = 0,

    /// <summary>A filled cell, with the glyph inverted against the cursor colour.</summary>
    Block = 1,

    /// <summary>A vertical bar at the left edge of the cell.</summary>
    Bar = 2,

    /// <summary>A rule along the bottom of the cell.</summary>
    Underline = 3,
}
