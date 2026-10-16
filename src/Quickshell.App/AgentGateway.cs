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
    private readonly OutputSpool _spool = new();

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
                    ["budget"] = BudgetProperty(),
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
        Tool("job_start", "Starts a long command detached on a saved session and returns its job id at once. The job outlives "
                          + "this call, the connection and the gateway; its state and output stay on the server under "
                          + "~/.quickshell/jobs. POSIX servers only.",
             new JsonObject
             {
                 ["host"] = HostProperty(),
                 ["argv"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" }, ["minItems"] = 1,
                                             ["description"] = "The program and its arguments, each exactly as the program should receive it." },
                 ["cwd"] = new JsonObject { ["type"] = "string", ["description"] = "The directory to run it in; the account's home where absent." },
             }, "host", "argv"),
        Tool("job_status", "Whether a job is running, exited (with its exit code), cancelled, or lost.",
             new JsonObject { ["host"] = HostProperty(), ["job"] = JobProperty() }, "host", "job"),
        Tool("job_output", "A job's output from a cursor on, up to 64 KB, and the cursor to pass next time, so nothing is read twice.",
             new JsonObject
             {
                 ["host"] = HostProperty(),
                 ["job"] = JobProperty(),
                 ["cursor"] = new JsonObject { ["type"] = "integer", ["description"] = "Where to start: 0 at first, then the cursor the last call returned." },
                 ["stream"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("stdout", "stderr"), ["description"] = "stdout by default." },
             }, "host", "job"),
        Tool("job_cancel", "Ends a job: TERM to it and everything it started, then KILL after five seconds.",
             new JsonObject { ["host"] = HostProperty(), ["job"] = JobProperty() }, "host", "job"),
        Tool("output_page", "Lines of an output exec cut to fit its budget, numbered, from the gateway's copy: nothing runs again.",
             new JsonObject
             {
                 ["output"] = OutputProperty(),
                 ["stream"] = StreamProperty(),
                 ["from"] = new JsonObject { ["type"] = "integer", ["description"] = "The first line, from 1." },
                 ["to"] = new JsonObject { ["type"] = "integer", ["description"] = "The last line; as many as fit the budget where absent." },
                 ["budget"] = BudgetProperty(),
             }, "output", "from"),
        Tool("output_grep", "The lines of a cut output matching a .NET regular expression, numbered, at most 200, from the gateway's copy.",
             new JsonObject
             {
                 ["output"] = OutputProperty(),
                 ["stream"] = StreamProperty(),
                 ["pattern"] = new JsonObject { ["type"] = "string", ["description"] = "What to look for." },
             }, "output", "pattern"),
    ];

    private static JsonObject BudgetProperty() => new()
    {
        ["type"] = "integer",
        ["description"] = string.Create(CultureInfo.InvariantCulture,
            $"The most bytes of each stream to return, {OutputSpool.DefaultBudget} by default and {OutputSpool.LargestBudget} at most. Over it, the head and the tail are kept and the cut is named."),
    };

    private static JsonObject OutputProperty() => new() { ["type"] = "string", ["description"] = "The output id an exec answer carried." };

    private static JsonObject StreamProperty() =>
        new() { ["type"] = "string", ["enum"] = new JsonArray("stdout", "stderr"), ["description"] = "stdout by default." };

    private static int Budget(JsonObject arguments) =>
        arguments["budget"]?.GetValue<int>() is { } asked and > 0 ? Math.Min(asked, OutputSpool.LargestBudget) : OutputSpool.DefaultBudget;

    private static JsonObject Tool(string name, string description, JsonObject properties, params string[] required) => new()
    {
        ["name"] = name,
        ["description"] = description,
        ["inputSchema"] = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = new JsonArray([.. required.Select(each => (JsonNode?)JsonValue.Create(each))]),
        },
    };

    private static JsonObject HostProperty() => new() { ["type"] = "string", ["description"] = "The session's path, as hosts_list names it." };

    private static JsonObject JobProperty() => new() { ["type"] = "string", ["description"] = "The id job_start returned." };

    /// <summary>Where every call is recorded (QS333); null records nothing, as in a test that does not ask.</summary>
    public AuditLog? Audit { get; init; }

    /// <summary>
    /// The file whose presence means the person stopped every agent (QS334); null where nothing can stop
    /// this gateway, as in a test that does not ask.
    /// </summary>
    public string? StopFile { get; init; }

    /// <summary>What a person watching sees: each call as terminal output, which the window shows as the agent's tab (QS335).</summary>
    public AgentFeed? Feed { get; init; }

    /// <summary>One call or one stop at a time, so a stop never closes a connection under a call it did not cancel.</summary>
    private readonly SemaphoreSlim _turn = new(1, 1);

    /// <summary>Cancelled by a stop, so a call in flight ends now rather than when it would have.</summary>
    private CancellationTokenSource _calls = new();

    private bool _swept;

    /// <summary>Whether the person has stopped the agents, by the file's presence.</summary>
    public bool Stopped => StopFile is { } file && File.Exists(file);

    /// <summary>
    /// The kill switch (QS334): ends any call in flight, cancels every job still running on each session
    /// this gateway holds, closes every connection, and records it. Afterwards every call is refused
    /// until the stop file is gone.
    /// </summary>
    /// <returns>Each job cancelled, as <c>session: id</c>.</returns>
    public async Task<IReadOnlyList<string>> StopAsync()
    {
        await _calls.CancelAsync().ConfigureAwait(false);
        await _turn.WaitAsync().ConfigureAwait(false);

        try
        {
            return await SweepAsync().ConfigureAwait(false);
        }
        finally
        {
            _turn.Release();
        }
    }

    private async Task<IReadOnlyList<string>> SweepAsync()
    {
        List<string> cancelled = [];

        foreach ((string session, IGatewayConnection connection) in _open)
        {
            try
            {
                using CancellationTokenSource limit = new(TimeSpan.FromSeconds(40));

                cancelled.AddRange((await RemoteJobs.CancelAllAsync(connection, limit.Token).ConfigureAwait(false)).Select(job => $"{session}: {job}"));
            }
            catch (Exception failed) when (failed is SshException or OperationCanceledException)
            {
                cancelled.Add($"{session}: its jobs could not be reached to cancel ({failed.Message})");
            }

            await connection.DisposeAsync().ConfigureAwait(false);
        }

        _open.Clear();
        _swept = true;

        if (_calls.IsCancellationRequested)
        {
            _calls.Dispose();
            _calls = new CancellationTokenSource();
        }

        Audit?.Record(DateTimeOffset.Now, "stop", null, new JsonObject(),
                      new JsonObject { ["cancelled"] = new JsonArray([.. cancelled.Select(each => (JsonNode?)JsonValue.Create(each))]) },
                      failed: false);

        return cancelled;
    }

    private const string StoppedReason =
        "The person stopped every agent from quickshell, so nothing runs through the gateway until they let it run again. "
        + "Report this to them rather than retrying.";

    private async Task<JsonObject> CallAsync(JsonObject? call, CancellationToken cancellationToken)
    {
        string name = call?["name"]?.GetValue<string>() ?? throw new GatewayRefusal("A tool call names no tool.");
        JsonObject arguments = call["arguments"] as JsonObject ?? new JsonObject();

        JsonObject answer;

        Feed?.Begin(name, arguments);

        await _turn.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (Stopped)
            {
                if (!_swept)
                {
                    await SweepAsync().ConfigureAwait(false);
                }

                answer = Content(new JsonObject { ["error"] = StoppedReason, ["stopped"] = true }, isError: true);
            }
            else
            {
                _swept = false;

                using CancellationTokenSource either = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _calls.Token);

                try
                {
                    answer = await AnsweredAsync(name, arguments, either.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_calls.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    answer = Content(new JsonObject { ["error"] = StoppedReason, ["stopped"] = true }, isError: true);
                }
            }
        }
        finally
        {
            _turn.Release();
        }

        Feed?.End((JsonObject)answer["structuredContent"]!, answer["isError"]!.GetValue<bool>());

        if (Audit is { } audit)
        {
            ResolvedSession? session = arguments["host"] is JsonValue host && host.TryGetValue(out string? path) ? _store().Session(path) : null;

            audit.Record(DateTimeOffset.Now, name, session, arguments, (JsonObject)answer["structuredContent"]!,
                         answer["isError"]!.GetValue<bool>());
        }

        return answer;
    }

    private async Task<JsonObject> AnsweredAsync(string name, JsonObject arguments, CancellationToken cancellationToken)
    {
        try
        {
            JsonObject result = name switch
            {
                "hosts_list" => HostsList(),
                "exec" => await ExecAsync(arguments, cancellationToken).ConfigureAwait(false),
                "read_file" => await ReadFileAsync(arguments, cancellationToken).ConfigureAwait(false),
                "edit_file" => await EditFileAsync(arguments, cancellationToken).ConfigureAwait(false),
                "job_start" or "job_status" or "job_output" or "job_cancel" => await JobAsync(name, arguments, cancellationToken).ConfigureAwait(false),
                "output_page" => OutputPage(arguments),
                "output_grep" => OutputGrep(arguments),
                _ => throw new GatewayRefusal($"There is no tool {name} here; tools/list names the ones there are."),
            };

            // Every answer through the redactor before it leaves the process, and the count beside it,
            // so the agent knows a value was hidden (QS330).
            if (SecretRedactor.RedactAll(result) is var hidden and > 0)
            {
                result["redacted"] = hidden;
            }

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
        catch (OutputRefusal refused)
        {
            return Content(new JsonObject { ["error"] = refused.Message }, isError: true);
        }
        catch (JobRefusal refused)
        {
            return Content(new JsonObject { ["error"] = refused.Message }, isError: true);
        }
        catch (GatewayRefusal refused) when (name is not "hosts_list")
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

    private JsonObject OutputPage(JsonObject arguments)
    {
        (string id, string stream) = KeptOutput(arguments);
        int from = arguments["from"]?.GetValue<int>() ?? 1;
        int to = arguments["to"]?.GetValue<int>() ?? int.MaxValue;

        (string text, int next, int lines) = _spool.Page(id, stream, from, to, Budget(arguments));

        return new JsonObject { ["text"] = text, ["next"] = next == 0 ? null : next, ["lines"] = lines };
    }

    private JsonObject OutputGrep(JsonObject arguments)
    {
        (string id, string stream) = KeptOutput(arguments);
        string pattern = arguments["pattern"]?.GetValue<string>() is { Length: > 0 } given
            ? given
            : throw new GatewayRefusal("output_grep needs pattern, what to look for.");

        (string text, int matches, bool more) = _spool.Grep(id, stream, pattern);

        return new JsonObject { ["text"] = text, ["matches"] = matches, ["more"] = more };
    }

    private static (string Id, string Stream) KeptOutput(JsonObject arguments) =>
        (arguments["output"]?.GetValue<string>() ?? throw new GatewayRefusal("This needs output, the id an exec answer carried."),
         arguments["stream"]?.GetValue<string>() ?? "stdout");

    private async Task<JsonObject> JobAsync(string tool, JsonObject arguments, CancellationToken cancellationToken)
    {
        string host = arguments["host"]?.GetValue<string>() is { Length: > 0 } named
            ? named
            : throw new GatewayRefusal("A job tool needs host, the session's path as hosts_list names it.");

        ResolvedSession session = _store().Session(host)
            ?? throw new GatewayRefusal($"There is no saved session {host}. hosts_list names the ones there are.");

        IGatewayConnection connection = await ConnectionAsync(session, cancellationToken).ConfigureAwait(false);

        if (tool == "job_start")
        {
            string[] argv = arguments["argv"] is JsonArray words && words.Count > 0
                ? [.. words.Select(word => word?.GetValue<string>() ?? throw new GatewayRefusal("Every word of argv must be a string."))]
                : throw new GatewayRefusal("job_start needs argv, the program and its arguments as an array of strings.");

            string id = await RemoteJobs.StartAsync(connection, argv, arguments["cwd"]?.GetValue<string>(), cancellationToken).ConfigureAwait(false);

            return new JsonObject { ["job"] = id };
        }

        string job = arguments["job"]?.GetValue<string>() ?? throw new GatewayRefusal($"{tool} needs job, the id job_start returned.");

        if (tool == "job_output")
        {
            JobOutput output = await RemoteJobs.OutputAsync(connection, job, arguments["stream"]?.GetValue<string>() ?? "stdout",
                                                            arguments["cursor"]?.GetValue<long>() ?? 0, cancellationToken).ConfigureAwait(false);

            return new JsonObject { ["text"] = output.Text, ["cursor"] = output.Cursor, ["more"] = output.More };
        }

        JobStatus status = tool == "job_cancel"
            ? await RemoteJobs.CancelAsync(connection, job, cancellationToken).ConfigureAwait(false)
            : await RemoteJobs.StatusAsync(connection, job, cancellationToken).ConfigureAwait(false);

        return new JsonObject { ["state"] = status.State, ["exitCode"] = status.ExitCode };
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

        // Within its budget the output comes back whole; over it, each stream keeps its two ends and
        // the whole of it is kept here for output_page and output_grep (QS329).
        int budget = Budget(arguments);
        bool over = System.Text.Encoding.UTF8.GetByteCount(ran.Output) > budget || System.Text.Encoding.UTF8.GetByteCount(ran.Error) > budget;
        string? kept = over ? _spool.Keep(ran.Output, ran.Error) : null;
        Budgeted stdout = OutputSpool.Fit(ran.Output, budget, kept, "stdout");
        Budgeted stderr = OutputSpool.Fit(ran.Error, budget, kept, "stderr");

        JsonObject answer = new()
        {
            ["exitCode"] = ran.ExitCode,
            ["stdout"] = stdout.Text,
            ["stderr"] = stderr.Text,
            ["timedOut"] = ran.TimedOut,
            ["prompt"] = asked?.Name,
            ["hint"] = asked?.Hint,
        };

        if (kept is not null)
        {
            answer["output"] = kept;
            answer["cut"] = new JsonObject
            {
                ["stdout"] = new JsonObject { ["lines"] = stdout.CutLines, ["bytes"] = stdout.CutBytes, ["of"] = stdout.Lines },
                ["stderr"] = new JsonObject { ["lines"] = stderr.CutLines, ["bytes"] = stderr.CutBytes, ["of"] = stderr.Lines },
            };
        }

        return answer;
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
