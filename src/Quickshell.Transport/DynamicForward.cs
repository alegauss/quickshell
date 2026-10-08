using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using Renci.SshNet;

namespace Quickshell.Transport;

/// <summary>
/// A SOCKS proxy on a local port, every connection through it carried to the remote network: one
/// forward that reaches a whole network, which is what a browser or a cloud tool needs (QS127).
///
/// <para><b>Not the library's, and why.</b> SSH.NET's <c>ForwardedPortDynamic</c> answered about one
/// request in six with the target's own first bytes where its SOCKS reply should have been, and
/// answered BIND with success and then a connection somewhere else (measured 2026-08-30). The fault
/// is the order of two writes into one socket: the channel starts writing what the target sends the
/// moment it is open, and the reply can lose the race. So the SOCKS conversation is held here, the
/// channel is handed a socket of its own rather than the client's, and the client hears the reply
/// before a single byte of the target's — every time, because nothing is relayed until it has.</para>
///
/// <para><b>SOCKS5 with no authentication, and SOCKS4a for the old tools that still speak it;
/// CONNECT only (QS68).</b> BIND and UDP ASSOCIATE are refused with the reply SOCKS has for a command
/// it does not support, rather than pretended to. A host name, in either version, goes to the server
/// unresolved: resolving it here would tell this network's DNS every name visited, and fail every
/// name that exists only on the far side. A refusal from the far
/// side is answered with the reply that names it: connection refused where the target did not
/// accept, not allowed where the server forbids forwarding.</para>
///
/// <para>Loopback unless a caller names another binding, as for <see cref="LocalForward"/> — and
/// bound wide, a proxy is a door into the remote network for anything that can reach this
/// machine.</para>
/// </summary>
public sealed class DynamicForward : IAsyncDisposable
{
    private const byte Socks5 = 5;
    private const byte Socks4 = 4;
    private const byte NoAuthentication = 0;
    private const byte NoAcceptableMethod = 0xFF;
    private const byte Connect = 1;
    private const byte IPv4 = 1;
    private const byte Domain = 3;
    private const byte IPv6 = 4;

    // SOCKS5 replies.
    private const byte Granted = 0;
    private const byte GeneralFailure = 1;
    private const byte NotAllowed = 2;
    private const byte HostUnreachable = 4;
    private const byte ConnectionRefused = 5;
    private const byte CommandNotSupported = 7;
    private const byte AddressNotSupported = 8;

    private readonly TcpListener _listener;
    private readonly TcpListener _pairing;
    private readonly object _session;
    private readonly ForwardedPortLocal _anchor;
    private readonly ChannelRefusals _refusals;
    private readonly CancellationTokenSource _stopping = new();
    private readonly List<IDisposable> _carrying = [];
    private readonly Lock _guard = new();
    private readonly Lock _pairingGuard = new();
    private SessionLog? _log;

    private Task _accepting = Task.CompletedTask;
    private long _connections;
    private bool _disposed;

    private DynamicForward(TcpListener listener, TcpListener pairing, object session, ForwardBinding binding)
    {
        _listener = listener;
        _pairing = pairing;
        _session = session;
        Binding = binding;

        // As in LocalForward: never started, there so each channel closes when this forward does.
        _anchor = new ForwardedPortLocal(IPAddress.Loopback.ToString(), 0, "socks", 1);
        _refusals = new ChannelRefusals(session);
    }

    /// <summary>Where this accepts from.</summary>
    public ForwardBinding Binding { get; }

    /// <summary>The local port, which is the one the system chose where zero was asked for.</summary>
    public int BoundPort => ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <summary>How many connections have been carried.</summary>
    public long Connections => Interlocked.Read(ref _connections);

    /// <summary>Whether the listener is up.</summary>
    public bool IsOpen => !_disposed && !_accepting.IsCompleted;

