using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Quickshell.App;
using Xunit;

namespace Quickshell.App.Tests;

/// <summary>
/// The tab strip the design draws (QS278), read off the element tree: one row 42 high, Home first,
/// each tab with its state, its title and a close button, the tab on screen underlined, New tab after
/// the last and Commands at the right.
/// </summary>
public sealed class TabStripTests : IDisposable
{
    /// <summary>The one device, atlas and render loop these tabs draw with. See QS49.</summary>
    private static readonly TerminalShare Shared = new();

    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"quickshell-strip-{Guid.NewGuid():N}");

    public TabStripTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    /// <summary>
    /// The row and its tabs at the design's sizes, each tab saying its state — a local shell here — and
    /// only the one on screen underlined and SemiBold.
    /// </summary>
    [Fact]
    public void TheStripIsTheDesigns()
    {
        (double row, double[] tabs, string[] states, bool[] underlined, string[] weights, double[] close, double[] adding, string keys) = Sta.Run(() =>
        {
            MainWindow window = Window();

            window.Add(TerminalTab.Open(Settings.Default, Shared, "first"));
            window.Add(TerminalTab.Open(Settings.Default, Shared, "second"));

            TabItem[] items = [.. Under(window).OfType<TabItem>()];
            Button plus = Find<Button>(window, "NewTab");

            return (Find<Border>(window, "TabStrip").Height,
                    items.Select(item => item.Height).ToArray(),
                    items.Select(AutomationProperties.GetItemStatus).ToArray(),
                    items.Select(item => Find<Border>(item, "Underline").Visibility == Visibility.Visible).ToArray(),
                    items.Select(item => Under(item).OfType<TextBlock>().Single(each => each.Text == AutomationProperties.GetName(item)).FontWeight.ToString()).ToArray(),
                    new[] { Find<Button>(items[0], "CloseTab").Width, Find<Button>(items[0], "CloseTab").Height },
                    new[] { plus.Width, plus.Height },
                    AutomationProperties.GetAcceleratorKey(Find<Button>(window, "Commands")));
        });

        Assert.Equal(MainWindow.StripHeight, row);
        Assert.Equal([38d, 38d], tabs);
        Assert.Equal(["Local", "Local"], states);
        Assert.Equal([false, true], underlined);
        Assert.Equal(["Normal", "SemiBold"], weights);
        Assert.Equal([22d, 22d], close);
        Assert.Equal([30d, 28d], adding);
        Assert.Equal("Ctrl+Shift+P", keys);
    }

    /// <summary>A tab's own close button closes that tab, whichever is on screen.</summary>
    [Fact]
    public void ATabsCloseButtonClosesThatTab()
    {
        string[] left = Sta.Run(() =>
        {
            MainWindow window = Window();

            window.Add(TerminalTab.Open(Settings.Default, Shared, "first"));
            window.Add(TerminalTab.Open(Settings.Default, Shared, "second"));

            TabItem first = Under(window).OfType<TabItem>().First();

            Find<Button>(first, "CloseTab").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

            return window.Held.Select(tab => tab.Host).ToArray();
        });

        Assert.Equal(["second"], left);
    }

    /// <summary>A middle click on the second of three tabs closes it, and the first stays on screen (QS310).</summary>
    [Fact]
    public void AMiddleClickClosesTheTabUnderThePointerAndNotTheOneOnScreen()
    {
        (string[] left, string? onScreen) = Sta.Run(() =>
        {
            MainWindow window = Window();

            window.Add(TerminalTab.Open(Settings.Default, Shared, "first"));
            window.Add(TerminalTab.Open(Settings.Default, Shared, "second"));
            window.Add(TerminalTab.Open(Settings.Default, Shared, "third"));
            window.Active = 0;

            MiddleClick(Under(window).OfType<TabItem>().ElementAt(1));

            return (window.Held.Select(tab => tab.Host).ToArray(), window.Current?.Host);
        });

        Assert.Equal(["first", "third"], left);
        Assert.Equal("first", onScreen);
    }

    /// <summary>A middle click on a tab whose session is live asks first, as the close button does, and keeps it on No.</summary>
    [Fact]
    public void AMiddleClickOnALiveSessionAsksFirst()
    {
        (int asked, int open) = Sta.Run(() =>
        {
            int times = 0;

            MainWindow window = Window();

            window.Guard = CloseGuard.Asking();
            window.AskingToClose = _ =>
            {
                times++;

                return ClosingAnswer.Stay;
            };

            TerminalTab live = TerminalTab.Open(Settings.Default, Shared, "cmd.exe");

            window.Add(TerminalTab.Open(Settings.Default, Shared, "first"));
            window.Add(live);
            live.ConnectAsync("cmd.exe /d /k").GetAwaiter().GetResult();
            Assert.True(live.IsLive, "the shell this test needs did not start");

            window.Active = 0;

            MiddleClick(Under(window).OfType<TabItem>().ElementAt(1));

            int stillOpen = window.Held.Count;

            live.DisposeAsync().AsTask().GetAwaiter().GetResult();

            return (times, stillOpen);
        });

        Assert.Equal(1, asked);
        Assert.Equal(2, open);
    }

    /// <summary>A middle-button press and release on an element, as the pointer would send them.</summary>
    private static void MiddleClick(UIElement on)
    {
        on.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Middle)
        {
            RoutedEvent = UIElement.PreviewMouseDownEvent,
        });

        on.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Middle)
        {
            RoutedEvent = UIElement.PreviewMouseUpEvent,
        });
    }

    /// <summary>While Home is on screen it is the strip's underlined tab, and the tab the strip still has selected is not.</summary>
    [Fact]
    public void HomeIsUnderlinedWhileItShowsAndNoTabIs()
    {
        (bool home, bool tab, bool homeAfter, bool tabAfter) = Sta.Run(() =>
        {
            MainWindow window = Window();

            window.Add(TerminalTab.Open(Settings.Default, Shared, "local"));
            window.ShowHome();

            Button button = Find<Button>(window, "HomeButton");
            TabItem item = Under(window).OfType<TabItem>().Single();

            bool homeLine = Find<Border>(button, "Underline").Visibility == Visibility.Visible;
            bool tabLine = Find<Border>(item, "Underline").Visibility == Visibility.Visible;

            window.Active = 0;

            return (homeLine, tabLine,
                    Find<Border>(button, "Underline").Visibility == Visibility.Visible,
                    Find<Border>(item, "Underline").Visibility == Visibility.Visible);
        });

        Assert.True(home, "Home was on screen and not underlined");
        Assert.False(tab, "a tab was underlined while Home was on screen");
        Assert.False(homeAfter);
        Assert.True(tabAfter, "the tab chosen from Home was not underlined");
    }

    /// <summary>One tab and no Home is no choice, and the row goes with the strip: New tab and Commands are not a strip on their own.</summary>
    [Fact]
    public void TheRowGoesWithTheStrip()
    {
        Visibility row = Sta.Run(() =>
        {
            MainWindow window = Window();

            window.Add(TerminalTab.Open(Settings.Default, Shared, "only"));

            return Find<Border>(window, "TabStrip").Visibility;
        });

        Assert.Equal(Visibility.Collapsed, row);
    }

    private MainWindow Window() =>
        new() { SessionsFile = Path.Combine(_directory, "sessions.json"), SshConfigFile = Path.Combine(_directory, "none") };

    private static T Find<T>(DependencyObject root, string id) where T : DependencyObject =>
        Under(root).OfType<T>().First(each => AutomationProperties.GetAutomationId(each) == id);

    /// <summary>Everything under an element in the logical tree, which is there before any layout.</summary>
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
