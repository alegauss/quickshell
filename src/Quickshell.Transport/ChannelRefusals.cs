using System.Reflection;

namespace Quickshell.Transport;

/// <summary>
/// What the server said about each channel it would not open, kept by channel number until the
/// connection that asked reads it (QS125).
///
/// <para>The library reports a refused open on the session and not on the channel, and the report
/// carries the protocol's reason code and the server's own words — the one place a target that did
/// not accept and a server that forbids forwarding differ. Every forward that opens its own channels
/// listens here: <see cref="LocalForward"/> and <see cref="DynamicForward"/>.</para>
/// </summary>
internal sealed class ChannelRefusals : IDisposable
{
    /// <summary>SSH_OPEN_ADMINISTRATIVELY_PROHIBITED: the server's policy said no.</summary>
    internal const uint Prohibited = 1;

    /// <summary>SSH_OPEN_CONNECT_FAILED: the server tried and the target did not answer.</summary>
    internal const uint ConnectFailed = 2;

    private readonly object _session;
    private readonly Delegate? _hearing;
    private readonly Dictionary<uint, (uint Code, string Said)> _heard = [];
    private readonly Lock _guard = new();

    internal ChannelRefusals(object session)
    {
        _session = session;

        // The event's type is the library's own; a handler taking (object, EventArgs) binds to it
        // because a delegate accepts a wider parameter.
        if (LocalForward.OpenRefused?.EventHandlerType is { } handler)
        {
            _hearing = Delegate.CreateDelegate(
                handler, this, typeof(ChannelRefusals).GetMethod(nameof(Heard), BindingFlags.NonPublic | BindingFlags.Instance)!);
            LocalForward.OpenRefused.AddEventHandler(session, _hearing);
        }
    }

    /// <summary>What was said about this channel, or a zero code and nothing where nothing was.</summary>
    internal (uint Code, string Said) Take(uint channel)
    {
        lock (_guard)
        {
            return _heard.Remove(channel, out (uint, string) said) ? said : (0u, string.Empty);
        }
    }

    public void Dispose()
    {
        if (_hearing is not null)
        {
            LocalForward.OpenRefused?.RemoveEventHandler(_session, _hearing);
        }
    }

    private void Heard(object? sender, EventArgs what)
    {
        object? message = what.GetType().GetProperty("Message")?.GetValue(what);

        if (message?.GetType() is not { } type
            || type.GetProperty("LocalChannelNumber")?.GetValue(message) is not uint number)
        {
            return;
        }

        uint code = type.GetProperty("ReasonCode")?.GetValue(message) is uint reason ? reason : 0;
        string said = type.GetProperty("Description")?.GetValue(message) as string ?? string.Empty;

        lock (_guard)
        {
            _heard[number] = (code, said);
        }
    }
}
