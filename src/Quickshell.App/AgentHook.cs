using System.Text.Json;
using System.Text.Json.Nodes;

namespace Quickshell.App;

/// <summary>
/// <c>quickshell --agent-hook</c>: a Claude Code <c>PreToolUse</c> hook on the agent's shell tools that
/// turns back a command reaching a server around the gateway (QS332), with the gateway tool to use
/// instead as the reason the agent reads.
///
/// <para><b>The client is the hook,</b> so it needs no runtime the user has to install and is the same
/// version as the gateway it points at. It reads one call, answers once, and exits; a call it cannot
/// read, or one that is not a shell command, gets no answer, which leaves the decision where it was.</para>
/// </summary>
public static class AgentHook
{
    /// <summary>The answer to one hook call: a denial as Claude Code reads one, or nothing.</summary>
    /// <param name="call">The JSON Claude Code wrote to the hook's standard input.</param>
    public static string Answer(string call)
    {
        ArgumentNullException.ThrowIfNull(call);

        string? command;

        try
        {
            command = JsonNode.Parse(call)?["tool_input"]?["command"]?.GetValue<string>();
        }
        catch (Exception unreadable) when (unreadable is JsonException or InvalidOperationException or FormatException)
        {
            return string.Empty;
        }

        if (command is null || SideDoors.Check(command) is not { } why)
        {
            return string.Empty;
        }

        return new JsonObject
        {
            ["hookSpecificOutput"] = new JsonObject
            {
                ["hookEventName"] = "PreToolUse",
                ["permissionDecision"] = "deny",
                ["permissionDecisionReason"] = why,
            },
        }.ToJsonString();
    }
}
