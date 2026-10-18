using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using Quickshell.Terminal;

namespace Quickshell.App;

/// <summary>
/// A window over the settings file, with the file still in charge (QS170).
///
/// <para><b>Every change is a write, and every write starts from the file.</b> There is no OK button
/// and no copy held here: a control that changes reads the file as it is now, changes its one key,
/// and writes it back through <see cref="SettingsFile.WriteTo"/>, which edits the value where it sits
/// and leaves the user's comments alone. The watch that already applies a hand edit applies this one,
/// so the window is a second way of typing into the file and never a second source of truth.</para>
///
/// <para><b>One control per documented key, said in the reference's own words.</b> What each key
/// means is a sentence out of <c>docs/SETTINGS.md</c>, and a test holds the two together; new prose
/// here would be a second description to drift from the first. The window is also not where a new
/// setting appears — a key comes first, and a control follows it.</para>
///
/// <para>A file that does not parse is not written over. It reads as the defaults, and writing those
/// back would replace somebody's half-finished edit with nothing of theirs, so the window says so
/// and leaves the file to be fixed by hand.</para>
/// </summary>
public sealed class SettingsWindow : Window
{
    /// <summary>What each key means, in <c>docs/SETTINGS.md</c>'s words, in the order it lists them.</summary>
    public static readonly IReadOnlyList<(string Key, string Label, string Means)> Keys =
    [
        ("theme", "Theme", "How the chrome is painted — the title bar, the tab strip, the dialogs."),
        ("fontFamily", "Font", "The terminal's typeface, by name. Anything installed on the machine."),
        ("fontSize", "Font size", "Its size in points. The grid follows: a bigger font in the same window is fewer columns, and the program on the far end is told."),
        ("scrollback", "Scrollback", "How many lines each session keeps once they have left the screen. Ten thousand by default."),
        ("ligatures", "Ligatures", "Whether the font's ligatures are formed — whether != is drawn as two characters or as one."),
        ("cursor", "Cursor", "Block, Underline or Bar."),
        ("cursorBlink", "Cursor blinks", "Whether the cursor blinks."),
        ("warnOnPaste", "Warn on paste", "Whether a paste carrying a newline is shown before any of it is sent."),
        ("colourScheme", "Colour scheme", "A path to a scheme file. Empty — the default — is the built-in scheme."),
        ("sessionsSidebar", "Sessions sidebar", "Whether the saved sessions are listed down the left of the window, or folded to a narrow rail."),
        ("startWith", "Start with", "What a new window opens on: Home, or a local shell."),
        ("vaultFile", "Vault kept up to date", "The KeePass vault the saved passwords are written to again whenever one changes. Empty, the default, keeps none."),
        ("tagColours", "Tag colours", "Which tags a tab shows besides prod, and in which colour, as staging=caution, db=accent. Empty, the default, shows prod alone."),
        ("agentGateway", "Agent gateway", "Whether an agent such as Claude Code may run commands on your saved sessions through quickshell --mcp. Off by default. What it may run on each is the agent policy's to say."),
        ("claudeCommand", "Claude Code command", "What Open Claude Code runs in its pane, found on the path: claude by default."),
        ("agentCertificateHello", "Windows Hello before an agent signs in", "Whether Windows Hello asks for you before quickshell signs an agent a certificate. Off by default; on, an agent signs in only while you are at the desk to answer."),
        ("menuBar", "Menu bar", "When the menu bar of every action is on screen: Hidden, Alt (shown by pressing and releasing Alt) or Always (the default)."),
    ];

