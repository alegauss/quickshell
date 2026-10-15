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

        Assert.Equal(["hosts_list", "exec"], listed["result"]!["tools"]!.AsArray().Select(tool => tool!["name"]!.GetValue<string>()));

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

        Assert.Equal("cd '/srv/my app' && printf %s 'it'\\''s $HOME\nand `this`'", connection.Commands[0]);

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

        public ValueTask<CommandResult> RunAsync(string command, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            Commands.Add(command);

            return ValueTask.FromResult(new CommandResult(0, "out", "err"));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
