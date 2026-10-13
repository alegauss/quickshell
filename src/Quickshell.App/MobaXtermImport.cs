using System.Globalization;
using System.IO;

namespace Quickshell.App;

/// <summary>What an import would do to one session, decided before anything is written.</summary>
/// <param name="Folder">The folder it sits in, empty at the root.</param>
/// <param name="Name">What the incumbent called it.</param>
/// <param name="Node">The session this becomes, or null where it is not carried over.</param>
/// <param name="Skipped">Why it is not carried over, empty where it is.</param>
/// <param name="Unmapped">
/// Settings the source carried that this client has nowhere to put. Named per session rather than
/// dropped: a user told what did not come across has an accurate picture, and one told nothing
/// discovers it three weeks later and blames the client for hiding it.
/// </param>
public sealed record ImportedSession(string Folder, string Name, SessionNode? Node, string Skipped,
                                     IReadOnlyList<string> Unmapped)
{
    /// <summary>Whether this one would be created.</summary>
    public bool Carried => Node is not null;
}

/// <summary>
/// What an import would do, in full, before it does any of it.
/// </summary>
/// <param name="Sessions">Every session the source held, carried or not.</param>
/// <param name="Source">Where it was read from.</param>
public sealed record ImportPreview(IReadOnlyList<ImportedSession> Sessions, string Source)
{
    /// <summary>How many sessions would be created.</summary>
    public int Carrying => Sessions.Count(session => session.Carried);

    /// <summary>How many would not, which is a number a user should see before agreeing.</summary>
    public int Skipping => Sessions.Count(session => !session.Carried);

    /// <summary>
    /// How many sessions the store already holds, all of which stay (QS216). Said in the preview,
    /// because a user agreeing to an import is also agreeing to what happens to those.
    /// </summary>
    public int Kept { get; init; }

    /// <summary>The folder the import goes under, so nothing it brings collides with what is there.</summary>
    public string Folder { get; init; } = "imported";

    /// <summary>The client the sessions are read from, as the preview and the notes name it.</summary>
    public string Client { get; init; } = "MobaXterm";

    /// <summary>
    /// The tree this would produce, folders and all.
    ///
    /// <para>Built rather than written: nothing lands until a caller takes this and saves it, which
    /// is what makes the preview a preview.</para>
    /// </summary>
    public SessionNode Tree() =>
        Built(Folder, [.. Sessions.Where(session => session.Carried).Select(session => (FolderPath(session), session.Node!))]);

    /// <summary>
    /// A session's folder as the names leading to it, MobaXterm's nesting kept (QS296): a backslash
    /// in its <c>SubRep</c> is a level, not part of a name.
    /// </summary>
    internal static IReadOnlyList<string> FolderPath(ImportedSession session) =>
        session.Folder.Split('\\', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>A folder named <paramref name="name"/> holding its sessions and, after them, its folders in order.</summary>
    private static SessionNode Built(string name, List<(IReadOnlyList<string> Folder, SessionNode Node)> inside)
    {
        List<SessionNode> children = [.. inside.Where(each => each.Folder.Count == 0).Select(each => each.Node)];

        foreach (IGrouping<string, (IReadOnlyList<string> Folder, SessionNode Node)> folder in
                 inside.Where(each => each.Folder.Count > 0)
                       .GroupBy(each => each.Folder[0], StringComparer.Ordinal)
                       .OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            children.Add(Built(folder.Key, [.. folder.Select(each => ((IReadOnlyList<string>)[.. each.Folder.Skip(1)], each.Node))]));
        }

        return new SessionNode { Name = name, Children = children };
    }

    /// <summary>
    /// What each carried session could not bring, as comments to write before it in the store, by
    /// the names leading to it from under <paramref name="into"/> (QS294) — so it is found beside
    /// the session weeks later, not only in a preview that was dismissed.
    /// </summary>
    /// <param name="into">The folder the import is written under.</param>
    public IEnumerable<(IReadOnlyList<string> Path, string Text)> Notes(string into) =>
        Sessions.Where(session => session.Carried)
                .SelectMany(session => session.Unmapped.Select(what =>
                    ((IReadOnlyList<string>)[into, .. FolderPath(session), session.Name],
                     $"Not carried over from {Client}: {what}.")));

    /// <summary>
    /// What was left behind, one line per thing, with the sessions that had it — two hundred sessions
    /// with compression on are one line naming a few of them and how many more, not two hundred.
    /// </summary>
    public IReadOnlyList<string> LeftBehind()
    {
        const int Named = 3;

        return [.. Sessions.Where(session => session.Carried)
                           .SelectMany(session => session.Unmapped.Select(what => (what, session.Name)))
                           .GroupBy(pair => pair.what, StringComparer.Ordinal)
                           .OrderByDescending(group => group.Count())
                           .ThenBy(group => group.Key, StringComparer.Ordinal)
                           .Select(group =>
                           {
                               List<string> names = [.. group.Select(pair => pair.Name).Distinct(StringComparer.Ordinal)];
                               string who = names.Count <= Named
                                   ? string.Join(", ", names)
                                   : string.Join(", ", names.Take(Named)) + $" and {names.Count - Named} more";

                               return $"{group.Key} ({who})";
                           })];
    }
}

/// <summary>
/// Reading MobaXterm's own session file.
///
/// <para><b>The barrier to leaving is not features, it is the two hundred sessions somebody
/// accumulated over five years.</b> So this reads them. It is the one place this project reads a
/// competitor's format, and it is not the non-goal about carrying features over: what comes across
/// is the user's own data, and what the incumbent can do that this client will not is reported
/// rather than implemented.</para>
///
/// <para><b>The format, as observed against a real file of forty-nine sessions.</b> Sessions live
/// under <c>[Bookmarks]</c> and <c>[Bookmarks_N]</c>, one section per folder, each carrying a
/// <c>SubRep</c> that names the folder and an <c>ImgNum</c> that does not matter here. Every other
/// line is <c>Name=#icon#type%field%field%…</c>. Type 0 is SSH; the fields after it are host, port
/// and user, and the rest are settings this client mostly has nowhere to put.</para>
///
/// <para><b>Nothing is converted and nothing is copied.</b> A key is referenced where it lies, a
/// <c>.ppk</c> included — converting somebody's key without being asked is the kind of surprise
/// that costs trust in the first ten minutes.</para>
/// </summary>
public static class MobaXtermImport
{
    /// <summary>MobaXterm's own numbering for what a session connects with.</summary>
    private static readonly Dictionary<int, string> Kinds = new()
    {
        [0] = "SSH",
        [1] = "Telnet",
        [2] = "Rsh",
        [3] = "Xdmcp",
        [4] = "RDP",
        [5] = "VNC",
        [6] = "FTP",
        [7] = "SFTP",
        [8] = "Serial",
        [9] = "File",
        [10] = "Shell",
        [11] = "Browser",
        [12] = "Mosh",
        [13] = "S3",
        [14] = "WSL",
    };

