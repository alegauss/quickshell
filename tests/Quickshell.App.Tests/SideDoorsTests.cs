using Xunit;

namespace Quickshell.App.Tests;

/// <summary>
/// QS332: the ways an agent's shell reaches a server around the gateway are named and turned back,
/// and the ordinary commands an agent runs locally are not.
/// </summary>
public sealed class SideDoorsTests
{
    [Theory]
    [InlineData("ssh probe@127.0.0.1 -p 2222 uptime")]
    [InlineData("C:\\Windows\\System32\\OpenSSH\\ssh.exe -i key probe@db1 'cat /etc/hosts'")]
    [InlineData("scp -P 2222 notes.txt probe@127.0.0.1:/tmp/")]
    [InlineData("sftp -b batch probe@db1")]
    [InlineData("plink -batch probe@db1 uptime")]
    [InlineData("echo ok && sudo ssh root@db1 reboot")]
    [InlineData("cat hosts.txt | xargs -I{} ssh {} uptime")]
    [InlineData("rsync -az ./site/ deploy@web1:/srv/site/")]
    [InlineData("rsync -e 'ssh -p 2222' -a out/ probe@127.0.0.1:out/")]
    [InlineData("git clone git@github.com:acme/app.git")]
    [InlineData("git push ssh://git@example.invalid/acme/app.git main")]
    [InlineData("python3 -c \"import paramiko; c = paramiko.SSHClient()\"")]
    [InlineData("Enter-PSSession -HostName db1 -UserName probe")]
    [InlineData("timeout 30 ssh probe@db1 uptime")]
    [InlineData("out=$(ssh probe@db1 hostname)")]
    public void AWayAroundTheGatewayIsTurnedBack(string command)
    {
        string? why = SideDoors.Check(command);

        Assert.NotNull(why);
        Assert.Contains("quickshell", why, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("dotnet build Quickshell.sln")]
    [InlineData("ssh-keygen -t ed25519 -f key")]
    [InlineData("git status && git log --oneline -5")]
    [InlineData("git clone https://github.com/acme/app.git")]
    [InlineData("rsync -a ./a/ ./b/")]
    [InlineData("grep -rn ssh src")]
    [InlineData("echo 'use ssh to connect'")]
    [InlineData("python3 -m pytest tests")]
    [InlineData("cat ~/.ssh/config")]
    public void AnOrdinaryLocalCommandPasses(string command) => Assert.Null(SideDoors.Check(command));

    /// <summary>The hook answers a shell call around the gateway with a denial Claude Code reads, and anything else with nothing.</summary>
    [Fact]
    public void TheHookDeniesASideDoorAndLeavesEverythingElseAlone()
    {
        string denied = AgentHook.Answer("""{"hook_event_name":"PreToolUse","tool_name":"Bash","tool_input":{"command":"ssh probe@db1 uptime"}}""");
        System.Text.Json.Nodes.JsonNode answer = System.Text.Json.Nodes.JsonNode.Parse(denied)!["hookSpecificOutput"]!;

        Assert.Equal("PreToolUse", answer["hookEventName"]!.GetValue<string>());
        Assert.Equal("deny", answer["permissionDecision"]!.GetValue<string>());
        Assert.Contains("exec", answer["permissionDecisionReason"]!.GetValue<string>(), StringComparison.Ordinal);

        Assert.Equal(string.Empty, AgentHook.Answer("""{"tool_name":"Bash","tool_input":{"command":"dotnet build"}}"""));
        Assert.Equal(string.Empty, AgentHook.Answer("""{"tool_name":"Read","tool_input":{"file_path":"C:/x"}}"""));
        Assert.Equal(string.Empty, AgentHook.Answer("not json"));
    }
}
