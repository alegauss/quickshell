using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Quickshell.App;

/// <summary>One command the allowlist names as a read, and what would make it write.</summary>
/// <param name="Command">The program's name, as the agent's argument vector gives it, without a path.</param>
/// <param name="Subcommands">The subcommands that are reads, or null where any is.</param>
/// <param name="Writes">The arguments that make it write, whole or, ending in <c>*</c>, as a prefix.</param>
/// <param name="Bare">Whether it is a read only with no arguments at all.</param>
public sealed record ReadCommand(string Command, IReadOnlyList<string>? Subcommands, IReadOnlyList<string> Writes, bool Bare);

/// <summary>
/// Whether a command an agent asks to run is a read (QS323, built by QS336): a read only when it can be
/// shown to be one, and a mutation in every other case, uncertain included. The number that must be
/// zero is the false allow; a false ask is friction, and the corpus test counts it.
///
/// <para><b>An argument vector first.</b> <c>exec</c> takes the program and its arguments, so most
/// calls need no shell at all: the vector is unwrapped through <c>sudo</c>, <c>env</c>,
/// <c>timeout</c> and <c>nice</c> as narrowly as the design allows, and its program looked up in
/// <c>agent-reads.json</c>. Interpreters and stream editors are never reads, whatever their arguments.</para>
///
/// <para><b>A shell string only through a narrow parser.</b> <c>sh -c '…'</c> is the one way to a pipe,
/// so its script is parsed, and only plain and quoted words, <c>$NAME</c> as a whole argument, pipes,
/// <c>&amp;&amp;</c>, <c>||</c>, <c>;</c> and <c>2&gt;/dev/null</c> are understood. Anything else is
/// uncertain, so the script is a mutation. A real shell accepts far more, and that is the point.</para>
/// </summary>
public sealed class CommandJudge
{
    /// <summary>Programs whose behaviour is in their argument, so never a read.</summary>
    private static readonly HashSet<string> Interpreters = new(StringComparer.Ordinal)
    {
        "sh", "bash", "dash", "zsh", "ksh", "fish", "csh", "tcsh", "busybox", "python", "python2", "python3", "perl", "ruby",
        "node", "php", "lua", "awk", "gawk", "mawk", "nawk", "sed", "tee", "xargs", "nohup", "setsid", "watch", "eval", "exec",
        "source", ".", "vi", "vim", "nano", "emacs", "ed", "ex", "script", "expect", "pwsh", "powershell",
    };

    private static readonly HashSet<string> Shells = new(StringComparer.Ordinal) { "sh", "bash", "dash" };

    private readonly Dictionary<string, ReadCommand> _reads;

    private CommandJudge(Dictionary<string, ReadCommand> reads) => _reads = reads;

    /// <summary>The allowlist shipped with the client.</summary>
    public static CommandJudge Shipped { get; } = Load(Shipped_());

    /// <summary>The shipped allowlist with <paramref name="extra"/> reads added, as a policy rule adds them.</summary>
    public CommandJudge With(IEnumerable<ReadCommand> extra)
    {
        Dictionary<string, ReadCommand> reads = new(_reads, StringComparer.Ordinal);

        foreach (ReadCommand read in extra)
        {
            reads[read.Command] = read;
        }

        return new CommandJudge(reads);
    }

