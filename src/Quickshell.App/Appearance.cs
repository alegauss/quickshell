using Quickshell.Terminal;

namespace Quickshell.App;

/// <summary>Which way the application's chrome is painted.</summary>
public enum ChromeTheme
{
    /// <summary>Follow whatever Windows is doing, and keep following it while running.</summary>
    System,

    /// <summary>Light, whatever Windows is doing.</summary>
    Light,

    /// <summary>Dark, whatever Windows is doing.</summary>
    Dark,
}

/// <summary>
/// The colours the terminal itself is drawn in.
///
/// <para><b>Not the same thing as <see cref="ChromeTheme"/>, and conflating them is the mistake this
/// type exists to prevent.</b> A user with a favourite scheme wants it under light chrome and under
/// dark chrome; a client that switched their terminal to a light scheme because Windows went light
/// has thrown away a choice they made deliberately in favour of one they made about their
/// operating system. The two travel separately, are stored separately, and change separately.</para>
/// </summary>
/// <param name="Name">What the scheme is called, as a user chose it.</param>
/// <param name="Foreground">Default text.</param>
/// <param name="Background">The ground behind it.</param>
/// <param name="Cursor">The cursor, which a block one inverts the glyph against.</param>
/// <param name="Selection">The ground a selected cell takes.</param>
public readonly record struct TerminalPalette(string Name, Rgb Foreground, Rgb Background,
                                              Rgb Cursor, Rgb Selection)
{
    /// <summary>
    /// What a terminal looks like before anybody has chosen anything: light text on a dark ground,
    /// which is what a terminal has looked like since terminals were furniture.
    /// </summary>
    public static TerminalPalette Default { get; } = new(
        "quickshell",
        new Rgb(214, 219, 228),
        new Rgb(16, 18, 24),
        new Rgb(220, 220, 220),
        new Rgb(52, 78, 120));
}

/// <summary>
/// How the window looks: the chrome's theme and the terminal's scheme, which are two settings and
/// not one.
///
/// <para><b>Following the system means following it, not reading it once.</b> A user who switches
/// Windows to dark at sunset expects the window to follow while it is open — so the chrome theme is
/// handed to WPF's own <c>ThemeMode</c>, which watches the setting, rather than being resolved to a
/// colour at start-up. Reading it once is the bug that looks like it works, because it works every
/// time anybody tests it by restarting.</para>
/// </summary>
public sealed record Appearance
{
    /// <summary>What a fresh installation looks like.</summary>
    public static Appearance Default { get; } = new();

    /// <summary>The chrome's theme. Follows Windows unless the user says otherwise.</summary>
    public ChromeTheme Theme { get; init; } = ChromeTheme.System;

    /// <summary>The terminal's own colours, which the chrome's theme never touches.</summary>
    public TerminalPalette Palette { get; init; } = TerminalPalette.Default;

    /// <summary>Whether the chrome is following the system rather than holding a fixed answer.</summary>
    public bool FollowsSystem => Theme == ChromeTheme.System;
}
