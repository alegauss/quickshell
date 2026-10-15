using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

using Xunit;

namespace Quickshell.App.Tests;

/// <summary>
/// QS317: a pane opened by Split closes on its own — by the cross in the strip above it, by
/// Ctrl+Shift+W while its tab has others, and by Close pane in the palette — through the one
/// ClosePane path that asks first for a live session.
/// </summary>
public sealed class PaneCloseTests : IDisposable
{
    private static readonly TerminalShare Shared = new();

    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"quickshell-paneclose-{Guid.NewGuid():N}");

    public PaneCloseTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    /// <summary>
    /// Each pane of a split tab carries a cross above it; the second pane's closes the second pane,
    /// whichever has the keyboard, and the one left has none, its tab being one pane again.
    /// </summary>
    [Fact]
    public void ThePaneCloseButtonClosesThePaneItIsOn()
    {
        (int crossesSplit, int panesAfter, bool firstLeft, int crossesAfter) = Sta.Run(() =>
        {
            MainWindow window = Shown();
            TerminalTab tab = TerminalTab.Open(Settings.Default, Shared, "first");

            window.Add(tab);
            TerminalLeaf first = tab.Focused;

            window.SplitPane(Divide.Beside, "second");
            window.UpdateLayout();

            TerminalLeaf second = tab.Focused;
            tab.Focus(tab.PaneOf(first));

            Button[] crosses = Visible(window);
            Button onSecond = crosses.OrderBy(cross => Canvas.GetLeft(cross)).Last();

            onSecond.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            window.UpdateLayout();

            int after = Visible(window).Length;
            bool kept = tab.Leaves.Contains(first) && !tab.Leaves.Contains(second);

            window.Close();

            return (crosses.Length, tab.Layout.Count, kept, after);
        });

        Assert.Equal(2, crossesSplit);
        Assert.Equal(1, panesAfter);
        Assert.True(firstLeft, "the cross closed a pane other than the one it was on");
        Assert.Equal(0, crossesAfter);
    }

    /// <summary>Ctrl+Shift+W closes a pane while its tab has two, and the tab once it has one.</summary>
    [Fact]
    public void TheChordClosesAPaneThenTheTab()
    {
        (int panes, int tabsAfterPane, int tabsAfterTab) = Sta.Run(() =>
        {
            MainWindow window = Shown();
            TerminalTab tab = TerminalTab.Open(Settings.Default, Shared, "first");

            window.Add(TerminalTab.Open(Settings.Default, Shared, "other"));
            window.Add(tab);
            window.SplitPane(Divide.Below, "second");

            Command chord = window.Actions.Single(one => one.Name == "Close pane or tab");

            chord.Run();
            int left = tab.Layout.Count;
            int tabs = window.Held.Count;

            chord.Run();
            int tabsAfter = window.Held.Count;

            window.Close();

            return (left, tabs, tabsAfter);
        });

        Assert.Equal(1, panes);
        Assert.Equal(2, tabsAfterPane);
        Assert.Equal(1, tabsAfterTab);
    }

    /// <summary>Close pane is in the palette while the tab has another pane, and not otherwise.</summary>
    [Fact]
    public void ClosePaneIsInThePaletteOnlyWhereThereIsAnotherPane()
    {
        (bool single, bool split) = Sta.Run(() =>
        {
            MainWindow window = new();

            window.Add(TerminalTab.Open(Settings.Default, Shared, "first"));

            bool alone = window.Actions.Any(one => one.Name == "Close pane");

            window.SplitPane(Divide.Beside, "second");

            return (alone, window.Actions.Any(one => one.Name == "Close pane"));
        });

        Assert.False(single);
        Assert.True(split);
    }

    /// <summary>A window shown off the desk and without the focus, so it lays its panes out.</summary>
    private MainWindow Shown()
    {
        MainWindow window = new()
        {
            SessionsFile = Path.Combine(_directory, "sessions.json"),
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -20000,
            Top = -20000,
            Width = 1000,
            Height = 700,
            ShowActivated = false,
        };

        window.Show();

        return window;
    }

    private static Button[] Visible(MainWindow window) =>
        [.. Under(window).OfType<Button>()
                         .Where(button => System.Windows.Automation.AutomationProperties.GetAutomationId(button) == "PaneClose"
                                          && button.Visibility == Visibility.Visible)];

    private static IEnumerable<DependencyObject> Under(DependencyObject root)
    {
        yield return root;

        foreach (DependencyObject child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            foreach (DependencyObject below in Under(child))
            {
                yield return below;
            }
        }
    }
}
