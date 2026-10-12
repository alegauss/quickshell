using Quickshell.App;
using Quickshell.Transport;
using Xunit;

namespace Quickshell.App.Tests;

/// <summary>
/// The session bar and the status bar over a remote tab (QS269): what they say, and that a window of
/// local shells has neither. The bars against a real connection are in
/// <see cref="RemoteShellTests.TheSessionBarSaysWhereATabGoesAndWhichForwardsItHolds"/>.
/// </summary>
public sealed class ConnectionBarTests
{
    /// <summary>The one device, atlas and render loop these tabs draw with. See QS49.</summary>
    private static readonly TerminalShare Shared = new();

    private static ConnectionView View(ConnectionState state = ConnectionState.Connected, string? through = "bastion",
                                       params string[] forwards) =>
        new("db-primary", "deploy@db1.example.com:2222", through, state, forwards, Reconnects: true,
            "xterm-256color", 120, 34);

    // ---- The words ----

    [Theory]
    [InlineData(SessionState.Live, false, ConnectionState.Connected)]
    [InlineData(SessionState.Waiting, false, ConnectionState.Reconnecting)]
    [InlineData(SessionState.Connecting, false, ConnectionState.Reconnecting)]
    [InlineData(SessionState.Ended, false, ConnectionState.Disconnected)]
    [InlineData(SessionState.Live, true, ConnectionState.Disconnected)]
    public void AStateIsOneOfThreeWords(SessionState session, bool ended, ConnectionState expected)
    {
        ConnectionState state = ConnectionView.StateOf(new SessionStatus(session, 1, TimeSpan.Zero, string.Empty), ended);

        Assert.Equal(expected, state);
        Assert.Contains(ConnectionView.Word(state), (string[])["Connected", "Reconnecting", "Disconnected"]);
    }

    [Fact]
    public void TheRouteNamesItsJumpHostAndThePortAlways()
    {
        Assert.Equal("deploy@db1.example.com:2222 · via bastion", View().Address);
        Assert.Equal("deploy@db1.example.com:2222", View(through: null).Address);
        Assert.Equal("probe@web:22", ConnectionView.RouteOf(SshEndpoint.For("web", "probe")));
    }

    [Fact]
    public void TheStatusBarSaysTheStateTheRouteAndTheGrid()
    {
        ConnectionView view = View();

        Assert.Equal("Connected to db1.example.com through bastion", view.Status);
        Assert.Equal("Reconnecting to db1.example.com", View(ConnectionState.Reconnecting, through: null).Status);
        Assert.Equal("Reconnects after a drop", view.Reconnecting);
        Assert.Equal("Does not reconnect", (view with { Reconnects = false }).Reconnecting);
        Assert.Equal("120 × 34", view.Grid);
    }

    [Fact]
    public void AForwardsChipSaysWhereItListensAndWhereItGoes()
    {
        Assert.Equal("Forward 5432 → localhost:5432",
                     ConnectionView.Chip(new StartedForward(new ForwardSpec(ForwardKind.Local, 5432, "localhost", 5432), 5432)));
        Assert.Equal("Remote 8080 → localhost:80",
                     ConnectionView.Chip(new StartedForward(new ForwardSpec(ForwardKind.Remote, 8080, "localhost", 80), 8080)));
        Assert.Equal("SOCKS 1080", ConnectionView.Chip(new StartedForward(new ForwardSpec(ForwardKind.Dynamic, 0), 1080)));
    }

    [Fact]
    public void TwoViewsSayingTheSameAreEqual()
    {
        Assert.Equal(View(forwards: "Forward 1 → a:1"), View(forwards: "Forward 1 → a:1"));
        Assert.NotEqual(View(forwards: "Forward 1 → a:1"), View(forwards: "Forward 2 → a:1"));
        Assert.NotEqual(View(), View(ConnectionState.Reconnecting));
    }

    // ---- The bars ----

    /// <summary>The chip says its word, and a forward's chip comes and goes with the forward.</summary>
    [Fact]
    public void TheSessionBarSaysTheStateAndAChipPerForward()
    {
        (string first, string[] chips, string later, string[] none) = Sta.Run(() =>
        {
            SessionBar bar = new(new MainWindow());

            bar.Show(View(forwards: "Forward 5432 → localhost:5432"));

            string said = bar.StateSaid;
            string[] shown = [.. bar.ForwardChips];

            bar.Show(View(ConnectionState.Reconnecting));

            return (said, shown, bar.StateSaid, bar.ForwardChips.ToArray());
        });

        Assert.Equal("Connected", first);
        Assert.Equal(["Forward 5432 → localhost:5432"], chips);
        Assert.Equal("Reconnecting", later);
        Assert.Empty(none);
    }

    /// <summary>A view equal to the last builds nothing, which is what keeps the two-hertz wake-up free.</summary>
    [Fact]
    public void AnEqualViewTouchesNothing()
    {
        bool same = Sta.Run(() =>
        {
            SessionBar bar = new(new MainWindow());

            bar.Show(View(forwards: "Forward 1 → a:1"));

            object chip = ((System.Windows.Controls.DockPanel)bar.Child).Children[2];
            object before = ((System.Windows.Controls.Panel)chip).Children[0];

            bar.Show(View(forwards: "Forward 1 → a:1"));

            return ReferenceEquals(before, ((System.Windows.Controls.Panel)chip).Children[0]);
        });

        Assert.True(same, "an unchanged view rebuilt the forward chips");
    }

    [Fact]
    public void TheStatusBarSaysEveryWordOfTheDesign()
    {
        string[] said = Sta.Run(() =>
        {
            ConnectionStatusBar bar = new();

            bar.Show(View());

            return bar.Said.ToArray();
        });

        Assert.Equal(["Connected to db1.example.com through bastion", "Reconnects after a drop", "UTF-8",
                      "xterm-256color", "120 × 34"], said);
    }

    /// <summary>
    /// A window of local shells has no connection bars — not hidden ones, none — so the default
    /// window is what its own test says it is.
    /// </summary>
    [Fact]
    public void ALocalTabBuildsNeitherBar()
    {
        (bool showing, bool built) = Sta.Run(() =>
        {
            MainWindow window = new();

            window.Add(TerminalTab.Open(Settings.Default, Shared, "local"));
            window.Retitle();

            return (window.ConnectionShowing, window.SessionBar is not null || window.StatusBar is not null);
        });

        Assert.False(showing);
        Assert.False(built);
    }

    /// <summary>The menu offers what the palette does for a session, and closing the tab always.</summary>
    [Fact]
    public void TheMenuOffersThePalettesSessionEntries()
    {
        string[] offered = Sta.Run(() =>
        {
            MainWindow window = new();

            window.Add(TerminalTab.Open(Settings.Default, Shared, "local"));

            return window.SessionActions().Select(action => action.Header).ToArray();
        });

        Assert.Equal(["Browse files", "Show forwards", "Close tab"], offered);
    }
}
