using System.IO;

using Quickshell.Terminal;

namespace Quickshell.App;

/// <summary>
/// Claude Code in a local pane, wired to the agent gateway, and <b>Send to Claude</b>, which gives it what a
/// pane shows with the session it came from (QS342).
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>
    /// Opens a local tab titled as given and running the command line given, and answers it — or null while
    /// nothing can. The program sets it, because the tab needs the device every pane shares.
    /// </summary>
    public Func<string, string, TerminalTab?>? OpensProgram { get; set; }

    /// <summary>Where the pane's two configuration files are written: quickshell's folder unless a caller says otherwise.</summary>
    public string? ClaudeConfigFolder { get; set; }

    /// <summary>The pane Claude Code runs in, while one this window opened is still running.</summary>
    public TerminalLeaf? ClaudeLeaf => _claude is { Ended: null } leaf && Held.Any(tab => tab.Leaves.Contains(leaf)) ? leaf : null;

    private TerminalLeaf? _claude;

    /// <summary>
    /// Opens Claude Code in a local pane with the gateway as its MCP server and the hook in place, or goes
    /// to the one already open. Said, and nothing opened, while the gateway is off: the pane exists to
    /// reach servers through it.
    /// </summary>
    /// <returns>The pane, or null where none was opened.</returns>
    public TerminalLeaf? OpenClaude()
    {
        if (ClaudeLeaf is { } open)
        {
            GoToLeaf(open);

            return open;
        }

        if (!Settings.AgentGateway)
        {
            Say("Claude Code opens here wired to the agent gateway, which is off. Turn on agentGateway in the settings "
                + "file (SETTINGS.md) and choose who may do what in agent-policy.json first.", "Claude Code");

            return null;
        }

        if (OpensProgram is not { } opens)
        {
            return null;
        }

        string quickshell = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "quickshell.exe");
        (string mcp, string settings) = ClaudePane.Configure(ClaudeConfigFolder ?? Locations.Current.Root, quickshell);

        TerminalTab? tab = opens(ClaudePane.Title, ClaudePane.CommandLine(LocalSession.Shell, Settings.ClaudeCommand, mcp, settings));

        _claude = tab?.Focused;

        return _claude;
    }

    /// <summary>
    /// Sends the selection in the pane with the keyboard, or its screen where nothing is selected, to the
    /// Claude pane, with a line naming the session and tags it came from — opening the pane first where
    /// there is none. Pasted, bracketed where Claude Code asked, and not entered: the person adds the
    /// question.
    /// </summary>
    /// <returns>What was sent, or empty where nothing was.</returns>
    public async Task<string> SendToClaude()
    {
        if (Current?.Focused is not { } source || source == ClaudeLeaf)
        {
            return string.Empty;
        }

        string selected = source.Terminal.Selected();
        string text = selected.Length > 0 ? selected : ClaudePane.ScreenText(source.Emulator.Buffer);

        if (text.Trim().Length == 0)
        {
            return string.Empty;
        }

        string from = Current.Saved ?? source.Host;
        // The tags it inherits as well as its own: a folder tagged prod is what makes the session prod.
        IReadOnlyList<string> tags = Current.Saved is { } saved && _store?.Session(saved) is { } session ? session.Tags : [];
        string context = ClaudePane.Context(from, tags, text);

        TerminalLeaf? claude = ClaudeLeaf;

        if (claude is null)
        {
            claude = OpenClaude();

            // A pane just opened has no program reading yet: what is typed before Claude Code draws is
            // read by the shell starting it, or by nothing.
            if (claude is null || !await Drawn(claude).ConfigureAwait(true))
            {
                return string.Empty;
            }
        }

        char[] cleaned = new char[Paste.MeasureClean(context)];
        string sent = new(cleaned, 0, Paste.Clean(context, cleaned));

        if (claude.Emulator.BracketedPaste)
        {
            sent = Paste.Start + sent + Paste.Finish;
        }

        claude.Typist.Type(sent, System.Windows.Input.ModifierKeys.None);
        GoToLeaf(claude);

        return sent;
    }

    /// <summary>Whether the pane has drawn anything, waited for up to half a minute and a moment more after.</summary>
    private static async Task<bool> Drawn(TerminalLeaf leaf)
    {
        for (DateTime giveUp = DateTime.UtcNow.AddSeconds(30); DateTime.UtcNow < giveUp; )
        {
            if (leaf.Ended is not null)
            {
                return false;
            }

            if (ClaudePane.ScreenText(leaf.Emulator.Buffer).Length > 0)
            {
                // Drawn is not yet reading: the first frame comes a moment before the prompt takes input.
                await Task.Delay(TimeSpan.FromSeconds(1.5)).ConfigureAwait(true);

                return true;
            }

            await Task.Delay(200).ConfigureAwait(true);
        }

        return false;
    }

    /// <summary>Puts the tab holding <paramref name="leaf"/> on screen: the Claude pane's, which holds it alone.</summary>
    private void GoToLeaf(TerminalLeaf leaf)
    {
        if (_open.FindIndex(tab => tab.Leaves.Contains(leaf)) is >= 0 and var at)
        {
            Active = at;
            Focus();
        }
    }

    /// <summary>The palette: open Claude Code in a pane (QS342).</summary>
    private sealed class OpeningClaude(MainWindow window) : Doing(window)
    {
        /// <inheritdoc/>
        public override string Name => "Open Claude Code";

        /// <inheritdoc/>
        public override void Execute(object? parameter) => Window.OpenClaude();
    }

    /// <summary>The palette: send the selection, or the screen, to Claude Code (QS342).</summary>
    private sealed class SendingToClaude(MainWindow window) : Doing(window)
    {
        /// <inheritdoc/>
        public override string Name => "Send to Claude";

        /// <inheritdoc/>
        public override void Execute(object? parameter) => _ = Window.SendToClaude();
    }
}
