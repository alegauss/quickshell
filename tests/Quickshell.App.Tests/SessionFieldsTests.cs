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

    // ---- Credential ----

    /// <summary>
    /// The falsification for the credential: a session that names one is offered the password kept
    /// under that name, whatever host it is for, and a password it is told to remember is kept there.
    /// </summary>
    [Fact]
    public async Task ANamedCredentialIsOfferedAndRememberedUnderItsName()
    {
        SecretStore store = SecretStore.In(Path.Combine(_folder, "secrets"));

        using (Secret shared = Secret.From("shared"))
        {
            store.SaveNamed("deploy", shared);
        }

        SshEndpoint web = SshEndpoint.For("web.example", "deploy");

        (_, IReadOnlyList<SshCredential> offered) = SignIn.For(web, Declined, store, "deploy");
        (_, IReadOnlyList<SshCredential> unnamed) = SignIn.For(web, Declined, store);

        Assert.Contains(offered, credential => credential is SshCredential.Password);
        Assert.DoesNotContain(unnamed, credential => credential is SshCredential.Password);

        // Remembered under the name, so the next host that names it finds it.
        (SignIn signIn, _) = SignIn.For(SshEndpoint.For("db.example", "deploy"),
                                        (_, _) => ValueTask.FromResult<SignInAnswer?>(new SignInAnswer("typed", Remember: true)),
                                        store, "fresh");

        Assert.NotNull(await signIn.AskPasswordAsync(CancellationToken.None));
        signIn.Commit();

        using Secret? kept = store.LoadNamed("fresh");

        Assert.Equal("typed", System.Text.Encoding.UTF8.GetString(kept!.Bytes));
        Assert.Null(store.Load(SshEndpoint.For("db.example", "deploy")));
    }

    private static ValueTask<SignInAnswer?> Declined(SignInQuestion question, CancellationToken token) =>
        ValueTask.FromResult<SignInAnswer?>(null);

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
