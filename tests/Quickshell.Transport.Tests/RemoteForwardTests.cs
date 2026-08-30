using System.Net;
using System.Net.Sockets;
using System.Text;
using Quickshell.Transport;
using Xunit;

namespace Quickshell.Transport.Tests;

/// <summary>
/// A port on the server that reaches this machine, and the refusal the protocol gives no reason for.
///
/// <para>Both servers here are real: one that allows forwarding and one built with
/// <c>AllowTcpForwarding no</c>, which is what a hardened estate looks like. A refusal produced by
/// a switch in this client would prove nothing about either.</para>
/// </summary>
public sealed class RemoteForwardTests : IAsyncDisposable
{
    private const string Host = "127.0.0.1";
    private const int Ordinary = 2222;
    private const int Refusing = 2226;
    private const int Elsewhere = 2223;

    private readonly List<TcpListener> _listening = [];
    private readonly CancellationTokenSource _serving = new();

    private static CancellationToken Stop => TestContext.Current.CancellationToken;

    public async ValueTask DisposeAsync()
    {
        await _serving.CancelAsync();

        foreach (TcpListener listener in _listening)
        {
            listener.Stop();
        }

        _serving.Dispose();
    }

    // ---- The falsification ----

    /// <summary>
    /// The line's own falsification: a refused forward is reported with what caused it.
    ///
    /// <para>The protocol carries no reason — SSH answers a global request with success or failure
    /// and no text — so there is no server message to relay, and the library's own says only
    /// "failed to start" while naming the local target rather than the port refused. Both are what
    /// this test exists to refuse: the message must name the port that was asked for and the two
    /// settings that produce this.</para>
    /// </summary>
    [Fact]
    public async Task ARefusedForwardNamesThePortAndWhatCausesIt()
    {
        SkipWithout(Refusing);

        await using SshNetTransport session = await Connected(Refusing);

        SshException refused = Assert.Throws<SshException>(() =>
            RemoteForward.Open(session, 20080, Host, Serving("nothing")));

        Assert.Equal(SshFailureKind.Refused, refused.Kind);

        // The port that was asked for, not the one the library talks about.
        Assert.Contains("20080", refused.Message, StringComparison.Ordinal);

        // The two settings that cause it, because the protocol supplies nothing.
        Assert.Contains("AllowTcpForwarding", refused.Remedy, StringComparison.Ordinal);
        Assert.Contains("already held", refused.Remedy, StringComparison.Ordinal);

        // And it is honest about there being no message from the server.
        Assert.Contains("no reason", refused.Means, StringComparison.Ordinal);
    }

    /// <summary>Asking the server to choose is refused the same way, and says so without a number.</summary>
    [Fact]
    public async Task ARefusalWithNoPortAskedForSaysSo()
    {
        SkipWithout(Refusing);

        await using SshNetTransport session = await Connected(Refusing);

        SshException refused = Assert.Throws<SshException>(() =>
            RemoteForward.Open(session, 0, Host, Serving("nothing")));

        Assert.Contains("any port", refused.Message, StringComparison.Ordinal);
    }

    // ---- What a remote forward is for ----

    /// <summary>
    /// The symptom, gone: a service running here is reached from the remote host.
    ///
    /// <para>The far side connects to its own loopback and gets what this machine served, which is
    /// the whole mechanism and the direction a local forward cannot do.</para>
    /// </summary>
    [Fact]
    public async Task AServiceRunningHereIsReachedFromTheServer()
    {
        SkipWithout(Ordinary);

        await using SshNetTransport session = await Connected(Ordinary);

        int mine = Serving("hello-from-this-machine");

        await using RemoteForward forward = RemoteForward.Open(session, 0, Host, mine);

        Assert.True(forward.IsOpen);

        Assert.Equal("hello-from-this-machine", await Reach(session, forward.BoundPort));
        Assert.Equal(1, forward.Connections);
    }

    /// <summary>
    /// Port zero means the server allocates, and the port it chose is what comes back.
    ///
    /// <para>Reading the reply is what lets the client show a port a user can act on rather than
    /// the zero they asked for.</para>
    /// </summary>
    [Fact]
    public async Task PortZeroIsChosenByTheServerAndReported()
    {
        SkipWithout(Ordinary);

        await using SshNetTransport session = await Connected(Ordinary);

        await using RemoteForward forward = RemoteForward.Open(session, 0, Host, Serving("chosen"));

        Assert.Equal(0, forward.Asked);
        Assert.InRange(forward.BoundPort, 1, 65535);

        // And it is the port that works, which is the only thing that makes it worth reporting.
        Assert.Equal("chosen", await Reach(session, forward.BoundPort));
    }

    // ---- Where the server actually listened ----

