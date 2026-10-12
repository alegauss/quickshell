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
