using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Quickshell.App;

/// <summary>What one step of the plugin's install was, and what Claude Code said to it.</summary>
/// <param name="Command">The words given to <c>claude</c>.</param>
/// <param name="ExitCode">How it exited.</param>
/// <param name="Said">What it wrote, both streams.</param>
public sealed record PluginStep(IReadOnlyList<string> Command, int ExitCode, string Said);

/// <summary>
/// The agent gateway as a Claude Code plugin (QS343): the MCP server, the hook and a skill in one install,
/// so wiring quickshell into Claude Code is one answer in Settings rather than three hand edits.
///
/// <para><b>Written by the client, never published.</b> The plugin's MCP entry and its hook name the
/// <c>quickshell.exe</c> that wrote them, by its full path, and carry that copy's version. A plugin
/// published once could name neither, since where a person installs quickshell is theirs to choose. So
/// the client writes the plugin, inside a marketplace of its own, into its own folder, and asks Claude
/// Code to add that marketplace and install from it. Installing again after an update writes the new
/// version over it.</para>
///
/// <para><b>Tied to its version.</b> The MCP entry starts <c>--mcp --plugin-version &lt;version&gt;</c>.
/// A gateway of another version refuses, naming both, so a skill describing tools this gateway does not
/// have is never served beside it.</para>
///
/// <para><b>No sandbox rules.</b> Claude Code's sandbox does not run on native Windows, which is where
/// this client runs; the hook is the only measure on the agent's side here, and the policy and the
/// server account are the rest.</para>
/// </summary>
public static class ClaudePlugin
{
    /// <summary>The plugin's name, which is also the MCP server's.</summary>
    public const string Name = "quickshell";

    /// <summary>The marketplace the client writes it into.</summary>
    public const string Marketplace = "quickshell-local";

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>
    /// Why a gateway of version <paramref name="mine"/> refuses a plugin written by <paramref name="plugin"/>,
    /// or null where it does not: they match, or no plugin started it.
    /// </summary>
    public static string? Mismatch(string? plugin, string mine) =>
        plugin is null || string.Equals(plugin, mine, StringComparison.Ordinal)
            ? null
            : $"the quickshell plugin for Claude Code was written by quickshell {plugin}, and this is quickshell {mine}. "
              + "Install the plugin again from quickshell's Settings, which writes it for this version.";

    /// <summary>
    /// Writes the marketplace and the plugin in it into <paramref name="folder"/>, naming
    /// <paramref name="quickshell"/> at <paramref name="version"/>, over anything an earlier install wrote.
    /// </summary>
    /// <returns>The marketplace's folder, which <c>claude plugin marketplace add</c> takes.</returns>
    public static string Write(string folder, string quickshell, string version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        ArgumentException.ThrowIfNullOrWhiteSpace(quickshell);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);

        string plugin = Path.Combine(folder, Name);

        if (Directory.Exists(plugin))
        {
            Directory.Delete(plugin, recursive: true);
        }

        Json(Path.Combine(folder, ".claude-plugin", "marketplace.json"), new JsonObject
        {
            ["name"] = Marketplace,
            ["description"] = "The quickshell agent gateway, written by the quickshell installed on this machine.",
            ["owner"] = new JsonObject { ["name"] = "quickshell" },
            ["plugins"] = new JsonArray(new JsonObject { ["name"] = Name, ["source"] = "./" + Name }),
        });

        Json(Path.Combine(plugin, ".claude-plugin", "plugin.json"), new JsonObject
        {
            ["name"] = Name,
            ["description"] = "Reach your quickshell sessions through its agent gateway: every call judged by your policy, "
                              + "changes approved in quickshell, and ssh around it turned back.",
            ["version"] = version,
            ["author"] = new JsonObject { ["name"] = "quickshell" },
            ["mcpServers"] = new JsonObject
            {
                [Name] = new JsonObject
                {
                    ["command"] = quickshell,
                    ["args"] = new JsonArray(CommandLine.Mcp, CommandLine.PluginVersion, version),
                },
            },
        });

        Json(Path.Combine(plugin, "hooks", "hooks.json"), new JsonObject
        {
            ["hooks"] = new JsonObject
            {
                ["PreToolUse"] = new JsonArray(new JsonObject
                {
                    ["matcher"] = "Bash|PowerShell|Write|Edit|MultiEdit|NotebookEdit",
                    ["hooks"] = new JsonArray(new JsonObject
                    {
                        ["type"] = "command",
                        ["command"] = $"\"{quickshell}\" {CommandLine.AgentHook}",
                        ["timeout"] = 10,
                    }),
                }),
            },
        });

