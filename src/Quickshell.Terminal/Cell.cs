using System.Runtime.InteropServices;

namespace Quickshell.Terminal;

/// <summary>
/// One cell of the buffer: sixteen bytes, and a value rather than an object.
///
/// <para><b>Sixteen bytes is a budget, not an outcome.</b> A row is <c>Columns</c> of these laid
/// out contiguously, so clearing a row is a fill and scrolling touches one row's worth of memory
/// however deep the scrollback is. An object per cell would make both of those a pointer chase and
/// a screenful of allocations.</para>
///
/// <para><b>The text is a codepoint when it fits and an index when it does not.</b> Almost every
/// cell holds one codepoint. A decomposed accent or an emoji ZWJ sequence does not fit in four
/// bytes, so it lives in the buffer's cluster table and the cell holds its index. Which of the two
/// it is, is the sign: negative means the table.</para>
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public readonly struct Cell : IEquatable<Cell>
{
    private const int WidthShift = 12;
    private const int UnderlineShift = 9;
    private const int LinkShift = 14;
    private const uint FlagMask = 0x1FF;

    /// <summary>
    /// How many distinct hyperlinks a screen's worth of cells can point at.
    ///
    /// <para>The attribute word had eighteen bits spare above the width, so a link costs nothing:
    /// the cell is the same sixteen bytes it was, and the renderer needs no change at all because a
    /// link is a fact about a cell rather than a thing drawn differently.</para>
    /// </summary>
    public const int MaximumLinks = (1 << 18) - 1;

    private readonly int _text;
    private readonly uint _foreground;
    private readonly uint _background;
    private readonly uint _attributes;

    private Cell(int text, uint foreground, uint background, uint attributes)
    {
        _text = text;
        _foreground = foreground;
        _background = background;
        _attributes = attributes;
    }

    /// <summary>The size the design bounds this at, asserted against the struct itself in a test.</summary>
    public const int Size = 16;

    /// <summary>
    /// An empty cell in the default colours: what a cleared row is filled with.
    ///
    /// <para>Default, and not the theme's current colours: a cleared row repaints with the theme
    /// the same way a written one does, which it would not if the clear had baked a colour in.</para>
    /// </summary>
    public static Cell Blank => new(' ', 0, 0, 1u << WidthShift);

    /// <summary>Whether the text is an index into the cluster table rather than a codepoint.</summary>
    public bool IsCluster => _text < 0;

    /// <summary>The codepoint, where this cell holds one. Undefined when <see cref="IsCluster"/>.</summary>
    public int Codepoint => _text < 0 ? 0xFFFD : _text;

    /// <summary>The index into the buffer's cluster table, where this cell holds one.</summary>
    public int ClusterIndex => _text < 0 ? ~_text : -1;

    /// <summary>The foreground as the host expressed it, which may be "the theme's".</summary>
    public Colour Foreground => Colour.FromPacked(_foreground);

    /// <summary>The background as the host expressed it, which may be "the theme's".</summary>
    public Colour Background => Colour.FromPacked(_background);

    /// <summary>Bold, slant, inverse, overline, strike and selection.</summary>
    public CellFlags Flags => (CellFlags)(_attributes & FlagMask);

    /// <summary>Which underline this cell carries, if any.</summary>
    public UnderlineStyle Underline => (UnderlineStyle)((_attributes >> UnderlineShift) & 0x7);

    /// <summary>
    /// How many cells this one occupies: two for a wide character, one for an ordinary one, and
    /// <b>zero for the trailing half of a wide pair</b> — which is a real cell holding no text of
    /// its own, and the only thing that keeps the column count honest.
    /// </summary>
    public int Width => (int)((_attributes >> WidthShift) & 0x3);

    /// <summary>
    /// Which hyperlink this cell is part of, or zero for none. An index into the buffer's own table,
    /// because the URI is shared by every cell of the run and storing it per cell would not fit.
    /// </summary>
    public int Link => (int)(_attributes >> LinkShift);

    /// <summary>Whether anything was ever written here, as against a cell that is still blank.</summary>
    public bool IsBlank => _text == ' ' && _foreground == 0 && _background == 0 && Flags == CellFlags.None;

    /// <summary>Builds a cell holding one codepoint.</summary>
    public static Cell For(int codepoint, Colour foreground, Colour background,
                           CellFlags flags = CellFlags.None,
                           UnderlineStyle underline = UnderlineStyle.None,
                           int width = 1, int link = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(codepoint);

        return new Cell(codepoint, foreground.Packed, background.Packed,
                        Attributes(flags, underline, width, link));
    }

    /// <summary>Builds a cell pointing at a cluster the buffer's table holds.</summary>
    public static Cell ForCluster(int clusterIndex, Colour foreground, Colour background,
                                  CellFlags flags = CellFlags.None,
                                  UnderlineStyle underline = UnderlineStyle.None,
                                  int width = 1, int link = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(clusterIndex);

        return new Cell(~clusterIndex, foreground.Packed, background.Packed,
                        Attributes(flags, underline, width, link));
    }

    /// <summary>The same cell in different colours, which is what an attribute change rewrites.</summary>
    public Cell With(Colour foreground, Colour background) =>
        new(_text, foreground.Packed, background.Packed, _attributes);

    /// <inheritdoc/>
    public bool Equals(Cell other) =>
        _text == other._text && _foreground == other._foreground
        && _background == other._background && _attributes == other._attributes;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is Cell other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(_text, _foreground, _background, _attributes);

    /// <summary>Whether two cells are the same in every field.</summary>
    public static bool operator ==(Cell left, Cell right) => left.Equals(right);

    /// <summary>Whether two cells differ in any field.</summary>
    public static bool operator !=(Cell left, Cell right) => !left.Equals(right);

    private static uint Attributes(CellFlags flags, UnderlineStyle underline, int width, int link)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(width);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(width, 2);
        ArgumentOutOfRangeException.ThrowIfNegative(link);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(link, MaximumLinks);

        return (uint)flags | ((uint)underline << UnderlineShift) | ((uint)width << WidthShift)
               | ((uint)link << LinkShift);
    }

}
