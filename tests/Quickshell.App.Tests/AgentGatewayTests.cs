using System.IO;
using System.Text.Json.Nodes;

using Quickshell.Transport;

using Xunit;

namespace Quickshell.App.Tests;

/// <summary>
/// QS325: <c>quickshell --mcp</c>, the agent gateway's spike. The protocol and the quoting are read
/// in process against a connection that records what it was asked; the connection itself is proven
/// against the guest's sshd and, where the fixture is up, the Linux one an argument vector is
/// written for.
/// </summary>
public sealed class AgentGatewayTests
{
    private const int GuestPort = 2240;
    private const string GuestKey = @"C:\ProgramData\quickshell-cases\probe_ed25519";

    private static CancellationToken Stop => TestContext.Current.CancellationToken;

    /// <summary>The handshake, the two tools, a notification that owes nothing, and a method there is not.</summary>
    [Fact]
    public async Task TheHandshakeOffersTwoToolsAndAnswersNothingElse()
    {
        await using AgentGateway gateway = new(Store, (_, _) => throw new InvalidOperationException("nothing connects here"), "1.2.3");

        JsonObject started = (await gateway.AnswerAsync("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"t","version":"1"}}}""", Stop))!;

        Assert.Equal("2025-06-18", started["result"]!["protocolVersion"]!.GetValue<string>());
        Assert.Equal("quickshell", started["result"]!["serverInfo"]!["name"]!.GetValue<string>());
        Assert.Equal("1.2.3", started["result"]!["serverInfo"]!["version"]!.GetValue<string>());

        Assert.Null(await gateway.AnswerAsync("""{"jsonrpc":"2.0","method":"notifications/initialized"}""", Stop));

        JsonObject listed = (await gateway.AnswerAsync("""{"jsonrpc":"2.0","id":2,"method":"tools/list"}""", Stop))!;

        Assert.Equal(["hosts_list", "exec", "read_file", "edit_file", "job_start", "job_status", "job_output", "job_cancel", "output_page", "output_grep"], listed["result"]!["tools"]!.AsArray().Select(tool => tool!["name"]!.GetValue<string>()));

        JsonObject unknown = (await gateway.AnswerAsync("""{"jsonrpc":"2.0","id":"x","method":"resources/list"}""", Stop))!;

        Assert.Equal(-32601, unknown["error"]!["code"]!.GetValue<int>());
        Assert.Equal("x", unknown["id"]!.GetValue<string>());
    }

