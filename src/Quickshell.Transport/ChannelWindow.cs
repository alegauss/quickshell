using System.Reflection;
using System.Runtime.ExceptionServices;
using Renci.SshNet;
using LibraryException = Renci.SshNet.Common.SshException;
using Renci.SshNet.Messages.Connection;

namespace Quickshell.Transport;

/// <summary>
/// A channel's receive window, granted back to the host as its reader takes bytes and not as bytes
/// arrive: the shell's (QS139) and each forwarded connection's (QS252).
///
/// <para><b>Why it exists.</b> SSH.NET opens every channel with a window of 2 GB, and tops it up
/// the moment data arrives rather than when anybody has read it. So a host printing on a loop is
/// never made to wait: ten seconds of an unread shell held 3,072 MB, and any host a user connects
/// to could exhaust this client's memory in seconds.</para>
///
/// <para><b>What it does.</b> The shell is opened the way <c>ShellStream</c>'s own constructor
/// opens it, with one difference: the channel offers <see cref="Size"/> instead of 2 GB, and the
/// library's own counter is then raised out of reach so it never adjusts the window itself. From
/// there the window is this class's: every byte the pane takes out of the stream is owed back to the
/// host, and once half a window is owed it is granted in one adjust. The host can never be more
/// than one window ahead of the reader, so what the stream holds is bounded by it.</para>
///
/// <para><b>The alternatives, and why not.</b> <c>Shell</c> bounds memory and cannot resize.
/// Blocking the library's receive thread stalls every channel on the connection, the file browser
/// and the tunnels with it. And writing the channel layer here is what the non-goal forbids. Every
/// member reached for is checked by <see cref="LibraryShape"/>, and where one is missing the shell
/// opens the library's way, unbounded but working.</para>
///
/// <para><b>A forward has the same window and a worse consequence.</b> Its channel writes what
/// arrives to a local socket on the session's one receive thread, so a local tool that stops reading
/// does not cost memory: the write blocks, and every channel on the connection stops with it.
/// <see cref="LocalForward"/> gives that channel a socket of its own pair, sized to hold a whole
/// window, and owes bytes back as they leave for the real socket, so the write never has to
/// wait.</para>
/// </summary>
internal sealed class ChannelWindow
{
    /// <summary>
    /// What the host may send ahead of the reader. Two megabytes is several round trips of a fast
    /// link at the rate the parser drains, and a rounding error beside what the library offered.
    /// </summary>
    internal const uint Size = 2 * 1024 * 1024;

    /// <summary>How much must be owed before an adjust is sent: half, so a grant is rare and the
    /// host still always has room to keep sending while the reader catches up.</summary>
    private const long Threshold = Size / 2;

    private const BindingFlags Hidden = BindingFlags.NonPublic | BindingFlags.Instance;
    private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

    private static readonly Assembly Library = typeof(SshClient).Assembly;

    internal static readonly PropertyInfo? SessionProperty =
        typeof(BaseClient).GetProperty(SharedSftpSession.SessionProperty, Hidden);

    /// <summary><c>ShellStream(ISession, int bufferSize, bool noTerminal)</c>, which builds the stream
    /// and its channel and opens nothing.</summary>
    internal static readonly ConstructorInfo? Unopened = SessionProperty is { } session
        ? typeof(ShellStream).GetConstructor(Hidden, [session.PropertyType, typeof(int), typeof(bool)])
        : null;

    internal static readonly Type? ChannelType = Library.GetType("Renci.SshNet.Channels.Channel");

    internal static readonly Type? ChannelSessionType = Library.GetType("Renci.SshNet.Channels.ChannelSession");

    internal static readonly MethodInfo? LocalWindow =
        ChannelType?.GetProperty("LocalWindowSize", Any)?.GetSetMethod(nonPublic: true);

    internal static readonly PropertyInfo? RemoteNumber = ChannelType?.GetProperty("RemoteChannelNumber", Any);

    internal static readonly MethodInfo? Send =
        ChannelType?.GetMethod("SendMessage", Hidden, [typeof(Renci.SshNet.Messages.Message)]);

    internal static readonly EventInfo? ExtendedData = ChannelType?.GetEvent("ExtendedDataReceived", Any);

    internal static readonly PropertyInfo? ExtendedBytes =
        ExtendedData?.EventHandlerType?.GetGenericArguments() is [{ } args] ? args.GetProperty("Data", Any) : null;

    internal static readonly MethodInfo? OpenChannel = ChannelSessionType?.GetMethod("Open", Any, Type.EmptyTypes);

    internal static readonly MethodInfo? PtyRequest = ChannelSessionType?.GetMethod("SendPseudoTerminalRequest", Any);

    internal static readonly MethodInfo? ShellRequest = ChannelSessionType?.GetMethod("SendShellRequest", Any, Type.EmptyTypes);

    /// <summary>Whether this build of the library still has every member a bounded window needs.</summary>
    internal static bool Steerable =>
        LocalWindow is not null && RemoteNumber is not null && Send is not null && ExtendedData is not null
        && ExtendedBytes is not null;

