using System.IO;
using System.Windows.Controls;
using System.Windows.Input;

using Xunit;

namespace Quickshell.App.Tests;

/// <summary>QS354: a menu bar of every action, built from the palette's own list, and not on screen by default.</summary>
public sealed class MenuBarTests : IDisposable
{
    private static readonly TerminalShare Shared = new();

    private readonly string _here = Path.Combine(Path.GetTempPath(), "qs-menu-" + Guid.NewGuid().ToString("N"));

    public MenuBarTests() => Directory.CreateDirectory(_here);

    public void Dispose() => Directory.Delete(_here, recursive: true);

    /// <summary>
    /// The falsification of the line: the menu and the palette enumerate one set of actions. Every action the
    /// palette offers is in the menu, under a heading, with its keys; the menu's set is the bindings' named
    /// actions, the ones that cannot run now included, so neither can hold an action the other lacks.
    /// </summary>
    [Fact]
    public void TheMenuListsWhatThePaletteListsUnderHeadingsWithItsKeys()
    {
        (string[] menu, string[] bound, string[] palette, string[] headings, (string Name, string Keys)[] keyed, bool emptyHeading) = Sta.Run(() =>
        {
            MainWindow window = Window();

            window.Add(TerminalTab.Open(Settings.Default, Shared, "local"));
            window.ShowMenuBar();

            MenuItem[] tops = [.. window.MenuBarMenu.Items.OfType<MenuItem>()];
            MenuItem[] items = [.. tops.SelectMany(top => top.Items.OfType<MenuItem>())];

            return (items.Select(System.Windows.Automation.AutomationProperties.GetName).Order(StringComparer.Ordinal).ToArray(),
                    Commands.From(window.InputBindings.OfType<InputBinding>(), runnable: false).Select(action => action.Name).Order(StringComparer.Ordinal).ToArray(),
                    window.Actions.Select(action => action.Name).ToArray(),
                    tops.Select(top => (string)top.Header).ToArray(),
                    items.Select(item => (System.Windows.Automation.AutomationProperties.GetName(item), item.InputGestureText)).ToArray(),
                    tops.Any(top => top.Items.Count == 0));
        });

        Assert.Equal(bound, menu);
        Assert.All(palette, name => Assert.Contains(name, menu));
        Assert.False(emptyHeading, "a heading with nothing under it was shown");
        Assert.All(headings, heading => Assert.Contains(heading, MenuBar.Headings));
        Assert.Equal("Ctrl+Shift+P", keyed.Single(item => item.Name == "Show all commands").Keys);
    }

    /// <summary>An action that cannot run now is in the menu, disabled, where the palette leaves it out.</summary>
    [Fact]
    public void AnActionThatCannotRunNowIsShownDisabled()
    {
        (bool listed, bool enabled, bool inPalette) = Sta.Run(() =>
        {
            MainWindow window = Window();

            window.Add(TerminalTab.Open(Settings.Default, Shared, "local"));
            window.ShowMenuBar();

            // One tab: there is no other tab to close.
            MenuItem? item = window.MenuBarMenu.Items.OfType<MenuItem>().SelectMany(top => top.Items.OfType<MenuItem>())
                .FirstOrDefault(each => System.Windows.Automation.AutomationProperties.GetName(each) == "Close other tabs");

            return (item is not null, item?.IsEnabled ?? true, window.Actions.Any(action => action.Name == "Close other tabs"));
        });

        Assert.True(listed, "Close other tabs is not in the menu with one tab open");
        Assert.False(enabled, "Close other tabs is enabled with no other tab");
        Assert.False(inPalette, "the palette offers Close other tabs with no other tab");
    }

    /// <summary>
    /// A default window shows no menu bar, as Block G promises; Always shows it at once and keeps it, the
    /// palette's entry writes that to the file, and Hidden lets the toggle do nothing.
    /// </summary>
    [Fact]
    public void ADefaultWindowHasNoMenuBarAndTheSettingDecides()
    {
        string settings = Path.Combine(_here, "settings.json");

        File.WriteAllText(settings, "{}");

        (bool atStart, bool toggled, bool hidden, bool kept, MenuBarMode written, bool hiddenToggled) = Sta.Run(() =>
        {
            MainWindow window = Window();

            window.SettingsPath = settings;
            window.Apply(Settings.Default);

            bool start = window.MenuBarShowing;

            window.ToggleMenuBar();

            bool shown = window.MenuBarShowing;

            window.HideMenuBar();

            bool gone = !window.MenuBarShowing;

            window.KeepMenuBar(always: true);
            window.HideMenuBar();

            bool stays = window.MenuBarShowing;
            MenuBarMode file = SettingsFile.ReadFrom(settings).MenuBar;

            window.Apply(Settings.Default with { MenuBar = MenuBarMode.Hidden });

            return (start, shown, gone, stays, file, window.MenuBarShowing);
        });

        Assert.False(atStart, "a default window opened with the menu bar on screen");
        Assert.True(toggled, "the toggle Alt makes did not show the bar");
        Assert.True(hidden, "the bar shown by Alt did not go");
        Assert.True(kept, "the bar kept on screen went when hidden");
        Assert.Equal(MenuBarMode.Always, written);
        Assert.False(hiddenToggled, "the bar stayed on screen with the setting at Hidden");
    }

    [Theory]
    [InlineData("Copy the selection", "_Edit")]
    [InlineData("Find in the scrollback", "_Edit")]
    [InlineData("Scroll back a page", "_Terminal")]
    [InlineData("Close other tabs", "Tab and _Pane")]
    [InlineData("Zoom pane", "Tab and _Pane")]
    [InlineData("Open session, recorded", "_Session")]
    [InlineData("Export saved passwords to a KeePass vault", "_Session")]
    [InlineData("Browse files", "T_ransfer")]
    [InlineData("Show forwards", "_Forward")]
    [InlineData("Stop every agent", "_Agents")]
    [InlineData("Send to Claude", "_Agents")]
    [InlineData("Settings", "_View")]
    [InlineData("Full screen", "_View")]
    [InlineData("Leave full screen", "_View")]
    [InlineData("Show all commands", "_Help")]
    [InlineData("Something nobody has named yet", "_Terminal")]
    public void AnActionsHeadingComesFromItsName(string name, string heading) => Assert.Equal(heading, MenuBar.HeadingOf(name));

    private MainWindow Window() =>
        new() { SessionsFile = Path.Combine(_here, "sessions.json"), SshConfigFile = Path.Combine(_here, "none"), SettingsPath = Path.Combine(_here, "settings.json") };
}
