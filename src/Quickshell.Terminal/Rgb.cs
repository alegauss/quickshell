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
/// What a cell is, beyond its character and its two colours.
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

    /// <summary>The cell carries an underline.</summary>
    Underline = 8,

    /// <summary>The cell carries a strikethrough.</summary>
    Strike = 16,

    /// <summary>The cursor is on this cell.</summary>
    Cursor = 32,
}
