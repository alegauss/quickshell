using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Quickshell.App;

/// <summary>
/// The settings a folder hands down and a session may override.
///
/// <para>Every field is optional, and that is what inheritance is: a value that is not set here is
/// the parent's. A hundred hosts in a fleet share a user and a jump host and differ in a name, which
/// is the only reason a fleet that size is manageable at all.</para>
///
/// <para><b><see cref="Credential"/> is a name and never a secret.</b> The file this lives in will be
/// committed, shared and backed up — a user will do that whether or not a design allowed for it, so
/// the design allows for it. What is stored is a reference that <c>SecretStore</c> resolves against
/// the user's own Credential Manager, and a file that leaked would cost nobody anything.</para>
/// </summary>
public sealed record SessionSettings
{
    /// <summary>The account to log in as.</summary>
    public string? User { get; init; }

    /// <summary>The port, where it is not the usual one.</summary>
    public int? Port { get; init; }

    /// <summary>A path to a private key file.</summary>
    public string? Key { get; init; }

    /// <summary>A host to reach this one through.</summary>
    public string? JumpHost { get; init; }

    /// <summary>Which colour scheme the terminal wears. Not the chrome's theme — see QS46.</summary>
    public string? Scheme { get; init; }

    /// <summary>
    /// The <em>name</em> of a saved credential, never the credential. A file holding one of these is
    /// a file that is safe to put in a repository.
    /// </summary>
    public string? Credential { get; init; }

    /// <summary>Point size for this session's text, where it differs from the global one.</summary>
    public double? FontSize { get; init; }

    /// <summary>What to claim to be — <c>xterm-256color</c> and its kind.</summary>
    public string? TerminalType { get; init; }

    /// <summary>How many lines of history to keep.</summary>
    public int? Scrollback { get; init; }

    /// <summary>Whether every field is unset, which is a node that inherits everything.</summary>
    [JsonIgnore]
    public bool IsEmpty =>
        User is null && Port is null && Key is null && JumpHost is null
        && Scheme is null && Credential is null && FontSize is null
        && TerminalType is null && Scrollback is null;
}

/// <summary>
/// One node of the tree: a folder if it has children, a session if it has a host.
///
/// <para>Folders are how people organise fleets — by environment, by customer, by datacentre — so
/// the store is a tree rather than a list with a label on each row.</para>
/// </summary>
public sealed record SessionNode
{
    /// <summary>What it is called, which is also how it is addressed within its parent.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>The machine, for a session. Null on a folder.</summary>
    public string? Host { get; init; }

    /// <summary>What this node sets, and what its children will inherit.</summary>
    public SessionSettings Settings { get; init; } = new();

    /// <summary>
    /// Text sent to the shell after login, as though somebody typed it.
    ///
    /// <para><b>Deliberately not in <see cref="SessionSettings"/>, which is the type that
    /// inherits.</b> A folder that could set this would be a folder that silently types into every
    /// machine under it, and a user who added a host to that folder would never see it happen. Being
    /// outside the inheriting type is what makes "never inherited" a property of the model rather
    /// than a rule somebody has to keep remembering.</para>
    /// </summary>
    public string? PostLogin { get; init; }

    /// <summary>Labels a search can find it by.</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>What is under it.</summary>
    public IReadOnlyList<SessionNode> Children { get; init; } = [];

    /// <summary>Whether this is something that can be opened rather than something to open.</summary>
    [JsonIgnore]
    public bool IsSession => Host is { Length: > 0 };
}

/// <summary>
/// A value and the node it came from.
///
/// <para>The design asks that each node state explicitly where a value came from, so a user can see
/// the source rather than deduce it. A user looking at a session that connects as the wrong account
/// wants to be told which folder said so, not to walk the tree working it out.</para>
/// </summary>
/// <param name="Value">The value in force.</param>
/// <param name="From">The path of the node that set it — the session's own path where it set it itself.</param>
public readonly record struct Source<T>(T Value, string From);

