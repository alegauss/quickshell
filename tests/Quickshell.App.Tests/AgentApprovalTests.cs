using System.IO;
using System.Text.Json.Nodes;

using Quickshell.Transport;

using Xunit;

namespace Quickshell.App.Tests;

/// <summary>
/// QS337: a call the policy asks about is put to the person, bound to its batch's hash, and runs only as
/// they answered; with nobody to ask, it is refused saying so.
/// </summary>
public sealed class AgentApprovalTests : IDisposable
{
    private readonly string _here = Path.Combine(Path.GetTempPath(), "qs-approve-" + Guid.NewGuid().ToString("N"));

    public AgentApprovalTests() => Directory.CreateDirectory(_here);

    private static CancellationToken Stop => TestContext.Current.CancellationToken;

    public void Dispose() => Directory.Delete(_here, recursive: true);

    /// <summary>Approved, the call runs, the answer carries the plan's hash, and the audit says who approved what.</summary>
    [Fact]
    public async Task AnApprovedCallRunsAndTheAuditNamesItsHash()
    {
        Connection connection = new();
        List<ApprovalRequest> asked = [];
        AuditLog audit = AuditLog.Start(_here, DateTimeOffset.Now);

        await using AgentGateway gateway = new(Store, (_, _) => ValueTask.FromResult<IGatewayConnection>(connection), "1.2.3")
        {
            Audit = audit,
            Asks = (request, _) =>
            {
                asked.Add(request);

                return Task.FromResult(new ApprovalAnswer(Decision.Approve, request.Hash));
            },
        };

        JsonObject answer = (await gateway.AnswerAsync(Exec(1, "db", "systemctl", "restart", "nginx"), Stop))!;

        Assert.False(answer["result"]!["isError"]!.GetValue<bool>(), answer.ToJsonString());
        Assert.Single(asked);
        Assert.Equal(["systemctl", "restart", "nginx"], asked[0].Calls[0].Words);
        Assert.Equal(asked[0].Hash, answer["result"]!["structuredContent"]!["plan"]!.GetValue<string>());
        Assert.Single(connection.Commands);

        string line = (await File.ReadAllLinesAsync(Directory.GetFiles(_here, "*.jsonl").Single(), Stop)).Single();

        Assert.Contains($"\"approval\":\"approved {asked[0].Hash}\"", line, StringComparison.Ordinal);
        Assert.Contains("\"verdict\":\"ask\"", line, StringComparison.Ordinal);
    }

