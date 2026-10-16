using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

using Quickshell.Transport;

using Xunit;

namespace Quickshell.App.Tests;

/// <summary>
/// QS340: an account for the agent on the server, limited there, so what the client's policy would refuse
/// the server refuses too, with that policy as open as it goes.
/// </summary>
public sealed class AgentAccountTests : IDisposable
{
    private const string Key = "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIFakeFakeFakeFakeFakeFakeFakeFakeFakeFakeFake quickshell-agent";

    private readonly string _here = Path.Combine(Path.GetTempPath(), "qs-account-" + Guid.NewGuid().ToString("N"));

    public AgentAccountTests() => Directory.CreateDirectory(_here);

    private static CancellationToken Stop => TestContext.Current.CancellationToken;

    public void Dispose() => Directory.Delete(_here, recursive: true);

    /// <summary>The script restricts the key, lists each command whole by its full path, checks sudoers before using it, and can be undone.</summary>
    [Fact]
    public void ThePlanRestrictsTheKeyAndListsEachCommandWhole()
    {
        AgentSetupPlan plan = AgentAccountSetup.Plan("agent", Key, "10.0.0.5", [["systemctl", "restart", "nginx"], ["uptime"]]);

        Assert.Contains("'restrict,from=\"10.0.0.5\" ssh-ed25519 ", plan.Script, StringComparison.Ordinal);
        Assert.Contains("path=$(command -v systemctl)", plan.Script, StringComparison.Ordinal);
        Assert.Contains("'restart nginx' >> \"$allowed\"", plan.Script, StringComparison.Ordinal);

        // No arguments is written as "", which in sudoers is none; bare, it would be any.
        Assert.Contains("'\"\"' >> \"$allowed\"", plan.Script, StringComparison.Ordinal);
        Assert.True(plan.Script.IndexOf("visudo -cf", StringComparison.Ordinal) < plan.Script.IndexOf("install -m 440", StringComparison.Ordinal));
        Assert.Contains("userdel -r agent", plan.Undo, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Agent", "uptime")]
    [InlineData("agent", "rm -rf")]
    [InlineData("agent", "ls *")]
    [InlineData("agent", "/usr/bin/id")]
    [InlineData("agent", "sh -c,x")]
    public void WhatSudoersCannotSayExactlyIsRefused(string account, string command) =>
        Assert.Throws<AgentSetupRefusal>(() => AgentAccountSetup.Plan(account, Key, null, [command.Contains(' ') && command.StartsWith("rm", StringComparison.Ordinal)
            ? ["rm", "-rf /"]
            : command.Split(' ')]));

    /// <summary>
    /// Against the Linux fixture: the setup run as root, then, through the gateway as the agent and with the
    /// client policy at break-glass, the allowed command runs as root and any other is refused by the server.
    /// </summary>
    [Fact]
    public async Task OnAPosixServerTheServerRefusesWhatTheListDoesNot()
    {
        SshFixture.SkipWithoutIt();

        string account = "qsagent" + Guid.NewGuid().ToString("N")[..6];
        string key = Path.Combine(_here, "agent_ed25519");
        string probeKey = Path.Combine(Repository.Root, "prototypes", "SshProbe", "fixture", "keys", "probe_ed25519");

        AgentAccountSetup.MakeKey(key);

        SessionTree store = SessionTree.Of(new SessionNode
        {
            Children =
            [
                new SessionNode
                {
                    Name = "db", Host = "127.0.0.1",
                    Settings = new SessionSettings { User = "probe", Port = 2222, Key = probeKey, AgentUser = account, AgentKey = key },
                },
            ],
        });
        TrustOnFirstUse trust = new(KnownHosts.ReadFrom(Path.Combine(_here, "known_hosts")), (_, _) => ValueTask.FromResult(SshHostKeyVerdict.Accept));

        // The address the server sees this machine connect from, asked as the person.
        ResolvedSession person = SessionTree.Of(new SessionNode
        {
            Children = [new SessionNode { Name = "me", Host = "127.0.0.1", Settings = new SessionSettings { User = "probe", Port = 2222, Key = probeKey } }],
        }).Session("me")!;

        string from;

        await using (IGatewayConnection mine = await AgentGateway.Connect(person, trust, Stop))
        {
            from = (await mine.RunAsync("echo ${SSH_CLIENT%% *}", TimeSpan.FromSeconds(10), Stop)).Output.Trim();
        }

        AgentSetupPlan plan = AgentAccountSetup.Plan(account, await File.ReadAllTextAsync(key + ".pub", Stop), from, [["whoami"]]);

        Assert.True(SshFixture.Docker($"echo {Convert.ToBase64String(Encoding.UTF8.GetBytes(plan.Script))} | base64 -d | sh"), "the setup did not run as root");

        try
        {
            await using AgentGateway gateway = new(() => store, (session, token) => AgentGateway.Connect(session, trust, token), "test")
            {
                PolicyFile = Policies.BreakGlass(),
                Asks = (request, _) => Task.FromResult(new ApprovalAnswer(Decision.Approve, request.Hash)),
            };

            JsonNode who = await ExecAsync(gateway, 1, "whoami");
            JsonNode root = await ExecAsync(gateway, 2, "sudo", "-n", "whoami");
            JsonNode other = await ExecAsync(gateway, 3, "sudo", "-n", "touch", "/root/qs340");

            Assert.Equal(account, who["stdout"]!.GetValue<string>().Trim());
            Assert.Equal("root", root["stdout"]!.GetValue<string>().Trim());
            Assert.NotEqual(0, other["exitCode"]!.GetValue<int>());
            Assert.False(SshFixture.Docker("test -e /root/qs340"), "the server let the agent write as root outside its list");
        }
        finally
        {
            SshFixture.Docker($"echo {Convert.ToBase64String(Encoding.UTF8.GetBytes(plan.Undo))} | base64 -d | sh");
        }
    }

    private static async Task<JsonNode> ExecAsync(AgentGateway gateway, int id, params string[] argv)
    {
        JsonObject answer = (await gateway.AnswerAsync(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = "tools/call",
            ["params"] = new JsonObject
            {
                ["name"] = "exec",
                ["arguments"] = new JsonObject { ["host"] = "db", ["argv"] = new JsonArray([.. argv.Select(word => (JsonNode?)JsonValue.Create(word))]) },
            },
        }.ToJsonString(), Stop))!;

        return answer["result"]!["structuredContent"] ?? throw new Xunit.Sdk.XunitException(answer.ToJsonString());
    }

    /// <summary>
    /// The guided flow's steps against the Linux fixture, as the window takes them: the address the server
    /// sees, a key made, the script staged in the person's home, the typed line run (as root by docker,
    /// standing in for the person answering sudo), the mark taken once, the identity recorded, and the
    /// gateway then signing in as the agent from the store alone.
    /// </summary>
    [Fact]
    public async Task TheGuidedStepsRecordAnIdentityTheGatewaySignsInWith()
    {
        SshFixture.SkipWithoutIt();

        string account = "qsagent" + Guid.NewGuid().ToString("N")[..6];
        string probeKey = Path.Combine(Repository.Root, "prototypes", "SshProbe", "fixture", "keys", "probe_ed25519");
        string storeFile = Path.Combine(_here, "sessions.json");

        SessionTree.Of(new SessionNode
        {
            Children = [new SessionNode { Name = "db", Host = "127.0.0.1", Settings = new SessionSettings { User = "probe", Port = 2222, Key = probeKey } }],
        }).WriteTo(storeFile);

        TrustOnFirstUse trust = new(KnownHosts.ReadFrom(Path.Combine(_here, "known_hosts")), (_, _) => ValueTask.FromResult(SshHostKeyVerdict.Accept));
        string key = AgentAccountSetup.KeyFor(Path.Combine(_here, "agent-keys"), "Work/db");

        Assert.EndsWith("Work_db_ed25519", key, StringComparison.Ordinal);
        Assert.True(AgentAccountSetup.MakeKey(key));
        Assert.False(AgentAccountSetup.MakeKey(key), "a key already there was made again");

        AgentSetupPlan plan;

        await using (IGatewayConnection person = await AgentGateway.Connect(SessionTree.ReadFrom(storeFile).Session("db")!, trust, Stop))
        {
            string from = await AgentAccountSetup.SeenFromAsync(person, Stop);

            plan = AgentAccountSetup.Plan(account, await File.ReadAllTextAsync(key + ".pub", Stop), from, [["whoami"]]);
            await AgentAccountSetup.StageAsync(person, plan, Stop);

            Assert.False(await AgentAccountSetup.ReadyAsync(person, Stop), "ready before anything ran");

            // The typed line, with root's shell for the person's sudo.
            string typed = AgentAccountSetup.Typed.TrimEnd('\r').Replace("sudo sh ~/", "sh ~/", StringComparison.Ordinal).Replace("~/", "/home/probe/", StringComparison.Ordinal);

            Assert.True(SshFixture.Docker(typed + " && chown probe /home/probe/" + AgentAccountSetup.Ready), "the staged script did not run");
            Assert.True(await AgentAccountSetup.ReadyAsync(person, Stop), "the mark the typed line leaves was not seen");
            Assert.False(await AgentAccountSetup.ReadyAsync(person, Stop), "the mark was seen twice");
        }

        try
        {
            AgentAccountSetup.Record(SessionTree.ReadFrom(storeFile), "db", account, key).WriteTo(storeFile);

            await using AgentGateway gateway = new(() => SessionTree.ReadFrom(storeFile), (session, token) => AgentGateway.Connect(session, trust, token), "test")
            {
                PolicyFile = Policies.BreakGlass(),
                Asks = (request, _) => Task.FromResult(new ApprovalAnswer(Decision.Approve, request.Hash)),
            };

            Assert.Equal(account, (await ExecAsync(gateway, 1, "whoami"))["stdout"]!.GetValue<string>().Trim());
            Assert.Equal("probe", SessionTree.ReadFrom(storeFile).Session("db")!.User?.Value);
        }
        finally
        {
            SshFixture.Docker($"echo {Convert.ToBase64String(Encoding.UTF8.GetBytes(plan.Undo))} | base64 -d | sh");
        }
    }

    /// <summary>The list starts from the sudo commands the policy's grants name for the session, sudo and its options taken off.</summary>
    [Fact]
    public void TheListStartsFromWhatThePolicyGrantedAsRoot()
    {
        AgentPolicy policy = AgentPolicy.Parse("""
            { "grants": [
                { "sessions": ["Work/*"], "until": "2020-01-01T00:00:00Z",
                  "commands": [["sudo", "-n", "systemctl", "restart", "nginx"], ["uptime"], ["sudo", "systemctl", "restart", "nginx"]] },
                { "sessions": ["Home/*"], "commands": [["sudo", "reboot"]] }
            ] }
            """);
        ResolvedSession db = SessionTree.Of(new SessionNode
        {
            Children = [new SessionNode { Name = "Work", Children = [new SessionNode { Name = "db", Host = "db" }] }],
        }).Session("Work/db")!;

        IReadOnlyList<IReadOnlyList<string>> granted = policy.GrantedAsRoot(db);

        Assert.Equal(["systemctl restart nginx"], granted.Select(command => string.Join(' ', command)));
    }

    /// <summary>The dialog writes the script from what is typed, and will not run while that cannot be written exactly.</summary>
    [Fact]
    public void TheDialogShowsTheScriptThatWillRunAndRefusesWhatSudoersCannotSay()
    {
        (string first, bool ran, string refused, bool runs) = Sta.Run(() =>
        {
            AgentSetupDialog dialog = new("db", Key, "10.0.0.5", [["systemctl", "restart", "nginx"]]);
            string script = dialog.Plan!.Script;

            TextBox commands = Find<TextBox>(dialog, "AgentCommands");
            Button run = Find<Button>(dialog, "RunAgentSetup");

            commands.Text = "rm -rf *";

            string said = Find<TextBlock>(dialog, "AgentRefused").Text;
            bool enabled = run.IsEnabled;

            commands.Text = "uptime";

            return (script, enabled, said, run.IsEnabled && dialog.Plan!.Script.Contains("command -v uptime", StringComparison.Ordinal));
        });

        Assert.Contains("'restart nginx' >> \"$allowed\"", first, StringComparison.Ordinal);
        Assert.False(ran, "Run was offered for a list sudoers cannot say exactly");
        Assert.Contains("cannot be written into sudoers exactly", refused, StringComparison.Ordinal);
        Assert.True(runs, "the script was not written again for the list as typed");
    }

    /// <summary>The menu offers the setup only where the person turned the gateway on, and a session that opens no tab is said, with nothing made.</summary>
    [Fact]
    public void TheMenuOffersItWithTheGatewayOnAndSaysWhyItStopped()
    {
        string storeFile = Path.Combine(_here, "sessions.json");
        string keys = Path.Combine(_here, "agent-keys");

        SessionTree.Of(new SessionNode { Children = [new SessionNode { Name = "db", Host = "db.example.com" }] }).WriteTo(storeFile);

        (bool off, bool on, string? said) = Sta.Run(() =>
        {
            List<string> told = [];
            MainWindow window = new()
            {
                SessionsFile = storeFile,
                SshConfigFile = Path.Combine(_here, "none"),
                AgentKeysFolder = keys,
                Tells = told.Add,
                OpensSession = _ => { },
            };

            window.RefreshSessions();

            SessionRow row = window.Sidebar.Rows.Single(each => each.Path == "db");

            bool Offers() => window.Sidebar.Menu(row).Items.OfType<MenuItem>().Any(entry => (string)entry.Header == "Set up an agent account…");

            bool before = Offers();

            window.Apply(Settings.Default with { AgentGateway = true });

            bool after = Offers();
            bool recorded = window.SetUpAgentAccount("db").GetAwaiter().GetResult();

            return (before, after && !recorded, told.SingleOrDefault());
        });

        Assert.False(off, "the menu offered agent setup with the gateway off");
        Assert.True(on, "the menu did not offer agent setup with the gateway on");
        Assert.Equal("db did not open in a tab, so there is nowhere to run the setup.", said);
        Assert.False(Directory.Exists(keys), "a key was made for a setup that never reached the server");
    }

    private static T Find<T>(DependencyObject root, string id)
        where T : DependencyObject =>
        Descendants(root).OfType<T>().Single(each => AutomationProperties.GetAutomationId(each) == id);

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        foreach (object child in LogicalTreeHelper.GetChildren(root))
        {
            if (child is DependencyObject node)
            {
                yield return node;

                foreach (DependencyObject below in Descendants(node))
                {
                    yield return below;
                }
            }
        }
    }
}
