using System.Net;
using System.Net.Sockets;
using System.Reflection;
using Renci.SshNet;
using Renci.SshNet.Messages;
using Renci.SshNet.Messages.Connection;

namespace Quickshell.Transport;

/// <summary>
/// The channels a remote forward's server opens, answered here instead of by the library (QS259).
///
/// <para><b>Why.</b> SSH.NET's <c>ForwardedPortRemote</c> answers each <c>forwarded-tcpip</c> open
/// itself: it connects the local service, confirms with a 2 GB window, and its channel writes what
/// arrives to that socket with a blocking send on the session's one receive thread. A local service
/// that stops reading then stops every channel on the connection, the shell with it. The library
/// creates the channel and confirms it in one call, so <see cref="ChannelWindow"/> has no moment to
/// set the window; the only way to bound it is to answer the open here.</para>
///
/// <para><b>What it does.</b> The library still asks the server to listen and still owns the port;
/// only its handler for the server's opens is taken off the session and this one put on. Each open
/// for this port is answered on a thread of its own: the local service is connected, the channel
/// is created through the session, confirmed offering <see cref="ChannelWindow.Size"/>, and its
/// data, taken from the channel's own event, goes into one end of a pair that holds a whole window.
/// <see cref="LocalForward"/>'s pump carries it to the service and grants the window back as it goes,
/// so the receive thread's write never waits. Every member reached is checked by
/// <see cref="LibraryShape"/>.</para>
/// </summary>
internal sealed class RemoteChannels : IDisposable
{
    private const BindingFlags Hidden = BindingFlags.NonPublic | BindingFlags.Instance;
    private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

    /// <summary>SSH_OPEN_CONNECT_FAILED: the target did not accept.</summary>
    private const uint ConnectFailed = 2;

    private static readonly Assembly Library = typeof(SshClient).Assembly;

    private static readonly Type? SessionType = Library.GetType("Renci.SshNet.ISession");

    internal static readonly EventInfo? Opened = SessionType?.GetEvent("ChannelOpenReceived", Any);

    internal static readonly MethodInfo? LibraryHandler =
        typeof(ForwardedPortRemote).GetMethod("Session_ChannelOpening", Hidden);

    internal static readonly MethodInfo? Create = SessionType?.GetMethod("CreateChannelForwardedTcpip", Any);

    internal static readonly MethodInfo? SessionSend = SessionType?.GetMethod("SendMessage", Any, [typeof(Message)]);

    internal static readonly MethodInfo? Confirm =
        Library.GetType("Renci.SshNet.Channels.ServerChannel")?.GetMethod("SendMessage", Hidden, [typeof(ChannelOpenConfirmationMessage)]);

    internal static readonly PropertyInfo? Info =
        Library.GetType("Renci.SshNet.Messages.Connection.ForwardedTcpipChannelInfo")?.GetProperty("ConnectedPort", Any);

    internal static readonly PropertyInfo? MessageOf = Opened?.EventHandlerType?.GetGenericArguments() is [{ } args]
        ? args.GetProperty("Message", Any)
        : null;

    internal static readonly EventInfo? DataEvent = ChannelWindow.ChannelType?.GetEvent("DataReceived", Any);

    internal static readonly EventInfo? EndEvent = ChannelWindow.ChannelType?.GetEvent("EndOfData", Any);

    internal static readonly EventInfo? ClosedEvent = ChannelWindow.ChannelType?.GetEvent("Closed", Any);

    internal static readonly PropertyInfo? DataBytes = DataEvent?.EventHandlerType?.GetGenericArguments() is [{ } data]
        ? data.GetProperty("Data", Any)
        : null;

    internal static readonly PropertyInfo? LocalNumber = ChannelWindow.ChannelType?.GetProperty("LocalChannelNumber", Any);

    internal static readonly PropertyInfo? PacketSize = ChannelWindow.ChannelType?.GetProperty("LocalPacketSize", Any);

