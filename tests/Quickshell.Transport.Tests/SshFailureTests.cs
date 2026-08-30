using System.Net;
using System.Net.Sockets;
using System.Text;
using Quickshell.Transport;
using Xunit;

namespace Quickshell.Transport.Tests;

/// <summary>
/// Every way a connection fails, provoked rather than imagined.
///
/// <para><b>Each of these makes the failure happen.</b> A name that does not resolve is a name that
/// does not resolve; a port that refuses is a port with nothing on it; a socket that is not an SSH
/// server is a real socket answering with HTTP. That matters more here than anywhere else in this
/// suite, because a classification rule written against a guess describes the wrong failure with
/// complete confidence and nothing ever contradicts it.</para>
///
/// <para><b>Two of these pin message text on purpose.</b> The library distinguishes a connect
/// timeout from a handshake timeout only by wording, so the rules read it — and a release that
/// rewords them must fail here rather than quietly start calling every timeout the other kind.</para>
/// </summary>
public sealed class SshFailureTests
{
    /// <summary>
    /// A credential that needs nothing on disk. Every failure below happens before authentication,
    /// so what is offered never matters — and a key file that does not exist would fail first, for
    /// a reason that has nothing to do with what is being tested.
    /// </summary>
    private static readonly SshCredential.Password AnyCredential = new("never offered");

    private static CancellationToken Stop => TestContext.Current.CancellationToken;

    private static ValueTask<SshHostKeyVerdict> Trusting(SshEndpoint _, SshHostKey __, CancellationToken ___) =>
        ValueTask.FromResult(SshHostKeyVerdict.Accept);

    /// <summary>The falsification, word for word: no failure surfaces a library exception type.</summary>
    [Fact]
    public void NoFailureCarriesALibraryExceptionWhereAUserCouldReachIt()
    {
        SshException failure = SshException.From(SshFailureKind.Dropped, "it ended",
                                                 new InvalidOperationException("the library's words"));

        Assert.Null(failure.InnerException);
        Assert.DoesNotContain("Renci", failure.Message, StringComparison.Ordinal);
        Assert.Contains("InvalidOperationException", failure.Origin, StringComparison.Ordinal);
    }

    /// <summary>
    /// Three clauses, and each says its own thing. A message that repeated itself across the three
    /// would fill a dialog and tell a user once.
    /// </summary>
    [Theory]
    [InlineData("no-such-host.invalid", 22, SshFailureKind.NameNotFound)]
    [InlineData("127.0.0.1", 2, SshFailureKind.Refused)]
    public async Task EachFailureSaysWhatHappenedWhatItMeansAndWhatToDo(
        string host, int port, SshFailureKind expected)
    {
        SshException failure = await Refused(SshEndpoint.For(host, "probe", port));

        Assert.Equal(expected, failure.Kind);
        Assert.NotEqual(string.Empty, failure.Message);
        Assert.NotEqual(string.Empty, failure.Means);
        Assert.NotEqual(string.Empty, failure.Remedy);

        Assert.NotEqual(failure.Message, failure.Means);
        Assert.NotEqual(failure.Means, failure.Remedy);

        // And all three read as sentences rather than as a log line.
        Assert.EndsWith(".", failure.Message, StringComparison.Ordinal);
        Assert.EndsWith(".", failure.Means, StringComparison.Ordinal);
        Assert.EndsWith(".", failure.Remedy, StringComparison.Ordinal);
    }

