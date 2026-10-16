using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;

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

        Keygen(key);

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

    /// <summary>An ed25519 pair with no passphrase, made by the OpenSSH that ships with Windows.</summary>
    private static void Keygen(string path)
    {
        using Process keygen = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "OpenSSH", "ssh-keygen.exe"))
        {
            ArgumentList = { "-q", "-t", "ed25519", "-N", "", "-C", "quickshell-agent", "-f", path },
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;

        keygen.WaitForExit();
        Assert.True(File.Exists(path + ".pub"), "ssh-keygen made no key: " + keygen.StandardError.ReadToEnd());
    }
}
