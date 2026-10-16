using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using System.Windows.Controls;

using Xunit;

namespace Quickshell.App.Tests;

/// <summary>QS343: the agent gateway as a Claude Code plugin, written by the client and installed in one answer.</summary>
public sealed class ClaudePluginTests : IDisposable
{
    private const string Exe = @"C:\Program Files\quickshell\quickshell.exe";

    private readonly string _here = Path.Combine(Path.GetTempPath(), "qs-plugin-" + Guid.NewGuid().ToString("N"));

    public ClaudePluginTests() => Directory.CreateDirectory(_here);

    public void Dispose() => Directory.Delete(_here, recursive: true);

    /// <summary>
    /// The marketplace holds the plugin, whose MCP entry starts this copy's gateway with its version, whose
    /// hook is this copy's, and whose skill is the one built in. Where Claude Code is on the path, it says
    /// the result is a valid plugin and marketplace, warnings counted as errors.
    /// </summary>
    [Fact]
    public void TheWrittenPluginNamesThisCopyAndItsVersion()
    {
        string folder = ClaudePlugin.Write(Path.Combine(_here, "market"), Exe, "1.2.3");
        string plugin = Path.Combine(folder, "quickshell");

        JsonNode market = Read(Path.Combine(folder, ".claude-plugin", "marketplace.json"));
        JsonNode manifest = Read(Path.Combine(plugin, ".claude-plugin", "plugin.json"));
        JsonNode hook = Read(Path.Combine(plugin, "hooks", "hooks.json"))["hooks"]!["PreToolUse"]![0]!;
        JsonNode server = manifest["mcpServers"]!["quickshell"]!;

        Assert.Equal("quickshell-local", market["name"]!.GetValue<string>());
        Assert.Equal("./quickshell", market["plugins"]![0]!["source"]!.GetValue<string>());
        Assert.Equal("1.2.3", manifest["version"]!.GetValue<string>());
        Assert.Equal(Exe, server["command"]!.GetValue<string>());
        Assert.Equal(["--mcp", "--plugin-version", "1.2.3"], server["args"]!.AsArray().Select(word => word!.GetValue<string>()));
        Assert.Equal($"\"{Exe}\" --agent-hook", hook["hooks"]![0]!["command"]!.GetValue<string>());
        Assert.Contains("name: quickshell-gateway", File.ReadAllText(Path.Combine(plugin, "skills", "quickshell-gateway", "SKILL.md")), StringComparison.Ordinal);

        // Written again, for another version, over the first.
        ClaudePlugin.Write(folder, Exe, "1.2.4");

        Assert.Equal("1.2.4", Read(Path.Combine(plugin, ".claude-plugin", "plugin.json"))["version"]!.GetValue<string>());

        if (ClaudePlugin.Find("claude") is not null)
        {
            foreach (string each in new[] { folder, plugin })
            {
                PluginStep validated = ClaudePlugin.Run("claude", ["plugin", "validate", "--strict", each]);

                Assert.True(validated.ExitCode == 0, $"claude plugin validate {each}: {validated.Said}");
            }
        }
    }

    /// <summary>A gateway refuses a plugin another version wrote, naming both, and serves one with none or the same.</summary>
    [Fact]
    public void AGatewayRefusesAPluginOfAnotherVersion()
    {
        Assert.Null(ClaudePlugin.Mismatch(null, "1.2.3"));
        Assert.Null(ClaudePlugin.Mismatch("1.2.3", "1.2.3"));
        Assert.Equal("the quickshell plugin for Claude Code was written by quickshell 1.2.2, and this is quickshell 1.2.3. "
                     + "Install the plugin again from quickshell's Settings, which writes it for this version.",
                     ClaudePlugin.Mismatch("1.2.2", "1.2.3"));
    }

    /// <summary>The built quickshell.exe, started by a stale plugin, refuses on standard error and exits 2 before reading anything.</summary>
    [Fact]
    public void TheBuiltGatewaySaysWhyItRefusesAStalePlugin()
    {
        string exe = Path.Combine(AppContext.BaseDirectory, "quickshell.exe");

        using Process gateway = Process.Start(new ProcessStartInfo(exe)
        {
            ArgumentList = { "--mcp", "--plugin-version", "0.0.1" },
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            Environment = { ["QUICKSHELL_DATA"] = _here },
        })!;

        string said = gateway.StandardError.ReadToEnd();

        Assert.True(gateway.WaitForExit(TimeSpan.FromSeconds(30)), "the gateway kept running for a stale plugin");
        Assert.Equal(2, gateway.ExitCode);
        Assert.Contains("written by quickshell 0.0.1, and this is quickshell", said, StringComparison.Ordinal);
    }

