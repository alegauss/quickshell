using System.IO;
using Quickshell.App;
using Quickshell.Terminal;
using Quickshell.Transport;
using Xunit;

namespace Quickshell.App.Tests;

/// <summary>
/// QS245: what a saved session says about its own scheme, scrollback and terminal type reaches the
/// session it opens, rather than being shown in the dialog and then ignored.
/// </summary>
public sealed class SessionFieldsTests : IDisposable
{
    private static readonly TerminalShare Shared = new();

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "qs245-" + Guid.NewGuid().ToString("N"));

    public SessionFieldsTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    // ---- Terminal type ----

    [Theory]
    [InlineData(null, "xterm-256color")]
    [InlineData("", "xterm-256color")]
    [InlineData("vt100", "vt100")]
    [InlineData("screen-256color", "screen-256color")]
    [InlineData("xterm+direct", "xterm+direct")]
    [InlineData("bad name", "xterm-256color")]
    [InlineData("evil\u001b[c", "xterm-256color")]
    public void ASessionsTerminalTypeIsClaimedWhenTerminfoCouldHoldIt(string? asked, string claimed)
    {
        Assert.Equal(claimed, RemoteShell.Claimed(asked));
    }

    [Fact]
    public void TheTransportSendsWhatItIsGiven()
    {
        Assert.Equal("vt220", new SshNetTransport { TerminalType = "vt220" }.TerminalType);
        Assert.Equal(SshNetTransport.DefaultTerminalType, new SshNetTransport().TerminalType);
    }

    // ---- Scheme and scrollback ----

    /// <summary>
    /// The falsification for the pane's two: a session's own scheme and scrollback change the pane it
    /// opens into, and a session that says nothing wears the window's.
    /// </summary>
    [Fact]
    public void EachPaneWearsItsSessionsSchemeAndScrollback()
    {
        File.WriteAllText(Path.Combine(_folder, "red.json"), """{ "background": "#800000", "foreground": "#FFFFFF" }""");
        string sessions = Path.Combine(_folder, "sessions.json");

        SessionTree tree = SessionTree.Of(new SessionNode
        {
            Name = string.Empty,
            Children =
            [
                new SessionNode
                {
                    Name = "Fleet",
                    Children =
                    [
                        new SessionNode
                        {
                            Name = "own", Host = "own.example",
                            Settings = new SessionSettings { Scheme = "red.json", Scrollback = 42 },
                        },
                        new SessionNode { Name = "plain", Host = "plain.example" },
                    ],
                },
            ],
        });

        (Rgb ownGround, int? ownDepth, Rgb plainGround, int? plainDepth) =
            Sta.Run<(Rgb, int?, Rgb, int?)>(() =>
            {
                MainWindow window = new() { SessionsFile = sessions };

                (TerminalTab? tab, _) = SessionGroup.Open(window, Settings.Default, Shared, tree.Group("Fleet"),
                    _ => (_, _, _, _, _) =>
                        Task.FromException<IShellSession>(new InvalidOperationException("no network in a test")));

                TerminalLeaf own = tab!.Leaves.Single(leaf => leaf.Host == "own.example");
                TerminalLeaf plain = tab.Leaves.Single(leaf => leaf.Host == "plain.example");

                return (own.Emulator.Palette.Background, own.OwnScrollback,
                        plain.Emulator.Palette.Background, plain.OwnScrollback);
            });

        Assert.Equal(new Rgb(0x80, 0, 0), ownGround);
        Assert.Equal(42, ownDepth);
        Assert.Equal(ColourScheme.Default.Background, plainGround);
        Assert.Null(plainDepth);
    }

    [Fact]
    public void ASchemeThatDoesNotReadLeavesTheWindowsScheme()
    {
        string sessions = Path.Combine(_folder, "sessions.json");

        SessionTree tree = SessionTree.Of(new SessionNode
        {
            Name = string.Empty,
            Children =
            [
                new SessionNode
                {
                    Name = "Fleet",
                    Children =
                    [
                        new SessionNode
                        {
                            Name = "typo", Host = "typo.example",
                            Settings = new SessionSettings { Scheme = "no-such-scheme.json" },
                        },
                    ],
                },
            ],
        });

        Rgb ground = Sta.Run(() =>
        {
            MainWindow window = new() { SessionsFile = sessions };

            (TerminalTab? tab, _) = SessionGroup.Open(window, Settings.Default, Shared, tree.Group("Fleet"),
                _ => (_, _, _, _, _) =>
                    Task.FromException<IShellSession>(new InvalidOperationException("no network in a test")));

            return tab!.Focused.Emulator.Palette.Background;
        });

        Assert.Equal(ColourScheme.Default.Background, ground);
    }
}