    /// <summary>What a user is told about this proxy, or empty where there is nothing to say.</summary>
    public string Warning =>
        Binding.IsLoopback
            ? string.Empty
            : $"This proxy accepts on {(Binding.IsEverywhere ? "every interface" : Binding.Address)}, so anything that "
              + $"can reach this machine on port {BoundPort} can reach anything on the remote network "
              + "without authenticating.";

    /// <summary>Opens a SOCKS proxy on a local port into the remote network.</summary>
    /// <param name="over">A connected session to carry it.</param>
    /// <param name="listenPort">The local port, or zero to let the system choose.</param>
    /// <param name="binding">Where to accept from. Loopback unless asked otherwise.</param>
    /// <exception cref="SshException">The local port could not be taken.</exception>
    public static DynamicForward Open(SshNetTransport over, int listenPort = 0, ForwardBinding? binding = null)
    {
        ArgumentNullException.ThrowIfNull(over);
        ArgumentOutOfRangeException.ThrowIfNegative(listenPort);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(listenPort, 65535);

        SshClient client = over.Client
            ?? throw new SshException(SshFailureKind.Dropped, "There is no connection to carry a proxy.",
                                      "The session is not open.");

        object session = typeof(BaseClient)
                             .GetProperty(SharedSftpSession.SessionProperty, BindingFlags.NonPublic | BindingFlags.Instance)
                             ?.GetValue(client)
            ?? throw new SshException(SshFailureKind.Dropped, "There is no connection to carry a proxy.",
                                      "The session is not open.");

        if (LocalForward.CreateChannel is null || LocalForward.OpenChannel is null
            || LocalForward.Pump is null || LocalForward.SayEnd is null)
        {
            throw new SshException(
                SshFailureKind.Unrecognised,
                "This build of SSH.NET cannot carry a proxy the way this client does.",
                "A channel member quickshell reaches by name is not there; LibraryShape names which.",
                "Report this with the SSH.NET version.");
        }

        ForwardBinding where = binding ?? ForwardBinding.Loopback;
        TcpListener listener = new(IPAddress.Parse(where.Address), listenPort);
        TcpListener pairing = new(IPAddress.Loopback, 0);

        try
        {
            listener.Start();
        }
        catch (SocketException taken)
        {
            throw new SshException(
                SshFailureKind.Refused,
                // Who holds it, in the sentence a pane shows (QS69).
                PortHolder.Describe(listenPort) is { } holder
                    ? $"The local port {listenPort} could not be opened: {holder} is listening on it."
                    : $"The local port {listenPort} could not be opened.",
                "Something on this machine is already listening on it.",
                "Choose another port, or pass zero and let the system choose a free one.",
                taken.Message);
        }

        pairing.Start();

        DynamicForward proxy = new(listener, pairing, session, where) { _log = over.Log };

        // A proxy has no one remote port; zero says so (QS130).
        proxy._log?.Forward(proxy.BoundPort, 0, started: true);

        proxy._accepting = proxy.AcceptAsync();

        _ = over.Disconnected.ContinueWith(_ => proxy.DisposeAsync().AsTask(), TaskScheduler.Default);

        return proxy;
    }

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

