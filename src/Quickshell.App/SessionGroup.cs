namespace Quickshell.App;

/// <summary>
/// A folder of saved sessions opened as one tab that types into all of them (QS179).
///
/// <para><b>A group is a folder, and not a new thing to store.</b> The session tree already holds
/// named sets of sessions; a fleet somebody broadcasts to every morning is the folder they keep it in.
/// Opening it opens each session as a pane of one new tab and turns broadcasting on for that tab — so
/// the target is still the panes on screen, and the edge QS53 draws is still what says which panes
/// hear a keystroke. Never across tabs, and never to a session that is not on screen.</para>
///
/// <para>A tab holds <see cref="MainWindow.MaximumPanes"/> panes, so a larger folder opens its first
/// sixteen and says how many it left out, rather than leaving them out quietly.</para>
/// </summary>
public static class SessionGroup
{
    /// <summary>
    /// Opens the group in a new tab of this window and turns broadcasting on.
    /// </summary>
    /// <param name="window">The window to open it in.</param>
    /// <param name="settings">What the panes open with.</param>
    /// <param name="share">The device every pane draws with.</param>
    /// <param name="members">The folder's sessions, in the tree's order.</param>
    /// <param name="opening">What connects one session, given the session.</param>
    /// <returns>The tab, and how many sessions did not fit in it.</returns>
    public static (TerminalTab? Tab, int LeftOut) Open(MainWindow window, Settings settings, TerminalShare share,
                                                       IReadOnlyList<ResolvedSession> members,
                                                       Func<ResolvedSession, ShellOpener> opening)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(members);
        ArgumentNullException.ThrowIfNull(opening);

        if (members.Count == 0)
        {
            return (null, 0);
        }

        ResolvedSession[] taken = [.. members.Take(MainWindow.MaximumPanes)];

        TerminalTab tab = TerminalTab.Open(settings, share, taken[0].Host);

        window.Apply(settings);
        window.Add(tab);
        window.Sessions.Open(taken[0].Host, another: true);

        _ = tab.Focused.ConnectAsync(opening(taken[0]));

        for (int at = 1; at < taken.Length; at++)
        {
            ResolvedSession member = taken[at];

            // Alternating, so a fleet of eight is a grid of panes and not eight slivers side by side.
            window.SplitPane(at % 2 == 1 ? Divide.Beside : Divide.Below, member.Host,
                             leaf => _ = leaf.ConnectAsync(opening(member)));
            window.Sessions.Open(member.Host, another: true);
        }

        if (taken.Length > 1)
        {
            window.Broadcast();
        }

        return (tab, members.Count - taken.Length);
    }
}