    /// <summary>Whether the argument vector is certainly a read.</summary>
    public bool IsRead(IReadOnlyList<string> argv)
    {
        ArgumentNullException.ThrowIfNull(argv);

        List<string>? command = Unwrapped(argv);

        if (command is null || command.Count == 0)
        {
            return false;
        }

        string program = Program(command[0]);

        // sh -c '<script>' and nothing else: the script parsed, and every command in it a read.
        if (Shells.Contains(program))
        {
            return command.Count == 3 && command[1] == "-c" && Parse(command[2]) is { } commands
                   && commands.Count > 0 && commands.All(IsRead);
        }

        if (Interpreters.Contains(program) || !_reads.TryGetValue(program, out ReadCommand? read))
        {
            return false;
        }

        string[] arguments = [.. command.Skip(1)];

        // A variable or a glob can expand to a writing option or a subcommand nobody wrote, so where
        // either is checked, an argument that expands is uncertain.
        if ((read.Writes.Count > 0 || read.Subcommands is not null)
            && arguments.Any(argument => argument.IndexOfAny(['$', '*', '?', '[']) >= 0))
        {
            return false;
        }

        if (read.Bare)
        {
            return arguments.Length == 0;
        }

        if (read.Subcommands is { } subcommands)
        {
            string? sub = subcommands.Any(each => each.StartsWith('-'))
                ? arguments.FirstOrDefault()
                : arguments.FirstOrDefault(each => !each.StartsWith('-'));

            if (sub is null || !subcommands.Contains(sub, StringComparer.Ordinal))
            {
                return false;
            }
        }

        return !arguments.Any(argument => read.Writes.Any(write => Writes(argument, write)));
    }

    /// <summary>
    /// The command a wrapper runs, unwrapped as narrowly as QS323 allows; null where a wrapper is used in a
    /// way that makes the command uncertain.
    /// </summary>
    private static List<string>? Unwrapped(IReadOnlyList<string> argv)
    {
        List<string> rest = [.. argv];

        while (rest.Count > 0)
        {
            switch (Program(rest[0]))
            {
                case "sudo":
                    rest.RemoveAt(0);

                    while (rest.Count > 0 && rest[0].StartsWith('-'))
                    {
                        if (rest[0] == "-n")
                        {
                            rest.RemoveAt(0);
                        }
                        else if (rest[0] == "-u" && rest.Count > 1)
                        {
                            rest.RemoveRange(0, 2);
                        }
                        else
                        {
                            return null;
                        }
                    }

                    break;

                case "env":
                    rest.RemoveAt(0);

                    while (rest.Count > 0 && rest[0].Contains('=', StringComparison.Ordinal) && !rest[0].StartsWith('-'))
                    {
                        string name = rest[0][..rest[0].IndexOf('=', StringComparison.Ordinal)];

                        if (name is "PATH" or "LD_PRELOAD" or "LD_LIBRARY_PATH" || name.StartsWith("BASH_", StringComparison.Ordinal))
                        {
                            return null;
                        }

                        rest.RemoveAt(0);
                    }

                    if (rest.Count > 0 && rest[0].StartsWith('-'))
                    {
                        return null;
                    }

                    break;

                case "timeout":
                    // timeout <duration> <command>, with no option.
                    if (rest.Count < 3 || rest[1].StartsWith('-'))
                    {
                        return null;
                    }

                    rest.RemoveRange(0, 2);

                    break;

                case "nice":
                    rest.RemoveAt(0);

                    if (rest.Count > 1 && rest[0] == "-n")
                    {
                        rest.RemoveRange(0, 2);
                    }
                    else if (rest.Count > 0 && rest[0].StartsWith('-'))
                    {
                        return null;
                    }

                    break;

                default:
                    return rest;
            }
        }

        return rest;
    }

    /// <summary>
    /// The simple commands of a shell script written in the narrow subset, or null where anything in it
    /// is outside the subset.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<string>>? Parse(string script)
    {
        ArgumentNullException.ThrowIfNull(script);

        List<IReadOnlyList<string>> commands = [];
        List<string> words = [];
        int at = 0;

        bool Finish()
        {
            if (words.Count == 0)
            {
                return false;
            }

            // A program must be a plain name: no variable, glob or assignment in the command position.
            if (words[0].StartsWith('$') || words[0].IndexOfAny(['*', '?', '[', '=']) >= 0)
            {
                return false;
            }

            commands.Add([.. words]);
            words.Clear();

            return true;
        }

        while (at < script.Length)
        {
            char letter = script[at];

            if (letter is ' ' or '\t')
            {
                at++;

                continue;
            }

            if (letter is ';' or '|' or '&')
            {
                string op = letter == ';' ? ";"
                    : at + 1 < script.Length && script[at + 1] == letter ? new string(letter, 2)
                    : letter == '|' ? "|" : "&";

                if (op == "&" || !Finish())
                {
                    return null;
                }

                at += op.Length;

                continue;
            }

            if (script.AsSpan(at).StartsWith("2>/dev/null", StringComparison.Ordinal)
                && (at + 11 == script.Length || script[at + 11] is ' ' or ';' or '|' or '&'))
            {
                at += 11;

                continue;
            }

            if (Word(script, ref at) is not { } word)
            {
                return null;
            }

            words.Add(word);
        }

        return Finish() ? commands : null;
    }

