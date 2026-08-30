using System.Globalization;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace Quickshell.Transport;

/// <summary>
/// A port on the server that reaches a service running here.
///
/// <para><b>The server holds the veto, and that is the whole difference from a local forward.</b>
/// The client asks; the server decides whether to listen at all, and where. Two settings in its
/// <c>sshd_config</c> decide it — <c>AllowTcpForwarding</c>, which is off in a great many hardened
/// estates, and <c>GatewayPorts</c>, which is off by default everywhere.</para>
///
/// <para><b>A refusal carries no reason, because the protocol has none to carry.</b> SSH answers a
/// global request with success or failure and no text, so there is no server message to relay —
/// measured against a server with <c>AllowTcpForwarding no</c>, the library reports only "failed to
/// start", and names the local target rather than the port that was refused. So this supplies what
/// the protocol cannot: the two settings that produce this exact failure, and the port that was
/// actually asked for.</para>
///
/// <para><b>A forward that works may still be unreachable from anywhere but the server.</b> With
/// <c>GatewayPorts no</c> the server binds its loopback whatever address was requested, silently.
/// Measured here: reachable from the server itself, refused from a second machine on the same
/// network. Nothing in the reply says this happened, so <see cref="Caveat"/> says it instead —
/// before the user spends an afternoon on it.</para>
/// </summary>
public sealed class RemoteForward : IAsyncDisposable
{
    private readonly ForwardedPortRemote _port;

    private long _connections;
    private bool _disposed;

    private RemoteForward(ForwardedPortRemote port, string localHost, int localPort, int asked)
    {
        _port = port;
        LocalHost = localHost;
        LocalPort = localPort;
        Asked = asked;
    }

    /// <summary>
    /// The port the server is listening on. Where zero was asked for, this is the server's own
    /// choice, read from its reply.
    /// </summary>
    public int BoundPort => (int)_port.BoundPort;

    /// <summary>What was asked for, which is zero where the server was left to choose.</summary>
    public int Asked { get; }

    /// <summary>The host on this machine that connections are delivered to.</summary>
    public string LocalHost { get; }

    /// <summary>The port on it.</summary>
    public int LocalPort { get; }

    /// <summary>Whether the server is still listening.</summary>
    public bool IsOpen => !_disposed && _port.IsStarted;

    /// <summary>How many connections the server has sent back.</summary>
    public long Connections => Interlocked.Read(ref _connections);

    /// <summary>
    /// What a user needs to know about every remote forward, whether or not anything went wrong.
    ///
    /// <para>It is not a warning about this forward: it is the one fact that turns "it does not
    /// work from my colleague's machine" from an afternoon into a sentence.</para>
    /// </summary>
    public string Caveat =>
        $"The server decides where it listens. Unless its sshd_config sets GatewayPorts, port "
        + $"{BoundPort} is bound on the server's own loopback only, so it is reachable from the "
        + "server and from nowhere else.";

    /// <summary>
    /// Asks the server to listen on a port and send what arrives back to a service running here.
    /// </summary>
    /// <param name="over">A connected session.</param>
    /// <param name="remotePort">The port to ask for on the server, or zero to let it choose.</param>
    /// <param name="localHost">Where connections are delivered on this machine.</param>
    /// <param name="localPort">The port there.</param>
    /// <exception cref="SshException">The server refused the request.</exception>
    public static RemoteForward Open(SshNetTransport over, int remotePort, string localHost,
                                     int localPort)
    {
        ArgumentNullException.ThrowIfNull(over);
        ArgumentException.ThrowIfNullOrWhiteSpace(localHost);
        ArgumentOutOfRangeException.ThrowIfNegative(remotePort);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(remotePort, 65535);
        ArgumentOutOfRangeException.ThrowIfLessThan(localPort, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(localPort, 65535);

        SshClient client = over.Client
            ?? throw new SshException(SshFailureKind.Dropped,
                                      "There is no connection to carry a forward.",
                                      "The session is not open.");

        // Loopback, named. The library resolves this string locally even though the server is what
        // binds it, so an address meaning "anywhere" cannot be expressed at all — see QS125.
        ForwardedPortRemote port =
            new("127.0.0.1", (uint)remotePort, localHost, (uint)localPort);

        RemoteForward forward = new(port, localHost, localPort, remotePort);

        port.RequestReceived += forward.Accepted;

        client.AddForwardedPort(port);

        try
        {
            port.Start();
        }
        catch (Renci.SshNet.Common.SshException refused)
        {
            client.RemoveForwardedPort(port);
            port.Dispose();

            throw Refused(remotePort, refused.Message);
        }

        return forward;
    }

    /// <summary>
    /// The refusal, said usefully.
    ///
    /// <para>The protocol carries no reason with a failed global request, and the library's message
    /// names the local target rather than the port that was refused. Both are corrected here: what
    /// the user asked for, and the two settings that are almost always the cause.</para>
    /// </summary>
    private static SshException Refused(int remotePort, string origin) =>
        new(SshFailureKind.Refused,
            remotePort == 0
                ? "The server refused to listen on any port for this forward."
                : $"The server refused to listen on port {remotePort}.",
            "SSH gives no reason with a refused forwarding request, so there is nothing here from "
            + "the server itself.",
            "Two settings cause this: AllowTcpForwarding off in the server's sshd_config, which "
            + "refuses every forward, and a port already held by another session — which a stale "
            + "listener from a dropped connection can hold for a while. Ask for port zero to have "
            + "the server choose a free one.",
            origin);

    private void Accepted(object? sender, PortForwardEventArgs what) =>
        Interlocked.Increment(ref _connections);

    /// <summary>Stops the server listening. No forward outlives the object that owns it.</summary>
    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;

        _port.RequestReceived -= Accepted;

        try
        {
            if (_port.IsStarted)
            {
                _port.Stop();
            }
        }
        catch (Exception)
        {
            // Gone with the session, which is the ordinary way this ends.
        }

        _port.Dispose();

        return ValueTask.CompletedTask;
    }

    /// <summary>How a person writes it down.</summary>
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture,
                      $"server:{BoundPort} -> {LocalHost}:{LocalPort}");
}
