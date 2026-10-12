using System.Globalization;
using Quickshell.Transport;

namespace Quickshell.App;

/// <summary>
/// What the session bar and the status bar say about the connection in the pane with the keyboard
/// (QS269): where it goes, whether it is up, which forwards it holds, and the terminal it is.
///
/// <para><b>Worked out, never kept.</b> Every word here is read from state the client already holds
/// — the session's own status, its forwards on the connection there is now, the grid the pane took
/// — on the window's existing two-hertz wake-up, and compared with what is on screen. Equal, nothing
/// is touched, so an idle window issues no layout and no draw for it.</para>
///
/// <para><b>A remote pane only.</b> A local shell has no route, no state and no forwards, and its
/// name is already in the title and the strip, so <see cref="Of"/> answers null and the bars are not
/// there: the window a default installation opens stays a title bar, the sidebar and a terminal.</para>
/// </summary>
/// <param name="Name">The session's own name.</param>
/// <param name="Route"><c>user@host:port</c>, as the session bar writes it under the name.</param>
/// <param name="Through">The jump host it is reached through, or null.</param>
/// <param name="State">Connected, Reconnecting or Disconnected: a word, never a colour alone.</param>
/// <param name="Forwards">One chip's words per running forward, in the order the session lists them.</param>
/// <param name="Reconnects">Whether a dropped link is tried again.</param>
/// <param name="TerminalType">What the server was told this terminal is.</param>
/// <param name="Columns">The pane's grid, across.</param>
/// <param name="Rows">And down.</param>
public sealed record ConnectionView(string Name, string Route, string? Through, ConnectionState State,
                                    IReadOnlyList<string> Forwards, bool Reconnects, string TerminalType,
                                    int Columns, int Rows)
{
    /// <summary>The encoding every pane reads its host in. There is no other, so it is a constant.</summary>
    public const string Encoding = "UTF-8";

    /// <summary>The state's word, which the chip says and a screen reader reads.</summary>
    public string Said => Word(State);

    /// <summary>Under the name: the route, and the jump host after it.</summary>
    public string Address => Through is { } jump ? $"{Route} · via {jump}" : Route;

    /// <summary>The status bar's first words: the state and the route, the jump host where there is one.</summary>
    public string Status
    {
        get
        {
            // The host alone: the port is on the session bar above, and a sentence reads better without it.
            string host = Route[(Route.IndexOf('@', StringComparison.Ordinal) + 1)..];

            host = host.LastIndexOf(':') is var colon and > 0 ? host[..colon] : host;

            return Through is { } jump ? $"{Said} to {host} through {jump}" : $"{Said} to {host}";
        }
    }

    /// <summary>Whether it reconnects, said as a fact about this session rather than as a setting's name.</summary>
    public string Reconnecting => Reconnects ? "Reconnects after a drop" : "Does not reconnect";

    /// <summary>The grid, as the status bar writes it.</summary>
    public string Grid => string.Create(CultureInfo.InvariantCulture, $"{Columns} × {Rows}");

    /// <summary>A state's word.</summary>
    public static string Word(ConnectionState state) => state switch
    {
        ConnectionState.Connected => "Connected",
        ConnectionState.Reconnecting => "Reconnecting",
        _ => "Disconnected",
    };

    /// <summary>
    /// Where a session is, in the three words the bars use. A session object exists only once its
    /// first connection was live, so connecting is always connecting <em>again</em>.
    /// </summary>
    /// <param name="status">What the session says now.</param>
    /// <param name="ended">Whether the pane has said the session ended.</param>
    public static ConnectionState StateOf(SessionStatus status, bool ended) =>
        ended
            ? ConnectionState.Disconnected
            : status.State switch
            {
                SessionState.Live => ConnectionState.Connected,
                SessionState.Ended => ConnectionState.Disconnected,
                _ => ConnectionState.Reconnecting,
            };

    /// <summary>
    /// A forward's chip, in the words a user recognises: the port here and where it reaches, the
    /// server's port and where it comes back to, or the SOCKS port.
    /// </summary>
    public static string Chip(StartedForward forward) => forward.Spec.Kind switch
    {
        ForwardKind.Local => string.Create(CultureInfo.InvariantCulture,
                                           $"Forward {forward.BoundPort} → {forward.Spec.TargetHost}:{forward.Spec.TargetPort}"),
        ForwardKind.Remote => string.Create(CultureInfo.InvariantCulture,
                                            $"Remote {forward.BoundPort} → {forward.Spec.TargetHost}:{forward.Spec.TargetPort}"),
        _ => string.Create(CultureInfo.InvariantCulture, $"SOCKS {forward.BoundPort}"),
    };

    /// <summary>The route as the session bar writes it: always with the port, since that is what tells two servers on one host apart.</summary>
    public static string RouteOf(SshEndpoint target) =>
        string.Create(CultureInfo.InvariantCulture, $"{target.User}@{target.Host}:{target.Port}");

    /// <summary>What to say about a pane, or null where it holds no remote session.</summary>
    public static ConnectionView? Of(TerminalLeaf? leaf)
    {
        if (leaf?.Remote is not { } remote)
        {
            return null;
        }

        return new ConnectionView(remote.Name, RouteOf(remote.Target), remote.JumpHost,
                                  StateOf(remote.Status, leaf.Ended is not null),
                                  [.. (remote.ForwardsNow?.Started ?? []).Select(Chip)],
                                  remote.Reconnects, remote.TerminalType,
                                  leaf.Emulator.Buffer.Columns, leaf.Emulator.Buffer.Rows);
    }

    /// <inheritdoc/>
    public bool Equals(ConnectionView? other) =>
        other is not null
        && Name == other.Name && Route == other.Route && Through == other.Through && State == other.State
        && Forwards.SequenceEqual(other.Forwards) && Reconnects == other.Reconnects
        && TerminalType == other.TerminalType && Columns == other.Columns && Rows == other.Rows;

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Name, Route, State, Forwards.Count, Columns, Rows);
}

/// <summary>Where a connection is, as the bars say it: a word every time, never a colour alone.</summary>
public enum ConnectionState
{
    /// <summary>Up, with a shell on the other end.</summary>
    Connected,

    /// <summary>The link went and another attempt is due or in flight.</summary>
    Reconnecting,

    /// <summary>Over, and not coming back.</summary>
    Disconnected,
}