    /// <summary>Installing takes off an earlier marketplace (a failure there meaning nothing), adds it, installs, and stops at a failure after that.</summary>
    [Fact]
    public void InstallingStopsAtTheFirstFailureThatMeansSomething()
    {
        List<string> asked = [];

        IReadOnlyList<PluginStep> ran = ClaudePlugin.Install(@"C:\data\claude-plugin", step =>
        {
            asked.Add(string.Join(' ', step));

            return new PluginStep(step, step[1] == "marketplace" && step[2] == "remove" ? 1 : 0, "ok");
        });

        Assert.Equal(["plugin marketplace remove quickshell-local", @"plugin marketplace add C:\data\claude-plugin",
                      "plugin install quickshell@quickshell-local -s user"], asked);
        Assert.Equal(0, ran[^1].ExitCode);

        IReadOnlyList<PluginStep> refused = ClaudePlugin.Install(@"C:\data\claude-plugin",
                                                                 step => new PluginStep(step, step[2] == "add" ? 1 : 0, "no such path"));

        Assert.Equal(2, refused.Count);
    }

    /// <summary>claude is found on the path by any extension Windows runs, and a full path is taken as itself.</summary>
    [Fact]
    public void ClaudeIsFoundOnThePath()
    {
        string bin = Path.Combine(_here, "bin");

        Directory.CreateDirectory(bin);
        File.WriteAllText(Path.Combine(bin, "claude.cmd"), "@echo off");

        // Windows names files without regard to case, so the extension comes back as PATHEXT spells it.
        Assert.Equal(Path.Combine(bin, "claude.cmd"), ClaudePlugin.Find("claude", $@"C:\nowhere;{bin}", ".EXE;.CMD"), StringComparer.OrdinalIgnoreCase);
        Assert.Null(ClaudePlugin.Find("claude", @"C:\nowhere", ".EXE;.CMD"));
        Assert.Equal(Path.Combine(bin, "claude.cmd"), ClaudePlugin.Find(Path.Combine(bin, "claude.cmd")));
    }

