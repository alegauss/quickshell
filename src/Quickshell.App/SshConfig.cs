using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace Quickshell.App;

/// <summary>What an <c>ssh_config</c> says about one host, once its blocks have been applied.</summary>
/// <param name="Alias">What the user asked for.</param>
/// <param name="HostName">The machine to actually connect to, where the config renames it.</param>
/// <param name="User">The account.</param>
/// <param name="Port">The port.</param>
/// <param name="IdentityFiles">Key files, in the order the config lists them.</param>
/// <param name="IdentitiesOnly">Whether to offer only those keys and nothing an agent holds.</param>
/// <param name="ProxyJump">What to reach this host through.</param>
/// <param name="ServerAliveInterval">How often the user asked for a keepalive.</param>
/// <param name="StrictHostKeyChecking">What the user told <c>ssh</c> to do about unknown keys.</param>
public sealed record SshConfigHost(string Alias, string? HostName, string? User, int? Port,
                                   IReadOnlyList<string> IdentityFiles, bool? IdentitiesOnly,
                                   string? ProxyJump, TimeSpan? ServerAliveInterval,
                                   string? StrictHostKeyChecking)
{
    /// <summary>The machine to connect to: what the config renamed it to, or the alias itself.</summary>
    public string Target => HostName ?? Alias;
}

/// <summary>
/// A directive this client read and did not act on, with where it was and why.
/// </summary>
/// <param name="File">Which file.</param>
/// <param name="Line">Which line of it, counting from one.</param>
/// <param name="Keyword">The directive.</param>
/// <param name="Value">What it was set to.</param>
/// <param name="Why">What this client does instead, in words a user can act on.</param>
public readonly record struct UnhonouredDirective(string File, int Line, string Keyword, string Value,
                                                  string Why)
{
    /// <summary>The line a user is shown, which names the file so they can go and look.</summary>
    public override string ToString() =>
        $"{File}:{Line} {Keyword} {Value} — {Why}";
}

/// <summary>
/// The user's own <c>ssh_config</c>, read and never written.
///
/// <para><b>Reading it is the difference between switching client in an evening and not switching.</b>
/// A developer arriving here very often has a config that already works — hosts, users, ports, keys,
/// jump chains accumulated over years — and every one of those is a decision they have already made.
/// Asking them to make each of them again is the real cost of changing tool.</para>
///
/// <para><b>Read-only, and that is the important decision.</b> The file is shared with <c>ssh</c>,
/// <c>scp</c>, <c>rsync</c> and <c>git</c>, so a client that reformatted or reordered it would have
/// quietly broken four other tools. There is no method here that writes, which is how that is
/// guaranteed rather than intended. quickshell's own additions live in quickshell's own store,
/// which may name a config host.</para>
///
/// <para><b>First value wins, because that is OpenSSH's rule.</b> A config written against it
/// behaves differently under any other, and a client that took the last value instead would connect
/// a user's hosts to the wrong places while looking entirely reasonable.</para>
///
/// <para><b>What is not honoured is reported.</b> Silently dropping a <c>ProxyCommand</c> produces a
/// host that looks configured and simply never connects, which is the worst diagnostic outcome
/// available — see <see cref="Unhonoured"/>.</para>
/// </summary>
public sealed partial class SshConfig
{
    /// <summary>How deep <c>Include</c> may nest before this stops following it.</summary>
    private const int IncludeDepth = 16;

    private readonly List<Block> _blocks = [];
    private readonly List<UnhonouredDirective> _unhonoured = [];

    private SshConfig()
    {
    }

    /// <summary>One <c>Host</c> or <c>Match</c> block and the settings under it.</summary>
    private sealed record Block(IReadOnlyList<string> Patterns, List<(string Keyword, string Value, string File, int Line)> Settings);

