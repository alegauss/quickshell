using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

using Quickshell.Transport;

namespace Quickshell.App;

/// <summary>
/// The agent gateway's spike (QS325): <c>quickshell --mcp</c>, a Model Context Protocol server on
/// standard input and output that lets an agent such as Claude Code run commands on the saved
/// sessions, over the client's own connections, instead of through an SSH key handed to it.
///
/// <para><b>Two tools and no policy yet.</b> <c>hosts_list</c> names the saved sessions and never a
/// secret; <c>exec</c> takes a session, an argument vector, an optional working directory and a
/// timeout, and answers the exit code, standard output and standard error apart. The argument vector
/// is quoted here, once, so a word reaches the program as it was given: no PowerShell, no local
/// <c>ssh</c>, no second shell to get the quoting wrong. A session's connection is kept and reused,
/// so the tenth command pays no handshake. What an agent may run is not limited by anything in this
/// file: that is the policy (QS322, QS323), which is why the gateway is off until the
/// <c>agentGateway</c> setting turns it on and is documented as unguarded until the policy lands.</para>
///
/// <para><b>The conditions QS321 set.</b> Its own process, so the window's figures do not move; no
/// listening port, only the pipe the agent's host started it with; nothing on the terminal's path.
/// It asks nobody anything: a host whose key is not already trusted is refused with the way to trust
/// it, and a session signs in with its key and the agents, never a typed password.</para>
/// </summary>
public sealed class AgentGateway : IAsyncDisposable
{
    /// <summary>The protocol revision answered where the agent's host names none.</summary>
    public const string Protocol = "2025-06-18";

    /// <summary>How long a command may run when the call names no timeout.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);

    /// <summary>The longest a call may ask for.</summary>
    public static readonly TimeSpan LongestTimeout = TimeSpan.FromMinutes(10);

    private static readonly JsonSerializerOptions Compact = new() { WriteIndented = false };

    private readonly Func<SessionTree> _store;
    private readonly Func<ResolvedSession, CancellationToken, ValueTask<IGatewayConnection>> _connect;
    private readonly string _version;
    private readonly Dictionary<string, IGatewayConnection> _open = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A gateway over a store, connecting through <paramref name="connect"/>.</summary>
    /// <param name="store">The saved sessions, read again at every call so an edit in the window is seen.</param>
    /// <param name="connect">Opens a connection to a session; <see cref="Connect(ResolvedSession, CancellationToken)"/> outside a test.</param>
    /// <param name="version">The client's version, which the handshake reports.</param>
    public AgentGateway(Func<SessionTree> store,
                        Func<ResolvedSession, CancellationToken, ValueTask<IGatewayConnection>> connect,
                        string version)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(connect);

