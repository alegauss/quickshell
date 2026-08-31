using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Quickshell.App;

/// <summary>
/// The window, and the argument it makes by what is not in it.
///
/// <para><b>A title bar, a terminal, and nothing else.</b> The tab strip builds itself and stays
/// collapsed while there is one tab. There is no toolbar, no status bar and no sidebar — not hidden
/// ones waiting to be switched on, but no elements at all, so that adding one is a change somebody
/// makes on purpose against a test that says what a default installation looks like.</para>
///
/// <para><b>The theme is handed to WPF rather than resolved here.</b> <c>ThemeMode.System</c> watches
/// the setting and repaints while the window is open, which is what "follow the system" has to mean
/// — reading it once at start-up is the bug that passes every test anybody runs by restarting.</para>
///
/// <para><b>Nothing on this path reads configuration or touches a network.</b> The window is
/// constructed, shown and interactive before any session work begins, because cold start is a number
/// this project publishes and the first paint is the half of it a user feels.</para>
/// </summary>
public sealed class MainWindow : Window
{
    private readonly ContentControl _terminal = new();
    private readonly TabControl _tabs = new();

    private bool _recording;

    /// <summary>Builds the window. Reads no file, opens no connection, and paints immediately.</summary>
    /// <param name="appearance">How it looks; <see cref="Appearance.Default"/> when null.</param>
    /// <param name="chrome">What it shows; <see cref="Chrome.Default"/> when null.</param>
    public MainWindow(Appearance? appearance = null, Chrome? chrome = null)
    {
        Appearance = appearance ?? Appearance.Default;
        Chrome = chrome ?? Chrome.Default;

        Title = "quickshell";
        Width = 960;
        Height = 600;
        MinWidth = 320;
        MinHeight = 200;

        ThemeMode = Appearance.Theme switch
        {
            ChromeTheme.Light => ThemeMode.Light,
            ChromeTheme.Dark => ThemeMode.Dark,
            _ => ThemeMode.System,
        };

        _tabs.Visibility = Visibility.Collapsed;

        Grid layout = new();

        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        Grid.SetRow(_tabs, 0);
        Grid.SetRow(_terminal, 1);

        layout.Children.Add(_tabs);
        layout.Children.Add(_terminal);

        Content = layout;

        // The one action that collects a defect report. A binding and not a menu item, because this
        // window has no menu and gaining one to hold a maintenance command would spend the argument
        // it makes. Ctrl+Shift+F1: F1 is where a person looks for help, and the two modifiers keep
        // it away from anything the terminal owes the host — an unmodified F1 belongs to the program
        // on the far side and always will.
        InputBindings.Add(new KeyBinding(new Diagnose(this), Key.F1,
                                         ModifierKeys.Control | ModifierKeys.Shift));

        // Importing the incumbent's sessions. Ctrl+Shift+I for the same reason as F1 above: two
        // modifiers keep it away from anything the terminal owes the host, and an unmodified key
        // belongs to the program on the far side.
        InputBindings.Add(new KeyBinding(new Import(this), Key.I,
                                         ModifierKeys.Control | ModifierKeys.Shift));
    }

    /// <summary>How this window looks. The palette here is the terminal's and not the chrome's.</summary>
    public Appearance Appearance { get; }

    /// <summary>What this window shows.</summary>
    public Chrome Chrome { get; }

    /// <summary>How many tabs are open, which is what decides whether the strip is visible.</summary>
    public int Tabs => Math.Max(1, _tabs.Items.Count);

    /// <summary>
    /// Whether this window is recording a session's output, shown in the title.
    ///
    /// <para><b>The title and not a status bar.</b> A recording that runs without saying so is a
    /// client writing a user's session to disk while they believe it is not — so it has to be
    /// visible, and the only surface this window has is the one it is named by. There is no status
    /// bar here to put it in, and adding one to carry a badge would spend the argument the whole
    /// window makes.</para>
    ///
    /// <para>It leads the title rather than trailing it, because a taskbar button shows the first
    /// few characters and nothing else.</para>
    /// </summary>
    public bool Recording
    {
        get => _recording;

        set
        {
            _recording = value;
            Title = value ? "● recording — quickshell" : "quickshell";
        }
    }

    /// <summary>Whether the tab strip is on screen.</summary>
    public bool TabStripShowing => _tabs.Visibility == Visibility.Visible;