    /// <summary>One word: plain, single-quoted, double-quoted with nothing that expands, or $NAME alone.</summary>
    private static string? Word(string script, ref int at)
    {
        StringBuilder word = new();

        while (at < script.Length && script[at] is not (' ' or '\t' or ';' or '|' or '&'))
        {
            char letter = script[at];

            switch (letter)
            {
                case '\'':
                    int close = script.IndexOf('\'', at + 1);

                    if (close < 0)
                    {
                        return null;
                    }

                    word.Append(script, at + 1, close - at - 1);
                    at = close + 1;

                    break;

                case '"':
                    int end = script.IndexOf('"', at + 1);

                    if (end < 0)
                    {
                        return null;
                    }

                    string inside = script[(at + 1)..end];

                    if (inside.Contains('$', StringComparison.Ordinal) || inside.Contains('`', StringComparison.Ordinal)
                        || inside.Contains('\\', StringComparison.Ordinal))
                    {
                        return null;
                    }

                    word.Append(inside);
                    at = end + 1;

                    break;

                case '$':
                    // $NAME as the whole word, never inside one and never ${…} or $(…).
                    int name = at + 1;

                    while (name < script.Length && (char.IsAsciiLetterOrDigit(script[name]) || script[name] == '_'))
                    {
                        name++;
                    }

                    if (word.Length > 0 || name == at + 1 || (name < script.Length && script[name] is not (' ' or '\t' or ';' or '|' or '&')))
                    {
                        return null;
                    }

                    word.Append(script, at, name - at);
                    at = name;

                    break;

                case '`' or '(' or ')' or '{' or '}' or '<' or '>' or '\\' or '!' or '\n' or '\r' or '#' or '~':
                    return null;

                default:
                    word.Append(letter);
                    at++;

                    break;
            }
        }

        return word.ToString();
    }

    private static bool Writes(string argument, string write) =>
        write.EndsWith('*')
            ? argument.StartsWith(write[..^1], StringComparison.Ordinal)
            : argument == write || argument.StartsWith(write + "=", StringComparison.Ordinal);

    private static string Program(string word) => word.Replace('\\', '/').Split('/')[^1];

    /// <summary>Reads an allowlist in agent-reads.json's shape.</summary>
    public static CommandJudge Load(string json)
    {
        JsonNode root = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })
                        ?? throw new InvalidDataException("the allowlist is empty");

        return new CommandJudge((root["reads"] as JsonArray ?? []).Select(Read).ToDictionary(read => read.Command, StringComparer.Ordinal));
    }

    /// <summary>One entry of an allowlist, as the shipped one and a policy rule's spell it.</summary>
    public static ReadCommand Read(JsonNode? entry) =>
        new(entry?["command"]?.GetValue<string>() ?? throw new InvalidDataException("a read names no command"),
            entry["subcommands"] is JsonArray subs ? [.. subs.Select(sub => sub!.GetValue<string>())] : null,
            entry["writes"] is JsonArray writes ? [.. writes.Select(write => write!.GetValue<string>())] : [],
            entry["bare"]?.GetValue<bool>() == true);

    private static string Shipped_()
    {
        using Stream stream = typeof(CommandJudge).Assembly.GetManifestResourceStream("Quickshell.App.agent-reads.json")
                              ?? throw new InvalidOperationException("agent-reads.json is not built into the client");
        using StreamReader reader = new(stream);

        return reader.ReadToEnd();
    }
}
