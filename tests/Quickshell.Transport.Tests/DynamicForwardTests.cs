using System.Net;
using System.Net.Sockets;
using System.Text;
using Quickshell.Transport;
using Xunit;

namespace Quickshell.Transport.Tests;

/// <summary>
/// The SOCKS proxy, spoken to as a browser speaks to one, against the fixture (QS127).
///
/// <para>The library's own proxy answered about one request in six with the target's bytes where
/// the reply belonged. Each test here reads the reply byte by byte before anything else, so a reply
/// that lost the race to the target's banner fails on the first byte rather than somewhere
/// later.</para>
/// </summary>
public sealed class DynamicForwardTests
{
    private const string Host = "127.0.0.1";
    private const int Port = 2222;

    /// <summary>Reachable only on the container network, so reaching it proves remote resolution.</summary>
    private const string OnlyOverThere = "qs-sshd-jump";

    private static CancellationToken Stop => TestContext.Current.CancellationToken;

    /// <summary>
    /// QS127's falsification: a hundred connects through the proxy all receive a well-formed reply,
    /// and then the target's own first bytes — an SSH banner — and never the other way round.
    /// </summary>
    [Fact]
    public async Task AHundredConnectsAllReceiveAWellFormedReply()
    {
        SkipWithoutFixture();

        await using SshNetTransport session = await Connected(Port);

        await using DynamicForward proxy = DynamicForward.Open(session);

        for (int each = 0; each < 100; each++)
        {
            using Socket socket = await Dial(proxy.BoundPort);

            byte[] reply = await Request(socket, 1, OnlyOverThere, 22);

            Assert.True(reply is [5, 0, 0, _, ..], $"connect {each} was answered {Convert.ToHexString(reply)}");
            Assert.StartsWith("SSH-2.0-", await Read(socket, 8), StringComparison.Ordinal);
        }

        Assert.Equal(100, proxy.Connections);
    }

    /// <summary>
    /// QS68's falsification: a host name is resolved by the server and never here. The name is one
    /// this machine cannot resolve at all, so a proxy that looked it up locally would fail every
    /// time; this one reaches it, over SOCKS5 and SOCKS4a alike.
    /// </summary>
    [Fact]
    public async Task ANameIsResolvedByTheServerAndNeverHere()
    {
        SkipWithoutFixture();

        await Assert.ThrowsAnyAsync<SocketException>(async () => await Dns.GetHostAddressesAsync(OnlyOverThere, Stop));

        await using SshNetTransport session = await Connected(Port);

        await using DynamicForward proxy = DynamicForward.Open(session);

        using (Socket five = await Dial(proxy.BoundPort))
        {
            Assert.Equal(0, (await Request(five, 1, OnlyOverThere, 22))[1]);
            Assert.StartsWith("SSH-2.0-", await Read(five, 8), StringComparison.Ordinal);
        }

        // SOCKS4a: 0.0.0.1 as the address says a name follows the (empty) user id.
        using Socket four = await Dial(proxy.BoundPort);
        List<byte> request = [4, 1, 0, 22, 0, 0, 0, 1, 0, .. Encoding.ASCII.GetBytes(OnlyOverThere), 0];

        await four.SendAsync(request.ToArray(), Stop);

        byte[] reply = await Exactly(four, 8);

        Assert.Equal([0, 0x5A], reply[..2]);
        Assert.StartsWith("SSH-2.0-", await Read(four, 8), StringComparison.Ordinal);
    }

    /// <summary>
    /// BIND is refused as a command the proxy does not support — not answered with success and then
    /// a connection somewhere else, which is what the library's did.
    /// </summary>
    [Fact]
    public async Task BindIsRefusedAsUnsupported()
    {
        SkipWithoutFixture();

        await using SshNetTransport session = await Connected(Port);

        await using DynamicForward proxy = DynamicForward.Open(session);

        using Socket socket = await Dial(proxy.BoundPort);

        byte[] reply = await Request(socket, 2, OnlyOverThere, 22);

        Assert.Equal(7, reply[1]);
    }

    /// <summary>
    /// A target with nothing listening is "connection refused", and a server that forbids forwarding
    /// is "not allowed" — the two codes a client can act on, from the server's own reason (QS125).
    /// </summary>
    [Fact]
    public async Task RefusalsAreAnsweredWithTheReplyThatNamesThem()
    {
        SkipWithoutFixture();

        await using (SshNetTransport session = await Connected(Port))
        await using (DynamicForward proxy = DynamicForward.Open(session))
        {
            using Socket socket = await Dial(proxy.BoundPort);

            Assert.Equal(5, (await Request(socket, 1, "127.0.0.1", 9))[1]);
        }

        await using (SshNetTransport forbidding = await Connected(2226))
        await using (DynamicForward proxy = DynamicForward.Open(forbidding))
        {
            using Socket socket = await Dial(proxy.BoundPort);

            Assert.Equal(2, (await Request(socket, 1, "127.0.0.1", 22))[1]);
        }
    }

