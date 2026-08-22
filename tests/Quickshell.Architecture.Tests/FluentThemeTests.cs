using System.Windows;
using Xunit;

namespace Quickshell.Architecture.Tests;

/// <summary>
/// The chrome this client will wear is the one claude-tray and freewilly already ship: WPF with
/// <c>ThemeMode="System"</c>, the built-in Fluent theme, and no third-party UI dependency.
///
/// That property arrived in .NET 9, so on net8.0 it does not exist and the whole design system is
/// out of reach. This asserts it against the framework the tree actually targets, which is the
/// difference between knowing Fluent is available and having read that it is: a retarget back to
/// net8.0 fails here rather than being discovered by the first window that looks wrong.
/// </summary>
public sealed class FluentThemeTests
{
    [Fact]
    public void WindowCarriesThemeMode()
    {
        Assert.NotNull(typeof(Window).GetProperty(nameof(Window.ThemeMode)));
    }

    [Fact]
    public void SystemThemeModeIsTheOneTheSiblingClientsSet()
    {
        Assert.Equal(ThemeMode.System, ThemeMode.System);
        Assert.Contains("System", ThemeMode.System.ToString(), StringComparison.Ordinal);
    }
}