    /// <summary>Where <c>ssh</c> keeps it, which is where this looks unless told otherwise.</summary>
    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                     ".ssh", "config");

    /// <summary>Directives that were read and not acted on, each with where it was and why.</summary>
    public IReadOnlyList<UnhonouredDirective> Unhonoured => _unhonoured;

    /// <summary>
    /// Every host the config names literally, which is what a palette lists.
    ///
    /// <para>Patterns are not among them: <c>Host *.prod</c> is a rule and not a machine, and
    /// offering it as something to connect to would be offering a name no server has.</para>
    /// </summary>
    public IReadOnlyList<string> Aliases =>
        [.. _blocks.SelectMany(block => block.Patterns)
                   .Where(pattern => !pattern.StartsWith('!')
                                     && !pattern.Contains('*', StringComparison.Ordinal)
                                     && !pattern.Contains('?', StringComparison.Ordinal))
                   .Distinct(StringComparer.OrdinalIgnoreCase)];

    /// <summary>Reads a config, or an empty one where there is none.</summary>
    public static SshConfig ReadFrom(string? file = null)
    {
        SshConfig config = new();
        string path = file ?? DefaultPath;

        if (File.Exists(path))
        {
            config.Read(path, IncludeDepth);
        }

        return config;
    }

    /// <summary>Reads a config from text, for a test or a file this client did not open itself.</summary>
    public static SshConfig Parse(string text, string name = "ssh_config")
    {
        ArgumentNullException.ThrowIfNull(text);

        SshConfig config = new();

        config.Lines(text.Split('\n'), name, IncludeDepth);

        return config;
    }

    /// <summary>
    /// What the config says about one host.
    ///
    /// <para>Blocks are applied in order and the first setting of each keyword is the one that
    /// sticks, which is OpenSSH's own rule and not a simplification of it.</para>
    /// </summary>
    public SshConfigHost Resolve(string host)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);

        string? hostName = null;
        string? user = null;
        int? port = null;
        List<string> identities = [];
        bool? identitiesOnly = null;
        string? proxyJump = null;
        TimeSpan? alive = null;
        string? strict = null;

        foreach (Block block in _blocks.Where(block => Matches(block.Patterns, host)))
        {
            foreach ((string keyword, string value, _, _) in block.Settings)
            {
                switch (keyword.ToLowerInvariant())
                {
                    case "hostname":
                        hostName ??= value;
                        break;

                    case "user":
                        user ??= value;
                        break;

                    case "port":
                        port ??= int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture,
                                              out int parsed) ? parsed : null;
                        break;

                    case "identityfile":
                        // Every one, in order: OpenSSH tries them in turn and so does this.
                        identities.Add(Expand(value));
                        break;

                    case "identitiesonly":
                        identitiesOnly ??= Yes(value);
                        break;

                    case "proxyjump":
                        proxyJump ??= value;
                        break;

                    case "serveraliveinterval":
                        alive ??= int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture,
                                               out int seconds)
                            ? TimeSpan.FromSeconds(seconds)
                            : null;
                        break;

                    case "stricthostkeychecking":
                        strict ??= value;
                        break;

                    default:
                        break;
                }
            }
        }

        return new SshConfigHost(host, hostName, user, port, identities, identitiesOnly, proxyJump,
                                 alive, strict);
    }

    /// <summary>
    /// Whether a host matches a block's patterns.
    ///
    /// <para>A negation beats everything: <c>Host * !internal</c> is every host except that one, and
    /// a client that applied the positive anyway would send internal traffic through a bastion.</para>
    /// </summary>
    private static bool Matches(IReadOnlyList<string> patterns, string host)
    {
        bool matched = false;

        foreach (string pattern in patterns)
        {
            bool negated = pattern.StartsWith('!');
            string bare = negated ? pattern[1..] : pattern;

            if (!Glob(bare, host))
            {
                continue;
            }

            if (negated)
            {
                return false;
            }

            matched = true;
        }

        return matched;
    }

    /// <summary>OpenSSH's two wildcards and nothing else: any run, and exactly one character.</summary>
    private static bool Glob(string pattern, string host) =>
        Regex.IsMatch(host,
                      "^" + Regex.Escape(pattern).Replace("\\*", ".*", StringComparison.Ordinal)
                                                 .Replace("\\?", ".", StringComparison.Ordinal) + "$",
                      RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                      TimeSpan.FromSeconds(1));

    private static bool Yes(string value) =>
        value.Equals("yes", StringComparison.OrdinalIgnoreCase);

    /// <summary>A leading tilde, as every one of these files uses.</summary>
    private static string Expand(string path) =>
        path.StartsWith('~')
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                           path[1..].TrimStart('/', '\\'))
            : path;

    private void Read(string file, int depth)
    {
        try
        {
            Lines(File.ReadAllLines(file), file, depth);
        }
        catch (IOException)
        {
            // A config this cannot open is a config with nothing in it as far as this is concerned.
            // Refusing to start because somebody's file is locked would be worse than reading none.
        }
    }

    private void Lines(string[] lines, string file, int depth)
    {
        Block current = new(["*"], []);

        _blocks.Add(current);

        for (int number = 0; number < lines.Length; number++)
        {
            string line = lines[number].Trim();

            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            // Keyword and value are separated by whitespace or an equals sign, either of which may
            // be surrounded by more whitespace. Both spellings are in the wild.
            string[] parts = line.Split(['=', ' ', '\t'], 2, StringSplitOptions.RemoveEmptyEntries
                                                             | StringSplitOptions.TrimEntries);

            if (parts.Length < 2)
            {
                continue;
            }

            string keyword = parts[0];
            string value = parts[1].Trim();

            switch (keyword.ToLowerInvariant())
            {
                case "host":
                    current = new Block([.. value.Split(' ', StringSplitOptions.RemoveEmptyEntries)], []);
                    _blocks.Add(current);
                    break;

                case "match":
                    current = Match(value, file, number + 1);
                    _blocks.Add(current);
                    break;

                case "include":
                    Include(value, file, depth);
                    break;

                default:
                    current.Settings.Add((keyword, value, file, number + 1));
                    Note(keyword, value, file, number + 1);
                    break;
            }
        }
    }

    /// <summary>
    /// <c>Match host</c>, which is the one form of it people write in a config a client can honour.
    ///
    /// <para>Everything else — <c>exec</c>, <c>originalhost</c>, <c>user</c>, <c>final</c> — depends
    /// on state this client does not have at the moment it reads the file, so those become blocks
    /// that match nothing and are reported.</para>
    /// </summary>
    private Block Match(string value, string file, int line)
    {
        string[] parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length >= 2 && parts[0].Equals("host", StringComparison.OrdinalIgnoreCase))
        {
            return new Block([.. parts[1].Split(',', StringSplitOptions.RemoveEmptyEntries)], []);
        }

        _unhonoured.Add(new UnhonouredDirective(
            file, line, "Match", value,
            "only 'Match host' is applied; this one depends on state read at connection time, so "
            + "nothing under it is used"));

        return new Block([], []);
    }

    private void Include(string value, string file, int depth)
    {
        if (depth <= 0)
        {
            return;
        }

        string directory = Path.GetDirectoryName(file) is { Length: > 0 } beside
            ? beside
            : Path.GetDirectoryName(DefaultPath)!;

        foreach (string pattern in value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            string expanded = Expand(pattern);
            string root = Path.IsPathRooted(expanded) ? expanded : Path.Combine(directory, expanded);
            string? folder = Path.GetDirectoryName(root);

            if (folder is null || !Directory.Exists(folder))
            {
                continue;
            }

            foreach (string included in Directory.EnumerateFiles(folder, Path.GetFileName(root))
                                                 .Order(StringComparer.Ordinal))
            {
                Read(included, depth - 1);
            }
        }
    }

    /// <summary>
    /// Records a directive this client will not act on.
    ///
    /// <para>The list is short on purpose: it names the ones a user would notice the absence of, and
    /// leaves alone the many that make no difference to a client that is not <c>ssh</c>.</para>
    /// </summary>
    private void Note(string keyword, string value, string file, int line)
    {
        string? why = keyword.ToLowerInvariant() switch
        {
            "proxycommand" =>
                "this client cannot run a command as a transport yet, so this host will not connect "
                + "through it — use ProxyJump where the command is a plain ssh, and QS118 otherwise",

            "localforward" or "remoteforward" or "dynamicforward" =>
                "port forwarding is not wired to the config yet, so this forward will not be made",

            "controlmaster" or "controlpath" or "controlpersist" =>
                "connection sharing is an OpenSSH feature and this client opens its own connections, "
                + "so this has no effect here",

            "requesttty" or "remotecommand" =>
                "this client always asks for a terminal and runs the login shell, so this is ignored",

            _ => null,
        };

        if (why is not null)
        {
            _unhonoured.Add(new UnhonouredDirective(file, line, keyword, value, why));
        }
    }
}
