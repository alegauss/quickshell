using System.Windows;

namespace Quickshell.App;

/// <summary>
/// The one mapping from the theme a user chose to the one WPF paints with (QS83).
///
/// <para><b>Set on each window, in code, from here.</b> The two clients this one borrows its design
/// from keep <c>ThemeMode="System"</c> in each window's markup, because freewilly measured the
/// Application-wide setting and a value set from code rendering differently from that attribute.
/// Every window here is built in code, with no markup to carry the attribute, so what is kept is the
/// other half of their rule: the mode is decided in one place, set on every window this client
/// opens, never on the Application, and the chrome paints no colour of its own.</para>
/// </summary>
public static class Theme
{
    /// <summary>What WPF is told for a chosen theme: following the system is its own mode, not a reading taken once.</summary>
    public static ThemeMode Mode(ChromeTheme theme) => theme switch
    {
        ChromeTheme.Light => ThemeMode.Light,
        ChromeTheme.Dark => ThemeMode.Dark,
        _ => ThemeMode.System,
    };
}