    /// <summary>The saved sessions, each with its place, address and tags, and nothing that signs in.</summary>
    [Fact]
    public async Task HostsListNamesTheSessionsAndNoSecret()
    {
        await using AgentGateway gateway = new(Store, (_, _) => throw new InvalidOperationException("nothing connects here"), "1.2.3");

        JsonObject answer = (await gateway.AnswerAsync(Call(1, "hosts_list", new JsonObject()), Stop))!;
        string text = answer.ToJsonString();
        JsonArray hosts = answer["result"]!["structuredContent"]!["hosts"]!.AsArray();

        Assert.Equal(["Work/db", "pi"], hosts.Select(host => host!["name"]!.GetValue<string>()));
        Assert.Equal("db1.example.invalid", hosts[0]!["host"]!.GetValue<string>());
        Assert.Equal(2200, hosts[0]!["port"]!.GetValue<int>());
        Assert.Equal("deploy", hosts[0]!["user"]!.GetValue<string>());
        Assert.Equal(["prod"], hosts[0]!["tags"]!.AsArray().Select(tag => tag!.GetValue<string>()));

        // The store names a credential and a key file; neither is the agent's to see.
        Assert.DoesNotContain("dba-credential", text, StringComparison.Ordinal);
        Assert.DoesNotContain("id_secret_key", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// The argument vector arrives as one command line a POSIX shell reads back into the same words,
    /// in the directory asked for, and ten calls to one session are ten commands on one connection.
    /// </summary>
    [Fact]
    public async Task ExecQuotesOnceAndKeepsTheConnection()
    {
        Recording connection = new();

        await using AgentGateway gateway = new(Store, (_, _) => ValueTask.FromResult<IGatewayConnection>(connection), "1.2.3");

        JsonObject answer = (await gateway.AnswerAsync(Call(1, "exec", new JsonObject
        {
            ["host"] = "Work/db",
            ["argv"] = new JsonArray("printf", "%s", "it's $HOME\nand `this`"),
            ["cwd"] = "/srv/my app",
        }), Stop))!;

        Assert.Equal(CommandPrompts.PosixPreamble + "cd '/srv/my app' && printf %s 'it'\\''s $HOME\nand `this`'", connection.Commands[0]);

        JsonNode result = answer["result"]!;

        Assert.False(result["isError"]!.GetValue<bool>());
        Assert.Equal(0, result["structuredContent"]!["exitCode"]!.GetValue<int>());
        Assert.Equal("out", result["structuredContent"]!["stdout"]!.GetValue<string>());
        Assert.Equal("err", result["structuredContent"]!["stderr"]!.GetValue<string>());

        for (int call = 2; call <= 10; call++)
        {
            await gateway.AnswerAsync(Call(call, "exec", new JsonObject { ["host"] = "Work/db", ["argv"] = new JsonArray("true") }), Stop);
        }

        Assert.Equal(10, connection.Commands.Count);
        Assert.Equal(1, gateway.Opened);

        // Dropped, it is replaced rather than used.
        connection.Up = false;
        await gateway.AnswerAsync(Call(11, "exec", new JsonObject { ["host"] = "Work/db", ["argv"] = new JsonArray("true") }), Stop);

        Assert.Equal(2, gateway.Opened);
    }

    /// <summary>A Windows server's shell is not POSIX, so its command carries no POSIX preamble (QS327).</summary>
    [Fact]
    public async Task AWindowsServerGetsTheCommandAlone()
    {
        Recording connection = new() { ServerVersion = "SSH-2.0-OpenSSH_for_Windows_9.5" };

        await using AgentGateway gateway = new(Store, (_, _) => ValueTask.FromResult<IGatewayConnection>(connection), "1.2.3");

        await gateway.AnswerAsync(Call(1, "exec", new JsonObject { ["host"] = "Work/db", ["argv"] = new JsonArray("whoami") }), Stop);

        Assert.Equal("whoami", connection.Commands[0]);
    }

    /// <summary>
    /// Against the Linux fixture, which carries sudo and less for this: a password prompt, a question
    /// and a pager each end in seconds, the first two named, rather than holding the call to its timeout.
    /// </summary>
    [Fact]
    public async Task OnAPosixServerAPromptEndsTheCommandWithItsName()
    {
        SshFixture.SkipWithoutIt();

        await using AgentGateway gateway = Real("probe", 2222,
            Path.Combine(Repository.Root, "prototypes", "SshProbe", "fixture", "keys", "probe_ed25519"));

        System.Diagnostics.Stopwatch took = System.Diagnostics.Stopwatch.StartNew();

        JsonNode sudo = await ExecAsync(gateway, 1, "sh", "-c", "sudo -k; sudo true");
        // Asked as apt and most tools ask, on their own output: bash's read -p prints its question only to
        // a terminal, so it would be no question at all here.
        JsonNode confirm = await ExecAsync(gateway, 2, "sh", "-c", "printf 'Remove everything? [y/N] '; read answer; [ \"$answer\" = y ]");
        JsonNode read = await ExecAsync(gateway, 3, "bash", "-c", "read -p 'Name? ' name; echo \"[$name]\"");
        JsonNode paged = await ExecAsync(gateway, 4, "less", "/etc/ssh/sshd_config");

        Assert.Equal("prompt.sudo_password", sudo["prompt"]?.GetValue<string>());
        Assert.NotEqual(0, sudo["exitCode"]!.GetValue<int>());
        Assert.Equal("prompt.confirmation", confirm["prompt"]?.GetValue<string>());
        Assert.Equal("[]\n", read["stdout"]!.GetValue<string>());
        Assert.Equal(0, paged["exitCode"]!.GetValue<int>());
        Assert.Contains("Port", paged["stdout"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Null(paged["prompt"]);
        Assert.All((JsonNode[])[sudo, confirm, read, paged], each => Assert.False(each["timedOut"]!.GetValue<bool>()));
        Assert.True(took.Elapsed < TimeSpan.FromSeconds(20), $"four commands that ask took {took.Elapsed.TotalSeconds:0} s");
    }

    /// <summary>A job id is twelve hex digits and nothing else, so no call can name a path outside the jobs (QS328).</summary>
    [Fact]
    public async Task AJobIdThatIsAPathIsRefused()
    {
        Recording connection = new();

        await using AgentGateway gateway = new(Store, (_, _) => ValueTask.FromResult<IGatewayConnection>(connection), "1.2.3");

        JsonObject answer = (await gateway.AnswerAsync(Call(1, "job_status", new JsonObject { ["host"] = "Work/db", ["job"] = "../../etc" }), Stop))!;

        Assert.True(answer["result"]!["isError"]!.GetValue<bool>());
        Assert.Contains("not a job id", answer["result"]!["structuredContent"]!["error"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Empty(connection.Commands);
    }

    /// <summary>A Windows server has no POSIX shell to detach a job under, and says so (QS328).</summary>
    [Fact]
    public async Task AJobOnAWindowsServerIsRefused()
    {
        Recording connection = new() { ServerVersion = "SSH-2.0-OpenSSH_for_Windows_9.5" };

        await using AgentGateway gateway = new(Store, (_, _) => ValueTask.FromResult<IGatewayConnection>(connection), "1.2.3");

        JsonObject answer = (await gateway.AnswerAsync(Call(1, "job_start", new JsonObject { ["host"] = "Work/db", ["argv"] = new JsonArray("make") }), Stop))!;

        Assert.True(answer["result"]!["isError"]!.GetValue<bool>());
        Assert.Empty(connection.Commands);
    }

    /// <summary>
    /// Against the Linux fixture: a job started, its gateway and connection gone, a new gateway looks
    /// back in, and the output is read to its end by cursor with the exit code the job ended with.
    /// </summary>
    [Fact]
    public async Task OnAPosixServerAJobOutlivesItsGatewayAndIsReadToTheEnd()
    {
        SshFixture.SkipWithoutIt();

        string job;

        await using (AgentGateway first = Fixture())
        {
            job = (await ToolAsync(first, 1, "job_start", new JsonObject
            {
                ["host"] = "real",
                ["argv"] = new JsonArray("sh", "-c", "for i in 1 2 3 4 5 6; do echo line $i; sleep 1; done; echo bye >&2; exit 3"),
            }))["job"]!.GetValue<string>();

            Assert.Equal("running", (await ToolAsync(first, 2, "job_status", new JsonObject { ["host"] = "real", ["job"] = job }))["state"]!.GetValue<string>());
        }

        // A new gateway on a new connection, as after the agent's host restarted it.
        await using AgentGateway second = Fixture();

        JsonNode status = await ToolAsync(second, 3, "job_status", new JsonObject { ["host"] = "real", ["job"] = job });

        for (int wait = 0; wait < 30 && status["state"]!.GetValue<string>() == "running"; wait++)
        {
            await Task.Delay(500, Stop);
            status = await ToolAsync(second, 4, "job_status", new JsonObject { ["host"] = "real", ["job"] = job });
        }

        Assert.Equal("exited", status["state"]!.GetValue<string>());
        Assert.Equal(3, status["exitCode"]!.GetValue<int>());

        JsonNode output = await ToolAsync(second, 5, "job_output", new JsonObject { ["host"] = "real", ["job"] = job, ["cursor"] = 0 });

        Assert.Equal("line 1\nline 2\nline 3\nline 4\nline 5\nline 6\n", output["text"]!.GetValue<string>());
        Assert.False(output["more"]!.GetValue<bool>());

        // From the cursor on there is nothing new, and nothing is sent twice.
        JsonNode again = await ToolAsync(second, 6, "job_output", new JsonObject
        {
            ["host"] = "real", ["job"] = job, ["cursor"] = output["cursor"]!.GetValue<long>(),
        });

        Assert.Equal(string.Empty, again["text"]!.GetValue<string>());

        JsonNode errors = await ToolAsync(second, 7, "job_output", new JsonObject { ["host"] = "real", ["job"] = job, ["stream"] = "stderr" });

        Assert.Equal("bye\n", errors["text"]!.GetValue<string>());
    }

    /// <summary>Against the Linux fixture: a job cancelled is ended, with everything it started, and says so.</summary>
    [Fact]
    public async Task OnAPosixServerACancelledJobEnds()
    {
        SshFixture.SkipWithoutIt();

        await using AgentGateway gateway = Fixture();

        string job = (await ToolAsync(gateway, 1, "job_start", new JsonObject
        {
            ["host"] = "real", ["argv"] = new JsonArray("sh", "-c", "sleep 300 & sleep 301; wait"),
        }))["job"]!.GetValue<string>();

        JsonNode cancelled = await ToolAsync(gateway, 2, "job_cancel", new JsonObject { ["host"] = "real", ["job"] = job });

        Assert.Equal("cancelled", cancelled["state"]!.GetValue<string>());

        JsonNode left = await ExecAsync(gateway, 3, "sh", "-c", "pgrep -u probe -f 'sleep 30[01]' | wc -l");

        Assert.Equal("0", left["stdout"]!.GetValue<string>().Trim());
    }

/// <summary>
    /// Against the Linux fixture, two hundred thousand lines: the answer stays inside its budget, keeps
    /// both ends and names the cut, and a line from the middle is found and paged without running
    /// anything again (QS329).
    /// </summary>
    [Fact]
    public async Task OnAPosixServerAHugeOutputFitsItsBudgetAndTheRestIsReachable()
    {
        SshFixture.SkipWithoutIt();

        await using AgentGateway gateway = Fixture();

        JsonObject answer = (await gateway.AnswerAsync(Call(1, "exec", new JsonObject
        {
            ["host"] = "real", ["argv"] = new JsonArray("seq", "1", "200000"), ["timeoutSeconds"] = 60,
        }), Stop))!;

        JsonNode result = answer["result"]!["structuredContent"]!;
        string stdout = result["stdout"]!.GetValue<string>();

        Assert.True(System.Text.Encoding.UTF8.GetByteCount(stdout) <= OutputSpool.DefaultBudget + 512,
                    $"{System.Text.Encoding.UTF8.GetByteCount(stdout)} bytes came back");
        Assert.StartsWith("1\n2\n3\n", stdout, StringComparison.Ordinal);
        Assert.EndsWith("199999\n200000\n", stdout, StringComparison.Ordinal);
        Assert.Equal(200000, result["cut"]!["stdout"]!["of"]!.GetValue<int>());
        Assert.True(result["cut"]!["stdout"]!["lines"]!.GetValue<int>() > 190000);

        string output = result["output"]!.GetValue<string>();
        JsonNode found = await ToolAsync(gateway, 2, "output_grep", new JsonObject { ["output"] = output, ["pattern"] = "^100000$" });

        Assert.Equal("100000\t100000\n", found["text"]!.GetValue<string>());
        Assert.Equal(1, found["matches"]!.GetValue<int>());

        JsonNode paged = await ToolAsync(gateway, 3, "output_page", new JsonObject { ["output"] = output, ["from"] = 100000, ["to"] = 100002 });

        Assert.Equal("100000\t100000\n100001\t100001\n100002\t100002\n", paged["text"]!.GetValue<string>());
        Assert.Equal(1, gateway.Opened);
    }

    private static AgentGateway Fixture() =>
        Real("probe", 2222, Path.Combine(Repository.Root, "prototypes", "SshProbe", "fixture", "keys", "probe_ed25519"));

    /// <summary>A session nobody saved is a tool error that says where the names are, and connects nothing.</summary>
    [Fact]
    public async Task ExecOnASessionNobodySavedSaysSo()
    {
        await using AgentGateway gateway = new(Store, (_, _) => throw new InvalidOperationException("nothing connects here"), "1.2.3");

        JsonObject answer = (await gateway.AnswerAsync(Call(1, "exec", new JsonObject { ["host"] = "nowhere", ["argv"] = new JsonArray("id") }), Stop))!;

        Assert.True(answer["result"]!["isError"]!.GetValue<bool>());
        Assert.Contains("hosts_list", answer["result"]!["structuredContent"]!["error"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal(0, gateway.Opened);
    }

    /// <summary>Whole lines in, whole lines out, and nothing on the output but the protocol.</summary>
    [Fact]
    public async Task ItServesLineByLineUntilTheInputEnds()
    {
        await using AgentGateway gateway = new(Store, (_, _) => throw new InvalidOperationException("nothing connects here"), "1.2.3");

        using StringReader input = new("""
            {"jsonrpc":"2.0","id":1,"method":"ping"}
            {"jsonrpc":"2.0","method":"notifications/initialized"}
            {"jsonrpc":"2.0","id":2,"method":"tools/list"}
            """);
        using StringWriter output = new();

        await gateway.ServeAsync(input, output, Stop);

        string[] lines = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        Assert.Equal(2, lines.Length);
        Assert.All(lines, line => Assert.Equal("2.0", JsonNode.Parse(line)!["jsonrpc"]!.GetValue<string>()));
    }

    /// <summary>
    /// In the guest, against its own Windows OpenSSH: a command answers its exit code and its streams
    /// apart, a program that is not there fails on standard error alone, and ten commands share one
    /// connection.
    /// </summary>
    [Fact]
    public async Task OnTheGuestsServerTenCommandsShareOneConnection()
    {
        Assert.SkipUnless(SshFixture.Listening(GuestPort) && File.Exists(GuestKey),
            $"Windows OpenSSH is not on 127.0.0.1:{GuestPort}: run tools/provision-guest-sshd.ps1 once, which sets it up in the guest");

        await using AgentGateway gateway = Real("probe", GuestPort, GuestKey);

        JsonNode who = await ExecAsync(gateway, 1, "whoami");

        Assert.Equal(0, who["exitCode"]!.GetValue<int>());
        Assert.EndsWith("probe", who["stdout"]!.GetValue<string>().Trim(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(string.Empty, who["stderr"]!.GetValue<string>());

        JsonNode missing = await ExecAsync(gateway, 2, "no-such-program-qs325");

        Assert.NotEqual(0, missing["exitCode"]!.GetValue<int>());
        Assert.Equal(string.Empty, missing["stdout"]!.GetValue<string>());
        Assert.NotEqual(string.Empty, missing["stderr"]!.GetValue<string>());

        for (int call = 3; call <= 10; call++)
        {
            Assert.Equal(0, (await ExecAsync(gateway, call, "hostname"))["exitCode"]!.GetValue<int>());
        }

        Assert.Equal(1, gateway.Opened);
    }

    /// <summary>
    /// Against the Linux fixture, where a POSIX shell reads the line: an argument holding quotes, a
    /// dollar sign and a newline arrives byte for byte, which is the claim the quoting exists for.
    /// </summary>
    [Fact]
    public async Task OnAPosixServerAnAwkwardArgumentArrivesByteForByte()
    {
        SshFixture.SkipWithoutIt();

        const string awkward = "it's \"quoted\" $HOME `id` \\ and\na second line";

        await using AgentGateway gateway = Real("probe", 2222,
            Path.Combine(Repository.Root, "prototypes", "SshProbe", "fixture", "keys", "probe_ed25519"));

        JsonNode echoed = await ExecAsync(gateway, 1, "printf", "%s", awkward);

        Assert.Equal(0, echoed["exitCode"]!.GetValue<int>());
        Assert.Equal(awkward, echoed["stdout"]!.GetValue<string>());

        JsonNode moved = await ExecAsync(gateway, 2, "pwd");

        Assert.NotEqual(string.Empty, moved["stdout"]!.GetValue<string>().Trim());
        Assert.Equal(1, gateway.Opened);
    }

    /// <summary>In the guest, against its Windows OpenSSH: a file read, edited, and its backup there.</summary>
    [Fact]
    public async Task OnTheGuestsServerAFileIsEditedWithItsBackupBeside()
    {
        Assert.SkipUnless(SshFixture.Listening(GuestPort) && File.Exists(GuestKey),
            $"Windows OpenSSH is not on 127.0.0.1:{GuestPort}: run tools/provision-guest-sshd.ps1 once, which sets it up in the guest");

        await EditsAFileAsync("probe", GuestPort, GuestKey);
    }

    /// <summary>Against the Linux fixture: the same, on the server an agent most often edits.</summary>
    [Fact]
    public async Task OnAPosixServerAFileIsEditedWithItsBackupBeside()
    {
        SshFixture.SkipWithoutIt();

        await EditsAFileAsync("probe", 2222, Path.Combine(Repository.Root, "prototypes", "SshProbe", "fixture", "keys", "probe_ed25519"));
    }

    /// <summary>
    /// A file put in the account's home is read with its numbers, edited by a replacement with quotes
    /// and a trailing newline, refused an ambiguous edit, and read back with its backup beside it.
    /// </summary>
    private static async Task EditsAFileAsync(string user, int port, string key)
    {
        await using AgentGateway gateway = Real(user, port, key);

        // Placed through a connection of the test's own, so the gateway's tools are what is proven.
        TrustOnFirstUse trust = new(KnownHosts.ReadFrom(Path.Combine(Path.GetTempPath(), $"qs326-{Guid.NewGuid():N}")),
                                    (_, _) => ValueTask.FromResult(SshHostKeyVerdict.Accept));
        ResolvedSession session = SessionTree.Of(new SessionNode
        {
            Children = [new SessionNode { Name = "real", Host = "127.0.0.1", Settings = new SessionSettings { User = user, Port = port, Key = key } }],
        }).Session("real")!;

        await using IGatewayConnection setup = await AgentGateway.Connect(session, trust, Stop);
        IFileTransferChannel files = await setup.FilesAsync(Stop);

        string home = files.WorkingDirectory.TrimEnd('/');
        string path = $"{home}/qs326-{Guid.NewGuid():N}.conf";

        using (MemoryStream content = new("listen = 80\nname = \"web\"\nport = 1\nport = 1\n"u8.ToArray()))
        {
            await files.UploadAsync(content, path, cancellationToken: Stop);
        }

        string? backup = null;

        try
        {
            JsonNode read = await ToolAsync(gateway, 1, "read_file", new JsonObject { ["host"] = "real", ["path"] = path, ["from"] = 2, ["to"] = 2 });

            Assert.Equal("2\tname = \"web\"\n", read["text"]!.GetValue<string>());
            Assert.Equal(4, read["lines"]!.GetValue<int>());

            JsonNode edited = await ToolAsync(gateway, 2, "edit_file", new JsonObject
            {
                ["host"] = "real", ["path"] = path, ["old_string"] = "name = \"web\"\n", ["new_string"] = "name = \"it's web\"\n",
            });

            backup = edited["backup"]!.GetValue<string>();

            Assert.Contains("-name = \"web\"\n+name = \"it's web\"\n", edited["diff"]!.GetValue<string>(), StringComparison.Ordinal);

            JsonObject twice = (await gateway.AnswerAsync(Call(3, "edit_file", new JsonObject
            {
                ["host"] = "real", ["path"] = path, ["old_string"] = "port = 1", ["new_string"] = "port = 2",
            }), Stop))!;

            Assert.True(twice["result"]!["isError"]!.GetValue<bool>());

            JsonNode after = await ToolAsync(gateway, 4, "read_file", new JsonObject { ["host"] = "real", ["path"] = path });
            JsonNode kept = await ToolAsync(gateway, 5, "read_file", new JsonObject { ["host"] = "real", ["path"] = backup });

            Assert.Equal("1\tlisten = 80\n2\tname = \"it's web\"\n3\tport = 1\n4\tport = 1\n", after["text"]!.GetValue<string>());
            Assert.Equal("1\tlisten = 80\n2\tname = \"web\"\n3\tport = 1\n4\tport = 1\n", kept["text"]!.GetValue<string>());
            Assert.Equal(1, gateway.Opened);
        }
        finally
        {
            foreach (string? left in (string?[])[path, backup])
            {
                if (left is { } named)
                {
                    try
                    {
                        await files.DeleteAsync(named, CancellationToken.None);
                    }
                    catch (SshException)
                    {
                        // Gone already, or never made.
                    }
                }
            }
        }
    }

    private static async Task<JsonNode> ToolAsync(AgentGateway gateway, int id, string tool, JsonObject arguments)
    {
        JsonObject answer = (await gateway.AnswerAsync(Call(id, tool, arguments), Stop))!;
        JsonNode result = answer["result"] ?? throw new Xunit.Sdk.XunitException(answer.ToJsonString());

        Assert.False(result["isError"]!.GetValue<bool>(), result.ToJsonString());

        return result["structuredContent"]!;
    }

    /// <summary>A gateway over one real session, trusting that server's key for this test alone.</summary>
    private static AgentGateway Real(string user, int port, string key)
    {
        SessionTree store = SessionTree.Of(new SessionNode
        {
            Children = [new SessionNode { Name = "real", Host = "127.0.0.1", Settings = new SessionSettings { User = user, Port = port, Key = key } }],
        });

        TrustOnFirstUse trust = new(KnownHosts.ReadFrom(Path.Combine(Path.GetTempPath(), $"qs325-{Guid.NewGuid():N}")),
                                    (_, _) => ValueTask.FromResult(SshHostKeyVerdict.Accept));

        return new AgentGateway(() => store, (session, token) => AgentGateway.Connect(session, trust, token), "test");
    }

    private static async Task<JsonNode> ExecAsync(AgentGateway gateway, int id, params string[] argv)
    {
        JsonObject answer = (await gateway.AnswerAsync(Call(id, "exec", new JsonObject
        {
            ["host"] = "real",
            ["argv"] = new JsonArray([.. argv.Select(word => (JsonNode?)JsonValue.Create(word))]),
            ["timeoutSeconds"] = 30,
        }), Stop))!;

        JsonNode result = answer["result"] ?? throw new Xunit.Sdk.XunitException(answer.ToJsonString());

        Assert.False(result["isError"]!.GetValue<bool>(), result.ToJsonString());

        return result["structuredContent"]!;
    }

    private static string Call(int id, string tool, JsonObject arguments) => new JsonObject
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id,
        ["method"] = "tools/call",
        ["params"] = new JsonObject { ["name"] = tool, ["arguments"] = arguments },
    }.ToJsonString();

    private static SessionTree Store() => SessionTree.Of(new SessionNode
    {
        Children =
        [
            new SessionNode
            {
                Name = "Work",
                Settings = new SessionSettings { User = "deploy", Credential = "dba-credential" },
                Children =
                [
                    new SessionNode
                    {
                        Name = "db", Host = "db1.example.invalid", Tags = ["prod"],
                        Settings = new SessionSettings { Port = 2200, Key = @"C:\keys\id_secret_key" },
                    },
                ],
            },
            new SessionNode { Name = "pi", Host = "pi.example.invalid" },
        ],
    });

    /// <summary>A connection that keeps what it was asked to run and answers the same each time.</summary>
    private sealed class Recording : IGatewayConnection
    {
        public List<string> Commands { get; } = [];

        public bool Up { get; set; } = true;

        public bool IsConnected => Up;

        public string? ServerVersion { get; set; } = "SSH-2.0-OpenSSH_9.6p1 Ubuntu-3ubuntu13";

        public ValueTask<CommandResult> RunAsync(string command, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            Commands.Add(command);

            return ValueTask.FromResult(new CommandResult(0, "out", "err"));
        }

        public ValueTask<IFileTransferChannel> FilesAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("this connection only runs commands");

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
