using System.IO;
using System.Text;
using System.Text.Json.Nodes;

using Quickshell.Terminal;

namespace Quickshell.App;

/// <summary>
/// Claude Code in a local pane, with the agent gateway already wired in, and what a pane shows sent to it
/// with where it came from (QS342).
///
/// <para><b>A pane, not a chat window.</b> Claude Code already is a chat interface, and a second one in
/// this window would cost the window's budget for something the person can run elsewhere against the same
/// gateway. What the client adds is the two things only it knows: which servers it can reach, given to the
/// agent as the gateway, and which server a piece of output came from, given with the output.</para>
///
/// <para><b>The configuration is written, not assumed.</b> Two files in quickshell's folder, written again
/// each time the pane opens so they name this copy of quickshell: one starting the gateway as an MCP
/// server (<c>--mcp-config</c>), one installing the hook that turns the agent's shell away from servers
/// and its file tools away from the files that limit it (<c>--settings</c>). Claude Code's sandbox is not
/// configured: it does not run on native Windows, and asked to fail where it cannot run, it would refuse
/// to start at all.</para>
/// </summary>
public static class ClaudePane
{
    /// <summary>The title of the pane's tab.</summary>
    public const string Title = "claude";

    /// <summary>
    /// Writes the gateway's MCP configuration and the hook's settings into <paramref name="folder"/>, both
    /// naming <paramref name="quickshell"/>.
    /// </summary>
    /// <returns>Where the MCP configuration and the settings are.</returns>
    public static (string Mcp, string Settings) Configure(string folder, string quickshell)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        ArgumentException.ThrowIfNullOrWhiteSpace(quickshell);

        Directory.CreateDirectory(folder);

        string mcp = Path.Combine(folder, "claude-mcp.json");
        string settings = Path.Combine(folder, "claude-settings.json");

        File.WriteAllText(mcp, new JsonObject
        {
            ["mcpServers"] = new JsonObject
            {
                ["quickshell"] = new JsonObject { ["command"] = quickshell, ["args"] = new JsonArray("--mcp") },
            },
        }.ToJsonString(Indented), new UTF8Encoding(false));

        File.WriteAllText(settings, new JsonObject
        {
            ["hooks"] = new JsonObject
            {
                ["PreToolUse"] = new JsonArray(new JsonObject
                {
                    ["matcher"] = "Bash|PowerShell|Write|Edit|MultiEdit|NotebookEdit",
                    ["hooks"] = new JsonArray(new JsonObject { ["type"] = "command", ["command"] = $"\"{quickshell}\" --agent-hook" }),
                }),
            },
        }.ToJsonString(Indented), new UTF8Encoding(false));

        return (mcp, settings);
    }

    /// <summary>
    /// The pane's command line: <paramref name="program"/> through the command processor, which finds a
    /// <c>claude.cmd</c> as readily as a <c>claude.exe</c>, given both files.
    /// </summary>
    public static string CommandLine(string shell, string program, string mcp, string settings) =>
        $"{Quoted(shell)} /c {program} --mcp-config {Quoted(mcp)} --settings {Quoted(settings)}";

    /// <summary>
    /// What is sent: a line naming where the text came from, then the text. A saved session is named by its
    /// path with its tags; anything else as the pane's own title.
    /// </summary>
    public static string Context(string from, IReadOnlyList<string> tags, string text)
    {
        ArgumentNullException.ThrowIfNull(tags);
        ArgumentNullException.ThrowIfNull(text);

        string tagged = tags.Count == 0 ? string.Empty : $" (tags: {string.Join(", ", tags)})";

        return $"From {from}{tagged}:\n{text.TrimEnd()}\n";
    }

    /// <summary>
    /// The screen as text, the last screenful a pane shows: a wrapped row joined to the next, each row's
    /// trailing blanks dropped, and blank rows at the bottom left out.
    /// </summary>
    public static string ScreenText(TerminalBuffer buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);

        StringBuilder text = new();

        for (int row = 0; row < buffer.Rows; row++)
        {
            StringBuilder line = new();

            foreach (Cell cell in buffer.Screen(row))
            {
                if (cell.Width != 0)
                {
                    line.Append(cell.Codepoint == 0 ? " " : buffer.TextOf(cell));
                }
            }

            text.Append(buffer.IsScreenWrapped(row) ? line.ToString() : line.ToString().TrimEnd() + "\n");
        }

        return text.ToString().TrimEnd();
    }

    private static string Quoted(string path) => path.Contains(' ', StringComparison.Ordinal) ? $"\"{path}\"" : path;

    private static readonly System.Text.Json.JsonSerializerOptions Indented = new() { WriteIndented = true };
}
