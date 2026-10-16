using System.IO;
using System.Text.Json.Nodes;

using Quickshell.Terminal;

using Xunit;

namespace Quickshell.App.Tests;

/// <summary>QS342: Claude Code in a pane, wired to the gateway, and what a pane shows sent to it with where it came from.</summary>
public sealed class ClaudePaneTests : IDisposable
{
    private readonly string _here = Path.Combine(Path.GetTempPath(), "qs-claude-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_here))
        {
            Directory.Delete(_here, recursive: true);
        }
    }

    /// <summary>The gateway is the pane's MCP server and the hook guards its shell and file tools, both naming this copy.</summary>
    [Fact]
    public void TheConfigurationStartsTheGatewayAndInstallsTheHook()
    {
        (string mcp, string settings) = ClaudePane.Configure(_here, @"C:\Program Files\quickshell\quickshell.exe");

        JsonNode server = JsonNode.Parse(File.ReadAllText(mcp))!["mcpServers"]!["quickshell"]!;
        JsonNode hook = JsonNode.Parse(File.ReadAllText(settings))!["hooks"]!["PreToolUse"]![0]!;

        Assert.Equal(@"C:\Program Files\quickshell\quickshell.exe", server["command"]!.GetValue<string>());
        Assert.Equal("--mcp", server["args"]![0]!.GetValue<string>());
        Assert.Equal("Bash|PowerShell|Write|Edit|MultiEdit|NotebookEdit", hook["matcher"]!.GetValue<string>());
        Assert.Equal("\"C:\\Program Files\\quickshell\\quickshell.exe\" --agent-hook", hook["hooks"]![0]!["command"]!.GetValue<string>());

        // No sandbox: on native Windows Claude Code has none, and asked to fail without one it would not start.
        Assert.Null(JsonNode.Parse(File.ReadAllText(settings))!["sandbox"]);
    }

    [Fact]
    public void TheCommandLineHandsBothFilesToTheProgramThroughTheCommandProcessor() =>
        Assert.Equal(@"C:\Windows\system32\cmd.exe /c claude --mcp-config ""C:\Users\a b\mcp.json"" --settings C:\data\s.json",
                     ClaudePane.CommandLine(@"C:\Windows\system32\cmd.exe", "claude", @"C:\Users\a b\mcp.json", @"C:\data\s.json"));

    [Fact]
    public void WhatIsSentNamesTheSessionAndItsTags()
    {
        Assert.Equal("From Customer/db (tags: prod, db):\nERROR: disk full\n",
                     ClaudePane.Context("Customer/db", ["prod", "db"], "ERROR: disk full\n\n"));
        Assert.Equal("From cmd.exe:\nhello\n", ClaudePane.Context("cmd.exe", [], "hello"));
    }

    /// <summary>The screen as text: rows as lines, a wrapped row joined to the next, blanks at the end dropped.</summary>
    [Fact]
    public void TheScreenIsReadAsItsLines()
    {
        Emulator emulator = new(10, 5);

        emulator.Feed("$ uptime\r\n"u8);
        emulator.Feed("0123456789abc\r\n"u8);
        emulator.Feed("$ "u8);

        Assert.Equal("$ uptime\n0123456789abc\n$", ClaudePane.ScreenText(emulator.Buffer));
    }
}