    /// <summary>
    /// A name nobody can resolve, told apart from a port nobody answers. The socket layer reports a
    /// number for this and a localised sentence — the number is what is read, because the sentence
    /// came back in Portuguese on the machine these rules were written on.
    /// </summary>
    [Fact]
    public async Task ANameThatDoesNotResolveSaysSoAndNamesTheHost()
    {
        SshException failure = await Refused(SshEndpoint.For("no-such-host.invalid", "probe"));

        Assert.Equal(SshFailureKind.NameNotFound, failure.Kind);
        Assert.Contains("no-such-host.invalid", failure.Message, StringComparison.Ordinal);
        Assert.Contains("resolve", failure.Means, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A port with nothing on it, and the remedy names the port SSH usually uses.</summary>
    [Fact]
    public async Task APortThatRefusesSuggestsTheUsualOne()
    {
        SshException failure = await Refused(SshEndpoint.For("127.0.0.1", "probe", 2));

        Assert.Equal(SshFailureKind.Refused, failure.Kind);
        Assert.Contains("port 2", failure.Message, StringComparison.Ordinal);
        Assert.Contains("22", failure.Remedy, StringComparison.Ordinal);
    }

    /// <summary>
    /// A socket that accepts and answers with something that is not an SSH banner.
    ///
    /// <para><b>This and a socket that says nothing at all are the same failure to this library</b>,
    /// and the message says so rather than asserting which it was. The protocol lets a server send
    /// arbitrary lines before its identification string, so a client cannot tell "not SSH" from
    /// "slow to say so" without waiting — and the wait is the timeout.</para>
    /// </summary>
    [Theory]
    [InlineData("HTTP/1.1 400 Bad Request\r\n\r\n")]
    [InlineData(null)]
    public async Task SomethingThatIsNotAnSshServerIsNamedAsSuch(string? greeting)
    {
        using Fake fake = new(greeting);

        SshException failure = await Refused(SshEndpoint.For("127.0.0.1", "probe", fake.Port),
                                             TimeSpan.FromSeconds(4));

        Assert.Equal(SshFailureKind.NotResponding, failure.Kind);
        Assert.Contains("SSH server", failure.Message, StringComparison.Ordinal);
        Assert.Contains("identified itself as SSH", failure.Means, StringComparison.Ordinal);
    }

    /// <summary>
    /// The server accepts a method this client did not offer. Measured against the fixture, whose
    /// <c>probe</c> account is under <c>AuthenticationMethods publickey</c> — so a password is never
    /// tried at all, and the useful thing to say is what the server does accept.
    /// </summary>
    [Fact]
    public async Task AMethodTheServerDoesNotAllowNamesWhatItDoesAllow()
    {
        SkipWithoutFixture();

        await using SshNetTransport transport = new();

        SshException failure = await Assert.ThrowsAsync<SshException>(async () =>
            await transport.ConnectAsync(Fixture, [new SshCredential.Password("anything")],
                                         Trusting, Stop));

        Assert.Equal(SshFailureKind.NoMethodAccepted, failure.Kind);
        Assert.Contains("publickey", failure.Means, StringComparison.Ordinal);
        Assert.Contains("accepts", failure.Means, StringComparison.Ordinal);
    }

    /// <summary>
    /// And a method that <em>was</em> tried and refused is a different kind with a different remedy.
    /// Measured with a real key the fixture has not authorised, which answers "Permission denied
    /// (publickey)." where the case above answers "No suitable authentication method found".
    /// </summary>
    [Fact]
    public async Task AKeyTheServerRefusesIsNotTheSameFailureAsAMethodItWouldNotTry()
    {
        SkipWithoutFixture();

        string unauthorised = Path.Combine(FixtureKeys(), "ca");

        Assert.True(File.Exists(unauthorised), "the fixture's CA key is what this offers as a wrong key");

        await using SshNetTransport transport = new();

        SshException failure = await Assert.ThrowsAsync<SshException>(async () =>
            await transport.ConnectAsync(Fixture, [new SshCredential.PrivateKey(unauthorised)],
                                         Trusting, Stop));

        Assert.Equal(SshFailureKind.CredentialRejected, failure.Kind);
        Assert.Contains("rejected", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("authorised", failure.Remedy, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A route to nowhere and a socket that never speaks are the same failure to this library, and
    /// the message says so instead of guessing which.
    ///
    /// <para>This is the design's one request that could not be met. It asks for a connect timeout
    /// and a handshake timeout to be distinguished; through <c>ConnectAsync</c> both arrive as
    /// <c>"Connection has timed out."</c> — identical type, identical wording. The synchronous entry
    /// point does tell them apart and takes no cancellation token, which is the worse trade. QS112
    /// carries it.</para>
    /// </summary>
    [Fact]
    public async Task ARouteToNowhereAndASilentSocketAreTheSameFailureAndSaySo()
    {
        // TEST-NET-1, reserved by RFC 5737 for documentation and routed nowhere. The first address
        // tried here was 10.255.255.1, which turned out to be WSL's own subnet on this machine and
        // answered — a test that measured the developer's network layout rather than a dead host.
        SshException connecting = await Refused(
            SshEndpoint.For("192.0.2.1", "probe"), TimeSpan.FromSeconds(3));

        using Fake silent = new(greeting: null);

        SshException handshaking = await Refused(
            SshEndpoint.For("127.0.0.1", "probe", silent.Port), TimeSpan.FromSeconds(3));

        Assert.Equal(SshFailureKind.NotResponding, connecting.Kind);
        Assert.Equal(SshFailureKind.NotResponding, handshaking.Kind);

        // Both readings are offered, because the client genuinely does not know which it was.
        Assert.Contains("firewall", connecting.Means, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("identified itself", connecting.Means, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Stopping is its own kind, and says nothing was left half-done.</summary>
    [Fact]
    public async Task AbandoningAnAttemptIsItsOwnKind()
    {
        using Fake silent = new(greeting: null);
        using CancellationTokenSource abandoning = new();

        await using SshNetTransport transport = new();

        Task<SshException> failing = Assert.ThrowsAsync<SshException>(async () =>
            await transport.ConnectAsync(SshEndpoint.For("127.0.0.1", "probe", silent.Port),
                                         [AnyCredential], Trusting, abandoning.Token));

        await abandoning.CancelAsync();

        SshException failure = await failing;

        Assert.Equal(SshFailureKind.Cancelled, failure.Kind);
        Assert.Contains("half-done", failure.Means, StringComparison.Ordinal);
    }

    // ---- plumbing ----

    private static SshEndpoint Fixture => SshEndpoint.For("127.0.0.1", "probe", 2222);

    /// <summary>Attempts a connection that is expected to fail, and hands back why.</summary>
    /// <summary>
    /// Attempts a connection that is expected to fail, and hands back why.
    ///
    /// <para>The wait is the transport's own <see cref="ISshTransport.Timeout"/> rather than a
    /// cancellation token, and that distinction is the whole point: a token that fires first
    /// produces <see cref="SshFailureKind.Cancelled"/> and tells this test nothing about how the
    /// server failed. Which is how the first draft of it passed while asserting nothing.</para>
    /// </summary>
    private static async Task<SshException> Refused(SshEndpoint endpoint, TimeSpan? within = null)
    {
        await using SshNetTransport transport = new()
        {
            Timeout = within ?? TimeSpan.FromSeconds(20),
        };

        return await Assert.ThrowsAsync<SshException>(async () =>
            await transport.ConnectAsync(endpoint, [AnyCredential], Trusting, Stop));
    }

    /// <summary>A socket that accepts, says its piece if it has one, and then waits.</summary>
    private sealed class Fake : IDisposable
    {
        private readonly TcpListener _listener;

        public Fake(string? greeting)
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();

            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;

            _ = Task.Run(async () =>
            {
                try
                {
                    using TcpClient accepted = await _listener.AcceptTcpClientAsync();

                    if (greeting is not null)
                    {
                        await accepted.GetStream().WriteAsync(Encoding.ASCII.GetBytes(greeting));
                        await accepted.GetStream().FlushAsync();
                    }

                    await Task.Delay(TimeSpan.FromSeconds(30));
                }
                catch (Exception)
                {
                    // The listener being stopped underneath this is how it ends, every time.
                }
            });
        }

        public int Port { get; }

        public void Dispose() => _listener.Stop();
    }

    private static string FixtureKeys() =>
        Path.Combine(RepositoryRoot(), "prototypes", "SshProbe", "fixture", "keys");

    private static void SkipWithoutFixture()
    {
        bool up;

        try
        {
            using TcpClient probe = new();

            up = probe.ConnectAsync("127.0.0.1", 2222).Wait(TimeSpan.FromSeconds(2));
        }
        catch (Exception)
        {
            up = false;
        }

        Assert.SkipUnless(up && Directory.Exists(FixtureKeys()),
            "nothing is listening on 127.0.0.1:2222: run prototypes/SshProbe/fixture/up.sh");
    }

    private static string RepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Quickshell.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);

        return directory.FullName;
    }
}