    /// <summary>
    /// Puts the terminal's own window inside this one.
    ///
    /// <para>Separate from the constructor because the pane creates a handle, and a handle is the
    /// one thing on this path that a first paint should not wait for.</para>
    /// </summary>
    public void Show(TerminalPane pane)
    {
        ArgumentNullException.ThrowIfNull(pane);

        _terminal.Content = pane;
    }

    /// <summary>Adds a tab, and shows the strip once there is something to choose between.</summary>
    public void AddTab(string title)
    {
        _tabs.Items.Add(new TabItem { Header = title });

        _tabs.Visibility = _tabs.Items.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Removes a tab, and hides the strip again when one is left.</summary>
    public void RemoveTab()
    {
        if (_tabs.Items.Count > 0)
        {
            _tabs.Items.RemoveAt(_tabs.Items.Count - 1);
        }

        _tabs.Visibility = _tabs.Items.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// What happens once a bundle is written. A dialog naming the file, unless a caller says
    /// otherwise — which is how a test asks this question without a modal window in the way.
    /// </summary>
    public Action<string>? Wrote { get; set; }

    /// <summary>
    /// Where bundles go. The client's own folder unless a caller says otherwise.
    /// </summary>
    public string? DiagnosticsFolder { get; set; }

    /// <summary>
    /// Writes one bundle and says where it went.
    ///
    /// <para>Reached from Ctrl+Shift+F1, and from nowhere automatic: this reads files and asks DXGI
    /// a question, neither of which belongs on a path a user did not ask for. Nothing is sent, and
    /// the file is the user's to read first.</para>
    /// </summary>
    public string WriteDiagnostics()
    {
        string path = DiagnosticBundle.WriteTo(DiagnosticsFolder ?? DiagnosticBundle.Folder(),
                                               DiagnosticSources.Default(), DateTimeOffset.UtcNow);

        (Wrote ?? Told)(path);

        return path;
    }

    /// <summary>
    /// Applies what was read from the settings file to a window that is already up.
    ///
    /// <para><b>A correction and not a precondition</b>, which is the same shape as
    /// <see cref="PlaceAt"/> and for the same reason: nothing is read before the first paint, so a
    /// slow disk costs a window that repaints once rather than a window that is late. WPF's
    /// <c>ThemeMode</c> is designed for exactly this — it repaints a live window.</para>
    ///
    /// <para>The typeface, its size and the scrollback depth are in the file and in
    /// <see cref="Settings"/>, and nothing consumes them yet: the terminal pane that would is not
    /// wired to a session. They are written and read faithfully, which is what keeps a user's choice
    /// from being lost in the meantime.</para>
    /// </summary>
    public void Apply(Settings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        ThemeMode = settings.Theme switch
        {
            ChromeTheme.Light => ThemeMode.Light,
            ChromeTheme.Dark => ThemeMode.Dark,
            _ => ThemeMode.System,
        };
    }

    /// <summary>
    /// What happens once an import has been previewed. A dialog that shows what would be created and
    /// what would not, unless a caller says otherwise.
    /// </summary>
    public Func<ImportPreview, bool>? Importing { get; set; }

    /// <summary>Where imported sessions are written. The client's own store unless a caller says.</summary>
    public string? SessionsFile { get; set; }

    /// <summary>
    /// Reads the incumbent's sessions, shows what the import would do, and writes only if the user
    /// agrees.
    ///
    /// <para><b>Nothing lands unseen</b>, which is the design's own requirement and the reason this
    /// is two steps rather than one: the preview is built and shown, and the tree is written after
    /// the answer and not before it.</para>
    /// </summary>
    /// <returns>Where the sessions were written, or empty where nothing was.</returns>
    public string ImportSessions()
    {
        if (MobaXtermImport.Find() is not { } found)
        {
            (Importing ?? Asked)(new ImportPreview([], string.Empty));

            return string.Empty;
        }

        ImportPreview preview = MobaXtermImport.Preview(found);

        if (!(Importing ?? Asked)(preview))
        {
            return string.Empty;
        }

        string into = SessionsFile ?? Locations.Current.Sessions;

        SessionTree.Of(preview.Tree()).WriteTo(into);

        return into;
    }

    /// <summary>Puts the window where it was last time on this arrangement of screens.</summary>
    public void PlaceAt(Placement? placement)
    {
        if (placement is not { } where)
        {
            // Left to the window manager, which knows where a new window goes and puts it somewhere
            // a person can see. Any number invented here would be a worse guess.
            WindowStartupLocation = WindowStartupLocation.CenterScreen;

            return;
        }

        WindowStartupLocation = WindowStartupLocation.Manual;

        Left = where.X;
        Top = where.Y;
        Width = where.Width;
        Height = where.Height;
        WindowState = where.Maximised ? WindowState.Maximized : WindowState.Normal;
    }

    /// <summary>
    /// The default telling: a dialog that offers to open the file, never one that sends it.
    ///
    /// <para>The same shape as the crash path's, and for the same reason — a report the user has not
    /// read is a report they should not be asked to send.</para>
    /// </summary>
    private static void Told(string path)
    {
        MessageBoxResult answer = MessageBox.Show(
            $"What your client was doing is written to {path}.\n\n"
            + "Read it before sending it to anybody. Nothing has been sent, and passwords and key "
            + "material are not in it.\n\nOpen it now?",
            "quickshell diagnostics", MessageBoxButton.YesNo, MessageBoxImage.Information);

        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception)
        {
            // The file is there and the dialog named it. Failing to open it is not worth a second
            // dialog.
        }
    }

    /// <summary>
    /// The default asking: what the import would do, and a choice.
    ///
    /// <para>The counts first, because a user with two hundred sessions wants to know the number
    /// before the detail; then what will not come across, named. A user told nothing discovers it
    /// three weeks later and blames the client for hiding it.</para>
    /// </summary>
    private static bool Asked(ImportPreview preview)
    {
        if (preview.Source.Length == 0)
        {
            MessageBox.Show("No MobaXterm session file was found on this machine.",
                            "Import sessions", MessageBoxButton.OK, MessageBoxImage.Information);

            return false;
        }

        StringBuilder said = new();

        said.AppendLine(preview.Source)
            .AppendLine()
            .AppendLine(Count(preview.Carrying, "session") + " would be imported.");

        if (preview.Skipping > 0)
        {
            said.AppendLine(Count(preview.Skipping, "session")
                            + " would not, because this client does not connect that way:");

            foreach (IGrouping<string, ImportedSession> why in
                     preview.Sessions.Where(session => !session.Carried)
                                     .GroupBy(session => session.Skipped))
            {
                said.AppendLine("  • " + Count(why.Count(), "session") + " — " + why.Key);
            }
        }

        int noted = preview.Sessions.Sum(session => session.Unmapped.Count);

        if (noted > 0)
        {
            said.AppendLine()
                .AppendLine(Count(noted, "setting")
                            + " across those sessions have nowhere to go here, and each is named "
                            + "beside the session it came from rather than dropped.");
        }

        said.AppendLine().Append("Import them now?");

        return MessageBox.Show(said.ToString(), "Import sessions", MessageBoxButton.YesNo,
                               MessageBoxImage.Question) == MessageBoxResult.Yes;
    }

    /// <summary>A count with its noun, pluralised, because "1 sessions" reads as a bug.</summary>
    private static string Count(int how, string what) =>
        how.ToString(CultureInfo.InvariantCulture) + " " + what + (how == 1 ? string.Empty : "s");

    /// <summary>The binding's command, which is the whole of what a command is here.</summary>
    private sealed class Diagnose(MainWindow window) : ICommand
    {
        /// <inheritdoc/>
        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }

        /// <inheritdoc/>
        public bool CanExecute(object? parameter) => true;

        /// <inheritdoc/>
        public void Execute(object? parameter) => window.WriteDiagnostics();
    }

    /// <summary>The import binding's command.</summary>
    private sealed class Import(MainWindow window) : ICommand
    {
        /// <inheritdoc/>
        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }

        /// <inheritdoc/>
        public bool CanExecute(object? parameter) => true;

        /// <inheritdoc/>
        public void Execute(object? parameter) => window.ImportSessions();
    }

    /// <summary>Where this window is now, for remembering.</summary>
    public Placement Where() => new(
        (int)(WindowState == WindowState.Maximized ? RestoreBounds.Left : Left),
        (int)(WindowState == WindowState.Maximized ? RestoreBounds.Top : Top),
        (int)(WindowState == WindowState.Maximized ? RestoreBounds.Width : Width),
        (int)(WindowState == WindowState.Maximized ? RestoreBounds.Height : Height),
        WindowState == WindowState.Maximized);
}