    /// <summary>
    /// A forward that works from the server is unreachable from anywhere else, and the client says
    /// so before anybody has to find out.
    ///
    /// <para>Measured from a second machine on the same network as the server. With
    /// <c>GatewayPorts</c> unset the server binds its own loopback whatever was asked, and nothing
    /// in its reply mentions that — which is why the sentence exists rather than the reply.</para>
    /// </summary>
    [Fact]
    public async Task AForwardBoundOnTheServersLoopbackSaysThatItIs()
    {
        SkipWithout(Ordinary);
        SkipWithout(Elsewhere);

        await using SshNetTransport session = await Connected(Ordinary);

        await using RemoteForward forward = RemoteForward.Open(session, 0, Host, Serving("private"));

        // The server itself reaches it.
        Assert.Equal("private", await Reach(session, forward.BoundPort));

        // A second machine on the same network does not.
        await using SshNetTransport elsewhere = await Connected(Elsewhere);

        string fromThere = await Run(elsewhere,
            $"timeout 3 bash -c 'cat < /dev/tcp/qs-sshd-target/{forward.BoundPort}' 2>&1 || echo REFUSED");

        Assert.Contains("REFUSED", fromThere, StringComparison.Ordinal);

        // And the client had already said why, in the words a user can act on.
        Assert.Contains("GatewayPorts", forward.Caveat, StringComparison.Ordinal);
        Assert.Contains($"{forward.BoundPort}", forward.Caveat, StringComparison.Ordinal);
    }

    // ---- Nothing outlives its owner ----

    /// <summary>The server stops listening when the forward goes.</summary>
    [Fact]
    public async Task NoListenerOutlivesTheForward()
    {
        SkipWithout(Ordinary);

        await using SshNetTransport session = await Connected(Ordinary);

        RemoteForward forward = RemoteForward.Open(session, 0, Host, Serving("briefly"));

        int port = forward.BoundPort;

        Assert.Equal("briefly", await Reach(session, port));

        await forward.DisposeAsync();

        Assert.False(forward.IsOpen);

        Assert.Contains("REFUSED", await Reach(session, port), StringComparison.Ordinal);
    }

    // ---- plumbing ----

    /// <summary>A local listener that answers with one line and hangs up.</summary>
    private int Serving(string answer)
    {
        TcpListener listener = new(IPAddress.Loopback, 0);

        listener.Start();

        _listening.Add(listener);

        _ = Task.Run(async () =>
        {
            try
            {
                while (!_serving.IsCancellationRequested)
                {
                    using TcpClient accepted =
                        await listener.AcceptTcpClientAsync(_serving.Token);

                    await accepted.GetStream().WriteAsync(Encoding.ASCII.GetBytes(answer),
                                                          _serving.Token);
                }
            }
            catch (Exception)
            {
                // The test finished, which is how this loop is meant to end.
            }
        }, CancellationToken.None);

        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    /// <summary>What the server gets when it connects to its own loopback on that port.</summary>
    private static async Task<string> Reach(SshNetTransport session, int port) =>
        (await Run(session,
                   $"timeout 3 bash -c 'cat < /dev/tcp/127.0.0.1/{port}' 2>&1 || echo REFUSED"))
        .Trim();

    /// <summary>Runs a command on the far side and returns what it printed.</summary>
    private static async Task<string> Run(SshNetTransport session, string command)
    {
        await using IPtyChannel shell = await session.OpenShellAsync(200, 25, Stop);

        string begin = $"qsB{Guid.NewGuid():N}";
        string end = $"qsE{Guid.NewGuid():N}";

        StringBuilder seen = new();
        byte[] buffer = new byte[8 * 1024];

        await shell.WriteAsync(Encoding.UTF8.GetBytes($"echo {begin}; {command}; echo {end}\n"),
                               Stop);

        using CancellationTokenSource waiting = CancellationTokenSource.CreateLinkedTokenSource(Stop);
        waiting.CancelAfter(TimeSpan.FromSeconds(25));

        try
        {
            while (!Closed(seen.ToString(), begin, end))
            {
                int read = await shell.ReadAsync(buffer, waiting.Token);

                if (read == 0)
                {
                    break;
                }

                seen.Append(Encoding.UTF8.GetString(buffer, 0, read));
            }
        }
        catch (OperationCanceledException)
        {
        }

        string all = seen.ToString().Replace("\r", string.Empty, StringComparison.Ordinal);

        int from = all.IndexOf($"{begin}\n", StringComparison.Ordinal);

        if (from < 0)
        {
            return string.Empty;
        }

        from += begin.Length + 1;

        int to = all.IndexOf(end, from, StringComparison.Ordinal);

        return to <= from ? string.Empty : all[from..to];
    }

    private static bool Closed(string all, string begin, string end)
    {
        int from = all.IndexOf($"{begin}\n", StringComparison.Ordinal);

        return from >= 0 && all.IndexOf(end, from + begin.Length + 1, StringComparison.Ordinal) >= 0;
    }

    private static async Task<SshNetTransport> Connected(int port)
    {
        SshNetTransport session = new();

        await session.ConnectAsync(SshEndpoint.For(Host, "probe", port), [Key()], Trusting, Stop);

        return session;
    }

    private static ValueTask<SshHostKeyVerdict> Trusting(SshEndpoint _, SshHostKey __,
                                                         CancellationToken ___) =>
        ValueTask.FromResult(SshHostKeyVerdict.Accept);

    private static SshCredential.PrivateKey Key() =>
        new(Path.Combine(RepositoryRoot(), "prototypes", "SshProbe", "fixture", "keys",
                         "probe_ed25519"));

    private static void SkipWithout(int port)
    {
        bool up;

        try
        {
            using TcpClient probe = new();

            up = probe.ConnectAsync(Host, port).Wait(TimeSpan.FromSeconds(2));
        }
        catch (Exception)
        {
            up = false;
        }

        Assert.SkipUnless(up, $"nothing is listening on 127.0.0.1:{port}: run prototypes/SshProbe/fixture/up.sh");
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
