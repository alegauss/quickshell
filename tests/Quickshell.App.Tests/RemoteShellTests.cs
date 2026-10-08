using System.IO;
using System.Net.Sockets;
using System.Text;
using Quickshell.App;
using Quickshell.Terminal;
using Quickshell.Transport;
using Xunit;

namespace Quickshell.App.Tests;

/// <summary>
/// A saved session, connected from the store the way the window connects it (QS126), against the
/// fixture's real OpenSSH servers.
///
/// <para>Each test reads the store from a file and nothing else, so what is being shown is the path a
/// user takes after a restart: a session saved earlier, read back, connected, its shell parsed into a
/// model. The host key goes through a <see cref="TrustOnFirstUse"/> over a <c>known_hosts</c> of the
/// test's own, which is how the second connection is shown to ask nothing.</para>
/// </summary>
public sealed class RemoteShellTests : IDisposable
{
    private readonly string _here = Path.Combine(Path.GetTempPath(), $"quickshell-remote-{Guid.NewGuid():N}");

    public RemoteShellTests() => Directory.CreateDirectory(_here);

    private static CancellationToken Stop => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        if (Directory.Exists(_here))
        {
            Directory.Delete(_here, recursive: true);
        }
    }

    /// <summary>
    /// QS126's own criterion: a saved session connects after a restart. Saved to a file, read back
    /// by a store that knows nothing else, connected, and its shell answers; then again, as the next
    /// run would, and the host key it was asked about the first time is not asked about twice.
    /// </summary>
    [Fact]
    public async Task ASavedSessionConnectsAfterARestart()
    {
        SkipWithoutFixture();

        string store = Store("""
            { "Name": "", "Children": [
                { "Name": "web", "Host": "127.0.0.1", "Settings": { "User": "probe", "Port": 2222, "Key": "KEY" } }
            ] }
            """);

        int asked = 0;
        string knownHosts = Path.Combine(_here, "known_hosts");

        for (int run = 0; run < 2; run++)
        {
            TrustOnFirstUse trust = new(KnownHosts.ReadFrom(knownHosts), (_, _) =>
            {
                asked++;

                return ValueTask.FromResult(SshHostKeyVerdict.AcceptAndRemember);
            });

            ResolvedSession web = SessionTree.ReadFrom(store).Session("web")
                                  ?? throw new InvalidOperationException("the store lost its session");

            Emulator emulator = new(80, 25);

            await using RemoteShell shell = await RemoteShell.OpenAsync(web, trust, emulator, new DamageSignal(),
                                                                        80, 25, Stop);

            await shell.Pipeline.TypeAsync(Encoding.ASCII.GetBytes("hostname\r"), Stop);

            await Until(() => Screen(emulator).Contains("qs-sshd-target", StringComparison.Ordinal));
        }

        // Asked on the first run, remembered, and not asked on the second.
        Assert.Equal(1, asked);
    }

    /// <summary>
    /// A session with a jump host is reached through it: the target is a name only the container
    /// network resolves, so the shell that answers is one carried by the bastion.
    /// </summary>
    [Fact]
    public async Task ASessionWithAJumpHostIsReachedThroughIt()
    {
        SkipWithoutFixture();

        string store = Store("""
            { "Name": "", "Settings": { "User": "probe", "Key": "KEY", "JumpHost": "probe@127.0.0.1:2223" },
              "Children": [ { "Name": "inside", "Host": "qs-sshd-target" } ] }
            """);

        ResolvedSession inside = SessionTree.ReadFrom(store).Session("inside")!;
        TrustOnFirstUse trust = new(KnownHosts.ReadFrom(Path.Combine(_here, "known_hosts")),
                                    (_, _) => ValueTask.FromResult(SshHostKeyVerdict.Accept));
        Emulator emulator = new(80, 25);

        await using RemoteShell shell = await RemoteShell.OpenAsync(inside, trust, emulator, new DamageSignal(),
                                                                    80, 25, Stop);

        await shell.Pipeline.TypeAsync(Encoding.ASCII.GetBytes("hostname\r"), Stop);

        await Until(() => Screen(emulator).Contains("qs-sshd-target", StringComparison.Ordinal));
    }

    /// <summary>
    /// QS113's falsification: a two-step sign-in does not show the same thing at second five as at
    /// second one. Against the fixture's <c>twofactor</c> account the pane shows where it is
    /// connecting, the server's banner and that the key was accepted with a second factor still to
    /// come — before anything else happens. Opened with nobody to ask, as here, that factor has no
    /// answer, which is why the connection then fails by name (QS218 answers it from the window).
    /// </summary>
    [Fact]
    public async Task ATwoStepSignInSaysWhereItIsInThePane()
    {
        SkipWithoutFixture();

        string store = Store("""
            { "Name": "", "Children": [
                { "Name": "mfa", "Host": "127.0.0.1", "Settings": { "User": "twofactor", "Port": 2222, "Key": "KEY" } }
            ] }
            """);

        ResolvedSession mfa = SessionTree.ReadFrom(store).Session("mfa")!;
        TrustOnFirstUse trust = new(KnownHosts.ReadFrom(Path.Combine(_here, "known_hosts")),
                                    (_, _) => ValueTask.FromResult(SshHostKeyVerdict.Accept));
        Emulator emulator = new(100, 25);

        await Assert.ThrowsAsync<SshException>(async () =>
            await RemoteShell.OpenAsync(mfa, trust, emulator, new DamageSignal(), 100, 25, Stop));

        string screen = Screen(emulator);

        Assert.Contains("Connecting to twofactor@127.0.0.1:2222", screen, StringComparison.Ordinal);
        Assert.Contains("Authorised use only.", screen, StringComparison.Ordinal);
        Assert.Contains("accepted publickey and wants keyboard-interactive next", screen, StringComparison.Ordinal);
    }

    /// <summary>
    /// QS218's falsification, first half: a session to a host that wants a password after its key is
    /// connected from the client. The fixture's <c>twofactor</c> account asks through PAM, and the
    /// question reaches the window as the server wrote it, hidden, with remembering offered.
    /// </summary>
    [Fact]
    public async Task AServerThatAsksForAPasswordIsAnsweredFromTheWindow()
    {
        SkipWithoutFixture();

        ResolvedSession mfa = TwoFactor();
        TrustOnFirstUse trust = new(KnownHosts.ReadFrom(Path.Combine(_here, "known_hosts")),
                                    (_, _) => ValueTask.FromResult(SshHostKeyVerdict.Accept));
        List<SignInQuestion> asked = [];
        Emulator emulator = new(100, 25);

        await using RemoteShell shell = await RemoteShell.OpenAsync(
            mfa, trust, emulator, new DamageSignal(), 100, 25, Stop, ask: (question, _) =>
            {
                asked.Add(question);

                return ValueTask.FromResult<SignInAnswer?>(new SignInAnswer("twofactor-pw", Remember: false));
            }, secrets: SecretStore.In(Path.Combine(_here, "secrets")));

        await shell.Pipeline.TypeAsync(Encoding.ASCII.GetBytes("whoami\r"), Stop);
        await Until(() => Screen(emulator).Contains("twofactor", StringComparison.Ordinal)
                          && Screen(emulator).Split('\n').Any(line => line.Trim() == "twofactor"));

        SignInQuestion question = Assert.Single(asked);

        Assert.Contains("assword", question.Prompt, StringComparison.Ordinal);
        Assert.False(question.Echoed);
        Assert.True(question.MayRemember);
        Assert.Equal("twofactor", question.Endpoint.User);
    }

    /// <summary>
    /// The second half: a password the person chose to remember is not asked for again. The first
    /// connection is answered and remembered; the second is answered from the store, and a question
    /// put to the window would fail the test.
    /// </summary>
    [Fact]
    public async Task ARememberedPasswordIsNotAskedForAgain()
    {
        SkipWithoutFixture();

        ResolvedSession mfa = TwoFactor();
        TrustOnFirstUse trust = new(KnownHosts.ReadFrom(Path.Combine(_here, "known_hosts")),
                                    (_, _) => ValueTask.FromResult(SshHostKeyVerdict.Accept));
        SecretStore secrets = SecretStore.In(Path.Combine(_here, "secrets"));

        await using (await RemoteShell.OpenAsync(mfa, trust, new Emulator(80, 25), new DamageSignal(), 80, 25, Stop,
                                                 ask: (_, _) => ValueTask.FromResult<SignInAnswer?>(
                                                     new SignInAnswer("twofactor-pw", Remember: true)),
                                                 secrets: secrets))
        {
        }

        int askedAgain = 0;

        await using (await RemoteShell.OpenAsync(mfa, trust, new Emulator(80, 25), new DamageSignal(), 80, 25, Stop,
                                                 ask: (_, _) =>
                                                 {
                                                     askedAgain++;

                                                     return ValueTask.FromResult<SignInAnswer?>(null);
                                                 },
                                                 secrets: secrets))
        {
        }

        Assert.Equal(0, askedAgain);
    }

    /// <summary>A password the server refused is never kept, however hard the person asked to keep it.</summary>
    [Fact]
    public async Task ARefusedPasswordIsNotRemembered()
    {
        SkipWithoutFixture();

        ResolvedSession mfa = TwoFactor();
        TrustOnFirstUse trust = new(KnownHosts.ReadFrom(Path.Combine(_here, "known_hosts")),
                                    (_, _) => ValueTask.FromResult(SshHostKeyVerdict.Accept));
        SecretStore secrets = SecretStore.In(Path.Combine(_here, "secrets"));
        int asked = 0;

        await Assert.ThrowsAnyAsync<Exception>(async () =>
            await RemoteShell.OpenAsync(mfa, trust, new Emulator(80, 25), new DamageSignal(), 80, 25, Stop,
                                        ask: (_, _) => ValueTask.FromResult<SignInAnswer?>(
                                            asked++ == 0 ? new SignInAnswer("not-the-password", Remember: true) : null),
                                        secrets: secrets));

        Assert.Null(secrets.Load(SshEndpoint.For("127.0.0.1", "twofactor", 2222)));
    }

    /// <summary>The fixture's two-step account, saved and read back as the window reads it.</summary>
    private ResolvedSession TwoFactor() =>
        SessionTree.ReadFrom(Store("""
            { "Name": "", "Children": [
                { "Name": "mfa", "Host": "127.0.0.1", "Settings": { "User": "twofactor", "Port": 2222, "Key": "KEY" } }
            ] }
            """)).Session("mfa")!;

    /// <summary>
    /// QS69: a session's forwards start with it, one that cannot start is said in the pane and costs
    /// nothing else, and — the line's falsification — closing the session leaves no listener behind.
    /// </summary>
    [Fact]
    public async Task ASessionsForwardsStartWithItAndGoWithIt()
    {
        SkipWithoutFixture();

        using TcpListener busy = new(System.Net.IPAddress.Loopback, 0);

        busy.Start();

        int taken = ((System.Net.IPEndPoint)busy.LocalEndpoint).Port;

        string store = Store($$"""
            { "Name": "", "Children": [
                { "Name": "web", "Host": "127.0.0.1", "Settings": { "User": "probe", "Port": 2222, "Key": "KEY" },
                  "Forwards": [
                    { "Kind": "Local", "ListenPort": 0, "TargetHost": "qs-sshd-jump", "TargetPort": 22 },
                    { "Kind": "Dynamic", "ListenPort": 0 },
                    { "Kind": "Local", "ListenPort": {{taken}}, "TargetHost": "qs-sshd-jump", "TargetPort": 22 }
                  ] }
            ] }
            """);

        ResolvedSession web = SessionTree.ReadFrom(store).Session("web")!;
        TrustOnFirstUse trust = new(KnownHosts.ReadFrom(Path.Combine(_here, "known_hosts")),
                                    (_, _) => ValueTask.FromResult(SshHostKeyVerdict.Accept));
        Emulator emulator = new(120, 25);

        RemoteShell shell = await RemoteShell.OpenAsync(web, trust, emulator, new DamageSignal(), 120, 25, Stop);
        int[] listening;

        try
        {
            Assert.Equal(2, shell.Forwards.Started.Count);

            FailedForward failed = Assert.Single(shell.Forwards.Failed);

            Assert.Equal(taken, failed.Spec.ListenPort);

            // The pane says both, and the shell is there all the same.
            string screen = Screen(emulator);

            Assert.Contains("is listening on port", screen, StringComparison.Ordinal);
            Assert.Contains($"-L {taken}:qs-sshd-jump:22 did not start", screen, StringComparison.Ordinal);

            await shell.Pipeline.TypeAsync(Encoding.ASCII.GetBytes("hostname\r"), Stop);
            await Until(() => Screen(emulator).Contains("qs-sshd-target", StringComparison.Ordinal));

            // One stopped and started again on its own, leaving the other and the session alone.
            StartedForward first = shell.Forwards.Started[0];

            await shell.Forwards.StopAsync(first.Spec);

            Assert.Empty(LocalForward.ListeningOn(first.BoundPort));
            Assert.Single(shell.Forwards.Started);
            Assert.True(await shell.Forwards.StartAsync(first.Spec));

            listening = [.. shell.Forwards.Started.Select(started => started.BoundPort)];

            Assert.Equal(2, listening.Length);
            Assert.All(listening, port => Assert.NotEmpty(LocalForward.ListeningOn(port)));
        }
        finally
        {
            await shell.DisposeAsync();
        }

        Assert.All(listening, port => Assert.Empty(LocalForward.ListeningOn(port)));
    }

    /// <summary>
    /// QS129: a connection records itself in the log it is given, and one that fails says in the pane
    /// where that log is — which is where a user is looking when it happens.
    /// </summary>
    [Fact]
    public async Task AConnectionIsLoggedAndAFailureSaysWhereTheLogIs()
    {
        SkipWithoutFixture();

        string store = Store("""
            { "Name": "", "Children": [
                { "Name": "web", "Host": "127.0.0.1", "Settings": { "User": "probe", "Port": 2222, "Key": "KEY" } },
                { "Name": "nowhere", "Host": "127.0.0.1", "Settings": { "User": "probe", "Port": 2, "Key": "KEY" } }
            ] }
            """);

        SessionTree tree = SessionTree.ReadFrom(store);
        TrustOnFirstUse trust = new(KnownHosts.ReadFrom(Path.Combine(_here, "known_hosts")),
                                    (_, _) => ValueTask.FromResult(SshHostKeyVerdict.Accept));

        await using (SessionLog log = SessionLog.InFolder(Path.Combine(_here, "logs")))
        {
            await using (await RemoteShell.OpenAsync(tree.Session("web")!, trust, new Emulator(80, 25),
                                                     new DamageSignal(), 80, 25, Stop, log))
            {
            }

            Emulator failing = new(120, 25);

            await Assert.ThrowsAsync<SshException>(async () =>
                await RemoteShell.OpenAsync(tree.Session("nowhere")!, trust, failing, new DamageSignal(),
                                            120, 25, Stop, log));

            Assert.Contains($"The log for this connection is {log.Path}", Screen(failing).Replace("\n", string.Empty,
                            StringComparison.Ordinal), StringComparison.Ordinal);
        }

        string written = await File.ReadAllTextAsync(Path.Combine(_here, "logs", "quickshell.log"), Stop);

        Assert.Contains("connecting", written, StringComparison.Ordinal);
        Assert.Contains("connected", written, StringComparison.Ordinal);
        Assert.Contains("failed", written, StringComparison.Ordinal);
    }

    /// <summary>A jump host is written as OpenSSH writes one, and every part of it is optional but the host.</summary>
    [Theory]
    [InlineData("bastion.example", "me", "bastion.example", 22)]
    [InlineData("admin@bastion.example", "admin", "bastion.example", 22)]
    [InlineData("admin@bastion.example:2200", "admin", "bastion.example", 2200)]
    [InlineData("bastion.example:2200", "me", "bastion.example", 2200)]
    public void AJumpHostIsReadAsOpenSshWritesIt(string written, string user, string host, int port)
    {
        SshEndpoint through = RemoteShell.Through(written, "me");

        Assert.Equal((user, host, port), (through.User, through.Host, through.Port));
    }

    // ---- plumbing ----

    /// <summary>A store file, with the fixture's key where KEY is written.</summary>
    private string Store(string json)
    {
        string file = Path.Combine(_here, "sessions.json");

        File.WriteAllText(file, json.Replace("KEY", Key().Replace("\\", "\\\\", StringComparison.Ordinal),
                                             StringComparison.Ordinal));

        return file;
    }

    private static string Screen(Emulator emulator)
    {
        TerminalBuffer buffer = emulator.Buffer;
        StringBuilder text = new();
        Span<char> cell = stackalloc char[8];

        for (int row = 0; row < buffer.Rows; row++)
        {
            foreach (Cell glyph in buffer.Line((int)buffer.AbsoluteLine(row)))
            {
                text.Append(cell[..buffer.TextOf(glyph, cell)]);
            }

            text.Append('\n');
        }

        return text.ToString();
    }

    private static async Task Until(Func<bool> ready)
    {
        for (int wait = 0; wait < 150 && !ready(); wait++)
        {
            await Task.Delay(100, Stop);
        }

        Assert.True(ready(), "the remote shell never said what this was waiting for");
    }

    private static string Key() =>
        Path.Combine(Repository.Root, "prototypes", "SshProbe", "fixture", "keys", "probe_ed25519");

    private static void SkipWithoutFixture()
    {
        bool up;

        try
        {
            using TcpClient probe = new();

            up = probe.ConnectAsync("127.0.0.1", 2222).Wait(TimeSpan.FromSeconds(2));
        }
        catch (Exception failure) when (failure is SocketException or AggregateException)
        {
            up = false;
        }

        Assert.SkipUnless(up && File.Exists(Key()),
            "nothing is listening on 127.0.0.1:2222: run prototypes/SshProbe/fixture/up.sh");
    }
}
