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

    /// <summary>Close tabs to the right of the second of four leaves two, the second on screen where the one showing went (QS311).</summary>
    [Fact]
    public void ClosingTheTabsToTheRightLeavesTheTabItOpenedOn()
    {
        (string[] left, string? onScreen) = Sta.Run(() =>
        {
            MainWindow window = Window();

            window.Add(TerminalTab.Open(Settings.Default, Shared, "first"));
            window.Add(TerminalTab.Open(Settings.Default, Shared, "second"));
            window.Add(TerminalTab.Open(Settings.Default, Shared, "third"));
            window.Add(TerminalTab.Open(Settings.Default, Shared, "fourth"));

            window.CloseTabsToTheRight(1);

            return (window.Held.Select(tab => tab.Host).ToArray(), window.Current?.Host);
        });

        Assert.Equal(["first", "second"], left);
        Assert.Equal("second", onScreen);
    }

    /// <summary>Closing the others with two live sessions among them asks once, naming both, and closes none on No.</summary>
    [Fact]
    public void ClosingTheOthersAsksOnceForEveryLiveSession()
    {
        (List<int> asked, int afterNo, string[] afterYes) = Sta.Run(() =>
        {
            List<int> questions = [];
            bool answer = false;

            MainWindow window = Window();

            window.Guard = CloseGuard.Asking();
            window.AskingToClose = question =>
            {
                questions.Add(question.Open.Count);

                return new ClosingAnswer(Close: answer, NeverAgain: false);
            };

            TerminalTab[] live = [TerminalTab.Open(Settings.Default, Shared, "cmd.exe"), TerminalTab.Open(Settings.Default, Shared, "cmd.exe")];

            window.Add(TerminalTab.Open(Settings.Default, Shared, "kept"));
            window.Add(live[0]);
            window.Add(TerminalTab.Open(Settings.Default, Shared, "ended"));
            window.Add(live[1]);

            foreach (TerminalTab tab in live)
            {
                tab.ConnectAsync("cmd.exe /d /k").GetAwaiter().GetResult();
                Assert.True(tab.IsLive, "the shell this test needs did not start");
            }

            window.CloseOtherTabs(0);
            int stillOpen = window.Held.Count;

            answer = true;
            window.CloseOtherTabs(0);

            foreach (TerminalTab tab in live)
            {
                tab.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }

            return (questions, stillOpen, window.Held.Select(tab => tab.Host).ToArray());
        });

        Assert.Equal([2, 2], asked);
        Assert.Equal(4, afterNo);
        Assert.Equal(["kept"], afterYes);
    }

    /// <summary>
    /// A tab's menu names its three entries in order, says which can run for that tab, and choosing
    /// Close tabs to the right on the second of four leaves two.
    /// </summary>
    [Fact]
    public void ATabsMenuOffersWhatItsTabCanDo()
    {
        (string[] names, bool[] second, bool[] last, int left) = Sta.Run(() =>
        {
            MainWindow window = Window();

            window.Add(TerminalTab.Open(Settings.Default, Shared, "first"));
            window.Add(TerminalTab.Open(Settings.Default, Shared, "second"));
            window.Add(TerminalTab.Open(Settings.Default, Shared, "third"));
            window.Add(TerminalTab.Open(Settings.Default, Shared, "fourth"));

            MenuItem[] onSecond = [.. window.TabMenu(1).Items.OfType<MenuItem>()];
            MenuItem[] onLast = [.. window.TabMenu(3).Items.OfType<MenuItem>()];

            onSecond[2].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

            return (onSecond.Select(entry => (string)entry.Header).ToArray(),
                    onSecond.Select(entry => entry.IsEnabled).ToArray(),
                    onLast.Select(entry => entry.IsEnabled).ToArray(),
                    window.Held.Count);
        });

        Assert.Equal(["Close", "Close other tabs", "Close tabs to the right"], names);
        Assert.Equal([true, true, true], second);
        Assert.Equal([true, true, false], last);
        Assert.Equal(2, left);
    }

    /// <summary>Each of the menu's three is also in the palette, so the menu is never the only way to it.</summary>
    [Fact]
    public void TheMenusCommandsAreInThePalette()
    {
        string[] names = Sta.Run(() =>
        {
            MainWindow window = Window();

            window.Add(TerminalTab.Open(Settings.Default, Shared, "first"));
            window.Add(TerminalTab.Open(Settings.Default, Shared, "second"));
            window.Active = 0;

            return window.Actions.Select(one => one.Name).ToArray();
        });

        Assert.Contains("Close tab", names);
        Assert.Contains("Close other tabs", names);
        Assert.Contains("Close tabs to the right", names);
    }

    /// <summary>
    /// A tab's close cross paints, at 10 points or more, inside its button and its tab, for a short
    /// title and a long one; New tab's plus is drawn the same way and is the measure the probe is
    /// checked against (QS312). The cross was in the tree, laid out, and painted nothing.
    /// </summary>
    [Fact]
    public void ATabsCloseCrossPaintsInsideItsTab()
    {
        (int plus, (int Inked, double FontSize, bool InButton, bool InTab)[] tabs) = Sta.Run(() =>
        {
            MainWindow window = Window();

            window.Add(TerminalTab.Open(Settings.Default, Shared, "a"));
            window.Add(TerminalTab.Open(Settings.Default, Shared, "a-much-longer-host-name.example.invalid"));

            // Shown, off the desk and without the focus, so the theme's styles reach it as they do a
            // client's window.
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = -20000;
            window.Top = -20000;
            window.ShowActivated = false;
            window.Show();
            window.UpdateLayout();

            int measure = Inked(Find<Button>(window, "NewTab"));

            (int, double, bool, bool)[] laid = [.. Under(window).OfType<TabItem>().Select(item =>
            {
                Button close = Find<Button>(item, "CloseTab");
                TextBlock glyph = Under(close).OfType<TextBlock>().Single();

                Rect inItem = glyph.TransformToAncestor(item).TransformBounds(new Rect(glyph.RenderSize));
                Rect inClose = glyph.TransformToAncestor(close).TransformBounds(new Rect(glyph.RenderSize));

                return (Inked(close), glyph.FontSize, new Rect(close.RenderSize).Contains(inClose), new Rect(item.RenderSize).Contains(inItem));
            })];

            window.Close();

            return (measure, laid);
        });

        Assert.True(plus > 6, $"the probe reads New tab's plus as {plus} pixels, so it reads nothing");
        Assert.All(tabs, tab =>
        {
            Assert.True(tab.Inked > 6, $"the cross paints {tab.Inked} pixels");
            Assert.True(tab.FontSize >= 10, $"the cross is {tab.FontSize} points");
            Assert.True(tab.InButton, "the cross is outside its button");
            Assert.True(tab.InTab, "the cross is outside its tab");
        });
    }

    /// <summary>
    /// How many pixels an element paints, drawn through a brush of itself so its place in its parent
    /// does not push it outside the picture.
    /// </summary>
    private static int Inked(FrameworkElement element)
    {
        System.Windows.Media.DrawingVisual drawn = new();

        using (System.Windows.Media.DrawingContext context = drawn.RenderOpen())
        {
            context.DrawRectangle(new System.Windows.Media.VisualBrush(element), null, new Rect(element.RenderSize));
        }

        System.Windows.Media.Imaging.RenderTargetBitmap shot = new(
            (int)Math.Ceiling(element.ActualWidth), (int)Math.Ceiling(element.ActualHeight), 96, 96,
            System.Windows.Media.PixelFormats.Pbgra32);

        shot.Render(drawn);

        byte[] pixels = new byte[shot.PixelWidth * shot.PixelHeight * 4];

        shot.CopyPixels(pixels, shot.PixelWidth * 4, 0);

        return Enumerable.Range(0, pixels.Length / 4).Count(at => pixels[(at * 4) + 3] > 60);
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