    private readonly string _path;
    private readonly Dictionary<string, FrameworkElement> _controls = new(StringComparer.Ordinal);
    private readonly TextBlock _refused = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12), Name = "Unused" };

    private bool _loading;

    /// <summary>Opens the window over a settings file.</summary>
    /// <param name="path">The file, which need not exist yet.</param>
    public SettingsWindow(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        _path = path;

        Title = "Settings";
        Width = 560;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        AutomationProperties.SetAutomationId(this, "Settings");

        StackPanel body = new() { Margin = new Thickness(18) };

        body.Children.Add(new TextBlock
        {
            Text = $"These are written to {path} as you change them, and applied at once. The file can "
                   + "also be edited by hand; it is the setting, and this is a way of typing into it.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 14),
        });

        body.Children.Add(_refused);

        foreach ((string key, string label, string means) in Keys)
        {
            FrameworkElement control = Control(key);

            AutomationProperties.SetAutomationId(control, key);
            AutomationProperties.SetName(control, label);
            AutomationProperties.SetHelpText(control, means);

            _controls[key] = control;

            body.Children.Add(new TextBlock { Text = label, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 6, 0, 2) });
            body.Children.Add(control);
            body.Children.Add(new TextBlock { Text = means, TextWrapping = TextWrapping.Wrap, Opacity = 0.75, Margin = new Thickness(0, 2, 0, 6) });
        }

        // The Claude Code plugin (QS343): offered where claude is on the path, never installed unasked.
        _plugin = new Button { Content = "Install the Claude Code plugin…", HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(12, 3, 12, 3) };
        _pluginSaid = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.75, Margin = new Thickness(0, 4, 0, 6) };

        AutomationProperties.SetAutomationId(_plugin, "InstallClaudePlugin");
        AutomationProperties.SetAutomationId(_pluginSaid, "ClaudePluginSaid");
        _plugin.Click += (_, _) => _ = InstallClaudePlugin();

        // And into a Claude Code inside WSL2, the wall QS358 recommends (QS363): looked for only when asked,
        // since looking starts every distribution that is stopped.
        Button wsl = new() { Content = "Install into WSL…", HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(12, 3, 12, 3), Margin = new Thickness(0, 4, 0, 0) };

        AutomationProperties.SetAutomationId(wsl, "InstallClaudePluginWsl");
        wsl.Click += (_, _) => _ = InstallClaudePluginInWsl();

        body.Children.Add(new TextBlock { Text = "Claude Code plugin", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 2) });
        body.Children.Add(_plugin);
        body.Children.Add(wsl);
        body.Children.Add(_pluginSaid);

        Content = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 720 };

        Reload();
    }

    private readonly Button _plugin;
    private readonly TextBlock _pluginSaid;

    /// <summary>Asks whether to install the plugin, given what it will change; a dialog unless a caller says otherwise.</summary>
    public Func<string, bool>? AsksToInstallPlugin { get; set; }

    /// <summary>Runs one step of the install through claude; <see cref="ClaudePlugin.Run"/> unless a caller says otherwise.</summary>
    public Func<IReadOnlyList<string>, PluginStep>? RunsClaude { get; set; }

    /// <summary>Where the plugin is written: <c>claude-plugin</c> in quickshell's folder unless a caller says otherwise.</summary>
    public string? PluginFolder { get; set; }

    /// <summary>Where Claude Code was found, or null; asked again each time the window reloads.</summary>
    public string? ClaudeFound { get; private set; }

    /// <summary>
    /// Writes the plugin and installs it into Claude Code, after saying exactly what that changes and being
    /// told yes (QS343), and says how it went under the button.
    /// </summary>
    /// <returns>Each step run, empty where the person said no or claude is not there.</returns>
    public async Task<IReadOnlyList<PluginStep>> InstallClaudePlugin()
    {
        string claude = SettingsFile.ReadFrom(_path).ClaudeCommand;

        if (ClaudePlugin.Find(claude) is null && RunsClaude is null)
        {
            return [];
        }

        string quickshell = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "quickshell.exe");
        string folder = PluginFolder ?? Path.Combine(Locations.Current.Root, "claude-plugin");
        string changes =
            $"This writes a Claude Code plugin into {folder}, and asks Claude Code ({claude}) to install it for your user:\n\n"
            + $"• the MCP server \"{ClaudePlugin.Name}\", which runs {quickshell} --mcp;\n"
            + $"• a hook before every shell command and file write, which runs {quickshell} --agent-hook and turns back ssh "
            + "around the gateway and writes to the files that limit agents;\n"
            + "• a skill telling Claude which gateway tool does what.\n\n"
            + "Claude Code commands it runs:\n"
            + string.Join("\n", ClaudePlugin.Steps(folder).Select(step => $"  {claude} {string.Join(' ', step)}"))
            + "\n\nWhat agents may do on each session is still the agent policy's to say, and agentGateway must be on.";

        bool yes = AsksToInstallPlugin is { } asks
            ? asks(changes)
            : Quickshell.App.Choice.Ask(this, "Install the Claude Code plugin", changes,
                         new ChoiceButton("Install it", "install"), new ChoiceButton("Not now", "cancel")) == "install";

        if (!yes)
        {
            return [];
        }

        _plugin.IsEnabled = false;
        _pluginSaid.Text = "Installing…";

        Func<IReadOnlyList<string>, PluginStep> run = RunsClaude ?? new Func<IReadOnlyList<string>, PluginStep>(step => ClaudePlugin.Run(claude, step));
        IReadOnlyList<PluginStep> ran = await Task.Run(() => ClaudePlugin.Install(
            ClaudePlugin.Write(folder, quickshell, Setup.Version), run)).ConfigureAwait(true);

        PluginStep last = ran[^1];

        _pluginSaid.Text = last.ExitCode == 0
            ? $"Installed: quickshell {Setup.Version}'s plugin is in Claude Code for your user. Start a new Claude Code session to use it."
            : $"Not installed: {claude} {string.Join(' ', last.Command)} said: {last.Said}";
        _plugin.IsEnabled = true;

        return ran;
    }

    /// <summary>Runs <c>wsl.exe</c>, or a program inside a distribution; the real one unless a caller says otherwise.</summary>
    public Func<string?, IReadOnlyList<string>, PluginStep>? RunsWsl { get; set; }

    /// <summary>Picks one of the distributions with Claude Code, or null for none; a dialog unless a caller says otherwise.</summary>
    public Func<IReadOnlyList<string>, string?>? PicksWsl { get; set; }

    /// <summary>
    /// Installs the plugin into a Claude Code inside WSL2 (QS363): the distributions with <c>claude</c> found,
    /// one picked, what will change said and agreed to, the plugin written with this <c>quickshell.exe</c>'s
    /// <c>/mnt</c> path, and installed with that distribution's own <c>claude plugin</c>.
    /// </summary>
    /// <returns>Each step run, empty where nothing was.</returns>
    public async Task<IReadOnlyList<PluginStep>> InstallClaudePluginInWsl()
    {
        _pluginSaid.Text = "Looking for Claude Code in WSL…";

        Func<string?, IReadOnlyList<string>, PluginStep>? wsl = RunsWsl;
        IReadOnlyList<(string Distribution, string Claude)> found = await Task.Run(() => ClaudePlugin.InWsl(wsl)).ConfigureAwait(true);

        if (found.Count == 0)
        {
            _pluginSaid.Text = "No WSL distribution here finds claude on its path, so there is nowhere in WSL to install the plugin.";

            return [];
        }

        string[] names = [.. found.Select(each => each.Distribution)];
        string? picked = names.Length == 1 && PicksWsl is null
            ? names[0]
            : (PicksWsl ?? (choices => Quickshell.App.Choice.Ask(this, "Install into WSL", "Which distribution's Claude Code?",
                                                                  [.. choices.Take(3).Select(name => new ChoiceButton(name, name)), new ChoiceButton("Not now", string.Empty)])))(names);

        if (string.IsNullOrEmpty(picked))
        {
            _pluginSaid.Text = string.Empty;

            return [];
        }

        string quickshell = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "quickshell.exe");
        string folder = PluginFolder ?? Path.Combine(Locations.Current.Root, "claude-plugin");
        string changes =
            $"This writes a Claude Code plugin into {folder}-wsl-…, naming {quickshell} by the path {picked} starts it by, and asks the "
            + $"Claude Code in {picked} ({found.Single(each => each.Distribution == picked).Claude}) to install it for your user: the MCP "
            + "server \"quickshell\" and the hook that turns back ssh around it, as on Windows.\n\n"
            + "Turn Claude Code's sandbox on in that distribution too (sandbox.enabled, failIfUnavailable, allowUnsandboxedCommands: false): "
            + "it is what stops a script the hook cannot see, and the plugin does not change your Claude Code settings.";

        bool yes = AsksToInstallPlugin is { } asks
            ? asks(changes)
            : Quickshell.App.Choice.Ask(this, "Install the Claude Code plugin into WSL", changes,
                                        new ChoiceButton("Install it", "install"), new ChoiceButton("Not now", "cancel")) == "install";

        if (!yes)
        {
            _pluginSaid.Text = string.Empty;

            return [];
        }

        _pluginSaid.Text = "Installing…";

        IReadOnlyList<PluginStep> ran = await Task.Run(() =>
        {
            (_, string inside) = ClaudePlugin.WriteForWsl(folder, picked, quickshell, Setup.Version, wsl);

            return ClaudePlugin.Install(inside, step => ClaudePlugin.RunInWsl(picked, step, wsl));
        }).ConfigureAwait(true);

        PluginStep last = ran[^1];

        _pluginSaid.Text = last.ExitCode == 0
            ? $"Installed into {picked}: quickshell {Setup.Version}'s plugin is in its Claude Code. Start a new Claude Code session there to use it."
            : $"Not installed into {picked}: claude {string.Join(' ', last.Command)} said: {last.Said}";

        return ran;
    }

    /// <summary>The offer as it stands: a button where claude is found, and a sentence either way.</summary>
    private void Offered(Settings now)
    {
        ClaudeFound = ClaudePlugin.Find(now.ClaudeCommand);
        _plugin.IsEnabled = ClaudeFound is not null;
        _pluginSaid.Text = ClaudeFound is { } found
            ? $"Claude Code is at {found}. Installing adds quickshell's agent gateway to it in one step, and says first what it changes."
            : $"{now.ClaudeCommand} is not on the path, so there is no Claude Code to install the plugin into.";
    }

    /// <summary>What the window is saying about the file, which is empty where the file is all used.</summary>
    public string Unused => _refused.Text;

    /// <summary>The control for one key, for a caller that reads or changes it.</summary>
    public FrameworkElement ControlFor(string key) => _controls[key];

    /// <summary>
    /// Shows the file as it is now — what a person reopening the window, or a hand edit landing
    /// while it is open, should see.
    /// </summary>
    public void Reload()
    {
        bool readable = Say();

        Settings now = SettingsFile.ReadFrom(_path);

        _loading = true;

        try
        {
            ((ComboBox)_controls["theme"]).SelectedItem = now.Theme;
            ((TextBox)_controls["fontFamily"]).Text = now.FontFamily;
            ((TextBox)_controls["fontSize"]).Text = now.FontSize.ToString(CultureInfo.InvariantCulture);
            ((TextBox)_controls["scrollback"]).Text = now.Scrollback.ToString(CultureInfo.InvariantCulture);
            ((CheckBox)_controls["ligatures"]).IsChecked = now.Ligatures;
            ((ComboBox)_controls["cursor"]).SelectedItem = now.Cursor;
            ((CheckBox)_controls["cursorBlink"]).IsChecked = now.CursorBlink;
            ((CheckBox)_controls["warnOnPaste"]).IsChecked = now.WarnOnPaste;
            ((TextBox)_controls["colourScheme"]).Text = now.Scheme;
            ((CheckBox)_controls["sessionsSidebar"]).IsChecked = now.SessionsSidebar;
            ((ComboBox)_controls["startWith"]).SelectedItem = now.StartWith;
            ((TextBox)_controls["vaultFile"]).Text = now.VaultFile;
            ((TextBox)_controls["tagColours"]).Text = now.TagColours;
            ((CheckBox)_controls["agentGateway"]).IsChecked = now.AgentGateway;
            ((TextBox)_controls["claudeCommand"]).Text = now.ClaudeCommand;
            ((ComboBox)_controls["menuBar"]).SelectedItem = now.MenuBar;
            ((CheckBox)_controls["agentCertificateHello"]).IsChecked = now.AgentCertificateHello;
        }
        finally
        {
            _loading = false;
        }

        foreach (FrameworkElement control in _controls.Values)
        {
            control.IsEnabled = readable;
        }

        Offered(now);
    }

    /// <summary>
    /// Reads the file, changes one key, and writes it back — the whole of what a control does.
    /// </summary>
    /// <returns>Whether anything was written: not while loading, and not over a file that does not parse.</returns>
    public bool Change(Func<Settings, Settings> edit)
    {
        ArgumentNullException.ThrowIfNull(edit);

        if (_loading || !SettingsFile.Readable(_path))
        {
            return false;
        }

        SettingsFile.WriteTo(_path, edit(SettingsFile.ReadFrom(_path)));

        // A scheme path typed here that leads nowhere is said here, at once.
        Say();

        return true;
    }

    /// <summary>
    /// Says what the file asked for that this client could not use, one line each and nothing else in
    /// the way (QS174) — the difference between a feature that does not work and a value with a typo
    /// in it, said where somebody looking at their settings is looking.
    /// </summary>
    /// <returns>Whether the file parses at all.</returns>
    private bool Say()
    {
        bool readable = SettingsFile.Readable(_path);
        List<string> said = [.. SettingsFile.Unused(_path)];

        if (!readable)
        {
            said.Add("Nothing here will be written over it. Fix it by hand — the last thing written to it "
                     + "was probably mid-edit — and reopen this window.");
        }

        _refused.Text = string.Join(Environment.NewLine, said);
        _refused.Visibility = said.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

        return readable;
    }

    private FrameworkElement Control(string key) => key switch
    {
        "theme" => Choice(Enum.GetValues<ChromeTheme>().Cast<object>(),
                          value => Change(now => now with { Theme = (ChromeTheme)value })),
        "cursor" => Choice([CursorShape.Block, CursorShape.Underline, CursorShape.Bar],
                           value => Change(now => now with { Cursor = (CursorShape)value })),
        "menuBar" => Choice(Enum.GetValues<MenuBarMode>().Cast<object>(),
                            value => Change(now => now with { MenuBar = (MenuBarMode)value })),
        "startWith" => Choice(Enum.GetValues<StartWith>().Cast<object>(),
                              value => Change(now => now with { StartWith = (StartWith)value })),
        "ligatures" => Toggle(on => Change(now => now with { Ligatures = on })),
        "cursorBlink" => Toggle(on => Change(now => now with { CursorBlink = on })),
        "warnOnPaste" => Toggle(on => Change(now => now with { WarnOnPaste = on })),
        "sessionsSidebar" => Toggle(on => Change(now => now with { SessionsSidebar = on })),
        "agentGateway" => Toggle(on => Change(now => now with { AgentGateway = on })),
        "agentCertificateHello" => Toggle(on => Change(now => now with { AgentCertificateHello = on })),
        "fontFamily" => Field(text => text.Trim().Length > 0 && Change(now => now with { FontFamily = text.Trim() })),
        "fontSize" => Field(text => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double size)
                                    && size > 0 && Change(now => now with { FontSize = size })),
        "scrollback" => Field(text => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int lines)
                                      && lines >= 0 && Change(now => now with { Scrollback = lines })),
        "colourScheme" => Field(text => Change(now => now with { Scheme = text.Trim() })),
        "vaultFile" => Field(text => Change(now => now with { VaultFile = text.Trim() })),
        "tagColours" => Field(text => Change(now => now with { TagColours = text.Trim() })),
        "claudeCommand" => Field(text => Change(now => now with { ClaudeCommand = text.Trim().Length > 0 ? text.Trim() : Settings.Default.ClaudeCommand })),
        _ => throw new ArgumentOutOfRangeException(nameof(key), key, "no control for this key"),
    };

    private static ComboBox Choice(IEnumerable<object> values, Action<object> chosen)
    {
        ComboBox box = new() { ItemsSource = values.ToArray() };

        box.SelectionChanged += (_, _) =>
        {
            if (box.SelectedItem is { } value)
            {
                chosen(value);
            }
        };

        return box;
    }

    private static CheckBox Toggle(Action<bool> toggled)
    {
        CheckBox box = new();

        box.Checked += (_, _) => toggled(true);
        box.Unchecked += (_, _) => toggled(false);

        return box;
    }

    /// <summary>
    /// A text field, written when it is left or Enter is pressed rather than on every keystroke: a
    /// font name typed a letter at a time is eleven fonts that do not exist on the way to one that
    /// does. A value that does not make sense is not written, and the field keeps it for fixing.
    /// </summary>
    private static TextBox Field(Func<string, bool> committed)
    {
        TextBox box = new();

        box.LostKeyboardFocus += (_, _) => committed(box.Text);
        box.KeyDown += (_, pressed) =>
        {
            if (pressed.Key == System.Windows.Input.Key.Enter)
            {
                committed(box.Text);
            }
        };

        return box;
    }
}
