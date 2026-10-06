using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
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
/// and closing one disturbs none of the others.</para>
///
/// <para><b>Half of a close is half (QS124).</b> SSH.NET's <c>ForwardedPortLocal</c> ended both
/// directions the moment one shut, so a protocol that sends, shuts its sending half and waits —
/// HTTP/1.0, several database wire protocols, anything shaped like <c>cat | remote-tool</c> — got a
/// closed socket instead of its answer. So the listener here is this client's own, and each
/// connection goes over a direct-tcpip channel the session opens for it. When the local side ends
/// its input the channel says EOF and stays open, carrying the far end's answer back until the far
/// end closes, which is what OpenSSH does. The channel is the library's; three of its members are
/// reached by name, and <see cref="LibraryShape"/> checks them.</para>
/// </summary>
public sealed class LocalForward : IAsyncDisposable
{
    private const BindingFlags Hidden = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

    // The library's own channel and the session that opens one, reached by name (QS124). Internal
    // so LibraryShape checks these very members.
    internal static readonly MethodInfo? CreateChannel =
        typeof(SshClient).Assembly.GetType("Renci.SshNet.ISession")?.GetMethod("CreateChannelDirectTcpip", Hidden);

    internal static readonly Type? ChannelType =
        typeof(SshClient).Assembly.GetType("Renci.SshNet.Channels.ChannelDirectTcpip");

    internal static readonly MethodInfo? OpenChannel = ChannelType?.GetMethod("Open", Hidden);

    internal static readonly MethodInfo? Pump = ChannelType?.GetMethod("Bind", Hidden, Type.EmptyTypes);

    internal static readonly MethodInfo? SayEnd =
        typeof(SshClient).Assembly.GetType("Renci.SshNet.Channels.IChannel")?.GetMethod("SendEof", Hidden, Type.EmptyTypes);

    internal static readonly PropertyInfo? Live = ChannelType?.GetProperty("IsOpen", Hidden);

    private readonly TcpListener _listener;
    private readonly object _session;
    private readonly ForwardedPortLocal _anchor;
    private readonly CancellationTokenSource _stopping = new();
    private readonly List<ForwardFailure> _failures = [];
    private readonly List<IDisposable> _carrying = [];
    private readonly Lock _guard = new();

    private Task _accepting = Task.CompletedTask;
    private long _connections;
    private bool _disposed;

    private LocalForward(TcpListener listener, object session, string target, int targetPort,
                         ForwardBinding binding)
    {
        _listener = listener;
        _session = session;
        TargetHost = target;
        TargetPort = targetPort;
        Binding = binding;

        // The channel subscribes to a forwarded port's closing to close itself; this one is never
        // started and exists to be that, so a channel ends when this forward does.
        _anchor = new ForwardedPortLocal(binding.Address, 0, target, (uint)targetPort);
    }

    /// <summary>The address the local listener accepts on.</summary>
    public string BoundHost => Binding.Address;

    /// <summary>The local port, which is the one the system chose where zero was asked for.</summary>
    public int BoundPort => ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <summary>The host the far side connects to, spelled as the remote network spells it.</summary>
    public string TargetHost { get; }

    /// <summary>The port on that host.</summary>
    public int TargetPort { get; }

    /// <summary>Where this accepts from.</summary>
    public ForwardBinding Binding { get; }

    /// <summary>Whether the listener is up.</summary>
    public bool IsOpen => !_disposed && !_accepting.IsCompleted;

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

        // Named, never defaulted: the listener binds exactly the address given, loopback unless a
        // caller widened it.
        ForwardBinding where = binding ?? ForwardBinding.Loopback;

        object session = typeof(BaseClient).GetProperty(SharedSftpSession.SessionProperty, Hidden)?.GetValue(client)
            ?? throw new SshException(SshFailureKind.Dropped,
                                      "There is no connection to carry a forward.",
                                      "The session is not open.");