/// <summary>One session with every inherited value resolved, and each one's origin beside it.</summary>
/// <param name="Path">Where it is in the tree, as a user reads it.</param>
/// <param name="Host">The machine.</param>
/// <param name="User">Who to log in as, and which node said so.</param>
/// <param name="Port">The port, and which node said so.</param>
/// <param name="Key">The key file, and which node said so.</param>
/// <param name="JumpHost">What to go through, and which node said so.</param>
/// <param name="Scheme">The terminal's colours, and which node said so.</param>
/// <param name="Credential">The name of a saved credential, and which node said so.</param>
/// <param name="Tags">Every tag on this node and on its folders.</param>
public sealed record ResolvedSession(string Path, string Host, Source<string>? User, Source<int>? Port,
                                     Source<string>? Key, Source<string>? JumpHost,
                                     Source<string>? Scheme, Source<string>? Credential,
                                     IReadOnlyList<string> Tags)
{
    /// <summary>Point size, and which node said so.</summary>
    public Source<double>? FontSize { get; init; }

    /// <summary>The terminal type to claim, and which node said so.</summary>
    public Source<string>? TerminalType { get; init; }

    /// <summary>Lines of history, and which node said so.</summary>
    public Source<int>? Scrollback { get; init; }

    /// <summary>
    /// What this session types after login, which is its own or nothing. No source accompanies it
    /// because there is only ever one place it can have come from.
    /// </summary>
    public string? PostLogin { get; init; }
}

/// <summary>
/// The sessions a user has accumulated, in a file they own.
///
/// <para><b>The format is a commitment and not a serialisation.</b> This is the artefact somebody
/// builds over years and the thing that makes leaving a client expensive, so it is readable,
/// diffable and editable without this client running: indented JSON, with comments and trailing
/// commas accepted on the way in so a hand-written file is a first-class one.</para>
///
/// <para><b>What a write does not preserve is said here rather than discovered.</b> The writer emits
/// the tree, so comments a user added are not in what it writes — a file edited by hand and then
/// edited again through the client loses them. Reading is lossless; writing is not, and QS117
/// carries it.</para>
/// </summary>
public sealed class SessionTree
{
    /// <summary>
    /// Comments and trailing commas accepted, because a hand-written file has both. A parser that
    /// refused them would make "editable by hand" a claim rather than a property.
    /// </summary>
    private static readonly JsonSerializerOptions Reading = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Indented and with nothing omitted that a reader would want to see.</summary>
    private static readonly JsonSerializerOptions Writing = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private SessionTree(SessionNode root) => Root = root;

    /// <summary>The top of the tree, which is a folder with no name of its own.</summary>
    public SessionNode Root { get; }

    /// <summary>An empty store.</summary>
    public static SessionTree Empty() => new(new SessionNode { Name = string.Empty });

    /// <summary>A store around a tree somebody built in memory.</summary>
    public static SessionTree Of(SessionNode root)
    {
        ArgumentNullException.ThrowIfNull(root);

        return new SessionTree(root);
    }

    /// <summary>
    /// Reads a store, or an empty one where the file is not there.
    /// </summary>
    /// <exception cref="SessionStoreException">The file is there and cannot be read.</exception>
    public static SessionTree ReadFrom(string file)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(file);

        if (!File.Exists(file))
        {
            return Empty();
        }

