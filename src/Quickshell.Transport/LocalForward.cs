using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace Quickshell.Transport;

/// <summary>
/// Where a forward's local listener accepts from.
///
/// <para><b>An address and not a switch, because "everywhere" is not available.</b> SSH.NET resolves
/// the bound host as a name, and refuses <c>0.0.0.0</c> outright; its one constructor that takes no
/// bound host binds to whatever that machine's empty-name resolution returns first, which measured
/// here was a link-local address other machines can reach. So there is no honest "all interfaces"
/// to offer, and QS125 carries that. What is offered instead is better: the caller names the
/// address, which is a narrower hole than "everywhere" and cannot be opened by accident.</para>
/// </summary>
/// <param name="Address">The local address to accept on.</param>
public readonly record struct ForwardBinding(string Address)
{
    /// <summary>This machine only, which is what anything gets without asking.</summary>
    public static ForwardBinding Loopback { get; } = new("127.0.0.1");

    /// <summary>Whether this is the default, private binding.</summary>
    public bool IsLoopback =>
        IPAddress.TryParse(Address, out IPAddress? address) && IPAddress.IsLoopback(address);

    /// <summary>
    /// One named address on this machine, so other machines can use the forward.
    /// </summary>
    /// <param name="address">An address this machine holds, as a literal rather than a name.</param>
    /// <exception cref="SshException">It is not an address, or it is one nothing can bind.</exception>
    public static ForwardBinding To(string address)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(address);

        if (!IPAddress.TryParse(address, out IPAddress? parsed))
        {
            throw new SshException(
                SshFailureKind.Unrecognised,
                $"{address} is not an address a forward can bind to.",
                "A binding is a literal address this machine holds, not a name.",
                "Use ForwardBinding.Loopback, or name an address from ipconfig.");
        }

        if (parsed.Equals(IPAddress.Any) || parsed.Equals(IPAddress.IPv6Any))
        {
            throw new SshException(
                SshFailureKind.Unrecognised,
                $"A forward cannot be bound to {address}.",
                "The library resolves the bound host as a name, and the unspecified address is not "
                + "one; there is no way through it to listen on every interface.",
                "Name the one address other machines should reach this forward at.");
        }

        return new ForwardBinding(address);
    }
}

/// <summary>
/// Why one connection through a forward did not work.
///
/// <para><b>Only some of these arrive.</b> A local port already in use is caught where the listener
/// opens and never appears here. A target that refuses is not reported by the library at all — the
/// channel closes with nothing sent, exactly as an ordinary close does — so it cannot be told apart
/// from a server that hung up, and QS125 carries that.</para>
/// </summary>
public enum ForwardTrouble
{
    /// <summary>The server would not open the channel at all.</summary>
    ServerRefused,

    /// <summary>The channel opened and the far side's target did not accept.</summary>
    TargetRefused,

    /// <summary>Something else, carried verbatim.</summary>
    Unrecognised,
}

/// <summary>One connection that failed, and what to do about it.</summary>
/// <param name="Trouble">Which of the three it was.</param>
/// <param name="Reason">What happened, in words.</param>
/// <param name="Remedy">What would fix it.</param>
public readonly record struct ForwardFailure(ForwardTrouble Trouble, string Reason, string Remedy);

/// <summary>
/// A local port that reaches a port on the remote network.
///
/// <para><b>The target is resolved by the server, not here.</b> A forward to <c>db.internal</c>
/// looks that name up in the remote network's DNS, where it means something — which is the whole
/// point and the thing users most often misunderstand when a name that resolves nowhere locally
/// works anyway.</para>
///
/// <para><b>Loopback unless somebody says otherwise, and this class is why.</b> SSH.NET's
/// convenience constructor takes a port with no bound host, and what it then binds to is not
/// loopback: measured against this machine, it listened on a link-local address reachable from the
/// network. So that constructor is never used here. Every forward names its bound host explicitly,
/// and widening it is a value a caller has to pass.</para>
///
/// <para><b>Port zero means the system chooses, and the choice is reported.</b> That is what lets
/// several forwards to the same service exist at once without somebody allocating numbers by
/// hand.</para>
///
/// <para><b>Each accepted connection is its own channel.</b> Twenty connections are twenty channels
/// and closing one disturbs none of the others. What does not work is a half-close: SSH.NET tears
/// the whole connection down when one direction shuts, which QS124 carries.</para>
/// </summary>
public sealed class LocalForward : IAsyncDisposable
{
    private readonly ForwardedPortLocal _port;
    private readonly List<ForwardFailure> _failures = [];
    private readonly Lock _guard = new();

    private long _connections;
    private bool _disposed;

    private LocalForward(ForwardedPortLocal port, string target, int targetPort,
                         ForwardBinding binding)
    {
        _port = port;
        TargetHost = target;
        TargetPort = targetPort;
        Binding = binding;
    }

    /// <summary>The address the local listener accepts on.</summary>
    public string BoundHost => _port.BoundHost;

    /// <summary>The local port, which is the one the system chose where zero was asked for.</summary>
    public int BoundPort => (int)_port.BoundPort;

    /// <summary>The host the far side connects to, spelled as the remote network spells it.</summary>
    public string TargetHost { get; }

    /// <summary>The port on that host.</summary>
    public int TargetPort { get; }

    /// <summary>Where this accepts from.</summary>
    public ForwardBinding Binding { get; }

    /// <summary>Whether the listener is up.</summary>
    public bool IsOpen => !_disposed && _port.IsStarted;

