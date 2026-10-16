using System.IO;
using System.Text.Json.Nodes;

using Quickshell.Transport;

using Xunit;

namespace Quickshell.App.Tests;

/// <summary>
/// QS339: an agent cannot widen its own policy. Its tools are turned back from the files that limit it,
/// and a change that gets past them, by a script, holds every call until the person accepts or rejects it.
/// </summary>
public sealed class PolicyGuardTests : IDisposable
{
    private readonly string _here = Path.Combine(Path.GetTempPath(), "qs-guard-" + Guid.NewGuid().ToString("N"));

    public PolicyGuardTests() => Directory.CreateDirectory(_here);

    private static CancellationToken Stop => TestContext.Current.CancellationToken;

    private string Policy => Path.Combine(_here, "agent-policy.json");

    public void Dispose() => Directory.Delete(_here, recursive: true);

    [Fact]
    public void NoPolicyIsApprovedAndAnyChangeWaitsUntilAnswered()
    {
        Assert.True(PolicyApproval.Approved(Policy));

        File.WriteAllText(Policy, """{ "default": "observe" }""");
        Assert.False(PolicyApproval.Approved(Policy));

        PolicyApproval.Accept(Policy);
        Assert.True(PolicyApproval.Approved(Policy));

        File.WriteAllText(Policy, """{ "default": "operate", "grants": [ { "sessions": ["*"], "level": "break-glass", "until": "2099-01-01T00:00:00Z" } ] }""");
        Assert.False(PolicyApproval.Approved(Policy));
        Assert.Contains("+{ \"default\": \"operate\"", PolicyApproval.Change(Policy), StringComparison.Ordinal);

        PolicyApproval.Reject(Policy);
        Assert.True(PolicyApproval.Approved(Policy));
        Assert.Equal("""{ "default": "observe" }""", File.ReadAllText(Policy));
    }

    /// <summary>A script that widened the policy past the hook gains nothing: every call is refused until the person answers.</summary>
    [Fact]
    public async Task AWidenedPolicyHoldsEveryCallUntilThePersonAnswers()
    {
        await File.WriteAllTextAsync(Policy, """{ "default": "observe" }""", Stop);
        PolicyApproval.Accept(Policy);

        Connection connection = new();

        await using AgentGateway gateway = new(Store, (_, _) => ValueTask.FromResult<IGatewayConnection>(connection), "1.2.3") { PolicyFile = Policy };

        Assert.False(await IsErrorAsync(gateway, 1, "uptime"));

        // What an agent's own script might write, past the hook.
        await File.WriteAllTextAsync(Policy, """{ "default": "operate", "grants": [ { "sessions": ["*"], "level": "break-glass", "until": "2099-01-01T00:00:00Z" } ] }""", Stop);

        JsonObject held = (await gateway.AnswerAsync(Exec(2, "systemctl", "restart", "nginx"), Stop))!;

        Assert.True(held["result"]!["isError"]!.GetValue<bool>());
        Assert.Contains("changed outside quickshell", held["result"]!["structuredContent"]!["rule"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.True(await IsErrorAsync(gateway, 3, "uptime"));

        // The person rejects it, and the approved policy is what the gateway judges by again.
        PolicyApproval.Reject(Policy);

        Assert.False(await IsErrorAsync(gateway, 4, "uptime"));
        Assert.True(await IsErrorAsync(gateway, 5, "systemctl", "restart", "nginx"));
        // Only the two reads the approved policy allowed ever reached the server.
        Assert.Equal(2, connection.Commands.Count);
        Assert.All(connection.Commands, command => Assert.EndsWith("uptime", command, StringComparison.Ordinal));
    }

    /// <summary>The agent's file tools and shell are turned back from every file that limits or watches it, and not from reading.</summary>
    [Theory]
    [InlineData("Edit", "file_path", "agent-policy.json")]
    [InlineData("Write", "file_path", "agent-policy.json.approved")]
    [InlineData("Write", "file_path", "agent-asks/abc.answer.json")]
    [InlineData("MultiEdit", "file_path", "agent-taints/run.taint")]
    [InlineData("Write", "file_path", "agents-stopped")]
    [InlineData("Write", "file_path", "audit/run.jsonl")]
    public void TheHookTurnsBackAWriteToWhatLimitsTheAgent(string tool, string field, string relative)
    {
        string path = Path.Combine(_here, relative.Replace('/', Path.DirectorySeparatorChar));
        string answer = AgentHook.Answer(Call(tool, new JsonObject { [field] = path }), Guarded());

        Assert.Equal("deny", JsonNode.Parse(answer)!["hookSpecificOutput"]!["permissionDecision"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("echo '{\"default\":\"operate\"}' > \"%APPDATA%/quickshell/agent-policy.json\"")]
    [InlineData("rm -f ~/AppData/Roaming/quickshell/agent-taints/*.taint")]
    [InlineData("Set-Content $env:APPDATA\\quickshell\\agent-asks\\x.answer.json '{}'")]
    public void TheHookTurnsBackAShellCommandNamingThem(string command) =>
        Assert.Contains("deny", AgentHook.Answer(Call("Bash", new JsonObject { ["command"] = command }), Guarded()), StringComparison.Ordinal);

    [Fact]
    public void ReadingThePolicyAndWritingElsewhereAreLeftAlone()
    {
        Assert.Equal(string.Empty, AgentHook.Answer(Call("Read", new JsonObject { ["file_path"] = Policy }), Guarded()));
        Assert.Equal(string.Empty, AgentHook.Answer(Call("Write", new JsonObject { ["file_path"] = Path.Combine(_here, "notes.md") }), Guarded()));
        Assert.Equal(string.Empty, AgentHook.Answer(Call("Bash", new JsonObject { ["command"] = "git log --oneline audit" }), Guarded()));
    }

    private string[] Guarded() =>
        [Policy, PolicyApproval.ApprovedCopy(Policy), Path.Combine(_here, "agent-taints"), Path.Combine(_here, "agent-asks"),
         Path.Combine(_here, "agents-stopped"), Path.Combine(_here, "audit")];

    private static string Call(string tool, JsonObject input) => new JsonObject
    {
        ["hook_event_name"] = "PreToolUse",
        ["tool_name"] = tool,
        ["tool_input"] = input,
    }.ToJsonString();

    private static async Task<bool> IsErrorAsync(AgentGateway gateway, int id, params string[] argv) =>
        (await gateway.AnswerAsync(Exec(id, argv), Stop))!["result"]!["isError"]!.GetValue<bool>();

    private static string Exec(int id, params string[] argv) => new JsonObject
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id,
        ["method"] = "tools/call",
        ["params"] = new JsonObject
        {
            ["name"] = "exec",
            ["arguments"] = new JsonObject { ["host"] = "web", ["argv"] = new JsonArray([.. argv.Select(word => (JsonNode?)JsonValue.Create(word))]) },
        },
    }.ToJsonString();

    private static SessionTree Store() => SessionTree.Of(new SessionNode { Children = [new SessionNode { Name = "web", Host = "web1.example.invalid" }] });

    private sealed class Connection : IGatewayConnection
    {
        public List<string> Commands { get; } = [];

        public bool IsConnected => true;

        public string? ServerVersion => "SSH-2.0-OpenSSH_9.6";

        public ValueTask<CommandResult> RunAsync(string command, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            Commands.Add(command);

            return ValueTask.FromResult(new CommandResult(0, string.Empty, string.Empty));
        }

        public ValueTask<IFileTransferChannel> FilesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
