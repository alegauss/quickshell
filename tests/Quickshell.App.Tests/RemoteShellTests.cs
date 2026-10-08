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

            await shell.TypeAsync(Encoding.ASCII.GetBytes("hostname\r"), Stop);

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

        await shell.TypeAsync(Encoding.ASCII.GetBytes("hostname\r"), Stop);

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

        await shell.TypeAsync(Encoding.ASCII.GetBytes("whoami\r"), Stop);
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

    /// <summary>
    /// QS218's criterion: a host whose only way in is the password method, with keyboard-interactive
    /// off as Ubuntu ships it, is connected from the client — asked about once, in this client's
    /// words since the server asks none, and not asked again once the password is kept.
    /// </summary>
    [Fact]
    public async Task AHostThatTakesOnlyAPasswordIsAskedForItOnceAndThenRemembered()
    {
        SkipWithoutFixture();

        ResolvedSession only = Account("passonly");
        TrustOnFirstUse trust = new(KnownHosts.ReadFrom(Path.Combine(_here, "known_hosts")),
                                    (_, _) => ValueTask.FromResult(SshHostKeyVerdict.Accept));
        SecretStore secrets = SecretStore.In(Path.Combine(_here, "secrets"));
        List<SignInQuestion> asked = [];
        Emulator emulator = new(80, 25);

        await using (RemoteShell shell = await RemoteShell.OpenAsync(
                         only, trust, emulator, new DamageSignal(), 80, 25, Stop, ask: (question, _) =>
                         {
                             asked.Add(question);

                             return ValueTask.FromResult<SignInAnswer?>(new SignInAnswer("passonly-pw", Remember: true));
                         }, secrets: secrets))
        {
            await shell.TypeAsync(Encoding.ASCII.GetBytes("whoami\r"), Stop);
            await Until(() => Screen(emulator).Split('\n').Any(line => line.Trim() == "passonly"));
        }

        SignInQuestion question = Assert.Single(asked);

        Assert.Equal("Password:", question.Prompt);
        Assert.False(question.Echoed);
        Assert.True(question.MayRemember);

        int again = 0;

        await using (await RemoteShell.OpenAsync(only, trust, new Emulator(80, 25), new DamageSignal(), 80, 25, Stop,
                                                 ask: (_, _) =>
                                                 {
                                                     again++;

                                                     return ValueTask.FromResult<SignInAnswer?>(null);
                                                 },
                                                 secrets: secrets))
        {
        }

        Assert.Equal(0, again);
    }

    /// <summary>
    /// A server that takes keys alone is never asked about a password: the question would be one
    /// whose answer could not be used, and a person who typed one would be taught the wrong thing.
    /// </summary>
    [Fact]
    public async Task AKeyOnlyHostIsNeverAskedForAPassword()
    {
        SkipWithoutFixture();

        // certonly authorises no key and takes publickey alone, so the fixture's key is refused.
        ResolvedSession keyOnly = Account("certonly");
        TrustOnFirstUse trust = new(KnownHosts.ReadFrom(Path.Combine(_here, "known_hosts")),
                                    (_, _) => ValueTask.FromResult(SshHostKeyVerdict.Accept));
        int asked = 0;

        await Assert.ThrowsAsync<SshException>(async () =>
            await RemoteShell.OpenAsync(keyOnly, trust, new Emulator(80, 25), new DamageSignal(), 80, 25, Stop,
                                        ask: (_, _) =>
                                        {
                                            asked++;

                                            return ValueTask.FromResult<SignInAnswer?>(null);
                                        },
                                        secrets: SecretStore.In(Path.Combine(_here, "secrets"))));

        Assert.Equal(0, asked);
    }

    /// <summary>
    /// QS219's falsification: the browser's host side over an SSH session lists that host — the
    /// account's home, which holds the <c>.ssh</c> the fixture authorised its key in — over a file
    /// channel of the session's own connection.
    /// </summary>
    [Fact]
    public async Task ASessionsFilesAreListedOverItsOwnConnection()
    {
        SkipWithoutFixture();

        ResolvedSession probe = Account("probe");
        TrustOnFirstUse trust = new(KnownHosts.ReadFrom(Path.Combine(_here, "known_hosts")),
                                    (_, _) => ValueTask.FromResult(SshHostKeyVerdict.Accept));

        await using RemoteShell shell = await RemoteShell.OpenAsync(probe, trust, new Emulator(80, 25),
                                                                    new DamageSignal(), 80, 25, Stop);

        await Until(() => shell.Files is not null);

        RemoteFiles files = shell.Files!;
        List<string> names = [];

        await foreach (FileItem item in files.ListAsync(files.Home, Stop))
        {
            names.Add(item.Name);
        }

        Assert.Equal("/home/probe", files.Home);
        Assert.Contains(".ssh", names);
        Assert.Equal("127.0.0.1", files.Title);
    }

    /// <summary>
    /// A server with no file subsystem leaves the session without a host side, and the shell
    /// untouched: the browser says there is none rather than this failing the connection.
    /// </summary>
    [Fact]
    public async Task AServerWithNoFileSubsystemLeavesTheShellAndNoFiles()
    {
        SkipWithoutFixture();

        ResolvedSession bare = SessionTree.ReadFrom(Store("""
            { "Name": "", "Children": [
                { "Name": "it", "Host": "127.0.0.1", "Settings": { "User": "probe", "Port": 2225, "Key": "KEY" } }
            ] }
            """)).Session("it")!;
        TrustOnFirstUse trust = new(KnownHosts.ReadFrom(Path.Combine(_here, "known_hosts")),
                                    (_, _) => ValueTask.FromResult(SshHostKeyVerdict.Accept));
        Emulator emulator = new(80, 25);

        await using RemoteShell shell = await RemoteShell.OpenAsync(bare, trust, emulator, new DamageSignal(), 80, 25, Stop);

        await shell.TypeAsync(Encoding.ASCII.GetBytes("hostname\r"), Stop);
        await Until(() => Screen(emulator).Contains("qs-sshd-nosftp", StringComparison.Ordinal));

        // Long enough for a channel that was going to open to have opened.
        await Task.Delay(TimeSpan.FromSeconds(2), Stop);

        Assert.Null(shell.Files);
    }

    /// <summary>
    /// QS220's falsification: an SSH tab whose link drops for ten seconds is connected again with its
    /// scrollback, and says which of its forwards came back. The fixture's <c>drop</c> server is
    /// stopped and started again, which is a link gone as a session sees it, and a new shell answers
    /// under the same model with the line typed before the drop still in it.
    /// </summary>
    [Fact]
    public async Task ADroppedLinkIsConnectedAgainWithItsScrollbackAndItsForwards()
    {
        SkipWithoutFixture();

        await BringBackTheDropServer();

        ResolvedSession dropping = SessionTree.ReadFrom(Store("""
            { "Name": "", "Children": [
                { "Name": "it", "Host": "127.0.0.1",
                  "Settings": { "User": "probe", "Port": 2228, "Key": "KEY", "Reconnect": true },
                  "Forwards": [ { "Kind": "Dynamic", "ListenPort": 0 } ] }
            ] }
            """)).Session("it")!;

        // Its own host key per start, so every reconnect meets a key nobody has kept: accepted
        // once each time, as a person told it is the same machine would.
        TrustOnFirstUse trust = new(KnownHosts.ReadFrom(Path.Combine(_here, "known_hosts")),
                                    (_, _) => ValueTask.FromResult(SshHostKeyVerdict.Accept));
        Emulator emulator = new(120, 40);

        await using RemoteShell shell = await RemoteShell.OpenAsync(dropping, trust, emulator, new DamageSignal(),
                                                                    120, 40, Stop);

        await shell.TypeAsync(Encoding.ASCII.GetBytes("echo before-the-drop\r"), Stop);
        await Until(() => Screen(emulator).Split('\n').Any(line => line.Trim() == "before-the-drop"));

        Docker("stop", "-t", "0", "qs-sshd-drop");
        await Task.Delay(TimeSpan.FromSeconds(10), Stop);
        await BringBackTheDropServer();

        System.Diagnostics.Stopwatch reconnecting = System.Diagnostics.Stopwatch.StartNew();

        while (reconnecting.Elapsed < TimeSpan.FromSeconds(60) && !(shell.Connections == 2 && shell.Status.IsLive))
        {
            await Task.Delay(100, Stop);
        }

        Assert.True(shell.Connections == 2 && shell.Status.IsLive,
                    $"not connected again: {shell.Connections} connections, {shell.Status}\n{Screen(emulator)}");

        await shell.TypeAsync(Encoding.ASCII.GetBytes("echo after-the-drop\r"), Stop);
        await Until(() => Screen(emulator).Split('\n').Any(line => line.Trim() == "after-the-drop"));

        string screen = Screen(emulator);

        // The scrollback kept, the attempt said, and the forward said again on the new connection.
        Assert.Contains("before-the-drop", screen, StringComparison.Ordinal);
        Assert.Contains("Trying again in", screen, StringComparison.Ordinal);
        Assert.Contains("Connecting to probe@127.0.0.1:2228 again", screen, StringComparison.Ordinal);
        Assert.Equal(2, screen.Split("is listening on port").Length - 1);
    }

    /// <summary>
    /// And off unless the session says so (QS38's design): the same drop, with no Reconnect set,
    /// ends the session and says why, rather than logging in again unasked.
    /// </summary>
    [Fact]
    public async Task ASessionNotSetToReconnectEndsWhenItsLinkDrops()
    {
        SkipWithoutFixture();

        await BringBackTheDropServer();

        ResolvedSession dropping = SessionTree.ReadFrom(Store("""
            { "Name": "", "Children": [
                { "Name": "it", "Host": "127.0.0.1", "Settings": { "User": "probe", "Port": 2228, "Key": "KEY" } }
            ] }
            """)).Session("it")!;
        TrustOnFirstUse trust = new(KnownHosts.ReadFrom(Path.Combine(_here, "known_hosts")),
                                    (_, _) => ValueTask.FromResult(SshHostKeyVerdict.Accept));

        await using RemoteShell shell = await RemoteShell.OpenAsync(dropping, trust, new Emulator(80, 25),
                                                                    new DamageSignal(), 80, 25, Stop);

        try
        {
            Docker("stop", "-t", "0", "qs-sshd-drop");

            PtyExit ended = await shell.Ended.WaitAsync(TimeSpan.FromSeconds(60), Stop);

            Assert.False(ended.IsExit);
            Assert.Equal(1, shell.Connections);
        }
        finally
        {
            await BringBackTheDropServer();
        }
    }

    /// <summary>
    /// Starts the drop server again and waits until its sshd answers, so the next test that uses it
    /// meets a server and not a container still starting.
    /// </summary>
    private static async Task BringBackTheDropServer()
    {
        Docker("start", "qs-sshd-drop");

        System.Diagnostics.Stopwatch waited = System.Diagnostics.Stopwatch.StartNew();

        while (waited.Elapsed < TimeSpan.FromSeconds(30))
        {
            try
            {
                using TcpClient probe = new();

                await probe.ConnectAsync("127.0.0.1", 2228, Stop);

                byte[] said = new byte[4];

                if (await probe.GetStream().ReadAsync(said, Stop) == 4 && Encoding.ASCII.GetString(said) == "SSH-")
                {
                    return;
                }
            }
            catch (SocketException)
            {
                // Not listening yet.
            }
            catch (IOException)
            {
                // Listening, and closed on us before saying anything.
            }

            await Task.Delay(200, Stop);
        }
    }

    /// <summary>Runs docker against the fixture, which is how a test takes one of its servers away.</summary>
    private static void Docker(params string[] arguments)
    {
        System.Diagnostics.ProcessStartInfo start = new("docker") { UseShellExecute = false, CreateNoWindow = true };

        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using System.Diagnostics.Process docker = System.Diagnostics.Process.Start(start)!;

        docker.WaitForExit();
    }

    /// <summary>
    /// QS70's falsification: a running forward appears in the forwards view, with what it is
    /// carrying now — nothing, then the one connection a client opens through it — and is gone from
    /// the view once it is stopped there.
    /// </summary>
    [Fact]
    public async Task ARunningForwardIsListedWithWhatItCarriesNow()
    {
        SkipWithoutFixture();

        ResolvedSession web = SessionTree.ReadFrom(Store("""
            { "Name": "", "Children": [
                { "Name": "web", "Host": "127.0.0.1", "Settings": { "User": "probe", "Port": 2222, "Key": "KEY" },
                  "Forwards": [ { "Kind": "Local", "ListenPort": 0, "TargetHost": "qs-sshd-jump", "TargetPort": 22 } ] }
            ] }
            """)).Session("web")!;
        TrustOnFirstUse trust = new(KnownHosts.ReadFrom(Path.Combine(_here, "known_hosts")),
                                    (_, _) => ValueTask.FromResult(SshHostKeyVerdict.Accept));

        await using RemoteShell shell = await RemoteShell.OpenAsync(web, trust, new Emulator(80, 25), new DamageSignal(),
                                                                    80, 25, Stop);

        SessionForwards forwards = shell.ForwardsNow!;
        int port = forwards.Started[0].BoundPort;

        string idle = Sta.Run(() => new ForwardsWindow(() => [new SessionForwardsView("127.0.0.1", forwards)]).Lines.Single());

        Assert.Contains("-L 0:qs-sshd-jump:22", idle, StringComparison.Ordinal);
        Assert.Contains($"127.0.0.1:{port}", idle, StringComparison.Ordinal);
        Assert.Contains("0 carrying now", idle, StringComparison.Ordinal);

        // A client through it: the jump server's banner arriving is the connection being carried.
        using (TcpClient through = new())
        {
            await through.ConnectAsync("127.0.0.1", port, Stop);

            byte[] banner = new byte[4];

            await through.GetStream().ReadExactlyAsync(banner, Stop);

            string busy = Sta.Run(() => new ForwardsWindow(() => [new SessionForwardsView("127.0.0.1", forwards)]).Lines.Single());

            Assert.Contains("1 carrying now", busy, StringComparison.Ordinal);
        }

        await forwards.StopAsync(forwards.Started[0].Spec);

        string stopped = Sta.Run(() => new ForwardsWindow(() => [new SessionForwardsView("127.0.0.1", forwards)]).Lines.Single());

        Assert.StartsWith("No forwards are running", stopped, StringComparison.Ordinal);
    }

    /// <summary>One of the fixture's accounts on the target, with the fixture's key, read back from a store.</summary>
    private ResolvedSession Account(string user) =>
        SessionTree.ReadFrom(Store($$"""
            { "Name": "", "Children": [
                { "Name": "it", "Host": "127.0.0.1", "Settings": { "User": "{{user}}", "Port": 2222, "Key": "KEY" } }
            ] }
            """)).Session("it")!;

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

            // And who holds the port: this test, which is the process listening on it (QS69).
            Assert.Contains($"process {Environment.ProcessId}", failed.Reason + " " + screen, StringComparison.Ordinal);

            await shell.TypeAsync(Encoding.ASCII.GetBytes("hostname\r"), Stop);
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

    private static Task Until(Func<bool> ready) => Until(ready, TimeSpan.FromSeconds(15));

    private static async Task Until(Func<bool> ready, TimeSpan patience)
    {
        System.Diagnostics.Stopwatch waited = System.Diagnostics.Stopwatch.StartNew();

        while (waited.Elapsed < patience && !ready())
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
