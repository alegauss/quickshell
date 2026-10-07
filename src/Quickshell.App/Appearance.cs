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
/// How the window's chrome looks.
///
/// <para><b>The terminal's colours are not here, and conflating the two is the mistake to
/// avoid.</b> A user with a favourite scheme wants it under light chrome and under dark chrome; a
/// client that switched their terminal to a light scheme because Windows went light has thrown away
/// a choice they made deliberately in favour of one they made about their operating system. The
/// scheme is <see cref="Settings.Colours"/>, which every pane draws from, and nothing in this record
/// reaches it.</para>
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

    /// <summary>Whether the chrome is following the system rather than holding a fixed answer.</summary>
    public bool FollowsSystem => Theme == ChromeTheme.System;
}