    /// <summary>
    /// Where MobaXterm keeps it, which is beside the user's documents and follows OneDrive when
    /// that has taken the folder over.
    /// </summary>
    public static IEnumerable<string> LikelyPaths()
    {
        string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        yield return Path.Combine(documents, "MobaXterm", "MobaXterm.ini");
        yield return Path.Combine(profile, "OneDrive", "Documents", "MobaXterm", "MobaXterm.ini");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                                  "MobaXterm", "MobaXterm.ini");
    }

    /// <summary>The first of <see cref="LikelyPaths"/> that is there, or null.</summary>
    public static string? Find() => LikelyPaths().FirstOrDefault(File.Exists);

    /// <summary>
    /// Reads the file and says what an import would do. Writes nothing.
    /// </summary>
    /// <param name="path">The INI.</param>
    public static ImportPreview Preview(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        List<ImportedSession> sessions = [];
        string folder = string.Empty;
        bool inBookmarks = false;

        foreach (string line in File.ReadLines(path))
        {
            string trimmed = line.Trim();

            if (trimmed.StartsWith('['))
            {
                inBookmarks = trimmed.StartsWith("[Bookmarks", StringComparison.Ordinal);
                folder = string.Empty;

                continue;
            }

            if (!inBookmarks || trimmed.Length == 0 || trimmed.StartsWith(';'))
            {
                continue;
            }

            int equals = trimmed.IndexOf('=', StringComparison.Ordinal);

            if (equals <= 0)
            {
                continue;
            }

            string key = trimmed[..equals];
            string value = trimmed[(equals + 1)..];

            if (string.Equals(key, "SubRep", StringComparison.Ordinal))
            {
                // MobaXterm nests with a backslash, kept whole so the tree keeps its levels (QS296).
                folder = value.Trim();

                continue;
            }

            if (string.Equals(key, "ImgNum", StringComparison.Ordinal) || !value.StartsWith('#'))
            {
                continue;
            }

            sessions.Add(Read(folder, key, value));
        }

        return new ImportPreview(sessions, path);
    }

