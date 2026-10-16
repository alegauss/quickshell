using System.IO;
using System.Text.Json.Nodes;

using Quickshell.Transport;

using Xunit;

namespace Quickshell.App.Tests;

/// <summary>
/// QS333: every call the gateway answers is a line in an append-only log, each naming the one before
/// it, read back as a change summary a person can paste into a ticket.
/// </summary>
public sealed class AuditLogTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "qs-audit-" + Guid.NewGuid().ToString("N"));

    private static CancellationToken Stop => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    /// <summary>A run's calls, read back by what they did, with the log whole and no secret in it.</summary>
    [Fact]
    public async Task ARunIsSummarisedByWhatItDid()
    {
        AuditLog audit = await RunAsync();
        string written = await File.ReadAllTextAsync(Directory.GetFiles(_folder).Single(), Stop);

        AuditSummary summary = AuditLog.Read(_folder, audit.Run)!;

        Assert.Empty(summary.Breaks);
        Assert.Single(summary.Read);
        Assert.Contains(summary.Ran, line => line.Contains("ran systemctl restart nginx on Work/db (exit 0)", StringComparison.Ordinal));
        Assert.Contains(summary.Ran, line => line.Contains("ran sh -c", StringComparison.Ordinal));
        Assert.Single(summary.Refused);
        Assert.Contains("on nowhere", summary.Refused[0], StringComparison.Ordinal);
        Assert.Contains("The log is whole", summary.ToString(), StringComparison.Ordinal);

        // Each call that reached a session carries the verdict and the rule that gave it (QS336).
        Assert.Contains("\"verdict\":\"allow\",\"rule\":\"break-glass grant 1 until", written, StringComparison.Ordinal);

        // The arguments are redacted as answers are: the token typed into a command is not in the log.
        Assert.DoesNotContain("ghp_0123456789abcdefghijABCDEFGHIJ0123456789", written, StringComparison.Ordinal);
        Assert.Equal([audit.Run], AuditLog.Runs(_folder));
    }

    /// <summary>A line removed from the middle by hand breaks the chain where it was, and the summary says so.</summary>
    [Fact]
    public async Task ALineRemovedByHandIsReported()
    {
        AuditLog audit = await RunAsync();
        string file = Directory.GetFiles(_folder).Single();
        List<string> lines = [.. await File.ReadAllLinesAsync(file, Stop)];

        lines.RemoveAt(1);
        await File.WriteAllLinesAsync(file, lines, Stop);

        AuditSummary summary = AuditLog.Read(_folder, audit.Run)!;

        Assert.Equal(["line 2 does not follow the line before it: a line was removed, added or edited there"], summary.Breaks);
        Assert.Contains("TAMPERED", summary.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ARunNotRecordedIsNull() => Assert.Null(AuditLog.Read(_folder, "20260101T000000Z-000000"));

    /// <summary>Four calls through a gateway that records them: a read, two commands, and a refusal.</summary>
    private async Task<AuditLog> RunAsync()
    {
        AuditLog audit = AuditLog.Start(_folder, DateTimeOffset.Now);
        Connection connection = new();

        await using AgentGateway gateway = new(Store, (_, _) => ValueTask.FromResult<IGatewayConnection>(connection), "1.2.3")
        {
            Audit = audit,
            PolicyFile = Policies.BreakGlass(),
        };

        await gateway.AnswerAsync(Call(1, "hosts_list", new JsonObject()), Stop);
        await gateway.AnswerAsync(Call(2, "exec", new JsonObject { ["host"] = "Work/db", ["argv"] = new JsonArray("systemctl", "restart", "nginx") }), Stop);
        await gateway.AnswerAsync(Call(3, "exec", new JsonObject
        {
            ["host"] = "Work/db", ["argv"] = new JsonArray("sh", "-c", "GITHUB_TOKEN=ghp_0123456789abcdefghijABCDEFGHIJ0123456789 ./deploy"),
        }), Stop);
        await gateway.AnswerAsync(Call(4, "exec", new JsonObject { ["host"] = "nowhere", ["argv"] = new JsonArray("id") }), Stop);

        return audit;
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
        Children = [new SessionNode { Name = "Work", Children = [new SessionNode { Name = "db", Host = "db1.example.invalid", Tags = ["prod"] }] }],
    });

    private sealed class Connection : IGatewayConnection
    {
        public bool IsConnected => true;

        public string? ServerVersion => "SSH-2.0-OpenSSH_9.6";

        public ValueTask<CommandResult> RunAsync(string command, TimeSpan timeout, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new CommandResult(0, "done\n", string.Empty));

        public ValueTask<IFileTransferChannel> FilesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
