using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

using Quickshell.Transport;

using Xunit;

namespace Quickshell.App.Tests;

/// <summary>
/// QS336: every call that reaches a server is judged by the policy first. The judge's corpus allows
/// nothing it should not; the levels, the defaults and the grants decide as QS322 wrote them; and a
/// prod-tagged session on a real server refuses a write.
/// </summary>
public sealed class AgentPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 16, 12, 0, 0, TimeSpan.FromHours(-3));

    private static CancellationToken Stop => TestContext.Current.CancellationToken;

    /// <summary>
    /// The corpus: no command labelled a mutation is ever judged a read, which fails the build if it is,
    /// and the false asks over the commands agents commonly run are counted and reported.
    /// </summary>
    [Fact]
    public void TheJudgeAllowsNothingInTheCorpusItShouldNot()
    {
        JsonNode corpus = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "agent-commands.json")),
                                         documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip })!;

        List<string> falseAllows = [];
        List<string> falseAsks = [];
        int reads = 0;

        foreach (JsonNode? entry in corpus["commands"]!.AsArray())
        {
            string[] argv = [.. entry!["argv"]!.AsArray().Select(word => word!.GetValue<string>())];
            bool labelled = entry["read"]!.GetValue<bool>();
            bool judged = CommandJudge.Shipped.IsRead(argv);

            if (judged && !labelled)
            {
                falseAllows.Add(string.Join(' ', argv));
            }

            if (labelled)
            {
                reads++;

                if (!judged)
                {
                    falseAsks.Add(string.Join(' ', argv));
                }
            }
        }

        TestContext.Current.TestOutputHelper?.WriteLine($"false asks: {falseAsks.Count} of {reads} reads: {string.Join(" | ", falseAsks)}");

        Assert.True(falseAllows.Count == 0, "judged reads and are not: " + string.Join(" | ", falseAllows));
    }

    [Fact]
    public void WithNoFileEverySessionOperatesAndProdObserves()
    {
        PolicyVerdict restart = AgentPolicy.None.Judge(Session("web", []), ["systemctl", "restart", "nginx"], false, Now);
        PolicyVerdict status = AgentPolicy.None.Judge(Session("web", []), ["systemctl", "status", "nginx"], false, Now);
        PolicyVerdict prodWrite = AgentPolicy.None.Judge(Session("db", ["prod"]), ["touch", "x"], false, Now);
        PolicyVerdict prodRead = AgentPolicy.None.Judge(Session("db", ["prod"]), ["uptime"], false, Now);

        Assert.Equal(Verdict.Ask, restart.Verdict);
        Assert.Equal(Verdict.Allow, status.Verdict);
        Assert.Equal(Verdict.Refuse, prodWrite.Verdict);
        Assert.Contains("prod is at observe", prodWrite.Rule, StringComparison.Ordinal);
        Assert.Equal(Verdict.Allow, prodRead.Verdict);
    }

    /// <summary>Levels by tag and by glob, the narrowest winning, the default deny, and prod with no rule denied.</summary>
    [Fact]
    public void TheNarrowestLevelOfEveryMatchingRuleWins()
    {
        AgentPolicy policy = AgentPolicy.Parse("""
            { "schema": 1, "default": "deny", "rules": [
                { "tags": ["staging"], "level": "operate" },
                { "tags": ["db"], "level": "observe" },
                { "sessions": ["Work/scratch-*"], "level": "operate", "reads": [ { "command": "myapp-status" } ] }
            ] }
            """);

        Assert.Equal(Verdict.Ask, policy.Judge(Session("Work/api", ["staging"]), ["touch", "x"], false, Now).Verdict);
        Assert.Equal(Verdict.Refuse, policy.Judge(Session("Work/pg", ["staging", "db"]), ["touch", "x"], false, Now).Verdict);
        Assert.Equal(Verdict.Allow, policy.Judge(Session("Work/pg", ["staging", "db"]), ["uptime"], false, Now).Verdict);
        Assert.Equal(Verdict.Refuse, policy.Judge(Session("Home/nas", []), ["uptime"], false, Now).Verdict);
        Assert.Equal(Verdict.Refuse, policy.Judge(Session("Shop/db", ["prod"]), ["uptime"], false, Now).Verdict);

        // A rule's own reads apply to its sessions only.
        Assert.Equal(Verdict.Allow, policy.Judge(Session("Work/scratch-1", []), ["myapp-status"], false, Now).Verdict);
        Assert.Equal(Verdict.Ask, policy.Judge(Session("Work/api", ["staging"]), ["myapp-status"], false, Now).Verdict);
    }

    /// <summary>A grant admits its vectors whole, until its time, and never one that runs longer than four hours or has passed.</summary>
    [Fact]
    public void AGrantAdmitsItsVectorsWholeUntilItsTime()
    {
        string Policy(DateTimeOffset until) => $$"""
            { "default": "observe", "grants": [ { "sessions": ["Work/web-*"], "commands": [["systemctl", "restart", "nginx"]],
              "until": "{{until:O}}", "why": "ticket 4411" } ] }
            """;

        AgentPolicy live = AgentPolicy.Parse(Policy(Now.AddMinutes(30)));

        PolicyVerdict granted = live.Judge(Session("Work/web-1", []), ["systemctl", "restart", "nginx"], false, Now);

        Assert.Equal(Verdict.Allow, granted.Verdict);
        Assert.Contains("ticket 4411", granted.Rule, StringComparison.Ordinal);
        Assert.Equal(Verdict.Refuse, live.Judge(Session("Work/web-1", []), ["systemctl", "restart", "nginx", ";", "rm", "-rf", "/"], false, Now).Verdict);
        Assert.Equal(Verdict.Refuse, live.Judge(Session("Work/db-1", []), ["systemctl", "restart", "nginx"], false, Now).Verdict);
        Assert.Equal(Verdict.Refuse, AgentPolicy.Parse(Policy(Now.AddHours(5))).Judge(Session("Work/web-1", []), ["systemctl", "restart", "nginx"], false, Now).Verdict);
        Assert.Equal(Verdict.Refuse, AgentPolicy.Parse(Policy(Now.AddMinutes(-1))).Judge(Session("Work/web-1", []), ["systemctl", "restart", "nginx"], false, Now).Verdict);
    }

    /// <summary>A policy that does not read refuses everything, naming why, and break-glass is never a standing level.</summary>
    [Fact]
    public void ABrokenPolicyRefusesEverything()
    {
        PolicyVerdict broken = AgentPolicy.Parse("{ \"rules\": [ { \"tags\": [\"x\"], \"level\": \"break-glass\" } ] }")
                                          .Judge(Session("web", ["x"]), ["uptime"], false, Now);

        Assert.Equal(Verdict.Refuse, broken.Verdict);
        Assert.Contains("break-glass is a grant", broken.Rule, StringComparison.Ordinal);
        Assert.Equal(Verdict.Refuse, AgentPolicy.Parse("{ not json").Judge(Session("web", []), ["uptime"], false, Now).Verdict);
    }

    /// <summary>
    /// Against the Linux fixture, a session tagged prod with no policy file: a read runs, a write is refused
    /// with the rule named, and nothing was written.
    /// </summary>
    [Fact]
    public async Task OnAProdSessionAWriteIsRefusedAndAReadRuns()
    {
        SshFixture.SkipWithoutIt();

        string key = Path.Combine(Repository.Root, "prototypes", "SshProbe", "fixture", "keys", "probe_ed25519");
        SessionTree store = SessionTree.Of(new SessionNode
        {
            Children = [new SessionNode { Name = "db", Host = "127.0.0.1", Tags = ["prod"], Settings = new SessionSettings { User = "probe", Port = 2222, Key = key } }],
        });
        TrustOnFirstUse trust = new(KnownHosts.ReadFrom(Path.Combine(Path.GetTempPath(), $"qs336-{Guid.NewGuid():N}")), (_, _) => ValueTask.FromResult(SshHostKeyVerdict.Accept));
        string marker = $"/tmp/qs336-{Guid.NewGuid():N}";

        await using AgentGateway gateway = new(() => store, (session, token) => AgentGateway.Connect(session, trust, token), "test");

        JsonObject read = (await gateway.AnswerAsync(Call(1, "exec", new JsonObject { ["host"] = "db", ["argv"] = new JsonArray("uptime") }), Stop))!;
        JsonObject write = (await gateway.AnswerAsync(Call(2, "exec", new JsonObject { ["host"] = "db", ["argv"] = new JsonArray("touch", marker) }), Stop))!;
        JsonObject look = (await gateway.AnswerAsync(Call(3, "exec", new JsonObject { ["host"] = "db", ["argv"] = new JsonArray("ls", marker) }), Stop))!;

        Assert.False(read["result"]!["isError"]!.GetValue<bool>(), read.ToJsonString());
        Assert.True(write["result"]!["isError"]!.GetValue<bool>());
        Assert.Equal("refuse", write["result"]!["structuredContent"]!["verdict"]!.GetValue<string>());
        Assert.Contains("prod is at observe", write["result"]!["structuredContent"]!["rule"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.NotEqual(0, look["result"]!["structuredContent"]!["exitCode"]!.GetValue<int>());
    }

    private static ResolvedSession Session(string path, string[] tags)
    {
        string leaf = path.Split('/')[^1];
        string[] folders = path.Split('/')[..^1];

        SessionNode node = new() { Name = leaf, Host = leaf + ".example.invalid", Tags = tags };

        foreach (string folder in folders.Reverse())
        {
            node = new SessionNode { Name = folder, Children = [node] };
        }

        return SessionTree.Of(new SessionNode { Children = [node] }).Session(path)!;
    }

    private static string Call(int id, string tool, JsonObject arguments) => new JsonObject
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id,
        ["method"] = "tools/call",
        ["params"] = new JsonObject { ["name"] = tool, ["arguments"] = arguments },
    }.ToJsonString();
}