    /// <summary>How many connections have been carried.</summary>
    public long Connections => Interlocked.Read(ref _connections);

    /// <summary>
    /// What a user is told about this forward, or empty where there is nothing to say.
    ///
    /// <para>Bound wide, this is not a note: it says who else can now reach the remote network
    /// through this machine.</para>
    /// </summary>
    public string Warning =>
        Binding.IsLoopback
            ? string.Empty
            : $"This forward accepts on {Binding.Address}, so anything that can reach this machine "
              + $"there on port {BoundPort} can reach {TargetHost}:{TargetPort} on the remote "
              + "network without authenticating.";

    /// <summary>Connections that failed, and why.</summary>
    public IReadOnlyList<ForwardFailure> Failures
    {
        get
        {
            lock (_guard)
            {
                return [.. _failures];
            }
        }
    }

    /// <summary>
    /// Opens a forward from a local port to a host and port on the remote network.
    /// </summary>
    /// <param name="over">A connected session to carry it.</param>
    /// <param name="targetHost">The host, as the <em>server</em> resolves it.</param>
    /// <param name="targetPort">The port on that host.</param>
    /// <param name="listenPort">The local port, or zero to let the system choose.</param>
    /// <param name="binding">Where to accept from. Loopback unless asked otherwise.</param>
    /// <exception cref="SshException">The local port could not be taken.</exception>
    public static LocalForward Open(SshNetTransport over, string targetHost, int targetPort,
                                    int listenPort = 0, ForwardBinding? binding = null)
    {
        ArgumentNullException.ThrowIfNull(over);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetHost);
        ArgumentOutOfRangeException.ThrowIfLessThan(targetPort, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(targetPort, 65535);
        ArgumentOutOfRangeException.ThrowIfNegative(listenPort);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(listenPort, 65535);

        SshClient client = over.Client
            ?? throw new SshException(SshFailureKind.Dropped,
                                      "There is no connection to carry a forward.",
                                      "The session is not open.");

        // Named, never defaulted. The constructor that takes only a port binds somewhere this
        // design does not want, and there is no way to correct it afterwards.
        ForwardBinding where = binding ?? ForwardBinding.Loopback;

        ForwardedPortLocal port =
            new(where.Address, (uint)listenPort, targetHost, (uint)targetPort);

        LocalForward forward = new(port, targetHost, targetPort, where);

        port.Exception += forward.Trouble;
        port.RequestReceived += forward.Accepted;

        client.AddForwardedPort(port);

        try
        {
            port.Start();
        }
        catch (SocketException taken)
        {
            client.RemoveForwardedPort(port);
            port.Dispose();

            throw new SshException(
                SshFailureKind.Refused,
                $"The local port {listenPort} could not be opened.",
                "Something on this machine is already listening on it.",
                "Choose another port, or pass zero and let the system choose a free one.",
                taken.Message);
        }

        return forward;
    }

    /// <summary>
    /// Records what a failed connection was, told apart as far as the library allows.
    ///
    /// <para><b>Only two of the three are distinguishable here.</b> A local port already in use is
    /// caught where the listener starts. A server that refuses the channel arrives on this event. A
    /// target that refuses the connection does not: the channel simply closes with nothing sent, and
    /// SSH.NET reports it exactly as it reports an ordinary close. So it is inferred, and the
    /// message says it is an inference rather than pretending to certainty.</para>
    /// </summary>
    private void Trouble(object? sender, ExceptionEventArgs what)
    {
        ForwardFailure failure = what.Exception switch
        {
            SocketException socket => new ForwardFailure(
                ForwardTrouble.TargetRefused,
                $"{TargetHost}:{TargetPort} did not accept the connection: {socket.SocketErrorCode}.",
                "The name is resolved on the server, so check it from there rather than from here."),

            // Fully named: this file is in a namespace with an SshException of its own, and the
            // event carries the library's. An unqualified name here would bind to the wrong one and
            // this arm would never match.
            Renci.SshNet.Common.SshException channel => new ForwardFailure(
                ForwardTrouble.ServerRefused,
                $"The server would not open a channel to {TargetHost}:{TargetPort}: {channel.Message}",
                "The server may forbid forwarding: AllowTcpForwarding in its sshd config."),

            { } other => new ForwardFailure(ForwardTrouble.Unrecognised, other.Message, string.Empty),

            _ => new ForwardFailure(ForwardTrouble.Unrecognised, "something failed", string.Empty),
        };

        lock (_guard)
        {
            _failures.Add(failure);
        }
    }

    private void Accepted(object? sender, PortForwardEventArgs what) =>
        Interlocked.Increment(ref _connections);

    /// <summary>
    /// Closes the listener. No forward outlives the object that owns it.
    /// </summary>
    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;

        _port.Exception -= Trouble;
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
            // Already gone with the session, which is the ordinary way this ends.
        }

        _port.Dispose();

        return ValueTask.CompletedTask;
    }

    /// <summary>How a person writes it down.</summary>
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture,
                      $"{BoundHost}:{BoundPort} -> {TargetHost}:{TargetPort}");

    /// <summary>
    /// Every address a port is being listened on, which is how the loopback claim is checked
    /// against the operating system rather than against this client's own intention.
    /// </summary>
    /// <param name="port">The local port.</param>
    /// <returns>The addresses, as the system reports them.</returns>
    public static IReadOnlyList<IPAddress> ListeningOn(int port) =>
        [.. IPGlobalProperties.GetIPGlobalProperties()
            .GetActiveTcpListeners()
            .Where(listener => listener.Port == port)
            .Select(listener => listener.Address)];
}