    internal static readonly MethodInfo? SendData =
        ChannelWindow.ChannelType?.GetMethod("SendData", Any, [typeof(byte[]), typeof(int), typeof(int)]);

    internal static readonly MethodInfo? SendEof = ChannelWindow.ChannelType?.GetMethod("SendEof", Any, Type.EmptyTypes);

    /// <summary>Whether this build of the library has every member answering opens here needs.</summary>
    internal static bool Usable =>
        Opened is not null && LibraryHandler is not null && Create is not null && SessionSend is not null
        && Confirm is not null && Info is not null && MessageOf is not null && DataEvent is not null
        && EndEvent is not null && ClosedEvent is not null && DataBytes is not null && LocalNumber is not null
        && PacketSize is not null && SendData is not null && SendEof is not null && ChannelWindow.Steerable;

    private readonly object _session;
    private readonly ForwardedPortRemote _port;
    private readonly IPEndPoint _local;
    private readonly Action _accepted;
    private readonly Delegate _ours;
    private readonly Delegate _theirs;
    private readonly CancellationTokenSource _stopping = new();
    private readonly List<IDisposable> _carrying = [];
    private readonly Lock _guard = new();

    private RemoteChannels(object session, ForwardedPortRemote port, IPEndPoint local, Action accepted)
    {
        _session = session;
        _port = port;
        _local = local;
        _accepted = accepted;

        Action<object?, EventArgs> handler = Opening;

        _ours = Delegate.CreateDelegate(Opened!.EventHandlerType!, handler.Target, handler.Method);
        _theirs = Delegate.CreateDelegate(Opened.EventHandlerType!, port, LibraryHandler!);
    }

    /// <summary>
    /// Takes over a started port's opens, or answers null where this build of the library has moved a
    /// member and the library goes on answering them, unbounded but working.
    /// </summary>
    public static RemoteChannels? TakeOver(SshClient client, ForwardedPortRemote port, IPEndPoint local, Action accepted)
    {
        if (!Usable || ChannelWindow.SessionProperty?.GetValue(client) is not { } session)
        {
            return null;
        }

        RemoteChannels channels = new(session, port, local, accepted);

        // Ours on before theirs off, so no open the server sends in between goes unanswered: one
        // that both see is the port's, and the library's answer for that one stays unbounded.
        Opened!.AddEventHandler(session, channels._ours);
        Opened.RemoveEventHandler(session, channels._theirs);

        return channels;
    }

