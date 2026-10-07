using Quickshell.App;
using Xunit;

namespace Quickshell.App.Tests;

/// <summary>
/// QS179: a folder of saved sessions is a fleet chosen once — opened as one tab, every session a pane
/// of it, typing into all of them and nothing else.
/// </summary>
public sealed class SessionGroupTests
{
    private static readonly TerminalShare Shared = new();

    private static SessionTree Tree(int fleet) => SessionTree.Of(new SessionNode
    {
        Name = string.Empty,
        Children =
        [
            new SessionNode
            {
                Name = "Fleet",
                Children =
                [
                    .. Enumerable.Range(1, fleet).Select(at => new SessionNode { Name = $"web{at}", Host = $"web{at}.example" }),
                    new SessionNode { Name = "Inner", Children = [new SessionNode { Name = "db", Host = "db.example" }] },
                ],
            },
            new SessionNode { Name = "elsewhere", Host = "elsewhere.example" },
        ],
    });

    /// <summary>A folder's group is every session under it, folders inside included, and nothing else.</summary>
    [Fact]
    public void AFolderIsItsSessionsAndASessionIsNoGroup()
    {
        SessionTree tree = Tree(2);

        Assert.Equal(["web1.example", "web2.example", "db.example"], tree.Group("Fleet").Select(member => member.Host));
        Assert.Empty(tree.Group("Fleet/web1"));
        Assert.Empty(tree.Group("Nowhere"));
        Assert.Empty(tree.Group(string.Empty));
    }

    /// <summary>
    /// The falsification: opening a group leaves none of its sessions out of the tab, connects each
    /// pane to its own session, and broadcasts to those panes and no other.
    /// </summary>
    [Fact]
    public void OpeningAGroupPutsEverySessionInOneBroadcastingTab()
    {
        (string[] hosts, string[] opened, bool broadcasting, bool allReceive, int tabs, int leftOut) =
            Sta.Run<(string[], string[], bool, bool, int, int)>(() =>
        {
            MainWindow window = new();

            window.Add(TerminalTab.Open(Settings.Default, Shared, "a tab already open"));

            List<string> connected = [];

            (TerminalTab? tab, int left) = SessionGroup.Open(window, Settings.Default, Shared, Tree(2).Group("Fleet"),
                member => (_, _, _, _, _) =>
                {
                    connected.Add(member.Host);

                    return Task.FromException<IShellSession>(new InvalidOperationException("no network in a test"));
                });

            return ([.. tab!.Leaves.Select(leaf => leaf.Host).Order()], [.. connected.Order()], tab.Broadcasting,
                    tab.Leaves.All(leaf => leaf.Receiving), window.Held.Count, left);
        });

        Assert.Equal(["db.example", "web1.example", "web2.example"], hosts);
        Assert.Equal(hosts, opened);
        Assert.True(broadcasting);
        Assert.True(allReceive);

        // A tab of its own, beside the one that was already open, which it does not touch.
        Assert.Equal(2, tabs);
        Assert.Equal(0, leftOut);
    }

    /// <summary>A folder larger than a tab opens a tab's worth and says how many it did not.</summary>
    [Fact]
    public void AFolderLargerThanATabSaysWhatItLeftOut()
    {
        (int panes, int leftOut) = Sta.Run(() =>
        {
            MainWindow window = new();

            (TerminalTab? tab, int left) = SessionGroup.Open(window, Settings.Default, Shared, Tree(18).Group("Fleet"),
                _ => (_, _, _, _, _) => Task.FromException<IShellSession>(new InvalidOperationException("no network")));

            return (tab!.Layout.Count, left);
        });

        Assert.Equal(MainWindow.MaximumPanes, panes);
        Assert.Equal(19 - MainWindow.MaximumPanes, leftOut);
    }
}
