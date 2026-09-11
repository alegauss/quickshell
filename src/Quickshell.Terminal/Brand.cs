namespace Quickshell.Terminal;

/// <summary>
/// Every colour this client decides for itself, each declared once, as bytes (QS83).
///
/// <para><b>Borrowed from the two clients this one sits beside</b>: claude-tray's <c>Brand</c> and
/// freewilly's <c>Palette</c>. What they share is a pattern rather than a library. A colour is a
/// value, and each surface that needs one converts it at its own edge. Here there are two edges. The
/// terminal's palette and a scheme's defaults take the <see cref="Rgb"/> as it is, and the renderer
/// turns it into the floats a D3D11 constant buffer holds. The chrome has no edge yet: WPF's Fluent
/// theme paints all of it, and a brush edge belongs here the day a window paints something Fluent
/// does not.</para>
///
/// <para><b>Once means once.</b> Before this, the default text colour was written three times, the
/// ground three times and the cursor four, in three assemblies — each a copy that a later change would
/// have to find. An architecture test now refuses a colour written anywhere else in the sources.</para>
///
/// <para><b>What is not here.</b> The xterm table in <see cref="Palette"/>, which is a standard rather
/// than a decision. Nor what a scheme, a host or a user sets, which replaces these while the client
/// runs.</para>
/// </summary>
public static class Brand
{
    /// <summary>Default text: light, a little blue, as a terminal has looked since terminals were furniture.</summary>
    public static readonly Rgb Ink = new(214, 219, 228);

    /// <summary>The ground behind it.</summary>
    public static readonly Rgb Ground = new(16, 18, 24);

    /// <summary>The cursor, which a block cursor inverts the glyph against.</summary>
    public static readonly Rgb Cursor = new(220, 220, 220);

    /// <summary>The ground a selected cell takes where the renderer is asked to paint one.</summary>
    public static readonly Rgb Selection = new(52, 78, 120);

    /// <summary>
    /// The edge a pane is drawn with while it receives what is typed into another.
    ///
    /// <para><b>One colour and not the scheme's</b>, because it has to be unmistakable on every scheme
    /// at once: a mark that disappears against somebody's background is a mode that sends keystrokes
    /// to a host without saying so. This orange keeps better than three to one against white and
    /// better than five to one against black.</para>
    /// </summary>
    public static readonly Rgb Outline = new(232, 89, 12);
}