    private void Opening(object? sender, EventArgs opened)
    {
        if (MessageOf!.GetValue(opened) is not ChannelOpenMessage message
            || message.Info?.GetType() != Info!.DeclaringType
            || Info.GetValue(message.Info) is not uint port
            || port != _port.BoundPort
            || _stopping.IsCancellationRequested)
        {
            return;
        }

        // Off the receive thread at once: connecting the service can take as long as a connect does.
        _ = Task.Factory.StartNew(() => Carry(message), CancellationToken.None,
                                  TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    /// <summary>One connection from the server, from its open to its close.</summary>
    private void Carry(ChannelOpenMessage message)
    {
        Socket service = new(_local.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        IDisposable? channel = null;
        Socket? channelEnd = null;
        Socket? ours = null;
        Task delivered = Task.CompletedTask;
        ManualResetEventSlim ended = new();

        try
        {
            try
            {
                service.Connect(_local);
            }
            catch (SocketException refused)
            {
                Call(() => SessionSend!.Invoke(_session, [new ChannelOpenFailureMessage(
                    message.LocalChannelNumber, refused.Message, ConnectFailed, "en")]));

                return;
            }

            channel = (IDisposable)Call(() => Create!.Invoke(_session,
                [message.LocalChannelNumber, message.InitialWindowSize, message.MaximumPacketSize]))!;

            lock (_guard)
            {
                _carrying.Add(channel);
            }

            (channelEnd, ours) = LocalForward.Pair();

            Socket into = channelEnd;

            // Each packet into the pair, which holds the whole window, so this never waits on the
            // service; the far end's EOF and close end the pair's input after its last byte.
            Action<object?, EventArgs> arrived = (_, data) =>
            {
                if (DataBytes!.GetValue(data) is ArraySegment<byte> bytes)
                {
                    try
                    {
                        into.Send(bytes.AsSpan());
                    }
                    catch (Exception gone) when (gone is SocketException or ObjectDisposedException)
                    {
                        // The service went; the channel's close follows.
                    }
                }
            };
            Action<object?, EventArgs> finished = (_, _) =>
            {
                try
                {
                    into.Shutdown(SocketShutdown.Send);
                }
                catch (Exception gone) when (gone is SocketException or ObjectDisposedException)
                {
                    // Already shut.
                }
            };
            Action<object?, EventArgs> closed = (_, _) =>
            {
                finished(null, EventArgs.Empty);
                ended.Set();
            };

            DataEvent!.AddEventHandler(channel, Delegate.CreateDelegate(DataEvent.EventHandlerType!, arrived.Target, arrived.Method));
            EndEvent!.AddEventHandler(channel, Delegate.CreateDelegate(EndEvent.EventHandlerType!, finished.Target, finished.Method));
            ClosedEvent!.AddEventHandler(channel, Delegate.CreateDelegate(ClosedEvent.EventHandlerType!, closed.Target, closed.Method));

            object opening = channel;

            ChannelWindow? window = ChannelWindow.Bound(opening, () => Call(() => Confirm!.Invoke(opening,
                [new ChannelOpenConfirmationMessage(message.LocalChannelNumber, ChannelWindow.Size,
                                                    (uint)PacketSize!.GetValue(opening)!,
                                                    (uint)LocalNumber!.GetValue(opening)!)])));

            _accepted();

            Socket near = ours;

            delivered = Task.Factory.StartNew(() => LocalForward.Down(near, service, window), CancellationToken.None,
                                              TaskCreationOptions.LongRunning, TaskScheduler.Default);

            // The service's bytes to the far end, on this thread: a send waits for the far end's
            // window here, never on the receive thread.
            byte[] buffer = new byte[32 * 1024];

            try
            {
                for (int read; (read = service.Receive(buffer)) > 0;)
                {
                    Call(() => SendData!.Invoke(opening, [buffer, 0, read]));
                }

                Call(() => SendEof!.Invoke(opening, null));
            }
            catch (Exception gone) when (gone is SocketException or ObjectDisposedException
                                             or Renci.SshNet.Common.SshException or InvalidOperationException)
            {
                // Either end went; the channel's close is waited for below.
            }

            WaitHandle.WaitAny([ended.WaitHandle, _stopping.Token.WaitHandle]);
        }
        catch (Exception failed) when (failed is Renci.SshNet.Common.SshException or ObjectDisposedException
                                           or InvalidOperationException or SocketException)
        {
            // The session went while this connection was being set up, which is its ending.
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

            try
            {
                channelEnd?.Shutdown(SocketShutdown.Send);
            }
            catch (Exception gone) when (gone is SocketException or ObjectDisposedException)
            {
                // Already shut.
            }

            delivered.Wait(TimeSpan.FromSeconds(5));

            ours?.Dispose();
            channelEnd?.Dispose();
            service.Dispose();
            ended.Dispose();
        }
    }

    /// <summary>The library's own exception, as though it had been called directly.</summary>
    private static object? Call(Func<object?> call)
    {
        try
        {
            return call();
        }
        catch (TargetInvocationException called) when (called.InnerException is { } thrown)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(thrown);

            throw;
        }
    }

    /// <summary>Hands the opens back and closes every connection this carried.</summary>
    public void Dispose()
    {
        if (_stopping.IsCancellationRequested)
        {
            return;
        }

        _stopping.Cancel();

        Opened!.RemoveEventHandler(_session, _ours);

        IDisposable[] carrying;

        lock (_guard)
        {
            carrying = [.. _carrying];
        }

        foreach (IDisposable channel in carrying)
        {
            channel.Dispose();
        }
    }
}