    /// <summary>
    /// Half of a close is half through the proxy too (QS124): send, shut the sending half, and the
    /// fixture's counting service still answers.
    /// </summary>
    [Fact]
    public async Task ShuttingTheSendingHalfStillGetsTheAnswer()
    {
        SkipWithoutFixture();

        await using SshNetTransport session = await Connected(Port);

        await using DynamicForward proxy = DynamicForward.Open(session);

        using Socket socket = await Dial(proxy.BoundPort);

        Assert.Equal(0, (await Request(socket, 1, OnlyOverThere, 7007))[1]);

        await socket.SendAsync(Encoding.ASCII.GetBytes("hello"), Stop);
        socket.Shutdown(SocketShutdown.Send);

        Assert.Equal("5", (await Read(socket, int.MaxValue)).Trim());
    }

    /// <summary>The proxy binds loopback unless asked, and its listener goes when it does.</summary>
    [Fact]
    public async Task TheProxyIsLoopbackAndGoesWhenItIsClosed()
    {
        SkipWithoutFixture();

        await using SshNetTransport session = await Connected(Port);

        DynamicForward proxy = DynamicForward.Open(session);
        int port = proxy.BoundPort;

        Assert.All(LocalForward.ListeningOn(port), address => Assert.True(IPAddress.IsLoopback(address)));
        Assert.Equal(string.Empty, proxy.Warning);

        await proxy.DisposeAsync();

        Assert.False(proxy.IsOpen);
        Assert.Empty(LocalForward.ListeningOn(port));
    }

    // ---- plumbing ----

    /// <summary>The greeting, then one request; answers the ten-byte reply.</summary>
    private static async Task<byte[]> Request(Socket socket, byte command, string host, int port)
    {
        await socket.SendAsync(new byte[] { 5, 1, 0 }, Stop);

        byte[] method = await Exactly(socket, 2);

        Assert.Equal([5, 0], method);

        byte[] name = Encoding.ASCII.GetBytes(host);
        List<byte> request = [5, command, 0, 3, (byte)name.Length, .. name, (byte)(port >> 8), (byte)port];

        await socket.SendAsync(request.ToArray(), Stop);

        return await Exactly(socket, 10);
    }

    private static async Task<byte[]> Exactly(Socket socket, int count)
    {
        byte[] buffer = new byte[count];
        int filled = 0;

        using CancellationTokenSource waiting = CancellationTokenSource.CreateLinkedTokenSource(Stop);

        waiting.CancelAfter(TimeSpan.FromSeconds(10));

        while (filled < count)
        {
            int read = await socket.ReceiveAsync(buffer.AsMemory(filled), SocketFlags.None, waiting.Token);

            if (read == 0)
            {
                break;
            }

            filled += read;
        }

        return buffer[..filled];
    }

    /// <summary>Reads until this many characters have come, or the far end stops.</summary>
    private static async Task<string> Read(Socket socket, int atLeast)
    {
        StringBuilder text = new();
        byte[] buffer = new byte[256];

        using CancellationTokenSource waiting = CancellationTokenSource.CreateLinkedTokenSource(Stop);

        waiting.CancelAfter(TimeSpan.FromSeconds(10));

        while (text.Length < atLeast)
        {
            int read = await socket.ReceiveAsync(buffer, SocketFlags.None, waiting.Token);

            if (read == 0)
            {
                break;
            }

            text.Append(Encoding.ASCII.GetString(buffer, 0, read));
        }

        return text.ToString();
    }

    private static async Task<Socket> Dial(int port)
    {
        Socket socket = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

        await socket.ConnectAsync(IPAddress.Loopback, port, Stop);

        return socket;
    }

    private static async Task<SshNetTransport> Connected(int port)
    {
        SshNetTransport session = new();

        await session.ConnectAsync(SshEndpoint.For(Host, "probe", port), [Key()],
                                   (_, _, _) => ValueTask.FromResult(SshHostKeyVerdict.Accept), Stop);

        return session;
    }

    private static SshCredential.PrivateKey Key() =>
        new(Path.Combine(RepositoryRoot(), "prototypes", "SshProbe", "fixture", "keys", "probe_ed25519"));

    private static void SkipWithoutFixture()
    {
        bool up;

        try
        {
            using TcpClient probe = new();

            up = probe.ConnectAsync(Host, Port).Wait(TimeSpan.FromSeconds(2));
        }
        catch (Exception failure) when (failure is SocketException or AggregateException)
        {
            up = false;
        }

        Assert.SkipUnless(up && File.Exists(Key().Path),
            $"nothing is listening on {Host}:{Port}: run prototypes/SshProbe/fixture/up.sh");
    }

    private static string RepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Quickshell.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Quickshell.sln was not found above the test.");
    }
}