        _store = store;
        _connect = connect;
        _version = version;
    }

    /// <summary>How many connections this gateway has opened, which a test reads to see one reused.</summary>
    public int Opened { get; private set; }

    /// <summary>
    /// Serves requests from <paramref name="input"/>, one JSON-RPC message a line, answering on
    /// <paramref name="output"/>, until the input ends.
    /// </summary>
    public async Task ServeAsync(TextReader input, TextWriter output, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);

        while (await input.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (line.Trim().Length == 0)
            {
                continue;
            }

            if (await AnswerAsync(line, cancellationToken).ConfigureAwait(false) is { } answer)
            {
                await output.WriteLineAsync(answer.ToJsonString(Compact)).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>The answer to one message, or null where it was a notification and owes none.</summary>
    public async Task<JsonObject?> AnswerAsync(string message, CancellationToken cancellationToken = default)
    {
        JsonObject request;

        try
        {
            request = JsonNode.Parse(message) as JsonObject ?? throw new JsonException("not an object");
        }
        catch (JsonException unreadable)
        {
            return Failure(null, -32700, $"The message is not JSON: {unreadable.Message}");
        }

        JsonNode? id = request["id"]?.DeepClone();
        string method = request["method"]?.GetValue<string>() ?? string.Empty;

        // A notification carries no id and is answered by nothing.
        if (id is null)
        {
            return null;
        }

        try
        {
            return method switch
            {
                "initialize" => Success(id, Initialize(request["params"] as JsonObject)),
                "ping" => Success(id, new JsonObject()),
                "tools/list" => Success(id, new JsonObject { ["tools"] = Tools() }),
                "tools/call" => Success(id, await CallAsync(request["params"] as JsonObject, cancellationToken).ConfigureAwait(false)),
                _ => Failure(id, -32601, $"There is no method {method} here."),
            };
        }
        catch (GatewayRefusal refused)
        {
            return Failure(id, -32602, refused.Message);
        }
    }

    private JsonObject Initialize(JsonObject? asked) => new()
    {
        ["protocolVersion"] = asked?["protocolVersion"]?.GetValue<string>() ?? Protocol,
        ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
        ["serverInfo"] = new JsonObject { ["name"] = "quickshell", ["version"] = _version },
        ["instructions"] =
            "Runs commands on the SSH sessions saved in quickshell. hosts_list names them; exec runs an argument "
            + "vector on one and answers its exit code, stdout and stderr apart. read_file and edit_file read a text "
            + "file there and change one exact piece of it, keeping the original beside it. No policy limits any of it yet.",
    };

    private static JsonArray Tools() =>
    [
        new JsonObject
        {
            ["name"] = "hosts_list",
            ["description"] = "The saved sessions: each one's path, host, port, user and tags. Never a password or a key.",
            ["inputSchema"] = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() },
        },
        new JsonObject
        {
            ["name"] = "exec",
            ["description"] = "Runs a program on a saved session over quickshell's own connection, which is kept for the next "
                              + "call. Takes the program and its arguments as an array, so nothing is quoted by hand; answers "
                              + "exitCode, stdout and stderr apart. The server's shell must be POSIX.",
            ["inputSchema"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["host"] = new JsonObject { ["type"] = "string", ["description"] = "The session's path, as hosts_list names it." },
                    ["argv"] = new JsonObject
                    {
                        ["type"] = "array",
                        ["items"] = new JsonObject { ["type"] = "string" },
                        ["minItems"] = 1,
                        ["description"] = "The program and its arguments, each exactly as the program should receive it.",
                    },
                    ["cwd"] = new JsonObject { ["type"] = "string", ["description"] = "The directory to run it in; the account's home where absent." },
                    ["timeoutSeconds"] = new JsonObject
                    {
                        ["type"] = "number",
                        ["description"] = string.Create(CultureInfo.InvariantCulture,
                            $"How long it may run, {DefaultTimeout.TotalSeconds:0} by default and {LongestTimeout.TotalSeconds:0} at most."),
                    },
                },
                ["required"] = new JsonArray("host", "argv"),
            },
        },
        new JsonObject
        {
            ["name"] = "read_file",
            ["description"] = "Reads a text file on a saved session over SFTP, each line led by its number and a tab. "
                              + "Refuses a binary file and one over a megabyte; give from and to for a range of lines.",
            ["inputSchema"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["host"] = new JsonObject { ["type"] = "string", ["description"] = "The session's path, as hosts_list names it." },
                    ["path"] = new JsonObject { ["type"] = "string", ["description"] = "The file's absolute path on the server." },
                    ["from"] = new JsonObject { ["type"] = "integer", ["description"] = "The first line to return, from 1." },
                    ["to"] = new JsonObject { ["type"] = "integer", ["description"] = "The last line to return." },
                },
                ["required"] = new JsonArray("host", "path"),
            },
        },
        new JsonObject
        {
            ["name"] = "edit_file",
            ["description"] = "Replaces the one occurrence of old_string in a text file on a saved session with new_string. "
                              + "Nothing is written where old_string occurs nowhere or more than once. The original is kept "
                              + "beside the file as <file>.qs-bak-<time>, and the answer carries the unified diff.",
            ["inputSchema"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["host"] = new JsonObject { ["type"] = "string", ["description"] = "The session's path, as hosts_list names it." },
                    ["path"] = new JsonObject { ["type"] = "string", ["description"] = "The file's absolute path on the server." },
                    ["old_string"] = new JsonObject { ["type"] = "string", ["description"] = "The exact text to replace, which must occur once." },
                    ["new_string"] = new JsonObject { ["type"] = "string", ["description"] = "What replaces it." },
                },
                ["required"] = new JsonArray("host", "path", "old_string", "new_string"),
            },
        },
    ];

    private async Task<JsonObject> CallAsync(JsonObject? call, CancellationToken cancellationToken)
    {
        string name = call?["name"]?.GetValue<string>() ?? throw new GatewayRefusal("A tool call names no tool.");
        JsonObject arguments = call["arguments"] as JsonObject ?? new JsonObject();

        try
        {
            JsonObject result = name switch
            {
                "hosts_list" => HostsList(),
                "exec" => await ExecAsync(arguments, cancellationToken).ConfigureAwait(false),
                "read_file" => await ReadFileAsync(arguments, cancellationToken).ConfigureAwait(false),
                "edit_file" => await EditFileAsync(arguments, cancellationToken).ConfigureAwait(false),
                _ => throw new GatewayRefusal($"There is no tool {name} here; hosts_list, exec, read_file and edit_file are."),
            };

            return Content(result, isError: false);
        }
        catch (SshException failed)
        {
            // The connection, not the call: said as a tool error the agent can read and report.
            return Content(new JsonObject { ["error"] = $"{failed.Message} {failed.Means}".Trim() }, isError: true);
        }
        catch (RemoteTextRefusal refused)
        {
            return Content(new JsonObject { ["error"] = refused.Message }, isError: true);
        }
        catch (GatewayRefusal refused) when (name is "exec" or "read_file" or "edit_file")
        {
            return Content(new JsonObject { ["error"] = refused.Message }, isError: true);
        }
    }

    private async Task<JsonObject> ReadFileAsync(JsonObject arguments, CancellationToken cancellationToken)
    {
        (IGatewayConnection connection, string path) = await FileAsync(arguments, cancellationToken).ConfigureAwait(false);
        IFileTransferChannel files = await connection.FilesAsync(cancellationToken).ConfigureAwait(false);

        RemoteRead read = await RemoteText.ReadAsync(files, path, arguments["from"]?.GetValue<int>(),
                                                     arguments["to"]?.GetValue<int>(), cancellationToken).ConfigureAwait(false);

        return new JsonObject { ["text"] = read.Text, ["lines"] = read.Lines };
    }

    private async Task<JsonObject> EditFileAsync(JsonObject arguments, CancellationToken cancellationToken)
    {
        (IGatewayConnection connection, string path) = await FileAsync(arguments, cancellationToken).ConfigureAwait(false);

        string old = arguments["old_string"]?.GetValue<string>() is { Length: > 0 } given
            ? given
            : throw new GatewayRefusal("edit_file needs old_string, the exact text to replace.");
        string replacement = arguments["new_string"]?.GetValue<string>()
            ?? throw new GatewayRefusal("edit_file needs new_string, what replaces it; empty deletes old_string.");

        IFileTransferChannel files = await connection.FilesAsync(cancellationToken).ConfigureAwait(false);
        RemoteTextEdit edit = await RemoteText.EditAsync(files, path, old, replacement, DateTimeOffset.Now, cancellationToken)
                                          .ConfigureAwait(false);

        return new JsonObject { ["diff"] = edit.Diff, ["backup"] = edit.Backup };
    }

    /// <summary>The session's connection and the absolute path a file tool named.</summary>
    private async Task<(IGatewayConnection Connection, string Path)> FileAsync(JsonObject arguments, CancellationToken cancellationToken)
    {
        string host = arguments["host"]?.GetValue<string>() is { Length: > 0 } named
            ? named
            : throw new GatewayRefusal("A file tool needs host, the session's path as hosts_list names it.");
        string path = arguments["path"]?.GetValue<string>() is { Length: > 0 } file && file.StartsWith('/')
            ? file
            : throw new GatewayRefusal("A file tool needs path, the file's absolute path on the server, starting with /.");

        ResolvedSession session = _store().Session(host)
            ?? throw new GatewayRefusal($"There is no saved session {host}. hosts_list names the ones there are.");

        return (await ConnectionAsync(session, cancellationToken).ConfigureAwait(false), path);
    }

    private JsonObject HostsList()
    {
        JsonArray hosts = [];

        foreach (ResolvedSession session in _store().Sessions())
        {
            hosts.Add(new JsonObject
            {
                ["name"] = session.Path,
                ["host"] = session.Host,
                ["port"] = session.Port?.Value ?? SshEndpoint.DefaultPort,
                ["user"] = session.User?.Value,
                ["tags"] = new JsonArray([.. session.Tags.Select(tag => (JsonNode?)JsonValue.Create(tag))]),
            });
        }

        return new JsonObject { ["hosts"] = hosts };
    }

    private async Task<JsonObject> ExecAsync(JsonObject arguments, CancellationToken cancellationToken)
    {
        string path = arguments["host"]?.GetValue<string>() is { Length: > 0 } named
            ? named
            : throw new GatewayRefusal("exec needs host, the session's path as hosts_list names it.");

        string[] argv = arguments["argv"] is JsonArray words && words.Count > 0
            ? [.. words.Select(word => word?.GetValue<string>() ?? throw new GatewayRefusal("Every word of argv must be a string."))]
            : throw new GatewayRefusal("exec needs argv, the program and its arguments as an array of strings.");

        string? cwd = arguments["cwd"]?.GetValue<string>();
        TimeSpan timeout = arguments["timeoutSeconds"]?.GetValue<double>() is { } seconds and > 0
            ? TimeSpan.FromSeconds(Math.Min(seconds, LongestTimeout.TotalSeconds))
            : DefaultTimeout;

        ResolvedSession session = _store().Session(path)
            ?? throw new GatewayRefusal($"There is no saved session {path}. hosts_list names the ones there are.");

        IGatewayConnection connection = await ConnectionAsync(session, cancellationToken).ConfigureAwait(false);
        // On a POSIX server, nothing in the command can wait for a person (QS327). A Windows server's
        // shell is not POSIX, so it gets the line alone until QS356 quotes for it.
        string line = Argv.ForPosix(argv, cwd);
        string command = CommandPrompts.IsWindows(connection.ServerVersion) ? line : CommandPrompts.PosixPreamble + line;

        CommandResult ran = await connection.RunAsync(command, timeout, cancellationToken).ConfigureAwait(false);
        CommandPrompt? asked = CommandPrompts.Recognise(ran);

        return new JsonObject
        {
            ["exitCode"] = ran.ExitCode,
            ["stdout"] = ran.Output,
            ["stderr"] = ran.Error,
            ["timedOut"] = ran.TimedOut,
            ["prompt"] = asked?.Name,
            ["hint"] = asked?.Hint,
        };
    }

    /// <summary>The session's kept connection, or a new one where there is none or it dropped.</summary>
    private async ValueTask<IGatewayConnection> ConnectionAsync(ResolvedSession session, CancellationToken cancellationToken)
    {
        if (_open.TryGetValue(session.Path, out IGatewayConnection? kept))
        {
            if (kept.IsConnected)
            {
                return kept;
            }

            _open.Remove(session.Path);
            await kept.DisposeAsync().ConfigureAwait(false);
        }

        IGatewayConnection opened = await _connect(session, cancellationToken).ConfigureAwait(false);

        Opened++;
        _open[session.Path] = opened;

        return opened;
    }

    /// <summary>
    /// A connection to a saved session as the window would make it, asking nobody anything: its key
    /// and the agents, and a host key the user's own known_hosts already trusts.
    /// </summary>
    public static ValueTask<IGatewayConnection> Connect(ResolvedSession session, CancellationToken cancellationToken) =>
        Connect(session, new TrustOnFirstUse(KnownHosts.ReadFrom(), (_, _) => ValueTask.FromResult(SshHostKeyVerdict.Refuse)),
                cancellationToken);

    /// <summary>The same, over a host-key check of the caller's, which is how a test brings its own known_hosts.</summary>
    public static async ValueTask<IGatewayConnection> Connect(ResolvedSession session, TrustOnFirstUse trust,
                                                              CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(trust);

        string user = session.User?.Value ?? Environment.UserName;
        SshEndpoint target = SshEndpoint.For(session.Host, user, session.Port?.Value ?? SshEndpoint.DefaultPort);
        IReadOnlyList<SshCredential> keys = RemoteShell.Credentials(session);

        ISshTransport transport = session.JumpHost is { } jump
            ? new SshChain([.. RemoteShell.Hops(jump.Value, user).Select(hop => new SshHop(hop, keys, trust.CheckAsync)),
                            new SshHop(target, keys, trust.CheckAsync)])
            : new SshNetTransport { KeepAlive = TimeSpan.FromSeconds(15) };

        try
        {
            await transport.ConnectAsync(target, keys, trust.CheckAsync, cancellationToken).ConfigureAwait(false);
        }
        catch (SshException refused) when (trust.Last is { Verdict: KnownHostVerdict.Unknown })
        {
            await transport.DisposeAsync().ConfigureAwait(false);

            throw new SshException(refused.Kind, $"{target.Host}'s host key is not trusted yet.",
                                   "The gateway asks nobody, so it refuses a key it has not seen. Connect to the session once "
                                   + "from the quickshell window and remember its key, then call again.");
        }
        catch
        {
            await transport.DisposeAsync().ConfigureAwait(false);

            throw;
        }

        return new Kept(transport);
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        foreach (IGatewayConnection connection in _open.Values)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }

        _open.Clear();
    }

    private static JsonObject Content(JsonObject structured, bool isError) => new()
    {
        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = structured.ToJsonString(Compact) }),
        ["structuredContent"] = structured,
        ["isError"] = isError,
    };

    private static JsonObject Success(JsonNode id, JsonObject result) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id,
        ["result"] = result,
    };

    private static JsonObject Failure(JsonNode? id, int code, string message) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id,
        ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
    };

    /// <summary>A transport that can run a command, kept by the gateway between calls.</summary>
    private sealed class Kept(ISshTransport transport) : IGatewayConnection
    {
        private IFileTransferChannel? _files;

        public bool IsConnected => transport.IsConnected;

        public string? ServerVersion => ((ICommandChannel)transport).ServerVersion;

        public ValueTask<CommandResult> RunAsync(string command, TimeSpan timeout, CancellationToken cancellationToken = default) =>
            ((ICommandChannel)transport).RunAsync(command, timeout, cancellationToken);

        public async ValueTask<IFileTransferChannel> FilesAsync(CancellationToken cancellationToken = default) =>
            _files ??= await transport.OpenFileTransferAsync(cancellationToken).ConfigureAwait(false);

        public async ValueTask DisposeAsync()
        {
            if (_files is { } files)
            {
                await files.DisposeAsync().ConfigureAwait(false);
            }

            await transport.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>A call the gateway will not make, said in words the agent can act on.</summary>
    private sealed class GatewayRefusal(string message) : Exception(message);
}

/// <summary>One kept connection the gateway runs commands over; a fake in a test.</summary>
public interface IGatewayConnection : ICommandChannel, IAsyncDisposable
{
    /// <summary>Whether it is still up, so a dropped one is replaced rather than used.</summary>
    bool IsConnected { get; }

    /// <summary>The connection's file channel, opened on first use and kept with it (QS326).</summary>
    ValueTask<IFileTransferChannel> FilesAsync(CancellationToken cancellationToken = default);
}
