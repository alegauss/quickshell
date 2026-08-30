namespace Quickshell.App;

/// <summary>What happened when somebody asked to open a session.</summary>
public enum Opening
{
    /// <summary>A new one was started.</summary>
    Opened,

    /// <summary>One was already open, and it was brought forward instead.</summary>
    Focused,
}

/// <summary>
/// Which sessions are open, so asking for one that already is brings it forward.
///
/// <para><b>Focused rather than opened twice, unless the user asks.</b> A double-click on a host
/// somebody is already logged into should not make a second login — it is a second entry in the
/// server's auth log, a second shell, and on a jump box it may be the thing somebody is watching
/// for. The user who genuinely wants two says so.</para>
/// </summary>
public sealed class OpenSessions
{
    private readonly List<string> _open = [];

    /// <summary>The paths of what is open, in the order it was opened.</summary>
    public IReadOnlyList<string> Paths => _open;

    /// <summary>Whether this session is open.</summary>
    public bool IsOpen(string path) =>
        _open.Contains(path, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Asks to open a session.
    /// </summary>
    /// <param name="path">Which one.</param>
    /// <param name="another">
    /// True where the user deliberately asked for a second one, which is the only way to get one.
    /// </param>
    public Opening Open(string path, bool another = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!another && IsOpen(path))
        {
            return Opening.Focused;
        }

        _open.Add(path);

        return Opening.Opened;
    }

    /// <summary>One of them closed.</summary>
    public void Closed(string path)
    {
        int at = _open.FindIndex(open => string.Equals(open, path, StringComparison.OrdinalIgnoreCase));

        if (at >= 0)
        {
            _open.RemoveAt(at);
        }
    }

    /// <summary>
    /// What to list when the window is closing, which QS46's guard asks a user about.
    ///
    /// <para>Named rather than counted, and each named once however many copies are open: a user
    /// deciding whether to close is deciding about hosts, not about windows.</para>
    /// </summary>
    public IReadOnlyList<string> Closing() =>
        [.. _open.Distinct(StringComparer.OrdinalIgnoreCase)];
}