        string skill = Path.Combine(plugin, "skills", "quickshell-gateway", "SKILL.md");

        Directory.CreateDirectory(Path.GetDirectoryName(skill)!);

        using (Stream stream = typeof(ClaudePlugin).Assembly.GetManifestResourceStream("Quickshell.App.claude-plugin-skill.md")
                               ?? throw new InvalidOperationException("The plugin's skill is not in this build."))
        using (FileStream written = File.Create(skill))
        {
            stream.CopyTo(written);
        }

        return folder;
    }

    /// <summary>
    /// What installing asks Claude Code to do, in order: the marketplace taken off where an earlier install
    /// added it, added again from <paramref name="marketplace"/>, and the plugin installed from it for this
    /// user.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<string>> Steps(string marketplace) =>
    [
        ["plugin", "marketplace", "remove", Marketplace],
        ["plugin", "marketplace", "add", marketplace],
        // -s is Claude Code's own short form of its scope option; a long form here would read as one of
        // quickshell's flags to the test that keeps those to the reference.
        ["plugin", "install", $"{Name}@{Marketplace}", "-s", "user"],
    ];

    /// <summary>
    /// Runs <see cref="Steps"/> with <paramref name="run"/>, stopping at the first that fails after the
    /// first: taking off a marketplace that was never added is the one failure that means nothing.
    /// </summary>
    /// <returns>Every step run, the last being the one that failed where one did.</returns>
    public static IReadOnlyList<PluginStep> Install(string marketplace, Func<IReadOnlyList<string>, PluginStep> run)
    {
        ArgumentNullException.ThrowIfNull(run);

        List<PluginStep> ran = [];

        foreach (IReadOnlyList<string> step in Steps(marketplace))
        {
            PluginStep done = run(step);

            ran.Add(done);

            if (done.ExitCode != 0 && ran.Count > 1)
            {
                break;
            }
        }

        return ran;
    }

    /// <summary>
    /// Runs <paramref name="program"/> through the command processor, which finds a <c>claude.cmd</c> as
    /// readily as a <c>claude.exe</c>, with <paramref name="arguments"/> each quoted, and waits up to two
    /// minutes.
    /// </summary>
    public static PluginStep Run(string program, IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        ProcessStartInfo start = new(LocalSession.Shell)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        // cmd reads its own line after /s /c with the outer quotes taken off, so each argument is quoted
        // inside one pair of quotes around the whole.
        start.Arguments = "/s /c \"" + program + " " + string.Join(' ', arguments.Select(word => $"\"{word}\"")) + "\"";

        using Process claude = Process.Start(start) ?? throw new InvalidOperationException($"{program} did not start.");

        Task<string> output = claude.StandardOutput.ReadToEndAsync();
        Task<string> error = claude.StandardError.ReadToEndAsync();

        if (!claude.WaitForExit(TimeSpan.FromMinutes(2)))
        {
            claude.Kill(entireProcessTree: true);

            return new PluginStep(arguments, -1, $"{program} did not answer within two minutes.");
        }

        return new PluginStep(arguments, claude.ExitCode, (output.Result + error.Result).Trim());
    }

    /// <summary>
    /// Where <paramref name="program"/> is: itself where it names a file that is there, or the first match on
    /// the path with any of the extensions Windows runs; null where it is nowhere.
    /// </summary>
    public static string? Find(string program, string? path = null, string? extensions = null)
    {
        if (string.IsNullOrWhiteSpace(program))
        {
            return null;
        }

        if (program.Contains('\\', StringComparison.Ordinal) || program.Contains('/', StringComparison.Ordinal))
        {
            return File.Exists(program) ? program : null;
        }

        string[] endings = (extensions ?? Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD")
                           .Split(';', StringSplitOptions.RemoveEmptyEntries);

        foreach (string folder in (path ?? Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                                  .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (string ending in endings)
            {
                string candidate = Path.Combine(folder.Trim('"'), program + ending);

                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// The WSL distributions whose shell finds a <c>claude</c> (QS363), each with where it found it. Asked
    /// only when the person asks, since asking starts every distribution that is stopped.
    /// </summary>
    /// <param name="wsl">Runs a program inside a distribution, or <c>wsl.exe</c> itself where the name is null.</param>
    public static IReadOnlyList<(string Distribution, string Claude)> InWsl(Func<string?, IReadOnlyList<string>, PluginStep>? wsl = null)
    {
        wsl ??= Wsl;

        PluginStep listed = wsl(null, ["-l", "-q"]);

        if (listed.ExitCode != 0)
        {
            return [];
        }

        List<(string, string)> found = [];

        foreach (string distribution in listed.Said.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            PluginStep where = wsl(distribution, ["sh", "-lc", "command -v claude"]);

            if (where.ExitCode == 0 && where.Said.Trim() is { Length: > 0 } path)
            {
                found.Add((distribution, path));
            }
        }

        return found;
    }

    /// <summary>
    /// Writes the plugin for a Claude Code inside <paramref name="distribution"/> (QS363): the same plugin,
    /// its commands naming this <c>quickshell.exe</c> by the <c>/mnt</c> path that distribution starts it by,
    /// in a marketplace folder of its own beside the Windows one.
    /// </summary>
    /// <returns>The marketplace's folder as Windows names it, and as the distribution does.</returns>
    public static (string Windows, string Inside) WriteForWsl(string folder, string distribution, string quickshell, string version,
                                                              Func<string?, IReadOnlyList<string>, PluginStep>? wsl = null)
    {
        wsl ??= Wsl;

        string inside = Inside(distribution, quickshell, wsl);
        string written = Write($"{folder}-wsl-{Safe(distribution)}", inside, version);

        return (written, Inside(distribution, written, wsl));
    }

    /// <summary>One step of the install, run by the distribution's own <c>claude</c> through its login shell, where its PATH is.</summary>
    public static PluginStep RunInWsl(string distribution, IReadOnlyList<string> step, Func<string?, IReadOnlyList<string>, PluginStep>? wsl = null) =>
        (wsl ?? Wsl)(distribution, ["sh", "-lc", "exec claude \"$@\"", "claude", .. step]);

    /// <summary>A path as <paramref name="distribution"/> names it, by its own <c>wslpath</c>.</summary>
    private static string Inside(string distribution, string path, Func<string?, IReadOnlyList<string>, PluginStep> wsl)
    {
        PluginStep converted = wsl(distribution, ["wslpath", "-u", path]);

        return converted.ExitCode == 0 && converted.Said.Trim() is { Length: > 0 } inside
            ? inside
            : throw new InvalidOperationException($"{distribution} could not name {path}: {converted.Said}");
    }

    private static string Safe(string name) => new([.. name.Select(letter => char.IsAsciiLetterOrDigit(letter) ? letter : '_')]);

    /// <summary>
    /// <c>wsl.exe</c>, with each word passed as its own argument and its output as UTF-8 (<c>WSL_UTF8</c>), so a
    /// distribution's name reads as itself; a distribution named runs the words there with no shell between.
    /// </summary>
    private static PluginStep Wsl(string? distribution, IReadOnlyList<string> words)
    {
        ProcessStartInfo start = new(Path.Combine(Environment.SystemDirectory, "wsl.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            Environment = { ["WSL_UTF8"] = "1" },
        };

        if (distribution is not null)
        {
            start.ArgumentList.Add("-d");
            start.ArgumentList.Add(distribution);
            start.ArgumentList.Add("-e");
        }

        foreach (string word in words)
        {
            start.ArgumentList.Add(word);
        }

        try
        {
            using Process wsl = Process.Start(start) ?? throw new InvalidOperationException("wsl.exe did not start.");

            Task<string> output = wsl.StandardOutput.ReadToEndAsync();
            Task<string> error = wsl.StandardError.ReadToEndAsync();

            if (!wsl.WaitForExit(TimeSpan.FromMinutes(2)))
            {
                wsl.Kill(entireProcessTree: true);

                return new PluginStep(words, -1, "wsl.exe did not answer within two minutes.");
            }

            return new PluginStep(words, wsl.ExitCode, (output.Result + error.Result).Replace("\0", string.Empty, StringComparison.Ordinal).Trim());
        }
        catch (System.ComponentModel.Win32Exception missing)
        {
            return new PluginStep(words, -1, $"WSL is not installed: {missing.Message}");
        }
    }

    private static void Json(string path, JsonObject value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, value.ToJsonString(Indented), new UTF8Encoding(false));
    }
}
