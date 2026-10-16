using System.IO;
using Quickshell.App;
using Xunit;

namespace Quickshell.App.Tests;

/// <summary>
/// The Home page (QS272): a way to a server on screen, in place of the terminal and without taking
/// the terminal away.
/// </summary>
public sealed class HomeTests : IDisposable
{
    /// <summary>The one device, atlas and render loop these tabs draw with. See QS49.</summary>
    private static readonly TerminalShare Shared = new();

    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"quickshell-home-{Guid.NewGuid():N}");

    public HomeTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private MainWindow Window(Action<MainWindow>? also = null)
    {
        string store = Path.Combine(_directory, "sessions.json");

        File.WriteAllText(store, """
            { "Name": "", "Children": [
                { "Name": "Work", "Children": [
                    { "Name": "db-primary", "Host": "db1.example.com", "Tags": ["prod"], "Settings": { "User": "deploy" } },
                    { "Name": "build-box", "Host": "build.example.com" }
                ] },
                { "Name": "pi", "Host": "pi.lan" },
                { "Name": "nas", "Host": "nas.lan" }
            ] }
            """);

        MainWindow window = new() { SessionsFile = store, SshConfigFile = Path.Combine(_directory, "none") };

        also?.Invoke(window);
        window.RefreshSessions();

        return window;
    }

    /// <summary>
    /// QS272's falsification turned around: Home is a way to a server on screen — quick connect and
    /// the sessions as cards — and while it is, no terminal is current, so nothing typed or copied
    /// reaches a shell nobody can see.
    /// </summary>
    [Fact]
    public void HomeIsAWayToAServerInPlaceOfTheTerminal()
    {
        (bool showing, bool current, string title, bool strip, string heading, string[] cards) = Sta.Run(() =>
        {
            MainWindow window = Window();

            window.Add(TerminalTab.Open(Settings.Default, Shared, "local"));

            HomePage home = window.ShowHome();

            return (window.HomeShowing, window.Current is not null, window.Title, window.TabStripShowing,
                    home.CardsHeading, home.Cards.ToArray());
        });

        Assert.True(showing);
        Assert.False(current, "a terminal was current while Home was on screen");
        Assert.StartsWith("Home — ", title, StringComparison.Ordinal);
        Assert.True(strip, "one tab and Home is a choice, and the strip did not show it");
        Assert.Equal("Saved sessions", heading);
        Assert.Equal(["Work/db-primary", "Work/build-box", "pi"], cards);
    }

    /// <summary>Choosing the tab that was showing brings it back, and Home stays on the strip.</summary>
    [Fact]
    public void ChoosingATabLeavesHomeOpen()
    {
        (bool showing, bool open, bool current) = Sta.Run(() =>
        {
            MainWindow window = Window();

            window.Add(TerminalTab.Open(Settings.Default, Shared, "local"));
            window.ShowHome();
            window.Active = 0;

            return (window.HomeShowing, window.HomeOpen, window.Current is not null);
        });

        Assert.False(showing);
        Assert.True(open);
        Assert.True(current);
    }

    /// <summary>With Home open, the last tab closing goes back to Home rather than closing the window.</summary>
    [Fact]
    public void TheLastTabClosingGoesBackToHome()
    {
        (bool showing, bool closed) = Sta.Run(() =>
        {
            bool shut = false;
            MainWindow window = Window(made => made.Closed += (_, _) => shut = true);

            window.Add(TerminalTab.Open(Settings.Default, Shared, "local"));
            window.ShowHome();
            window.Active = 0;
            window.CloseTab();

            return (window.HomeShowing, shut);
        });

        Assert.True(showing);
        Assert.False(closed, "the window closed with Home open");
    }

    /// <summary>Closing Home takes its button off the strip and puts the tab back.</summary>
    [Fact]
    public void ClosingHomePutsTheTabBack()
    {
        (bool open, bool current, bool strip) = Sta.Run(() =>
        {
            MainWindow window = Window();

            window.Add(TerminalTab.Open(Settings.Default, Shared, "local"));
            window.ShowHome();
            window.Actions.Single(action => action.Name == "Close Home").Run();

            return (window.HomeOpen, window.Current is not null, window.TabStripShowing);
        });

        Assert.False(open);
        Assert.True(current);
        Assert.False(strip, "one tab and no Home is no choice, and the strip stayed");
    }

    /// <summary>
    /// QS279: a session whose connection went live is the first card in the next window too — the list
    /// outlives the window that made it — with when it connected in words.
    /// </summary>
    [Fact]
    public void ASessionThatConnectedIsTheFirstCardAfterARestart()
    {
        DateTimeOffset noon = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);

        (string heading, string[] cards, string[] when) = Sta.Run(() =>
        {
            MainWindow first = Window(made => made.Clock = () => noon);

            first.SessionWentLive("nas");
            first.SessionWentLive("Work/db-primary");

            // Asked for and never connected: not recent.
            first.OpensSession = _ => { };
            first.ConnectSession("pi");

            MainWindow next = Window(made => made.Clock = () => noon.AddDays(1));

            next.Add(TerminalTab.Open(Settings.Default, Shared, "local"));

            HomePage home = next.ShowHome();

            return (home.CardsHeading, home.Cards.ToArray(),
                    Under(home).OfType<System.Windows.Controls.TextBlock>()
                               .Where(each => System.Windows.Automation.AutomationProperties.GetAutomationId(each) == "When")
                               .Select(each => each.Text).ToArray());
        });

        Assert.Equal("Recent", heading);
        Assert.Equal(["Work/db-primary", "nas"], cards);
        Assert.Equal(["Yesterday", "Yesterday"], when);
    }

    /// <summary>
    /// QS279's cards, at the design's sizes: three columns, padding 16, radius 8, a 34-pixel tile, the
    /// folder as the first chip, and a 28-high Connect.
    /// </summary>
    [Fact]
    public void TheCardsAreTheDesigns()
    {
        (int columns, double padding, double radius, double tile, string firstChip, double connect) = Sta.Run(() =>
        {
            MainWindow window = Window();

            window.Add(TerminalTab.Open(Settings.Default, Shared, "local"));
            window.SessionWentLive("Work/db-primary");

            HomePage home = window.ShowHome();

            System.Windows.Controls.Primitives.UniformGrid grid = Find<System.Windows.Controls.Primitives.UniformGrid>(home, "HomeSessions");
            System.Windows.Controls.Border card = grid.Children.OfType<System.Windows.Controls.Border>().First();
            System.Windows.Controls.WrapPanel chips = Find<System.Windows.Controls.WrapPanel>(card, "Chips");

            return (grid.Columns, card.Padding.Left, card.CornerRadius.TopLeft, Find<System.Windows.Controls.Border>(card, "Tile").Width,
                    ((System.Windows.Controls.TextBlock)((System.Windows.Controls.Border)chips.Children[0]).Child).Text,
                    Find<System.Windows.Controls.Button>(card, "CardConnect").Height);
        });

        Assert.Equal(3, columns);
        Assert.Equal(16, padding);
        Assert.Equal(8, radius);
        Assert.Equal(34, tile);
        Assert.Equal("Work", firstChip);
        Assert.Equal(28, connect);
    }

    /// <summary>
    /// A card whose session a tab holds offers Go to tab, which puts that tab on screen and opens
    /// nothing, and New connection beside it, which opens another; with no tab, Connect again (QS313).
    /// </summary>
    [Fact]
    public void ACardWhoseSessionIsOpenGoesToItsTab()
    {
        (string[] heldButtons, string? onScreen, bool homeAfter, int tabsAfter, int opened, string[] freeButtons) = Sta.Run(() =>
        {
            int opens = 0;
            MainWindow window = Window();

            window.OpensSession = _ => opens++;

            TerminalTab db = TerminalTab.Open(Settings.Default, Shared, "db1");

            db.Saved = "Work/db-primary";
            window.Add(TerminalTab.Open(Settings.Default, Shared, "local"));
            window.Add(db);
            window.Active = 0;
            window.SessionWentLive("Work/db-primary");

            HomePage home = window.ShowHome();
            System.Windows.Controls.Border card = Card(home, "Work/db-primary");
            string[] held = [.. Buttons(card)];

            Find<System.Windows.Controls.Button>(card, "CardGoTo")
                .RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));

            string? current = window.Current?.Host;
            bool homeShowing = window.HomeShowing;
            int count = window.Held.Count;

            home = window.ShowHome();
            Find<System.Windows.Controls.Button>(Card(home, "Work/db-primary"), "CardNewConnection")
                .RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));

            window.CloseTab(1);
            home = window.ShowHome();

            return (held, current, homeShowing, count, opens, Buttons(Card(home, "Work/db-primary")).ToArray());
        });

        Assert.Equal(["CardNewConnection", "CardGoTo"], heldButtons);
        Assert.Equal("db1", onScreen);
        Assert.False(homeAfter, "Go to tab left Home on screen");
        Assert.Equal(2, tabsAfter);
        Assert.Equal(1, opened);
        Assert.Equal(["CardConnect"], freeButtons);
    }

    /// <summary>The sidebar's menu for a session a tab holds leads with Go to tab and New connection, and with Connect otherwise.</summary>
    [Fact]
    public void TheSidebarsMenuGoesToTheTabWhereOneHoldsIt()
    {
        (string[] held, string[] free) = Sta.Run(() =>
        {
            MainWindow window = Window();
            TerminalTab db = TerminalTab.Open(Settings.Default, Shared, "db1");

            db.Saved = "Work/db-primary";
            window.Add(db);
            window.RefreshSessions();

            // Folders start folded (QS304), so the folder is opened for its session to be listed.
            window.ToggleFolder("Work");

            SessionRow primary = window.Sidebar.Rows.Single(row => row.Path == "Work/db-primary");
            SessionRow other = window.Sidebar.Rows.Single(row => row.Path == "nas");

            static string[] Headers(System.Windows.Controls.ContextMenu menu) =>
                [.. menu.Items.OfType<System.Windows.Controls.MenuItem>().Take(2).Select(entry => (string)entry.Header)];

            return (Headers(window.Sidebar.Menu(primary)), Headers(window.Sidebar.Menu(other)));
        });

        Assert.Equal(["Go to tab", "New connection"], held);
        Assert.Equal(["Connect", "Edit…"], free);
    }

    /// <summary>
    /// QS353: a connected card too narrow for its two buttons side by side, as in a restored window, wraps
    /// Go to tab under New connection; neither is cut, both lie inside the card, and the when has a row of
    /// its own rather than the buttons' cell. Wide enough, they sit side by side.
    /// </summary>
    [Fact]
    public void ANarrowConnectedCardWrapsItsButtonsInsideIt()
    {
        (bool clipped, bool inside, bool wrapped, bool besideWhenWide, bool whenAbove) = Sta.Run(() =>
        {
            MainWindow window = Window();
            TerminalTab db = TerminalTab.Open(Settings.Default, Shared, "db1");

            db.Saved = "Work/db-primary";
            window.Add(db);
            window.SessionWentLive("Work/db-primary");

            System.Windows.Controls.Border card = Card(window.ShowHome(), "Work/db-primary");
            System.Windows.Controls.Button another = Find<System.Windows.Controls.Button>(card, "CardNewConnection");
            System.Windows.Controls.Button go = Find<System.Windows.Controls.Button>(card, "CardGoTo");
            System.Windows.FrameworkElement when = Find<System.Windows.FrameworkElement>(card, "When");

            // Home's body laid out at a width, as a restored window gives it one: the card is a third of it.
            // The body and not the window, because a window never shown never templates Home's scroller, so
            // the body is in no visual tree the window's layout would reach.
            System.Windows.FrameworkElement body = card;

            while (System.Windows.Media.VisualTreeHelper.GetParent(body) is System.Windows.FrameworkElement above)
            {
                body = above;
            }

            void Lay(double width)
            {
                body.Measure(new System.Windows.Size(width, double.PositiveInfinity));
                body.Arrange(new System.Windows.Rect(0, 0, width, body.DesiredSize.Height));
                body.UpdateLayout();
            }

            Lay(2400);

            bool beside = Math.Abs(Top(go, card) - Top(another, card)) < 1;
            double inner = card.Padding.Left + card.Padding.Right + card.BorderThickness.Left + card.BorderThickness.Right;
            double widest = Math.Max(another.DesiredSize.Width, go.DesiredSize.Width);

            // Down from wide, to the first width at which the card is narrower than both buttons side by side
            // and still holds the wider one: the restored window of the report.
            // A desired width includes the button's margin, and the two side by side need both.
            for (double width = 2400; width > 120 && card.ActualWidth - inner >= another.DesiredSize.Width + go.DesiredSize.Width; width -= 8)
            {
                Lay(width);
            }

            Assert.True(card.ActualWidth - inner >= widest, $"the card at {card.ActualWidth} could not hold even one button");

            // Cut is drawn narrower than it asked for, its margin aside.
            bool cut = new[] { another, go }.Any(button => button.RenderSize.Width + button.Margin.Left + button.Margin.Right + 0.5 < button.DesiredSize.Width);
            bool within = new[] { another, go }.All(button => Left(button, card) >= 0 && Left(button, card) + button.RenderSize.Width <= card.ActualWidth + 0.5);

            return (cut, within, Top(go, card) > Top(another, card), beside, Top(when, card) + when.RenderSize.Height <= Top(another, card) + 0.5);
        });

        Assert.False(clipped, "a button on a narrow card was cut");
        Assert.True(inside, "a button on a narrow card lay outside it");
        Assert.True(wrapped, "Go to tab did not move under New connection on a narrow card");
        Assert.True(besideWhenWide, "the two buttons were not side by side on a wide card");
        Assert.True(whenAbove, "the when shares the buttons' row instead of one of its own above them");

        static double Left(System.Windows.UIElement element, System.Windows.UIElement card) =>
            element.TranslatePoint(new System.Windows.Point(0, 0), card).X;
    }

    private static double Top(System.Windows.UIElement element, System.Windows.UIElement card) =>
        element.TranslatePoint(new System.Windows.Point(0, 0), card).Y;

    private static System.Windows.Controls.Border Card(HomePage home, string path) =>
        Find<System.Windows.Controls.Primitives.UniformGrid>(home, "HomeSessions").Children.OfType<System.Windows.Controls.Border>()
            .Single(card => System.Windows.Automation.AutomationProperties.GetName(card) == path);

    private static IEnumerable<string> Buttons(System.Windows.DependencyObject card) =>
        Under(card).OfType<System.Windows.Controls.Button>().Select(System.Windows.Automation.AutomationProperties.GetAutomationId);

    /// <summary>Show all sessions opens a folded sidebar, where the rest of the sessions are.</summary>
    [Fact]
    public void ShowAllSessionsOpensTheSidebar()
    {
        (bool before, bool after) = Sta.Run(() =>
        {
            MainWindow window = Window();

            window.Add(TerminalTab.Open(Settings.Default, Shared, "local"));
            window.Sidebar.Shape(open: false);

            HomePage home = window.ShowHome();
            bool folded = window.SidebarShowing;

            Find<System.Windows.Controls.Button>(home, "ShowAllSessions")
                .RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));

            return (folded, window.SidebarShowing);
        });

        Assert.False(before);
        Assert.True(after, "Show all sessions left the sidebar folded");
    }

    private static T Find<T>(System.Windows.DependencyObject root, string id) where T : System.Windows.DependencyObject =>
        Under(root).OfType<T>().First(each => System.Windows.Automation.AutomationProperties.GetAutomationId(each) == id);

    /// <summary>A rename or a move carries a session's place in the recent list, and a delete drops it.</summary>
    [Fact]
    public void ARenameOrAMoveCarriesItsPlaceAndADeleteDropsIt()
    {
        (string[] moved, string[] deleted) = Sta.Run(() =>
        {
            MainWindow window = Window();

            window.SessionWentLive("pi");
            window.SessionWentLive("Work/db-primary");
            window.MoveSession("Work/db-primary", string.Empty);

            string[] afterMove = [.. window.Recents().Paths];

            window.AsksToDelete = _ => true;
            window.DeleteSession("pi");

            return (afterMove, window.Recents().Paths.ToArray());
        });

        Assert.Equal(["db-primary", "pi"], moved);
        Assert.Equal(["db-primary"], deleted);
    }

    /// <summary>Everything under an element in the logical tree.</summary>
    private static IEnumerable<System.Windows.DependencyObject> Under(System.Windows.DependencyObject root)
    {
        yield return root;

        foreach (System.Windows.DependencyObject child in System.Windows.LogicalTreeHelper.GetChildren(root).OfType<System.Windows.DependencyObject>())
        {
            foreach (System.Windows.DependencyObject below in Under(child))
            {
                yield return below;
            }
        }
    }

    /// <summary>The palette offers Home, and a window that never opened it holds none of it.</summary>
    [Fact]
    public void ThePaletteOffersHomeAndAWindowWithoutItHoldsNone()
    {
        (bool before, bool after) = Sta.Run(() =>
        {
            MainWindow window = Window();

            window.Add(TerminalTab.Open(Settings.Default, Shared, "local"));

            bool none = window.Home is null;

            window.Actions.Single(action => action.Name == "Home").Run();

            return (none, window.HomeShowing);
        });

        Assert.True(before);
        Assert.True(after);
    }
}