    /// <summary>One bookmark line, as what it would become and what it would lose.</summary>
    private static ImportedSession Read(string folder, string name, string value)
    {
        // #icon#type%host%port%user%...
        string[] head = value.Split('#', StringSplitOptions.RemoveEmptyEntries);
        string[] fields = value.Split('%');

        int kind = head.Length >= 2 && int.TryParse(head[1].Split('%')[0], CultureInfo.InvariantCulture,
                                                    out int parsed)
            ? parsed
            : -1;

        if (kind != 0)
        {
            string what = Kinds.TryGetValue(kind, out string? named) ? named : $"type {kind}";

            // Named rather than dropped, and named as what it is: a user with twelve RDP sessions
            // should be told this client does not do RDP, not left to notice twelve absences.
            return new ImportedSession(folder, name, null,
                                       $"{what} is not something this client connects with", []);
        }

        string host = At(fields, 1);

        if (host.Length == 0)
        {
            return new ImportedSession(folder, name, null, "the session names no host", []);
        }

        List<string> unmapped = [];

        SessionSettings settings = new()
        {
            User = Empty(At(fields, 3)),
            Port = int.TryParse(At(fields, 2), CultureInfo.InvariantCulture, out int port) && port != 22
                ? port
                : null,
            Key = Empty(Key(fields, unmapped)),
            JumpHost = Empty(Jump(fields, unmapped)),
        };

        Note(fields, unmapped);

        return new ImportedSession(folder, name,
                                   new SessionNode { Name = name, Host = host, Settings = settings },
                                   string.Empty, unmapped);
    }

    /// <summary>
    /// The private key, referenced where it lies.
    ///
    /// <para>A <c>.ppk</c> is carried across as a path and not converted. QS41 is what says this
    /// client can open one; converting it here would rewrite a file the user did not ask about.</para>
    /// </summary>
    private static string Key(string[] fields, List<string> unmapped)
    {
        // Field 14 in the observed layout, and absent on sessions that authenticate by password.
        // Its own placeholders expanded (QS276), so the store holds a path that exists.
        string key = MobaXtermPaths.Expand(At(fields, 14));

        if (key.Length > 0 && key.EndsWith(".ppk", StringComparison.OrdinalIgnoreCase))
        {
            unmapped.Add("the key is PuTTY-format and is referenced where it lies, not converted");
        }

        return key;
    }

    /// <summary>
    /// The jump host, carried into the session's own (QS296), and said how it differs: a jump here
    /// signs in with the user's keys and agents alone, so a password MobaXterm used for it is asked of
    /// nobody and the jump is refused instead.
    /// </summary>
    private static string Jump(string[] fields, List<string> unmapped)
    {
        string jump = At(fields, 8);

        if (Chosen(jump))
        {
            unmapped.Add("the jump host is carried, and is reached with your keys and agents only, never a password");

            return jump;
        }

        return string.Empty;
    }

    /// <summary>The fields this import reads and turns into something.</summary>
    private static readonly int[] Consumed = [0, 1, 2, 3, 8, 14];

    /// <summary>
    /// What the positions mean, as far as they have been identified against a real file.
    ///
    /// <para>Deliberately partial. MobaXterm writes sixty-three fields and names none of them, and
    /// guessing at one is worse than admitting it is unidentified — a wrong name in a report is a
    /// user acting on a setting that was never there.</para>
    /// </summary>
    private static readonly Dictionary<int, string> Named = new()
    {
        [5] = "X11 forwarding, which this client does not do and will not (a non-goal)",
        [6] = "compression",
        [20] = "an execute-on-login command or macro",
    };

    /// <summary>
    /// Everything the line carried that this client has nowhere to put.
    ///
    /// <para><b>Every set field that is not consumed is named, not only the ones identified.</b>
    /// That is the whole of the line's falsification — an import that silently drops a setting the
    /// source carried is the failure — and naming only the four positions that had been worked out
    /// would have left the other fifty-eight to disappear quietly. An unidentified field is reported
    /// by its position, which is honest about what is known and still tells a user something was
    /// there.</para>
    ///
    /// <para>A value of <c>0</c>, <c>-1</c> or an empty string is MobaXterm's own "unset" and is not
    /// a setting anybody chose, so it is not reported. Reporting those would drown the four that
    /// matter in fifty that do not.</para>
    /// </summary>
    private static void Note(string[] fields, List<string> unmapped)
    {
        List<int> unidentified = [];

        for (int at = 0; at < fields.Length; at++)
        {
            if (Consumed.Contains(at) || !Chosen(At(fields, at)))
            {
                continue;
            }

            if (Named.TryGetValue(at, out string? what))
            {
                unmapped.Add($"{what} was set, and this client has nowhere to put it");
            }
            else
            {
                unidentified.Add(at);
            }
        }

        if (unidentified.Count > 0)
        {
            unmapped.Add("fields this import has not identified were set: "
                         + string.Join(", ", unidentified));
        }
    }

    /// <summary>Whether a field holds something somebody chose rather than MobaXterm's own blank.</summary>
    private static bool Chosen(string value) =>
        value.Length > 0 && value is not "0" and not "-1";

    private static string At(string[] fields, int index) =>
        index < fields.Length ? fields[index].Trim() : string.Empty;

    private static string? Empty(string value) => value.Length == 0 ? null : value;
}
