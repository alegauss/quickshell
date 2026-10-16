using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Quickshell.App;

/// <summary>
/// <c>quickshell --agent-hook</c>: a Claude Code <c>PreToolUse</c> hook that turns back what an agent
/// should not do from its own tools, with the reason the agent reads.
///
/// <list type="bullet">
/// <item>A shell command reaching a server around the gateway (QS332), naming the gateway tool to use
/// instead.</item>
/// <item>A write, by the agent's file tools or a shell command naming it, to the files that limit or
/// watch the agent (QS339): the policy and its approved copy, the taints, the approval requests and
/// answers, the stop and the audit. An agent that could write those could approve itself.</item>
/// </list>
///
/// <para><b>The client is the hook,</b> so it needs no runtime the user has to install and is the same
/// version as the gateway it points at. It reads one call, answers once, and exits; a call it cannot
/// read, or one it has nothing to say about, gets no answer, which leaves the decision where it was.</para>
/// </summary>
public static class AgentHook
{
    /// <summary>The agent's tools that write a file, and the field naming it.</summary>
    private static readonly Dictionary<string, string> Writers = new(StringComparer.Ordinal)
    {
        ["Write"] = "file_path", ["Edit"] = "file_path", ["MultiEdit"] = "file_path", ["NotebookEdit"] = "notebook_path",
    };

    /// <summary>The files and folders that limit or watch the agent, in this installation.</summary>
    public static IReadOnlyList<string> Guarded(Locations at)
    {
        ArgumentNullException.ThrowIfNull(at);

        return [at.AgentPolicy, PolicyApproval.ApprovedCopy(at.AgentPolicy), at.AgentTaints, at.AgentAsks, at.AgentStop, at.Audit];
    }

    /// <summary>The answer to one hook call: a denial as Claude Code reads one, or nothing.</summary>
    /// <param name="call">The JSON Claude Code wrote to the hook's standard input.</param>
    /// <param name="guarded">The guarded paths; this installation's where none are given.</param>
    public static string Answer(string call, IReadOnlyList<string>? guarded = null)
    {
        ArgumentNullException.ThrowIfNull(call);

        JsonNode? input;
        string? tool;

        try
        {
            JsonNode? root = JsonNode.Parse(call);

            input = root?["tool_input"];
            tool = root?["tool_name"]?.GetValue<string>();
        }
        catch (Exception unreadable) when (unreadable is JsonException or InvalidOperationException or FormatException)
        {
            return string.Empty;
        }

        guarded ??= Guarded(Locations.Current);

        string? why = null;

        if (tool is not null && Writers.TryGetValue(tool, out string? field) && input?[field]?.GetValue<string>() is { } path
            && guarded.FirstOrDefault(each => Within(path, each)) is { } hit)
        {
            why = $"{Path.GetFileName(hit)} limits or watches what agents do, so an agent's tools do not write it. "
                  + "The person changes it in quickshell.";
        }
        else if (input?["command"]?.GetValue<string>() is { } command)
        {
            why = SideDoors.Check(command)
                  ?? (guarded.FirstOrDefault(each => command.Contains(Path.GetFileName(each), StringComparison.OrdinalIgnoreCase)
                                                     && Path.GetFileName(each) is not "audit") is { } named
                      ? $"The command names {Path.GetFileName(named)}, which limits or watches what agents do; an agent's shell does not touch it. "
                        + "The person changes it in quickshell."
                      : null);
        }

        if (why is null)
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

    /// <summary>Whether <paramref name="path"/> is <paramref name="guarded"/> or inside it.</summary>
    private static bool Within(string path, string guarded)
    {
        try
        {
            string full = Path.GetFullPath(path).TrimEnd('\\', '/');
            string root = Path.GetFullPath(guarded).TrimEnd('\\', '/');

            return string.Equals(full, root, StringComparison.OrdinalIgnoreCase)
                   || full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception unusable) when (unusable is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}
