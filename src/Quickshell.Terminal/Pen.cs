namespace Quickshell.Terminal;

/// <summary>
/// Everything a printed cell inherits from the terminal's current state: two colours, the flags and
/// the underline style.
///
/// <para>It is saved and restored whole by DECSC and DECRC, which is the part programs rely on and
/// the part a client that saved only the position gets wrong. A value type, so saving it is a copy
/// and there is no way to save a reference to the live one by accident.</para>
/// </summary>
/// <param name="Foreground">The foreground, which may be the theme's.</param>
/// <param name="Background">The background, which may be the theme's.</param>
/// <param name="Flags">Bold, slant, inverse, overline, strike.</param>
/// <param name="Underline">Which underline, if any.</param>
/// <param name="Link">Which hyperlink the run belongs to, or zero for none.</param>
public readonly record struct Pen(
    Colour Foreground,
    Colour Background,
    CellFlags Flags = CellFlags.None,
    UnderlineStyle Underline = UnderlineStyle.None,
    int Link = 0)
{
    /// <summary>What a terminal starts with and what <c>SGR 0</c> puts it back to.</summary>
    public static Pen Default => new(Colour.Default, Colour.Default);

    /// <summary>The same pen with one flag set.</summary>
    public Pen Set(CellFlags flag) => this with { Flags = Flags | flag };

    /// <summary>
    /// The same pen with one flag cleared.
    ///
    /// <para>Each attribute has its own reset code because programs turn one off and expect the
    /// others to survive — <c>SGR 22</c> ends bold and must not end the underline beside it.</para>
    /// </summary>
    public Pen Clear(CellFlags flag) => this with { Flags = Flags & ~flag };
}
