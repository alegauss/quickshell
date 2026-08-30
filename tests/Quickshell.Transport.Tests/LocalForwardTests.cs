using System.Net;
using System.Net.Sockets;
using System.Text;
using Quickshell.Transport;
using Xunit;

namespace Quickshell.Transport.Tests;

/// <summary>
/// A local port that reaches the remote network, and the binding that must never widen by itself.
///
/// <para>Where a listener actually accepts is asked of the operating system rather than of this
/// client's intention. That distinction is not pedantry: SSH.NET's convenience constructor reports
/// an empty bound host and, measured this way, listens on a link-local address other machines can
/// reach.</para>
/// </summary>
public sealed class LocalForwardTests
{
    private const string Host = "127.0.0.1";
    private const int Port = 2222;

    /// <summary>Reachable only on the container network, so reaching it proves remote resolution.</summary>
    private const string OnlyOverThere = "qs-sshd-jump";

    private static CancellationToken Stop => TestContext.Current.CancellationToken;

    // ---- The falsification ----

    /// <summary>
    /// The line's own falsification: a forward binds beyond loopback only when asked.
    ///
    /// <para>Checked against the operating system's own list of listeners, because what this client
    /// meant to bind and what it bound are different questions — and the library's convenience
    /// constructor gets the second one wrong.</para>
    /// </summary>
    [Fact]
    public async Task AForwardBindsToLoopbackAndNothingElse()
    {
        SkipWithoutFixture();

        await using SshNetTransport session = await Connected();

        await using LocalForward forward = LocalForward.Open(session, OnlyOverThere, 22);

        IReadOnlyList<IPAddress> listening = LocalForward.ListeningOn(forward.BoundPort);

        Assert.NotEmpty(listening);

        Assert.All(listening, address =>
            Assert.True(IPAddress.IsLoopback(address),
                        $"it is listening on {address}, which is not loopback"));

        Assert.Equal("127.0.0.1", forward.BoundHost);

        // And nothing is said to the user, because nothing happened worth saying.
        Assert.Equal(string.Empty, forward.Warning);
    }

    /// <summary>
    /// Binding wide takes a value, and says who can now reach the remote network.
    ///
    /// <para>The warning is the point. A forward on every interface is a route into the remote
    /// network for anybody who can reach this machine, and it needs no credential of theirs.</para>
    /// </summary>
    [Fact]
    public async Task BindingWideTakesAskingAndWarns()
    {
        SkipWithoutFixture();

        await using SshNetTransport session = await Connected();

        string mine = Outward();

        await using LocalForward forward =
            LocalForward.Open(session, OnlyOverThere, 22, binding: ForwardBinding.To(mine));

        Assert.False(forward.Binding.IsLoopback);

        Assert.Contains(mine, forward.Warning, StringComparison.Ordinal);
        Assert.Contains("without authenticating", forward.Warning, StringComparison.Ordinal);

        // And it really did widen, so the warning is not decorative.
        Assert.Contains(LocalForward.ListeningOn(forward.BoundPort),
                        address => !IPAddress.IsLoopback(address));
    }