        if (CreateChannel is null || OpenChannel is null || Pump is null || SayEnd is null)
        {
            throw new SshException(
                SshFailureKind.Unrecognised,
                "This build of SSH.NET cannot carry a forward the way this client does.",
                "A channel member quickshell reaches by name is not there; LibraryShape names which.",
                "Report this with the SSH.NET version.");
        }

        TcpListener listener = new(IPAddress.Parse(where.Address), listenPort);

        try
        {
            listener.Start();
        }
        catch (SocketException taken)
        {
            throw new SshException(
                SshFailureKind.Refused,
                $"The local port {listenPort} could not be opened.",
                "Something on this machine is already listening on it.",
                "Choose another port, or pass zero and let the system choose a free one.",
                taken.Message);
        }

        LocalForward forward = new(listener, session, targetHost, targetPort, where);

        forward._accepting = forward.AcceptAsync();

        // Nothing outlives the session: when it goes, so does every listener it was carrying.
        _ = over.Disconnected.ContinueWith(_ => forward.DisposeAsync().AsTask(), TaskScheduler.Default);

        return forward;
    }

    /// <summary>Accepts until disposed, handing each connection to a channel of its own.</summary>
    private async Task AcceptAsync()
    {
        while (!_stopping.IsCancellationRequested)
        {
            Socket accepted;

            try
            {
                accepted = await _listener.AcceptSocketAsync(_stopping.Token).ConfigureAwait(false);
            }
            catch (Exception) when (_stopping.IsCancellationRequested)
            {
                return;
            }
            catch (SocketException)
            {
                return;
            }

            accepted.NoDelay = true;

            // On a thread of its own: the channel's pump blocks for the life of the connection.
            _ = Task.Factory.StartNew(() => Carry(accepted), CancellationToken.None,
                                      TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }
    }

    /// <summary>
    /// One connection, from open to the far end's close.
    ///
    /// <para>The pump returns when the local side's input ends — a full close or a half one, which a
    /// socket cannot tell apart from here. Either way the far end is told EOF and the channel stays
    /// open: what the far end still sends reaches the socket, and the channel's own handling of the
    /// far end's EOF and close shuts the socket's sending half and then the socket.</para>
    /// </summary>
    private void Carry(Socket accepted)
    {
        IDisposable? channel = null;

        try
        {
            channel = (IDisposable)CreateChannel!.Invoke(_session, null)!;

            lock (_guard)
            {
                _carrying.Add(channel);
            }

            OpenChannel!.Invoke(channel, [TargetHost, (uint)TargetPort, _anchor, accepted]);

            Interlocked.Increment(ref _connections);

            Pump!.Invoke(channel, null);

            if (Live?.GetValue(channel) is true)
            {
                SayEnd!.Invoke(channel, null);

                // Until the far end closes, or this forward ends.
                while (Live.GetValue(channel) is true && !_stopping.IsCancellationRequested)
                {
                    Thread.Sleep(20);
                }
            }
        }
        catch (TargetInvocationException failed) when (failed.InnerException is { } inner)
        {
            Trouble(inner);
        }
        catch (Exception failed) when (failed is ObjectDisposedException or SocketException)
        {
            // The connection or the forward went first, which is an ending and not a failure.
        }
        finally
        {
            if (channel is not null)
            {
                lock (_guard)
                {
                    _carrying.Remove(channel);
                }

                channel.Dispose();
            }

            accepted.Dispose();
        }
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
    private void Trouble(Exception what)
    {
        ForwardFailure failure = what switch
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

    /// <summary>
    /// Closes the listener and every channel it opened. No forward outlives the object that owns it.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        lock (_guard)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        await _stopping.CancelAsync().ConfigureAwait(false);

        _listener.Stop();

        await _accepting.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

        IDisposable[] open;

        lock (_guard)
        {
            open = [.. _carrying];
        }

        foreach (IDisposable channel in open)
        {
            try
            {
                channel.Dispose();
            }
            catch (Exception)
            {
                // Already gone with the session, which is the ordinary way this ends.
            }
        }

        _anchor.Dispose();
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
