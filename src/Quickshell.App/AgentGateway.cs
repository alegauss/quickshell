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
/// <c>agentGateway</c> setting turns it on, and every call that reaches a server is judged by the
/// policy before it runs (QS336).</para>
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
            + "file there and change one exact piece of it, keeping the original beside it. Every call is judged by the "
            + "person's policy first: a refusal names its rule; report it rather than working around it.",
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
        Tool("plan", "Several calls (exec, edit_file, job_start, read_file) judged together and, where the policy asks, approved by the "
                     + "person in quickshell once, as a batch bound to its hash; then run in order, stopping at the first that fails. "
                     + "Prefer one plan to many asks.",
             new JsonObject
             {
                 ["calls"] = new JsonObject
                 {
                     ["type"] = "array",
                     ["items"] = new JsonObject
                     {
                         ["type"] = "object",
                         ["properties"] = new JsonObject
                         {
                             ["tool"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("exec", "edit_file", "job_start", "read_file") },
                             ["arguments"] = new JsonObject { ["type"] = "object", ["description"] = "The tool's own arguments." },
                         },
                         ["required"] = new JsonArray("tool", "arguments"),
                     },
                 },
             }, "calls"),
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

    /// <summary>The agent policy's file (QS336), read at every call so a change applies at once; null judges by the policy with no file.</summary>
    public string? PolicyFile { get; init; }

    /// <summary>The clock grants are judged against: now, unless a test says otherwise.</summary>
    public Func<DateTimeOffset> Clock { get; init; } = () => DateTimeOffset.Now;

    /// <summary>
    /// The policy's verdict on a call that reaches a session, or null for one that does not
    /// (<c>hosts_list</c>, and the kept output, which came from calls already judged).
    /// </summary>
    private PolicyVerdict? Judge(string tool, JsonObject arguments)
    {
        if (tool is "hosts_list" or "output_page" or "output_grep"
            || arguments["host"] is not JsonValue host || !host.TryGetValue(out string? path)
            || _store().Session(path) is not { } session)
        {
            return null;
        }

        AgentPolicy policy = PolicyFile is { } file ? AgentPolicy.Load(file) : AgentPolicy.None;
        IReadOnlyList<string>? argv = tool is "exec" or "job_start" && arguments["argv"] is JsonArray words
            ? [.. words.Select(word => word?.ToString() ?? string.Empty)]
            : null;

        // A cancel only ends what the agent started, so it is judged as a read; an edit is a mutation.
        PolicyVerdict judged = policy.Judge(session, argv, read: tool is not "edit_file", Clock());

        // After untrusted text came in, a mutation the policy or a grant would allow asks the person
        // instead (QS338): reads stay automatic, so the cost falls only where the risk is.
        return judged is { Verdict: Verdict.Allow, Read: false } && Taint is { } why
            ? judged with { Verdict = Verdict.Ask, Rule = $"{judged.Rule}, but this agent read untrusted text ({why}), so every change asks" }
            : judged;
    }

    /// <summary>Where requests for the person's approval are written for the window (QS337); null asks nobody.</summary>
    public string? AsksFolder { get; init; }

    /// <summary>How the person is asked: the window through <see cref="AsksFolder"/>, unless a test answers instead.</summary>
    public Func<ApprovalRequest, CancellationToken, Task<ApprovalAnswer>>? Asks { get; init; }

    private static JsonObject Refused(PolicyVerdict judged) => Content(new JsonObject
    {
        ["error"] = $"Refused by the agent policy: {judged.Rule}.",
        ["verdict"] = "refuse",
        ["rule"] = judged.Rule,
    }, isError: true);

    /// <summary>
    /// Asks the person about a batch (QS337) and, where they approve it, runs it as asked, in order,
    /// stopping at the first call that fails; what they decided goes to the audit with the batch's hash.
    /// </summary>
    private async Task<(JsonObject Answer, string? Approval)> ApprovedAsync(List<(string Tool, JsonObject Arguments, PolicyVerdict? Judged)> batch,
                                                                         CancellationToken cancellationToken)
    {
        List<AskedCall> calls = [];

        try
        {
            foreach ((string tool, JsonObject arguments, PolicyVerdict? judged) in batch)
            {
                calls.Add(await AskedAsync(tool, arguments, judged, cancellationToken).ConfigureAwait(false));
            }
        }
        catch (RemoteTextRefusal refused)
        {
            return (Content(new JsonObject { ["error"] = refused.Message }, isError: true), null);
        }

        ApprovalRequest request = new(Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(6)),
                                      Audit?.Run ?? "test", calls);

        ApprovalAnswer answer = Asks is { } asks ? await asks(request, cancellationToken).ConfigureAwait(false)
            : AsksFolder is { } folder && AgentAsks.WindowOpen() ? await AgentAsks.AskAsync(folder, request, AgentAsks.Patience, cancellationToken).ConfigureAwait(false)
            : new ApprovalAnswer(Decision.Nobody, request.Hash);

        string said = $"plan {request.ShortHash}";

        if (answer.Decision is not (Decision.Approve or Decision.Grant))
        {
            string why = answer.Decision switch
            {
                Decision.Refuse => $"The person refused {said}" + (answer.Note.Length > 0 ? $": {answer.Note}" : ".") + " Do not retry it as it was.",
                Decision.TimedOut => string.Create(CultureInfo.InvariantCulture,
                    $"approval timed out: nobody answered {said} in quickshell within {AgentAsks.Patience.TotalMinutes:0} minutes."),
                _ => $"This needs the person's approval ({calls[0].Rule}), and no quickshell window is open to ask in, so it is refused.",
            };

            return (Content(new JsonObject
            {
                ["error"] = why,
                ["verdict"] = "ask",
                ["plan"] = request.Hash,
                ["decision"] = answer.Decision.ToString().ToLowerInvariant(),
            }, isError: true), $"{answer.Decision.ToString().ToLowerInvariant()} {request.Hash}");
        }

        string approval = answer.Decision == Decision.Grant
            ? string.Create(CultureInfo.InvariantCulture, $"approved {request.Hash} and granted for {answer.GrantMinutes} minutes")
            : $"approved {request.Hash}";

        if (batch.Count == 1)
        {
            JsonObject one = await AnsweredAsync(batch[0].Tool, batch[0].Arguments, cancellationToken).ConfigureAwait(false);

            ((JsonObject)one["structuredContent"]!)["plan"] = request.Hash;

            return (one, approval);
        }

        JsonArray results = [];
        bool failed = false;

        foreach ((string tool, JsonObject arguments, PolicyVerdict? _) in batch)
        {
            JsonObject result = await AnsweredAsync(tool, arguments, cancellationToken).ConfigureAwait(false);
            JsonNode structured = result["structuredContent"]!;

            results.Add(new JsonObject { ["tool"] = tool, ["result"] = structured.DeepClone(), ["isError"] = result["isError"]!.GetValue<bool>() });

            // Stops at the first call that fails, saying which: the batch was approved as a whole.
            if (result["isError"]!.GetValue<bool>() || structured["exitCode"] is JsonValue code && code.TryGetValue(out int exit) && exit != 0)
            {
                failed = true;

                break;
            }
        }

        return (Content(new JsonObject
        {
            ["plan"] = request.Hash,
            ["results"] = results,
            ["ran"] = results.Count,
            ["of"] = batch.Count,
            ["failed"] = failed,
        }, isError: failed), approval);
    }

    /// <summary>
    /// <c>plan</c>: several calls judged together and, where any asks, approved together, then run in
    /// order and stopped at the first that fails (QS324). A call the policy refuses refuses the plan.
    /// </summary>
    private async Task<JsonObject> PlanAsync(JsonObject arguments, CancellationToken cancellationToken)
    {
        JsonArray calls = arguments["calls"] is JsonArray { Count: > 0 } given
            ? given
            : throw new GatewayRefusal("plan needs calls, each a tool and its arguments.");

        List<(string Tool, JsonObject Arguments, PolicyVerdict? Judged)> batch = [];

        foreach (JsonNode? call in calls)
        {
            string tool = call?["tool"]?.GetValue<string>() is "exec" or "edit_file" or "job_start" or "read_file"
                ? call["tool"]!.GetValue<string>()
                : throw new GatewayRefusal("A plan's calls are exec, edit_file, job_start or read_file.");
            JsonObject args = call["arguments"]?.DeepClone() as JsonObject ?? new JsonObject();
            PolicyVerdict? judged = Judge(tool, args);

            if (judged is { Verdict: Verdict.Refuse })
            {
                JsonObject refused = (JsonObject)Refused(judged)["structuredContent"]!.DeepClone();

                refused["failed"] = true;

                return refused;
            }

            batch.Add((tool, args, judged));
        }

        if (batch.All(each => each.Judged is null or { Verdict: Verdict.Allow }))
        {
            JsonArray results = [];

            foreach ((string tool, JsonObject args, PolicyVerdict? _) in batch)
            {
                JsonObject result = await AnsweredAsync(tool, args, cancellationToken).ConfigureAwait(false);

                results.Add(new JsonObject { ["tool"] = tool, ["result"] = result["structuredContent"]!.DeepClone(), ["isError"] = result["isError"]!.GetValue<bool>() });

                if (result["isError"]!.GetValue<bool>())
                {
                    break;
                }
            }

            return new JsonObject
            {
                ["results"] = results,
                ["ran"] = results.Count,
                ["of"] = batch.Count,
                ["failed"] = results.Count < batch.Count || results[^1]!["isError"]!.GetValue<bool>(),
            };
        }

        (JsonObject answer, _) = await ApprovedAsync(batch, cancellationToken).ConfigureAwait(false);
        JsonObject structured = (JsonObject)answer["structuredContent"]!.DeepClone();

        structured["failed"] = answer["isError"]!.GetValue<bool>();

        return structured;
    }

    /// <summary>One call as the person is shown it: its session, address and tags, and its words or its diff.</summary>
    private async Task<AskedCall> AskedAsync(string tool, JsonObject arguments, PolicyVerdict? judged, CancellationToken cancellationToken)
    {
        string path = arguments["host"]?.GetValue<string>() ?? string.Empty;
        ResolvedSession session = _store().Session(path) ?? throw new GatewayRefusal($"There is no saved session {path}.");
        string address = string.Create(CultureInfo.InvariantCulture,
            $"{session.User?.Value ?? Environment.UserName}@{session.Host}:{session.Port?.Value ?? SshEndpoint.DefaultPort}");
        string[] words = arguments["argv"] is JsonArray argv ? [.. argv.Select(word => word?.ToString() ?? string.Empty)] : [];
        string? file = arguments["path"]?.GetValue<string>();
        string? diff = null;

        if (tool == "edit_file" && file is not null)
        {
            IGatewayConnection connection = await ConnectionAsync(session, cancellationToken).ConfigureAwait(false);
            IFileTransferChannel files = await connection.FilesAsync(cancellationToken).ConfigureAwait(false);

            diff = await RemoteText.PreviewAsync(files, file, arguments["old_string"]?.GetValue<string>() ?? string.Empty,
                                                 arguments["new_string"]?.GetValue<string>() ?? string.Empty, cancellationToken).ConfigureAwait(false);
            (diff, _) = SecretRedactor.Redact(diff);
        }

        return new AskedCall(session.Path, address, session.Tags, tool, words, file, diff, judged?.Rule ?? string.Empty);
    }

    /// <summary>
    /// Where this run's taint is kept (QS338): the file's presence is the taint, and its text says what
    /// brought it in. The person clears it from the window; nothing the agent calls does.
    /// </summary>
    public string? TaintFile { get; init; }

    private string? _taint;

    /// <summary>What made this run untrusted, or null while nothing has.</summary>
    public string? Taint
    {
        get
        {
            if (TaintFile is { } file)
            {
                return File.Exists(file) ? (_taint ??= File.ReadAllText(file).Trim()) : (_taint = null);
            }

            return _taint;
        }
    }

    /// <summary>Programs whose output is the machine's own state, not text anybody else wrote.</summary>
    private static readonly HashSet<string> Metadata = new(StringComparer.Ordinal)
    {
        "uptime", "df", "free", "whoami", "id", "groups", "hostname", "uname", "nproc", "date", "pwd", "du", "lscpu", "lsblk", "lsmem", "true", "false",
    };

    /// <summary>
    /// What untrusted text a call brought in, or null: a file read, a job's output, a page of kept output,
    /// or a command's output, unless the command only reports the machine's own state.
    /// </summary>
    private static string? Untrusted(string tool, JsonObject arguments, JsonObject answer)
    {
        string where = arguments["host"]?.ToString() ?? "a session";

        switch (tool)
        {
            case "read_file":
                return $"read_file {arguments["path"]} on {where}";

            case "job_output" or "output_page" or "output_grep":
                return answer["text"]?.ToString() is { Length: > 0 } ? $"{tool} on {where}" : null;

            case "exec":
                string[] argv = arguments["argv"] is JsonArray words ? [.. words.Select(word => word?.ToString() ?? string.Empty)] : [];
                bool said = answer["stdout"]?.ToString() is { Length: > 0 } || answer["stderr"]?.ToString() is { Length: > 0 };
                string program = argv.Length > 0 ? argv[0].Replace('\\', '/').Split('/')[^1] : string.Empty;
                bool state = Metadata.Contains(program) || program == "systemctl" && argv.Length > 1 && argv[1].StartsWith("is-", StringComparison.Ordinal);

                return said && !state ? $"the output of {string.Join(' ', argv.Take(4))} on {where}" : null;

            case "plan":
                foreach (JsonNode? result in answer["results"] as JsonArray ?? [])
                {
                    if (result?["tool"]?.ToString() is "read_file" or "exec" && result["result"] is JsonObject inner
                        && result["isError"]?.GetValue<bool>() != true
                        && (inner["text"]?.ToString() is { Length: > 0 } || inner["stdout"]?.ToString() is { Length: > 0 }))
                    {
                        return $"a plan's {result["tool"]} on {where}";
                    }
                }

                return null;

            default:
                return null;
        }
    }

    /// <summary>Marks this run untrusted, keeping what did it, and says so on the person's screen.</summary>
    private void Tainted(string source)
    {
        if (Taint is not null)
        {
            return;
        }

        _taint = source;

        if (TaintFile is { } file)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, source);
        }

        Feed?.Note($"untrusted text came in ({source}): from now on every change asks you, until you trust it again "
                   + "from the palette (Trust what agents have read)");
    }

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
        PolicyVerdict? judged = null;
        string? approval = null;

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
                    // The policy first (QS336): a call that would reach a server runs only where its
                    // session's level, its kind and any live grant allow it.
                    judged = Judge(name, arguments);

                    (answer, approval) = judged switch
                    {
                        null => (await AnsweredAsync(name, arguments, either.Token).ConfigureAwait(false), null),
                        { Verdict: Verdict.Allow } => (await AnsweredAsync(name, arguments, either.Token).ConfigureAwait(false), "automatic"),
                        { Verdict: Verdict.Refuse } => (Refused(judged), null),
                        _ => await ApprovedAsync([(name, arguments, judged)], either.Token).ConfigureAwait(false),
                    };
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

        if (!answer["isError"]!.GetValue<bool>() && Untrusted(name, arguments, (JsonObject)answer["structuredContent"]!) is { } source)
        {
            Tainted(source);
        }

        if (Audit is { } audit)
        {
            ResolvedSession? session = arguments["host"] is JsonValue host && host.TryGetValue(out string? path) ? _store().Session(path) : null;

            audit.Record(DateTimeOffset.Now, name, session, arguments, (JsonObject)answer["structuredContent"]!,
                         answer["isError"]!.GetValue<bool>(), judged, approval);
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
                "plan" => await PlanAsync(arguments, cancellationToken).ConfigureAwait(false),
                _ => throw new GatewayRefusal($"There is no tool {name} here; tools/list names the ones there are."),
            };

            // Every answer through the redactor before it leaves the process, and the count beside it,
            // so the agent knows a value was hidden (QS330).
            if (SecretRedactor.RedactAll(result) is var hidden and > 0)
            {
                result["redacted"] = hidden;
            }

            return Content(result, isError: name == "plan" && result["failed"]?.GetValue<bool>() == true);
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