    /// <summary>
    /// There is no way to ask for every interface at once, and asking says why rather than binding
    /// somewhere arbitrary.
    /// </summary>
    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("::")]
    public void TheUnspecifiedAddressIsRefusedWithItsReason(string address)
    {
        SshException refused = Assert.Throws<SshException>(() => ForwardBinding.To(address));

        Assert.Contains("every interface", refused.Means, StringComparison.Ordinal);
    }

    /// <summary>And a name is not an address, because a name can move.</summary>
    [Fact]
    public void ANameIsNotABinding()
    {
        SshException refused = Assert.Throws<SshException>(() => ForwardBinding.To("localhost"));

        Assert.Contains("not a name", refused.Means, StringComparison.Ordinal);
    }

    // ---- What a forward is for ----

    /// <summary>
    /// The symptom, gone: a host with no local route is reached through the forward.
    ///
    /// <para><c>qs-sshd-jump</c> resolves only on the container network, so a connection that gets
    /// its banner is one the <em>server</em> resolved and dialled. That is the part users most often
    /// misunderstand, and it is the whole mechanism.</para>
    /// </summary>
    [Fact]
    public async Task AHostWithNoLocalRouteIsReachedThroughIt()
    {
        SkipWithoutFixture();

        await using SshNetTransport session = await Connected();

        await using LocalForward forward = LocalForward.Open(session, OnlyOverThere, 22);

        Assert.StartsWith("SSH-2.0-", await Banner(forward.BoundPort), StringComparison.Ordinal);
        Assert.Equal(1, forward.Connections);

        // The name is never resolved here: nothing on this machine knows it.
        await Assert.ThrowsAnyAsync<SocketException>(async () =>
            await Dns.GetHostAddressesAsync(OnlyOverThere, Stop));
    }

    /// <summary>Port zero lets the system choose, and the choice is reported back.</summary>
    [Fact]
    public async Task PortZeroIsChosenBySystemAndReported()
    {
        SkipWithoutFixture();

        await using SshNetTransport session = await Connected();

        await using LocalForward one = LocalForward.Open(session, OnlyOverThere, 22);
        await using LocalForward other = LocalForward.Open(session, OnlyOverThere, 22);

        Assert.InRange(one.BoundPort, 1, 65535);
        Assert.InRange(other.BoundPort, 1, 65535);

        // Two forwards to the same service, and nobody allocated a number by hand.
        Assert.NotEqual(one.BoundPort, other.BoundPort);
    }

    /// <summary>
    /// Each connection is its own channel: one closing leaves the others carrying traffic.
    /// </summary>
    [Fact]
    public async Task OneConnectionClosingDisturbsNoOther()
    {
        SkipWithoutFixture();

        await using SshNetTransport session = await Connected();

        await using LocalForward forward = LocalForward.Open(session, OnlyOverThere, 22);

        List<Socket> open = [];

        try
        {
            for (int each = 0; each < 3; each++)
            {
                open.Add(await Dial(forward.BoundPort));
            }

            byte[] buffer = new byte[128];

            foreach (Socket socket in open)
            {
                Assert.True(await socket.ReceiveAsync(buffer, Stop) > 0);
            }

            open[0].Close();

            // The other two are still there and still speaking.
            for (int each = 1; each < open.Count; each++)
            {
                await open[each].SendAsync(Encoding.ASCII.GetBytes("SSH-2.0-quickshell-test\r\n"),
                                           Stop);

                Assert.True(open[each].Connected);
            }

            Assert.Equal(3, forward.Connections);
        }
        finally
        {
            foreach (Socket socket in open)
            {
                socket.Dispose();
            }
        }
    }

    // ---- Three errors, three remedies ----

    /// <summary>
    /// A local port already in use is refused where the listener is opened, naming the remedy.
    ///
    /// <para>The first of the three failures this design insists on telling apart, and the only one
    /// that can be answered before any traffic flows.</para>
    /// </summary>
    [Fact]
    public async Task ALocalPortAlreadyInUseSaysSoAndOffersZero()
    {
        SkipWithoutFixture();

        await using SshNetTransport session = await Connected();

        TcpListener busy = new(IPAddress.Loopback, 0);

        busy.Start();

        int taken = ((IPEndPoint)busy.LocalEndpoint).Port;

        try
        {
            SshException refused = Assert.Throws<SshException>(() =>
                LocalForward.Open(session, OnlyOverThere, 22, taken));

            Assert.Equal(SshFailureKind.Refused, refused.Kind);
            Assert.Contains($"{taken}", refused.Message, StringComparison.Ordinal);
            Assert.Contains("already listening", refused.Means, StringComparison.Ordinal);
            Assert.Contains("pass zero", refused.Remedy, StringComparison.Ordinal);
        }
        finally
        {
            busy.Stop();
        }
    }

    /// <summary>
    /// A target that refuses closes the connection with nothing sent, and the library says no more
    /// than that.
    ///
    /// <para><b>This test records a limitation rather than a behaviour.</b> Port nine on the far
    /// side has nothing listening, so the channel opens and the far end's connection fails — and
    /// SSH.NET reports it exactly as it reports an ordinary close: no exception, no event, an empty
    /// read. So of the three failures this design wanted told apart, only the local port clash is
    /// distinguishable today. QS125 carries the rest, and when it is answered this test is what
    /// changes.</para>
    /// </summary>
    [Fact]
    public async Task ATargetThatRefusesIsIndistinguishableFromAClose()
    {
        SkipWithoutFixture();

        await using SshNetTransport session = await Connected();

        await using LocalForward forward = LocalForward.Open(session, "127.0.0.1", 9);

        using (Socket socket = await Dial(forward.BoundPort))
        {
            byte[] buffer = new byte[16];

            // Nothing arrives: the far end could not connect, so the channel closes empty.
            Assert.Equal(0, await socket.ReceiveAsync(buffer, Stop));
        }

        await Settle(() => forward.Failures.Count > 0);

        // And nothing was reported, which is the limitation stated as an assertion so that the day
        // it stops being true, something says so.
        Assert.Empty(forward.Failures);
    }

    // ---- Nothing outlives its owner ----

    /// <summary>The listener goes when the forward does, so no port is left open behind it.</summary>
    [Fact]
    public async Task NoListenerOutlivesTheForward()
    {
        SkipWithoutFixture();

        await using SshNetTransport session = await Connected();

        LocalForward forward = LocalForward.Open(session, OnlyOverThere, 22);

        int port = forward.BoundPort;

        Assert.NotEmpty(LocalForward.ListeningOn(port));

        await forward.DisposeAsync();

        Assert.False(forward.IsOpen);

        await Settle(() => LocalForward.ListeningOn(port).Count == 0);

        Assert.Empty(LocalForward.ListeningOn(port));
    }

    // ---- plumbing ----

    /// <summary>An address this machine holds that is not loopback, or a skip where it has none.</summary>
    private static string Outward()
    {
        string? found = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
            .Where(card => card.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up)
            .SelectMany(card => card.GetIPProperties().UnicastAddresses)
            .Select(unicast => unicast.Address)
            .FirstOrDefault(address => address.AddressFamily == AddressFamily.InterNetwork
                                       && !IPAddress.IsLoopback(address))
            ?.ToString();

        Assert.SkipWhen(found is null, "this machine has no address but loopback to bind to");

        return found!;
    }

    private static async Task<string> Banner(int port)
    {
        using Socket socket = await Dial(port);

        byte[] buffer = new byte[128];

        int read = await socket.ReceiveAsync(buffer, Stop);

        return Encoding.ASCII.GetString(buffer, 0, read);
    }

    private static async Task<Socket> Dial(int port)
    {
        Socket socket = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

        await socket.ConnectAsync(IPAddress.Loopback, port, Stop);

        return socket;
    }

    /// <summary>Waits for something the far side does on its own schedule.</summary>
    private static async Task Settle(Func<bool> ready)
    {
        for (int attempt = 0; attempt < 50 && !ready(); attempt++)
        {
            await Task.Delay(100, Stop);
        }
    }

    private static async Task<SshNetTransport> Connected()
    {
        SshNetTransport session = new();

        await session.ConnectAsync(SshEndpoint.For(Host, "probe", Port), [Key()], Trusting, Stop);

        return session;
    }

    private static ValueTask<SshHostKeyVerdict> Trusting(SshEndpoint _, SshHostKey __,
                                                         CancellationToken ___) =>
        ValueTask.FromResult(SshHostKeyVerdict.Accept);

    private static SshCredential.PrivateKey Key() =>
        new(Path.Combine(RepositoryRoot(), "prototypes", "SshProbe", "fixture", "keys",
                         "probe_ed25519"));

    private static void SkipWithoutFixture()
    {
        bool up;

        try
        {
            using TcpClient probe = new();

            up = probe.ConnectAsync(Host, Port).Wait(TimeSpan.FromSeconds(2));
        }
        catch (Exception)
        {
            up = false;
        }

        Assert.SkipUnless(up, "nothing is listening on 127.0.0.1:2222: run prototypes/SshProbe/fixture/up.sh");
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