    /// <summary>Refused, nothing runs and the agent is told the person's note.</summary>
    [Fact]
    public async Task ARefusedCallDoesNotRunAndTheAgentHearsWhy()
    {
        Connection connection = new();

        await using AgentGateway gateway = new(Store, (_, _) => ValueTask.FromResult<IGatewayConnection>(connection), "1.2.3")
        {
            Asks = (request, _) => Task.FromResult(new ApprovalAnswer(Decision.Refuse, request.Hash, "not during the sale")),
        };

        JsonObject answer = (await gateway.AnswerAsync(Exec(1, "db", "systemctl", "restart", "nginx"), Stop))!;

        Assert.True(answer["result"]!["isError"]!.GetValue<bool>());
        Assert.Contains("not during the sale", answer["result"]!["structuredContent"]!["error"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Empty(connection.Commands);
    }

    /// <summary>With no window to ask in, an ask is a refusal that says so, and a read still runs.</summary>
    [Fact]
    public async Task WithNobodyToAskTheCallIsRefusedSayingSo()
    {
        Connection connection = new();

        await using AgentGateway gateway = new(Store, (_, _) => ValueTask.FromResult<IGatewayConnection>(connection), "1.2.3");

        JsonObject asked = (await gateway.AnswerAsync(Exec(1, "db", "systemctl", "restart", "nginx"), Stop))!;
        JsonObject read = (await gateway.AnswerAsync(Exec(2, "db", "uptime"), Stop))!;

        Assert.Contains("no quickshell window is open", asked["result"]!["structuredContent"]!["error"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.False(read["result"]!["isError"]!.GetValue<bool>());
        Assert.Equal(["uptime"], connection.Commands.Select(command => command[CommandPrompts.PosixPreamble.Length..]));
    }

    /// <summary>A plan is one ask for all its calls, run in order and stopped at the first that fails.</summary>
    [Fact]
    public async Task APlanIsOneAskRunInOrderAndStoppedAtTheFirstFailure()
    {
        Connection connection = new() { Failing = "false" };
        List<ApprovalRequest> asked = [];

        await using AgentGateway gateway = new(Store, (_, _) => ValueTask.FromResult<IGatewayConnection>(connection), "1.2.3")
        {
            Asks = (request, _) =>
            {
                asked.Add(request);

                return Task.FromResult(new ApprovalAnswer(Decision.Approve, request.Hash));
            },
        };

        JsonObject plan = new()
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "tools/call",
            ["params"] = new JsonObject
            {
                ["name"] = "plan",
                ["arguments"] = new JsonObject
                {
                    ["calls"] = new JsonArray(
                        Call("db", "systemctl", "stop", "app"),
                        Call("db", "false"),
                        Call("db", "systemctl", "start", "app")),
                },
            },
        };

        JsonObject answer = (await gateway.AnswerAsync(plan.ToJsonString(), Stop))!;
        JsonNode result = answer["result"]!["structuredContent"]!;

        Assert.Single(asked);
        Assert.Equal(3, asked[0].Calls.Count);
        Assert.Equal(2, result["ran"]!.GetValue<int>());
        Assert.True(answer["result"]!["isError"]!.GetValue<bool>());
        Assert.Equal(2, connection.Commands.Count);
    }

    /// <summary>An answer to another batch's hash approves nothing.</summary>
    [Fact]
    public async Task AnAnswerToAnotherHashIsARefusal()
    {
        ApprovalRequest request = new("r1", "run", [new AskedCall("db", "a@b:22", [], "exec", ["reboot"], null, null, "rule")]);

        Task<ApprovalAnswer> asking = AgentAsks.AskAsync(_here, request, TimeSpan.FromSeconds(20), Stop);

        while (!File.Exists(Path.Combine(_here, "r1.ask.json")))
        {
            await Task.Delay(50, Stop);
        }

        AgentAsks.WriteAnswer(_here, "r1", new ApprovalAnswer(Decision.Approve, "0000"));

        ApprovalAnswer answer = await asking;

        Assert.Equal(Decision.Refuse, answer.Decision);
        Assert.False(File.Exists(Path.Combine(_here, "r1.ask.json")));
    }

    /// <summary>A grant written from the dialog lets the same command run on the same tags without asking, until its time.</summary>
    [Fact]
    public void AGrantFromTheDialogAdmitsTheSameCommandAfterward()
    {
        string policy = Path.Combine(_here, "agent-policy.json");
        DateTimeOffset now = DateTimeOffset.Now;
        ResolvedSession db = Store().Session("db")!;
        ApprovalRequest request = new("r2", "run", [new AskedCall("db", "a@b:22", ["staging"], "exec", ["systemctl", "restart", "nginx"], null, null, "rule")]);

        ApprovalDialog.WriteGrant(policy, request, 30, now);

        AgentPolicy written = AgentPolicy.Load(policy);

        Assert.Equal(Verdict.Allow, written.Judge(db, ["systemctl", "restart", "nginx"], false, now.AddMinutes(10)).Verdict);
        Assert.Equal(Verdict.Ask, written.Judge(db, ["systemctl", "restart", "nginx"], false, now.AddMinutes(40)).Verdict);
        Assert.Equal(Verdict.Refuse, written.Judge(SessionTree.Of(new SessionNode { Children = [new SessionNode { Name = "p", Host = "p", Tags = ["prod"] }] }).Session("p")!,
                                                   ["touch", "x"], false, now).Verdict);
    }

    private static JsonObject Call(string host, params string[] argv) => new()
    {
        ["tool"] = "exec",
        ["arguments"] = new JsonObject { ["host"] = host, ["argv"] = new JsonArray([.. argv.Select(word => (JsonNode?)JsonValue.Create(word))]) },
    };

    private static string Exec(int id, string host, params string[] argv) => new JsonObject
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id,
        ["method"] = "tools/call",
        ["params"] = new JsonObject { ["name"] = "exec", ["arguments"] = Call(host, argv)["arguments"]!.DeepClone() },
    }.ToJsonString();

    private static SessionTree Store() => SessionTree.Of(new SessionNode { Children = [new SessionNode { Name = "db", Host = "db1.example.invalid", Tags = ["staging"] }] });

    private sealed class Connection : IGatewayConnection
    {
        public List<string> Commands { get; } = [];

        public string? Failing { get; init; }

        public bool IsConnected => true;

        public string? ServerVersion => "SSH-2.0-OpenSSH_9.6";

        public ValueTask<CommandResult> RunAsync(string command, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            Commands.Add(command);

            return ValueTask.FromResult(new CommandResult(Failing is { } failing && command.EndsWith(failing, StringComparison.Ordinal) ? 1 : 0, string.Empty, string.Empty));
        }

        public ValueTask<IFileTransferChannel> FilesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
