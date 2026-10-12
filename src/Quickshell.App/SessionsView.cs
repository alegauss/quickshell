using System.Globalization;

namespace Quickshell.App;

/// <summary>
/// One row of the sessions sidebar: a folder or a session, at a depth in the tree (QS268).
/// </summary>
/// <param name="Path">Where it is in the store, which is what opening it takes.</param>
/// <param name="Name">What the row says first.</param>
/// <param name="Meta">The line beside or under it: a folder's hand-downs, a session's host.</param>
/// <param name="Depth">How far in it sits, the top level being 0.</param>
/// <param name="IsFolder">A folder, which a click folds, rather than a session, which it connects.</param>
/// <param name="Expanded">For a folder, whether what is in it is listed below it.</param>
/// <param name="Connected">For a session, whether a tab in this window holds it.</param>
public sealed record SessionRow(string Path, string Name, string Meta, int Depth, bool IsFolder, bool Expanded,
                                bool Connected);

/// <summary>
/// What the sessions sidebar lists, worked out from the store and nothing else (QS268).
///
/// <para><b>One source, two views.</b> The palette's <b>Open session</b> and this read the same
/// <see cref="SessionTree"/> and find a session by the same match — its path, then its host and tags,
/// scored as <see cref="Commands.Score"/> scores a command — so what one finds the other finds.</para>
///
/// <para><b>A search is a list, not a pruned tree.</b> With something typed the rows are the matching
/// sessions, best first, each carrying its folder in its line: somebody searching wants the session,
/// and a tree cut down to it is a tree they still have to read down.</para>
/// </summary>
public static class SessionsView
{
    /// <summary>The rows, top to bottom.</summary>
    /// <param name="store">The session store as it is now.</param>
    /// <param name="query">What is typed in the search box. Empty is the whole tree.</param>
    /// <param name="folded">The folders somebody folded, by path; every other folder is open.</param>
    /// <param name="connected">Whether a session, by path, is open in a tab.</param>
    public static IReadOnlyList<SessionRow> Rows(SessionTree store, string query, IReadOnlySet<string> folded,
                                                 Func<string, bool> connected)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(folded);
        ArgumentNullException.ThrowIfNull(connected);

        if (query.Trim().Length > 0)
        {
            return Searched(store, query, connected);
        }

        List<SessionRow> rows = [];

        foreach (SessionNode child in store.Root.Children)
        {
            Walk(child, string.Empty, 0);
        }

        return rows;

        void Walk(SessionNode node, string above, int depth)
        {
            string path = above.Length == 0 ? node.Name : $"{above}/{node.Name}";

            if (node.IsSession)
            {
                rows.Add(new SessionRow(path, node.Name, Reached(node), depth, IsFolder: false, Expanded: false,
                                        connected(path)));

                return;
            }

            bool open = !folded.Contains(path);

            rows.Add(new SessionRow(path, node.Name, HandsDown(node), depth, IsFolder: true, open, Connected: false));

            if (!open)
            {
                return;
            }

            foreach (SessionNode child in node.Children)
            {
                Walk(child, path, depth + 1);
            }
        }
    }

    /// <summary>The sessions a query finds, best first, each with its folder in its line.</summary>
    private static List<SessionRow> Searched(SessionTree store, string query, Func<string, bool> connected) =>
    [
        // Score first, then the tree's own order, so two equal matches keep still while typing.
        .. store.Sessions()
                .Select((each, order) => (Session: each, Score: Found(each, query), Order: order))
                .Where(one => one.Score >= 0)
                .OrderByDescending(one => one.Score)
                .ThenBy(one => one.Order)
                .Select(one =>
                {
                    string path = one.Session.Path;
                    int slash = path.LastIndexOf('/');
                    string where = slash < 0 ? string.Empty : $"{path[..slash]} · ";

                    return new SessionRow(path, path[(slash + 1)..], where + Reached(store.Find(path)), 0,
                                          IsFolder: false, Expanded: false, connected(path));
                }),
    ];

    /// <summary>How well a session answers the query, or -1: the palette's own match over the same two strings.</summary>
    private static int Found(ResolvedSession session, string query) =>
        Math.Max(Commands.Score(session.Path, query),
                 Commands.Score(string.Join(' ', [session.Host, .. session.Tags]), query));

    /// <summary>
    /// The address a session reaches, as far as the session itself says: its own user and port around
    /// its host. What a folder hands down is on the folder's row and is not repeated on each session.
    /// </summary>
    private static string Reached(SessionNode? node)
    {
        if (node is not { Host: { } host })
        {
            return string.Empty;
        }

        string user = node.Settings.User is { Length: > 0 } own ? $"{own}@" : string.Empty;
        string port = node.Settings.Port is { } number ? string.Create(CultureInfo.InvariantCulture, $":{number}") : string.Empty;

        return user + host + port;
    }

    /// <summary>What a folder hands down that changes where its sessions go: who they log in as, and through what.</summary>
    private static string HandsDown(SessionNode folder)
    {
        List<string> said = [];

        if (folder.Settings.User is { Length: > 0 } user)
        {
            said.Add(user);
        }

        if (folder.Settings.JumpHost is { Length: > 0 } jump)
        {
            said.Add($"via {jump}");
        }

        return string.Join(" · ", said);
    }
}
