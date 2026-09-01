using System.ComponentModel;
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
    /// What this window has open, which is what the closing question names.
    ///
    /// <para>Held here rather than derived from the tabs, because a session and a tab are not the
    /// same thing the moment QS48 splits a tab into panes — and what a user is deciding about when
    /// they close is hosts, not rectangles.</para>
    /// </summary>
    public OpenSessions Sessions { get; } = new();

    /// <summary>
    /// Whether to ask before closing with sessions open, or null to close without asking.
    ///
    /// <para>Null is what a test gets by default, because every window built in a test is one that
    /// has to close without a modal in front of it. The client sets one that remembers the answer
    /// across runs.</para>
    /// </summary>
    public CloseGuard? Guard { get; set; }

    /// <summary>
    /// How the closing question is put. A dialog naming what is open, unless a caller says
    /// otherwise — the same shape as <see cref="Importing"/>, and for the same reason.
    /// </summary>
    public Func<ClosingQuestion, ClosingAnswer>? AskingToClose { get; set; }

    /// <summary>
    /// Who this window's keystrokes belong to, or null while nothing is listening.
    ///
    /// <para>On the window rather than on the pane, and QS4 is why: the pane is a child HWND whose
    /// window procedure does nothing at all, deliberately, so that input arrives through WPF and
    /// pixels arrive through the swapchain. This is the WPF half of that sentence.</para>
    /// </summary>
    public Typist? Typing { get; set; }

    /// <summary>
    /// A key the window did not claim goes to the host.
    ///
    /// <para><b>After the base call and only when nothing has handled it</b>, which is what gives
    /// the local layer priority without this method knowing what is in it. A binding that ran has
    /// already marked the event, and the terminal never sees the chord — the ordering <c>Keys</c>
    /// describes, enforced by where these two lines are rather than by a second list.</para>
    /// </summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        ArgumentNullException.ThrowIfNull(e);

        // Alt chords arrive as System with the real key beside them, and a client reading only Key
        // would send alt-f as nothing at all.
        if (!e.Handled && Typing is not null
            && Typing.Press(e.Key == Key.System ? e.SystemKey : e.Key, Keyboard.Modifiers))
        {
            e.Handled = true;
        }
    }

    /// <summary>
    /// A character the keyboard layout resolved goes to the host as itself.
    ///
    /// <para><c>ControlText</c> where there is no <c>Text</c>: WPF puts control-C's <c>0x03</c>
    /// there, and a client reading only the latter is one where control-C does nothing.</para>
    /// </summary>
    protected override void OnTextInput(TextCompositionEventArgs e)
    {
        base.OnTextInput(e);

        ArgumentNullException.ThrowIfNull(e);

        if (e.Handled || Typing is null)
        {
            return;
        }

        string text = string.IsNullOrEmpty(e.Text) ? e.ControlText : e.Text;

        if (Typing.Type(text, Keyboard.Modifiers))
        {
            e.Handled = true;
        }
    }

    /// <summary>
    /// Asks about the sessions still open, once, and honours never again.
    ///
    /// <para><b>The answer is written down before the window goes.</b> There is no "afterwards" on
    /// the path where the close actually happens, so a never-again recorded after the fact would be
    /// recorded only on the path where the user stayed — and a checkbox that works when you cancel
    /// and not when you close is worse than none, which is <see cref="CloseGuard"/>'s whole
    /// argument.</para>
    ///
    /// <para>Nothing is asked when nothing is open, when the user has switched the question off, or
    /// when something else has already cancelled the close: two dialogs about one close is the kind
    /// of client this one is arguing with.</para>
    /// </summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);

        ArgumentNullException.ThrowIfNull(e);

        if (e.Cancel || Guard is not { } guard || guard.Ask(Sessions.Closing()) is not { } question)
        {
            return;
        }

        ClosingAnswer answer = (AskingToClose ?? AskedToClose)(question);

        if (answer.NeverAgain)
        {
            guard.NeverAgain();
        }

        e.Cancel = !answer.Close;
    }

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

    /// <summary>
    /// The default asking about closing: what is open, named, and a checkbox that means it.
    ///
    /// <para><b>A window and not a <c>MessageBox</c>, for one reason.</b> Every other dialog here is
    /// a message box, which is the right answer while a dialog only needs buttons. This one needs a
    /// checkbox — "never again" is half of what the design asks for — and a message box has none, so
    /// the alternative was a third button meaning "close and stop asking", which puts the setting
    /// inside the decision and offers no way to switch the question off while saying no.</para>
    ///
    /// <para>Owned by the window it is asked about and modal to it, so the terminal underneath is
    /// visible while the question is on screen: what somebody wants to look at before answering
    /// <em>is</em> the session it is asking about.</para>
    /// </summary>
    private ClosingAnswer AskedToClose(ClosingQuestion question)
    {
        CheckBox never = new()
        {
            Content = "Don't ask again",
            Margin = new Thickness(0, 16, 0, 0),
        };

        StackPanel body = new() { Margin = new Thickness(20) };

        body.Children.Add(new TextBlock
        {
            Text = question.Asking,
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 10),
        });

        // Named, and every one of them. A count is what the user already knows from the title bar;
        // the names are what stops somebody closing the one window they meant to keep.
        body.Children.Add(new TextBlock
        {
            Text = string.Join(Environment.NewLine, question.Open.Select(open => "• " + open)),
            TextWrapping = TextWrapping.Wrap,
        });

        body.Children.Add(never);

        StackPanel buttons = new()
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 20, 0, 0),
        };

        Button stay = new() { Content = "Cancel", MinWidth = 88, IsCancel = true };
        Button close = new() { Content = "Close", MinWidth = 88, Margin = new Thickness(8, 0, 0, 0) };

        buttons.Children.Add(stay);
        buttons.Children.Add(close);
        body.Children.Add(buttons);

        Window asking = new()
        {
            Title = "quickshell",
            Content = body,
            Owner = this,
            ThemeMode = ThemeMode,

            // A width, and only the height from the content. Sizing to both is what a dialog like
            // this normally does and it is wrong here: the list wraps, so its desired width depends
            // on the width it is given, and a measure with no answer settles at whatever WPF's
            // fallback is — which was a window with the buttons off the bottom of it.
            Width = 380,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };

        // Cancel is the default, so Enter and Escape both leave the sessions alone. Closing is the
        // irreversible half of this question and is never one keypress away from a person who was
        // reading rather than answering.
        stay.IsDefault = true;

        bool closing = false;

        close.Click += (_, _) =>
        {
            closing = true;
            asking.DialogResult = true;
        };

        asking.ShowDialog();

        // The checkbox is read whichever button was pressed, and even where the dialog was dismissed:
        // a user who ticked it has switched the question off, and what they then decided about this
        // one close is a separate answer.
        return new ClosingAnswer(closing, never.IsChecked == true);
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