        try
        {
            return new SessionTree(JsonSerializer.Deserialize<SessionNode>(File.ReadAllBytes(file), Reading)
                                   ?? new SessionNode());
        }
        catch (JsonException broken)
        {
            // Named rather than swallowed. A store silently replaced with an empty one is a user
            // whose hundred sessions have apparently vanished, and who has no idea a typo did it.
            throw new SessionStoreException(
                $"{file} could not be read as a session store.",
                $"Line {broken.LineNumber + 1} is not what this expected: {broken.Message}",
                "Fix the file or move it aside; nothing has been changed.");
        }
    }

    /// <summary>Writes the tree, indented so a diff of it reads as a change somebody made.</summary>
    public void WriteTo(string file)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(file);

        string? directory = Path.GetDirectoryName(file);

        if (directory is { Length: > 0 })
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllBytes(file, JsonSerializer.SerializeToUtf8Bytes(Root, Writing));
    }

    /// <summary>Every session in the tree, with its inherited values resolved.</summary>
    public IReadOnlyList<ResolvedSession> Sessions()
    {
        List<ResolvedSession> found = [];

        Walk(Root, string.Empty, new SessionSettings(),
             new Dictionary<string, string>(StringComparer.Ordinal), [], found);

        return found;
    }

    /// <summary>
    /// The node at a path, folder or session, or null where there is none. The empty path is the
    /// root.
    /// </summary>
    public SessionNode? Find(string path)
    {
        SessionNode? here = Root;

        foreach (string step in Segments(path))
        {
            here = here?.Children.FirstOrDefault(child =>
                string.Equals(child.Name, step, StringComparison.OrdinalIgnoreCase));
        }

        return here;
    }

    /// <summary>
    /// What a node at this path takes from the folders above it, by field name, each with the node
    /// that set it.
    ///
    /// <para>The node's own settings are not in it. That is the whole distinction the dialog rests
    /// on: what is here is what the user would be overriding, and a value the node set itself is
    /// not something it inherits.</para>
    /// </summary>
    public IReadOnlyDictionary<string, Source<string>> Inherits(string path)
    {
        Dictionary<string, string> from = new(StringComparer.Ordinal);
        SessionSettings carried = new();
        SessionNode? here = Root;
        string walked = string.Empty;

        // The root itself can set values, and the last segment is the node being edited, whose own
        // settings are what this deliberately stops short of.
        foreach (string step in Segments(path))
        {
            carried = Merge(carried, here?.Settings ?? new SessionSettings(),
                            walked.Length == 0 ? "/" : walked, from);

            here = here?.Children.FirstOrDefault(child =>
                string.Equals(child.Name, step, StringComparison.OrdinalIgnoreCase));

            walked = walked.Length == 0 ? step : $"{walked}/{step}";
        }

        if (Segments(path).Count == 0)
        {
            return new Dictionary<string, Source<string>>(StringComparer.Ordinal);
        }

        Dictionary<string, Source<string>> inherited = new(StringComparer.Ordinal);

        Note(nameof(SessionSettings.User), carried.User);
        Note(nameof(SessionSettings.Port), carried.Port?.ToString(CultureInfo.InvariantCulture));
        Note(nameof(SessionSettings.Key), carried.Key);
        Note(nameof(SessionSettings.JumpHost), carried.JumpHost);
        Note(nameof(SessionSettings.Scheme), carried.Scheme);
        Note(nameof(SessionSettings.Credential), carried.Credential);
        Note(nameof(SessionSettings.FontSize),
             carried.FontSize?.ToString(CultureInfo.InvariantCulture));
        Note(nameof(SessionSettings.TerminalType), carried.TerminalType);
        Note(nameof(SessionSettings.Scrollback),
             carried.Scrollback?.ToString(CultureInfo.InvariantCulture));

        return inherited;

        void Note(string field, string? value)
        {
            if (value is not null)
            {
                inherited[field] = new Source<string>(value, from[field]);
            }
        }
    }

    /// <summary>
    /// The same tree with one node put at a path, replacing what was there or adding it to the
    /// folder above. Nothing is mutated: what comes back is a new tree.
    /// </summary>
    public SessionTree With(string path, SessionNode node)
    {
        ArgumentNullException.ThrowIfNull(node);

        IReadOnlyList<string> steps = Segments(path);

        if (steps.Count == 0)
        {
            return new SessionTree(node);
        }

        return new SessionTree(Put(Root, steps, 0, node));
    }

    private static SessionNode Put(SessionNode parent, IReadOnlyList<string> steps, int depth,
                                   SessionNode node)
    {
        string wanted = steps[depth];
        bool last = depth == steps.Count - 1;

        List<SessionNode> children = [.. parent.Children];

        int at = children.FindIndex(child =>
            string.Equals(child.Name, wanted, StringComparison.OrdinalIgnoreCase));

        if (last)
        {
            if (at < 0)
            {
                children.Add(node);
            }
            else
            {
                children[at] = node;
            }

            return parent with { Children = children };
        }

        // A folder named in the path but not in the tree is created, because a caller placing a
        // session two folders down means both folders.
        SessionNode below = at < 0 ? new SessionNode { Name = wanted } : children[at];
        SessionNode rebuilt = Put(below, steps, depth + 1, node);

        if (at < 0)
        {
            children.Add(rebuilt);
        }
        else
        {
            children[at] = rebuilt;
        }

        return parent with { Children = children };
    }

    private static IReadOnlyList<string> Segments(string path) =>
        string.IsNullOrWhiteSpace(path) || path == "/"
            ? []
            : [.. path.Split('/', StringSplitOptions.RemoveEmptyEntries
                                  | StringSplitOptions.TrimEntries)];

    /// <summary>One session by its path, or null where there is none.</summary>
    public ResolvedSession? Session(string path) =>
        Sessions().FirstOrDefault(session =>
            string.Equals(session.Path, path, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Sessions matching a query by name, host, tag or folder.
    ///
    /// <para>Above about fifty entries the tree stops being how anybody finds anything, so this is
    /// not a convenience — it is the way the store is used once it is worth having.</para>
    /// </summary>
    public IReadOnlyList<ResolvedSession> Search(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return Sessions();
        }

        return [.. Sessions().Where(session =>
            Has(session.Path, query)
            || Has(session.Host, query)
            || session.Tags.Any(tag => Has(tag, query)))];
    }

    private static bool Has(string? text, string query) =>
        text is not null && text.Contains(query, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Walks the tree, carrying down what each folder set and remembering which one set it.
    /// </summary>
    private static void Walk(SessionNode node, string path, SessionSettings inherited,
                             Dictionary<string, string> from, IReadOnlyList<string> tags,
                             List<ResolvedSession> found)
    {
        string here = node.Name.Length == 0
            ? string.Empty
            : path.Length == 0 ? node.Name : $"{path}/{node.Name}";

        string named = here.Length == 0 ? "/" : here;

        Dictionary<string, string> sources = new(from, StringComparer.Ordinal);
        SessionSettings settings = Merge(inherited, node.Settings, named, sources);
        List<string> carried = [.. tags, .. node.Tags];

        if (node.IsSession)
        {
            found.Add(new ResolvedSession(
                named,
                node.Host!,
                Carry(settings.User, sources, nameof(SessionSettings.User)),
                settings.Port is { } port
                    ? new Source<int>(port, sources[nameof(SessionSettings.Port)])
                    : null,
                Carry(settings.Key, sources, nameof(SessionSettings.Key)),
                Carry(settings.JumpHost, sources, nameof(SessionSettings.JumpHost)),
                Carry(settings.Scheme, sources, nameof(SessionSettings.Scheme)),
                Carry(settings.Credential, sources, nameof(SessionSettings.Credential)),
                carried)
            {
                FontSize = settings.FontSize is { } size
                    ? new Source<double>(size, sources[nameof(SessionSettings.FontSize)])
                    : null,
                TerminalType = Carry(settings.TerminalType, sources,
                                     nameof(SessionSettings.TerminalType)),
                Scrollback = settings.Scrollback is { } lines
                    ? new Source<int>(lines, sources[nameof(SessionSettings.Scrollback)])
                    : null,

                // Its own, never the folder's: see SessionNode.PostLogin.
                PostLogin = node.PostLogin,
            });
        }

        foreach (SessionNode child in node.Children)
        {
            Walk(child, here, settings, sources, carried, found);
        }
    }

    private static Source<string>? Carry(string? value, Dictionary<string, string> sources,
                                         string field) =>
        value is null ? null : new Source<string>(value, sources[field]);

    /// <summary>
    /// What is in force below this node, and which node put it there.
    ///
    /// <para>A child that sets a field takes ownership of it, so the source a user is shown is the
    /// nearest node that decided — which is the one they would go and edit.</para>
    /// </summary>
    private static SessionSettings Merge(SessionSettings inherited, SessionSettings own, string at,
                                         Dictionary<string, string> sources)
    {
        Take(own.User, nameof(SessionSettings.User));
        Take(own.Port, nameof(SessionSettings.Port));
        Take(own.Key, nameof(SessionSettings.Key));
        Take(own.JumpHost, nameof(SessionSettings.JumpHost));
        Take(own.Scheme, nameof(SessionSettings.Scheme));
        Take(own.Credential, nameof(SessionSettings.Credential));
        Take(own.FontSize, nameof(SessionSettings.FontSize));
        Take(own.TerminalType, nameof(SessionSettings.TerminalType));
        Take(own.Scrollback, nameof(SessionSettings.Scrollback));

        return new SessionSettings
        {
            User = own.User ?? inherited.User,
            Port = own.Port ?? inherited.Port,
            Key = own.Key ?? inherited.Key,
            JumpHost = own.JumpHost ?? inherited.JumpHost,
            Scheme = own.Scheme ?? inherited.Scheme,
            Credential = own.Credential ?? inherited.Credential,
            FontSize = own.FontSize ?? inherited.FontSize,
            TerminalType = own.TerminalType ?? inherited.TerminalType,
            Scrollback = own.Scrollback ?? inherited.Scrollback,
        };

        void Take(object? value, string field)
        {
            if (value is not null)
            {
                sources[field] = at;
            }
        }
    }
}

/// <summary>A store that is there and cannot be read, which is a thing a user must be told about.</summary>
public sealed class SessionStoreException(string reason, string means = "", string remedy = "")
    : Exception(reason)
{
    /// <summary>What it means, for somebody who did not write the parser.</summary>
    public string Means { get; } = means;

    /// <summary>What to do about it.</summary>
    public string Remedy { get; } = remedy;
}