            _ = Task.Factory.StartNew(() => Serve(accepted), CancellationToken.None,
                                      TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }
    }

    /// <summary>One client: the SOCKS conversation, then the connection it asked for.</summary>
    private void Serve(Socket client)
    {
        try
        {
            using NetworkStream talk = new(client, ownsSocket: false);

            if (Asked(talk) is not { } wanted)
            {
                return;
            }

            Carry(client, talk, wanted.Host, wanted.Port, wanted.Version);
        }
        catch (Exception ended) when (ended is IOException or SocketException or ObjectDisposedException
                                          or EndOfStreamException)
        {
            // The client went, or the forward did: an ending and not a failure.
        }
        finally
        {
            client.Dispose();
        }
    }

    /// <summary>
    /// The greeting and the request, answered where they cannot be served. Null where the
    /// conversation ended here.
    /// </summary>
    private static (string Host, int Port, byte Version)? Asked(NetworkStream talk)
    {
        Span<byte> two = stackalloc byte[2];

        talk.ReadExactly(two);

        if (two[0] == Socks4)
        {
            return Asked4(talk, two[1]);
        }

        if (two[0] != Socks5)
        {
            return null;
        }

        byte[] methods = new byte[two[1]];

        talk.ReadExactly(methods);

        if (Array.IndexOf(methods, NoAuthentication) < 0)
        {
            talk.Write([Socks5, NoAcceptableMethod]);

            return null;
        }

        talk.Write([Socks5, NoAuthentication]);

        Span<byte> head = stackalloc byte[4];

        talk.ReadExactly(head);

        string host;

        switch (head[3])
        {
            case IPv4:
                byte[] four = new byte[4];
                talk.ReadExactly(four);
                host = new IPAddress(four).ToString();
                break;

            case IPv6:
                byte[] sixteen = new byte[16];
                talk.ReadExactly(sixteen);
                host = new IPAddress(sixteen).ToString();
                break;

            case Domain:
                int length = talk.ReadByte();

                if (length <= 0)
                {
                    Reply(talk, AddressNotSupported);

                    return null;
                }

                byte[] name = new byte[length];
                talk.ReadExactly(name);
                host = Encoding.ASCII.GetString(name);
                break;

            default:
                Reply(talk, AddressNotSupported);

                return null;
        }

        talk.ReadExactly(two);

        int port = BinaryPrimitives.ReadUInt16BigEndian(two);

        if (head[1] != Connect)
        {
            // BIND and UDP ASSOCIATE: said, rather than answered with success and a connection
            // somewhere else, which is what the library's proxy did with BIND.
            Reply(talk, CommandNotSupported);

            return null;
        }

        return (host, port, Socks5);
    }

    /// <summary>
    /// SOCKS4 and 4a, for the old tools that still speak it: a port, an address, a user name, and —
    /// where the address is 0.0.0.x — a host name after it, which goes to the server unresolved
    /// exactly as a SOCKS5 name does.
    /// </summary>
    private static (string Host, int Port, byte Version)? Asked4(NetworkStream talk, byte command)
    {
        Span<byte> six = stackalloc byte[6];

        talk.ReadExactly(six);

        int port = BinaryPrimitives.ReadUInt16BigEndian(six);
        byte[] address = six[2..].ToArray();

        _ = Terminated(talk);

        // 0.0.0.x with x not zero is 4a's sign that a name follows.
        string host = address is [0, 0, 0, not 0]
            ? Terminated(talk)
            : new IPAddress(address).ToString();

        if (command != Connect)
        {
            Reply(talk, CommandNotSupported, Socks4);

            return null;
        }

        return (host, port, Socks4);
    }

    /// <summary>A string ended by a zero byte, bounded so a client that never ends one is refused.</summary>
    private static string Terminated(NetworkStream talk)
    {
        StringBuilder text = new();

        for (int each; (each = talk.ReadByte()) > 0;)
        {
            if (text.Length == 255)
            {
                throw new IOException("a SOCKS4 field ran past 255 bytes");
            }

            text.Append((char)each);
        }

        return text.ToString();
    }

    /// <summary>
    /// The channel, opened on a socket of its own, and the client joined to it only after the reply.
    /// </summary>
    private void Carry(Socket client, NetworkStream talk, string host, int port, byte version)
    {
        (Socket inner, Socket outer) = Pair();
        IDisposable? channel = null;

        try
        {
            channel = (IDisposable)LocalForward.CreateChannel!.Invoke(_session, null)!;

            lock (_guard)
            {
                _carrying.Add(channel);
            }

            LocalForward.OpenChannel!.Invoke(channel, [host, (uint)port, _anchor, inner]);

            if (LocalForward.Live?.GetValue(channel) is not true)
            {
                (uint code, string said) = _refusals.Take(
                    LocalForward.ChannelNumber?.GetValue(channel) is uint number ? number : uint.MaxValue);

                _log?.ForwardFailed(BoundPort, $"{host}:{port} was refused ({code}) {said}".TrimEnd());

                Reply(talk, code switch
                {
                    ChannelRefusals.Prohibited => NotAllowed,
                    ChannelRefusals.ConnectFailed => ConnectionRefused,
                    _ => HostUnreachable,
                }, version);

                return;
            }

            Interlocked.Increment(ref _connections);

            // The reply first, then the relay: what the target has already sent is waiting in the
            // pair's buffer, and reaches the client only after this.
            Reply(talk, Granted, version);

            Task up = Relay(client, outer);
            Task down = Relay(outer, client);

            LocalForward.Pump!.Invoke(channel, null);

            if (LocalForward.Live?.GetValue(channel) is true)
            {
                LocalForward.SayEnd!.Invoke(channel, null);

                while (LocalForward.Live.GetValue(channel) is true && !_stopping.IsCancellationRequested)
                {
                    Thread.Sleep(20);
                }
            }

            // The channel's close shuts its end of the pair, which ends the relay down to the client.
            Task.WaitAll([up, down], TimeSpan.FromSeconds(5));
        }
        catch (TargetInvocationException)
        {
            Reply(talk, GeneralFailure, version);
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

            inner.Dispose();
            outer.Dispose();
        }
    }

    /// <summary>Copies one way until the source ends, then shuts the sending half of the other.</summary>
    private static Task Relay(Socket from, Socket to) => Task.Run(async () =>
    {
        byte[] buffer = new byte[16 * 1024];

        try
        {
            for (int read; (read = await from.ReceiveAsync(buffer, SocketFlags.None).ConfigureAwait(false)) > 0;)
            {
                await to.SendAsync(buffer.AsMemory(0, read), SocketFlags.None).ConfigureAwait(false);
            }

            to.Shutdown(SocketShutdown.Send);
        }
        catch (Exception ended) when (ended is SocketException or ObjectDisposedException)
        {
            // Either side went, which ends this direction.
        }
    });

    /// <summary>
    /// Two connected loopback sockets: the channel gets one, the relay the other.
    ///
    /// <para>Accepted on a listener of this proxy's own, one pairing at a time, and checked to be the
    /// connection just made — anything else that reached the port in that moment is closed.</para>
    /// </summary>
    private (Socket Inner, Socket Outer) Pair()
    {
        lock (_pairingGuard)
        {
            Socket outer = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };

            outer.Connect((IPEndPoint)_pairing.LocalEndpoint);

            while (true)
            {
                Socket inner = _pairing.AcceptSocket();

                if (inner.RemoteEndPoint is IPEndPoint from && from.Equals(outer.LocalEndPoint))
                {
                    inner.NoDelay = true;

                    return (inner, outer);
                }

                inner.Dispose();
            }
        }
    }

    private static void Reply(NetworkStream talk, byte code, byte version = Socks5)
    {
        // Bound address and port are zeros: nothing a client of a CONNECT relies on. SOCKS4 has
        // one way to say yes and one to say no.
        if (version == Socks4)
        {
            talk.Write([0, code == Granted ? (byte)0x5A : (byte)0x5B, 0, 0, 0, 0, 0, 0]);
        }
        else
        {
            talk.Write([Socks5, code, 0, IPv4, 0, 0, 0, 0, 0, 0]);
        }

        talk.Flush();
    }

    /// <summary>Closes the listener and every channel it opened.</summary>
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

        int bound = BoundPort;

        _listener.Stop();
        _pairing.Stop();

        _log?.Forward(bound, 0, started: false);
        _refusals.Dispose();

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
                // Already gone with the session.
            }
        }

        _anchor.Dispose();
    }

    /// <summary>How a person writes it down.</summary>
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"SOCKS {Binding.Address}:{BoundPort}");
}