    /// <summary>
    /// Settings says what installing changes before anything runs, writes the plugin, runs the steps through
    /// claude and says how it went; told no, it runs nothing.
    /// </summary>
    [Fact]
    public void SettingsAsksFirstThenInstalls()
    {
        string settings = Path.Combine(_here, "settings.json");
        string plugin = Path.Combine(_here, "claude-plugin");

        File.WriteAllText(settings, "{}");

        (string? asked, int declined, string[] ran, string said, bool written) = Sta.Run(() =>
        {
            // The window's own context, so what follows the install's await comes back to this thread.
            SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext());

            List<string> commands = [];
            string? question = null;
            SettingsWindow window = new(settings)
            {
                PluginFolder = plugin,
                RunsClaude = step =>
                {
                    commands.Add(string.Join(' ', step));

                    return new PluginStep(step, 0, string.Empty);
                },
                AsksToInstallPlugin = _ => false,
            };

            int no = window.InstallClaudePlugin().GetAwaiter().GetResult().Count;

            window.AsksToInstallPlugin = text =>
            {
                question = text;

                return true;
            };

            Task<IReadOnlyList<PluginStep>> installing = window.InstallClaudePlugin();

            while (!installing.IsCompleted)
            {
                System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);
                Thread.Sleep(20);
            }

            TextBlock result = (TextBlock)((StackPanel)((ScrollViewer)window.Content).Content).Children
                .OfType<TextBlock>().Single(block => System.Windows.Automation.AutomationProperties.GetAutomationId(block) == "ClaudePluginSaid");

            return (question, no, commands.ToArray(), result.Text, File.Exists(Path.Combine(plugin, "quickshell", ".claude-plugin", "plugin.json")));
        });

        Assert.Equal(0, declined);
        Assert.NotNull(asked);
        Assert.Contains("--agent-hook", asked, StringComparison.Ordinal);
        Assert.Contains("claude plugin install quickshell@quickshell-local -s user", asked, StringComparison.Ordinal);
        Assert.Equal(3, ran.Length);
        Assert.StartsWith("Installed: quickshell ", said, StringComparison.Ordinal);
        Assert.True(written, "the plugin was not written before it was installed");
    }

    /// <summary>
    /// QS363, against a stand-in wsl.exe: the distributions with claude are found and the one without is not;
    /// the plugin written for one names this copy by its /mnt path; and its three steps run through that
    /// distribution's own claude, from the marketplace's /mnt path.
    /// </summary>
    [Fact]
    public void TheWslCopyNamesTheClientByItsMntPathAndInstallsThere()
    {
        List<string> calls = [];

        PluginStep Wsl(string? distribution, IReadOnlyList<string> words)
        {
            calls.Add($"{distribution ?? "-"}: {string.Join(' ', words)}");

            return (distribution, words) switch
            {
                (null, ["-l", "-q"]) => new PluginStep(words, 0, "Ubuntu\nDebian\n"),
                ("Ubuntu", ["sh", "-lc", "command -v claude"]) => new PluginStep(words, 0, "/home/me/.local/bin/claude\n"),
                (_, ["sh", "-lc", "command -v claude"]) => new PluginStep(words, 1, string.Empty),
                (_, ["wslpath", "-u", var path]) => new PluginStep(words, 0, "/mnt/" + char.ToLowerInvariant(path[0]) + path[2..].Replace('\\', '/')),
                _ => new PluginStep(words, 0, "ok"),
            };
        }

        Assert.Equal([("Ubuntu", "/home/me/.local/bin/claude")], ClaudePlugin.InWsl(Wsl));

        (string windows, string inside) = ClaudePlugin.WriteForWsl(Path.Combine(_here, "claude-plugin"), "Ubuntu", @"C:\Program Files\quickshell\quickshell.exe", "1.2.3", Wsl);
        JsonNode manifest = Read(Path.Combine(windows, "quickshell", ".claude-plugin", "plugin.json"));

        Assert.EndsWith("claude-plugin-wsl-Ubuntu", windows, StringComparison.Ordinal);
        Assert.StartsWith("/mnt/", inside, StringComparison.Ordinal);
        Assert.Equal("/mnt/c/Program Files/quickshell/quickshell.exe", manifest["mcpServers"]!["quickshell"]!["command"]!.GetValue<string>());
        Assert.Equal("\"/mnt/c/Program Files/quickshell/quickshell.exe\" --agent-hook",
                     Read(Path.Combine(windows, "quickshell", "hooks", "hooks.json"))["hooks"]!["PreToolUse"]![0]!["hooks"]![0]!["command"]!.GetValue<string>());

        calls.Clear();

        IReadOnlyList<PluginStep> ran = ClaudePlugin.Install(inside, step => ClaudePlugin.RunInWsl("Ubuntu", step, Wsl));

        Assert.Equal(3, ran.Count);
        Assert.Equal($"Ubuntu: sh -lc exec claude \"$@\" claude plugin marketplace add {inside}", calls[1]);
        Assert.Equal("Ubuntu: sh -lc exec claude \"$@\" claude plugin install quickshell@quickshell-local -s user", calls[2]);
    }

    /// <summary>Settings looks in WSL only when asked, says so where no distribution has claude, and installs into the one picked after asking.</summary>
    [Fact]
    public void SettingsInstallsIntoWslWhenAsked()
    {
        string settings = Path.Combine(_here, "settings.json");

        File.WriteAllText(settings, "{}");

        (string none, string? asked, int ran, string said) = Sta.Run(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext());

            bool any = false;
            string? question = null;
            SettingsWindow window = new(settings)
            {
                PluginFolder = Path.Combine(_here, "claude-plugin"),
                AsksToInstallPlugin = text =>
                {
                    question = text;

                    return true;
                },
                RunsWsl = (distribution, words) => (distribution, words) switch
                {
                    (null, _) => new PluginStep(words, 0, "Ubuntu"),
                    (_, ["sh", "-lc", "command -v claude"]) => new PluginStep(words, any ? 0 : 1, any ? "/usr/bin/claude" : string.Empty),
                    (_, ["wslpath", "-u", var path]) => new PluginStep(words, 0, "/mnt/c/" + Path.GetFileName(path)),
                    _ => new PluginStep(words, 0, "ok"),
                },
            };

            T Wait<T>(Task<T> task)
            {
                while (!task.IsCompleted)
                {
                    System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);
                    Thread.Sleep(20);
                }

                return task.Result;
            }

            TextBlock result() => (TextBlock)((StackPanel)((ScrollViewer)window.Content).Content).Children
                .OfType<TextBlock>().Single(block => System.Windows.Automation.AutomationProperties.GetAutomationId(block) == "ClaudePluginSaid");

            Wait(window.InstallClaudePluginInWsl());

            string nowhere = result().Text;

            any = true;

            int steps = Wait(window.InstallClaudePluginInWsl()).Count;

            return (nowhere, question, steps, result().Text);
        });

        Assert.StartsWith("No WSL distribution here finds claude", none, StringComparison.Ordinal);
        Assert.Contains("Turn Claude Code's sandbox on in that distribution", asked, StringComparison.Ordinal);
        Assert.Equal(3, ran);
        Assert.StartsWith("Installed into Ubuntu: quickshell ", said, StringComparison.Ordinal);
    }

    private static JsonNode Read(string path) => JsonNode.Parse(File.ReadAllText(path))!;
}