    /// <summary>Whether this build of the library still has every member the bounded shell needs.</summary>
    internal static bool Usable =>
        Steerable && Unopened is not null && SharedField is not null && OpenChannel is not null
        && PtyRequest is not null && ShellRequest is not null;

    private static FieldInfo? SharedField => SshNetChannel.SessionField;

    private readonly object _channel;
    private long _owed;

    private ChannelWindow(object channel)
    {
        _channel = channel;

        // Extended data never reaches the stream, so nobody reads it: it is owed back the moment it
        // arrives, or a host writing to stderr would close the window on itself.
        Action<object?, EventArgs> handler = Discarded;

        ExtendedData!.AddEventHandler(channel,
            Delegate.CreateDelegate(ExtendedData.EventHandlerType!, handler.Target, handler.Method));
    }

    /// <summary>
    /// Opens a channel offering <see cref="Size"/> instead of the library's 2 GB, and takes its window
    /// over: from here the library never tops it up, and <see cref="Consumed"/> is the only grant.
    /// </summary>
    /// <param name="channel">A library channel, not yet open.</param>
    /// <param name="open">What opens it; the open message carries the window offered.</param>
    /// <returns>The window to tell what the reader took, or null where this build of the library has
    /// moved a member and the channel was opened unbounded.</returns>
    internal static ChannelWindow? Bound(object channel, Action open)
    {
        ArgumentNullException.ThrowIfNull(open);

        if (!Steerable)
        {
            open();

            return null;
        }

        // The size the open message offers, and then out of reach: the library tops the window up
        // when its counter falls under a packet, and after this it never will.
        LocalWindow!.Invoke(channel, [Size]);
        open();
        LocalWindow.Invoke(channel, [uint.MaxValue]);

        return new ChannelWindow(channel);
    }

    /// <summary>
    /// Opens a shell whose window is <see cref="Size"/>, or the library's way where this build of it
    /// has moved a member.
    /// </summary>
    /// <returns>The stream, and the window to tell what the reader took; no window where the shell
    /// was opened unbounded.</returns>
    internal static (ShellStream Shell, ChannelWindow? Window) OpenShell(SshClient client, string terminal,
                                                                 uint columns, uint rows, int bufferSize)
    {
        if (!Usable || SessionProperty!.GetValue(client) is not { } session)
        {
            return (client.CreateShellStream(terminal, columns, rows, 0, 0, bufferSize), null);
        }

        ShellStream shell = (ShellStream)Unopened!.Invoke([session, bufferSize, false]);

        try
        {
            object channel = SharedField!.GetValue(shell)!;

            ChannelWindow window = Bound(channel, () => Called(() => OpenChannel!.Invoke(channel, null)))!;

            // The library's own refusals, word for word, so a failure here reads as one there would.
            if (!(bool)Called(() => PtyRequest!.Invoke(channel, [terminal, columns, rows, 0u, 0u, null]))!)
            {
                throw new LibraryException("The pseudo-terminal request was not accepted by the server. "
                                       + "Consult the server log for more information.");
            }

            if (!(bool)Called(() => ShellRequest!.Invoke(channel, null))!)
            {
                throw new LibraryException("The request to start a shell was not accepted by the server. "
                                       + "Consult the server log for more information.");
            }

            return (shell, window);
        }
        catch
        {
            shell.Dispose();

            throw;
        }
    }

    /// <summary>
    /// The reader took <paramref name="bytes"/> out of the stream, so the host may send that many
    /// more. Granted in one adjust once half a window is owed.
    /// </summary>
    internal void Consumed(int bytes)
    {
        if (Interlocked.Add(ref _owed, bytes) < Threshold)
        {
            return;
        }

        long granted = Interlocked.Exchange(ref _owed, 0);

        if (granted <= 0)
        {
            return;
        }

        try
        {
            // The library's counter is put back out of reach first, so a packet arriving during the
            // grant can never bring it near the point where the library would adjust on its own.
            LocalWindow!.Invoke(_channel, [uint.MaxValue]);
            // The far end's number is read here and not at the open: a channel the server refused
            // never had one, and asking for it then would turn the refusal into an exception.
            uint remote = (uint)Called(() => RemoteNumber!.GetValue(_channel))!;

            Called(() => Send!.Invoke(_channel, [new ChannelWindowAdjustMessage(remote, (uint)granted)]));
        }
        catch (Exception gone) when (gone is LibraryException or ObjectDisposedException or InvalidOperationException)
        {
            // A grant to a connection that has gone is not worth failing a read over. The reader
            // finds out through Closed, which is where an ending belongs.
        }
    }

    private void Discarded(object? sender, EventArgs data)
    {
        if (ExtendedBytes!.GetValue(data) is ArraySegment<byte> segment)
        {
            Consumed(segment.Count);
        }
    }

    /// <summary>The library's own exception, as though it had been called directly.</summary>
    private static object? Called(Func<object?> call)
    {
        try
        {
            return call();
        }
        catch (TargetInvocationException called) when (called.InnerException is { } thrown)
        {
            ExceptionDispatchInfo.Throw(thrown);

            throw;
        }
    }
}
