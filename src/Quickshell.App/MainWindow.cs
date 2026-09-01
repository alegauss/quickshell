using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;

// Both namespaces name a Key. Here the window's is meant every time: these are chords a person
// presses, not sequences a host is sent.
using Key = System.Windows.Input.Key;
using Paste = Quickshell.Terminal.Paste;

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
    /// <summary>
    /// Where the panes live, all of them, with one visible.
    ///
    /// <para><b>Every pane stays in the tree and only one is shown, and that is not an
    /// optimisation.</b> A <see cref="TerminalPane"/> is an <c>HwndHost</c>: taking it out of the
    /// tree destroys its child window, and the swapchain a device is presenting into goes with it.
    /// So switching tabs is a visibility change and never a reparent.</para>
    ///
    /// <para><b>Hidden rather than collapsed</b>, for the same kind of reason. A collapsed element
    /// measures zero, so a background tab would resize its grid to one cell and tell the program on
    /// the far end that its terminal is one column wide. Hidden keeps the size it had.</para>
    ///
    /// <para><b>A canvas since QS48, because a tab holds a tree.</b> Every pane's place is a
    /// proportion of the tab and the pixels are worked out from the canvas each time it is laid out,
    /// so an arbitrarily deep arrangement needs no nested panels — one loop over the leaves puts
    /// each one where the tree says it goes.</para>
    /// </summary>
    private readonly Canvas _terminal = new() { ClipToBounds = true };

    private readonly List<TerminalTab> _open = [];
    private readonly TabControl _tabs = new();
    private readonly DockPanel _find = new() { Margin = new Thickness(8, 6, 8, 6) };
    private readonly TextBox _needle = new() { MinWidth = 220 };
    private readonly TextBlock _found = new()
    {
        Margin = new Thickness(10, 0, 0, 0),
        VerticalAlignment = VerticalAlignment.Center,
    };

    private bool _recording;
    private int _active = -1;
    private DispatcherTimer? _watching;
    private TerminalPane? _showing;

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
        _find.Visibility = Visibility.Collapsed;

        BuildFindBar();

        Grid layout = new();

        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        Grid.SetRow(_tabs, 0);
        Grid.SetRow(_find, 1);
        Grid.SetRow(_terminal, 2);

        layout.Children.Add(_tabs);
        layout.Children.Add(_find);
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

        // Copy and paste, on the two chords every terminal on this platform uses. Ctrl+C is not one
        // of them and must never become one: it is how a person stops a runaway program, and a
        // client that stole it would have taken away the thing they reach for when something has
        // gone wrong.
        InputBindings.Add(new KeyBinding(new Copy(this), Key.C,
                                         ModifierKeys.Control | ModifierKeys.Shift));

        InputBindings.Add(new KeyBinding(new PasteIn(this), Key.V,
                                         ModifierKeys.Control | ModifierKeys.Shift));

        // Finding something in the scrollback.
        InputBindings.Add(new KeyBinding(new Find(this), Key.F,
                                         ModifierKeys.Control | ModifierKeys.Shift));

        // Reading back through it. Shift and not a bare page key, because an unmodified PageUp is
        // the program's — a pager and an editor both bind it, and taking it would mean the terminal
        // scrolled while the thing on screen did not.
        InputBindings.Add(new KeyBinding(new Scroll(this, up: true), Key.PageUp,
                                         ModifierKeys.Shift));

        InputBindings.Add(new KeyBinding(new Scroll(this, up: false), Key.PageDown,
                                         ModifierKeys.Shift));

        // Tabs. Ctrl+Shift+T and Ctrl+Shift+W for opening and closing, which are the two chords
        // every tabbed application on this platform uses and which a terminal program does not.
        InputBindings.Add(new KeyBinding(new Opening(this), Key.T,
                                         ModifierKeys.Control | ModifierKeys.Shift));

        InputBindings.Add(new KeyBinding(new Shutting(this), Key.W,
                                         ModifierKeys.Control | ModifierKeys.Shift));

        // Next and previous. Ctrl+Tab is the one chord here that a full-screen program might
        // plausibly want, and it is taken anyway: a client where the user cannot leave the tab they
        // are in has no tabs. That cost is what the keybinding reference is for.
        InputBindings.Add(new KeyBinding(new Step(this, by: 1), Key.Tab, ModifierKeys.Control));

        InputBindings.Add(new KeyBinding(new Step(this, by: -1), Key.Tab,
                                         ModifierKeys.Control | ModifierKeys.Shift));

        // By index, on Alt rather than Ctrl: Ctrl with a digit is a control sequence a host has
        // meanings for, and Alt with one is not.
        for (int index = 1; index <= 9; index++)
        {
            InputBindings.Add(new KeyBinding(new Reach(this, index - 1),
                                             Key.D0 + index, ModifierKeys.Alt));
        }

        // Splitting, on the two chords tmux and every terminal that copied it use — the characters
        // are the picture: a vertical bar divides side by side, a minus divides one above the other.
        InputBindings.Add(new KeyBinding(new Splitting(this, Divide.Beside), Key.OemBackslash,
                                         ModifierKeys.Control | ModifierKeys.Shift));

        InputBindings.Add(new KeyBinding(new Splitting(this, Divide.Beside), Key.Oem5,
                                         ModifierKeys.Control | ModifierKeys.Shift));

        InputBindings.Add(new KeyBinding(new Splitting(this, Divide.Below), Key.OemMinus,
                                         ModifierKeys.Control | ModifierKeys.Shift));

        // The focus, by direction and on the arrows, because the gesture is about where things are
        // on screen and an arrow is the only key that says a direction.
        foreach ((Key key, Toward way) in new[]
                 {
                     (Key.Left, Toward.Left), (Key.Right, Toward.Right),
                     (Key.Up, Toward.Up), (Key.Down, Toward.Down),
                 })
        {
            InputBindings.Add(new KeyBinding(new Facing(this, way), key,
                                             ModifierKeys.Alt | ModifierKeys.Shift));
        }

        // Zoom and equalise, which are the two gestures that make a cramped split workable.
        InputBindings.Add(new KeyBinding(new Zooming(this), Key.Z,
                                         ModifierKeys.Control | ModifierKeys.Shift));

        InputBindings.Add(new KeyBinding(new Equalising(this), Key.E,
                                         ModifierKeys.Control | ModifierKeys.Shift));

        // Every pane's place is a proportion, so the pixels are worked out afresh whenever the space
        // they are proportions of changes.
        _terminal.SizeChanged += (_, _) => Arrange();
    }

    /// <summary>How this window looks. The palette here is the terminal's and not the chrome's.</summary>
    public Appearance Appearance { get; }

    /// <summary>What this window shows.</summary>
    public Chrome Chrome { get; }

    /// <summary>How many tabs are open, which is what decides whether the strip is visible.</summary>
    public int Tabs => Math.Max(1, _tabs.Items.Count);

    /// <summary>The tabs this window holds, in the order they appear.</summary>
    public IReadOnlyList<TerminalTab> Held => _open;

    /// <summary>
    /// Which tab is on screen, or -1 while there are none.
    ///
    /// <para>Setting it is what a keyboard chord and a click on the strip both do, so everything
    /// that follows from switching — the visible pane, the window's title, the activity mark and
    /// which session the copy, paste, find and scroll surfaces answer for — follows from here and
    /// from nowhere else.</para>
    /// </summary>
    public int Active
    {
        get => _active;

        set
        {
            if (_open.Count == 0)
            {
                _active = -1;

                return;
            }

            // Wrapped rather than clamped, because next-from-the-last is the first: a chord that
            // stopped at the end would be one the user presses twice and then reaches for the mouse.
            int at = ((value % _open.Count) + _open.Count) % _open.Count;

            _active = at;
            _tabs.SelectedIndex = at;

            for (int tab = 0; tab < _open.Count; tab++)
            {
                _open[tab].Showing(tab == at);
            }

            Arrange();
            Retitle();
        }
    }

    /// <summary>
    /// Puts every pane where its tab's tree says it goes, in the canvas's own pixels.
    ///
    /// <para><b>Every pane of every tab stays in the canvas and only the active tab's are shown.</b>
    /// A <see cref="TerminalPane"/> is an <c>HwndHost</c>: taking one out of the tree destroys the
    /// child window a swapchain is presenting into, so a tab switch and a split are both a matter of
    /// where things are and never of what is in the tree.</para>
    ///
    /// <para>A zoomed pane takes the whole tab and the others are hidden rather than resized — the
    /// arrangement has to come back exactly as it was, and the only way to be sure of that is not to
    /// have touched it.</para>
    /// </summary>
    public void Arrange()
    {
        double width = _terminal.ActualWidth;
        double height = _terminal.ActualHeight;

        if (width < 1d || height < 1d)
        {
            return;
        }

        // A pane put here by Show rather than by a tab fills the canvas. A canvas gives its children
        // no size of their own, and a pane with no size never gets a handle worth a swapchain — so
        // this is not tidiness, it is the difference between a terminal and a nothing.
        if (_showing is { } alone)
        {
            Canvas.SetLeft(alone, 0);
            Canvas.SetTop(alone, 0);

            alone.Width = width;
            alone.Height = height;
        }

        foreach (TerminalTab tab in _open)
        {
            bool showing = ReferenceEquals(tab, Current);

            foreach (int pane in tab.Layout.Panes)
            {
                if (tab.In(pane) is not { } leaf)
                {
                    continue;
                }

                Portion at = tab.Zoomed >= 0
                    ? new Portion(0, 0, 1, 1)
                    : tab.Layout.Portions[pane];

                bool visible = showing && (tab.Zoomed < 0 || tab.Zoomed == pane);

                leaf.Pane.Visibility = visible ? Visibility.Visible : Visibility.Hidden;

                // And the loop is told, because it cannot see a WPF visibility — QS166. A pane
                // behind another tab is not drawn at all rather than drawn and hidden.
                if (leaf.Terminal.View is { } view)
                {
                    view.Showing = visible;
                }

                // Rounded to whole pixels, and the far edge rounded rather than the width: two panes
                // sharing a divider must not leave a one-pixel seam of whatever is behind them.
                double left = Math.Floor(at.X * width);
                double top = Math.Floor(at.Y * height);

                Canvas.SetLeft(leaf.Pane, left);
                Canvas.SetTop(leaf.Pane, top);

                leaf.Pane.Width = Math.Max(1d, Math.Floor(at.Right * width) - left);
                leaf.Pane.Height = Math.Max(1d, Math.Floor(at.Bottom * height) - top);
            }
        }
    }

    /// <summary>The tab on screen, or null while there are none.</summary>
    public TerminalTab? Current => _active >= 0 && _active < _open.Count ? _open[_active] : null;

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

            Retitle();
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
    /// The input method's composition, and where its windows are put.
    ///
    /// <para><b>On the window and not on the pane, and QS4 is why</b> — the same sentence as
    /// <see cref="Typing"/>. The pane is a child HWND whose window procedure does nothing at all, so
    /// it never has focus and an input method's messages never reach it. They arrive here, at the
    /// window WPF gives keyboard focus to, which is also the window whose client pixels a candidate
    /// position is measured in.</para>
    /// </summary>
    public InputMethod Input { get; } = new();

    /// <summary>
    /// Puts this window's own handle under the hook the input method needs.
    ///
    /// <para>The earliest moment there is a handle, and the reason this is not in the constructor:
    /// before the source exists there is nothing to hook, and after the first composition it is too
    /// late.</para>
    /// </summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        (PresentationSource.FromVisual(this) as HwndSource)?.AddHook(Hooked);
    }

    /// <summary>
    /// Every message this window gets, of which three are ours.
    ///
    /// <para><b>Handled is never set.</b> WPF's own IME handling is what turns a committed phrase
    /// into the <c>WM_CHAR</c> that <see cref="OnTextInput"/> sends to the host, so a hook that
    /// consumed these would compose beautifully and commit nothing.</para>
    /// </summary>
    private nint Hooked(nint window, int message, nint wide, nint low, ref bool handled)
    {
        Input.Handle(window, message, low);

        return nint.Zero;
    }

    /// <summary>
    /// Who this window's keystrokes belong to, or null while nothing is listening.
    ///
    /// <para>On the window rather than on the pane, and QS4 is why: the pane is a child HWND whose
    /// window procedure does nothing at all, deliberately, so that input arrives through WPF and
    /// pixels arrive through the swapchain. This is the WPF half of that sentence.</para>
    /// </summary>
    public Typist? Typing => Current?.Focused.Typist;

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

        _showing = pane;

        _terminal.Children.Add(pane);

        Arrange();
    }

    /// <summary>
    /// Puts a pane in the canvas and gives it the keyboard when it is clicked.
    ///
    /// <para>The click is the pane's own message rather than a WPF event, for the reason the whole
    /// mouse path is: this is a child window, and WPF never sees what happens in it.</para>
    /// </summary>
    private void Hold(TerminalLeaf leaf)
    {
        _terminal.Children.Add(leaf.Pane);

        leaf.Pane.Mouse += mouse =>
        {
            if (mouse.Kind != PaneMouseKind.Pressed || Current is not { } tab)
            {
                return;
            }

            int pane = tab.PaneOf(leaf);

            if (pane >= 0 && pane != tab.FocusedPane)
            {
                tab.Focus(pane);
                Retitle();
            }
        };
    }

    /// <summary>Who gives a freshly split pane a session, or null while nothing can.</summary>
    public Action<TerminalLeaf>? Connects { get; set; }

    /// <summary>Splits the pane that has the keyboard, and gives the new one a session.</summary>
    public void SplitPane(Divide how)
    {
        if (Current?.Split(how) is not { } made)
        {
            return;
        }

        Hold(made);
        Arrange();
        Retitle();

        Connects?.Invoke(made);
    }

    /// <summary>
    /// Closes the pane that has the keyboard, asking first where its session is still live.
    ///
    /// <para>The last pane of a tab is the tab, so closing it closes that instead — which is what a
    /// user means either way, and saves them learning which chord applies.</para>
    /// </summary>
    public void ClosePane()
    {
        if (Current is not { } tab)
        {
            return;
        }

        if (tab.Layout.Count <= 1)
        {
            CloseTab();

            return;
        }

        if (tab.Focused.IsLive
            && Guard?.Ask([tab.Focused.Title]) is { } question
            && !(AskingToClose ?? AskedToClose)(question).Close)
        {
            return;
        }

        if (tab.ClosePane() is { } went)
        {
            _terminal.Children.Remove(went.Pane);
            EndsPane?.Invoke(went);
        }

        Arrange();
        Retitle();
    }

    /// <summary>Moves the keyboard to the pane in a direction, by where it is on screen.</summary>
    public void FocusPane(Toward direction)
    {
        if (Current?.Focus(direction) == true)
        {
            Retitle();
        }
    }

    /// <summary>Fills the tab with one pane, or gives the others their space back.</summary>
    public void ZoomPane()
    {
        Current?.Zoom();

        Arrange();
    }

    /// <summary>Gives every divider in this tab an even share again.</summary>
    public void EqualisePanes()
    {
        Current?.Layout.Equalise();

        Arrange();
    }

    /// <summary>
    /// Puts a tab in this window and brings it forward.
    ///
    /// <para>The pane goes into the tree here and stays in it for the tab's whole life — see
    /// <see cref="_terminal"/> for why taking it out again is not an option.</para>
    /// </summary>
    /// <returns>Where it landed, which is what a caller switches back to.</returns>
    public int Add(TerminalTab tab)
    {
        ArgumentNullException.ThrowIfNull(tab);

        _open.Add(tab);
        _tabs.Items.Add(new TabItem { Header = tab.Title });

        foreach (TerminalLeaf leaf in tab.Leaves)
        {
            Hold(leaf);
        }

        Strip();
        Watch();

        Active = _open.Count - 1;

        return _active;
    }

    /// <summary>
    /// Keeps the strip and the window's name current, at a rate nobody can see.
    ///
    /// <para><b>Polled, and there is no event that would do instead.</b> Both things it reads are
    /// written by the parser stage — the title a host sets through OSC, and the generation that says
    /// output arrived — and that stage owns the model on a thread this window may not be touched
    /// from. There is nothing to subscribe to and nowhere safe to raise it.</para>
    ///
    /// <para>It costs nothing when nothing changed: a header is only assigned where the string
    /// actually differs, so an idle window issues no layout and no draw. Two hertz, because this is
    /// a tab strip and not an animation.</para>
    /// </summary>
    private void Watch()
    {
        if (_watching is not null)
        {
            return;
        }

        _watching = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background,
                                        (_, _) => Retitle(), Dispatcher);

        _watching.Start();
    }

    /// <summary>
    /// Takes a tab out of the window and hands it back for the caller to end.
    ///
    /// <para><b>It is not disposed here.</b> Closing a tab and detaching one remove it identically;
    /// what differs is what happens next, and a window that ended the session would have decided
    /// that for both.</para>
    /// </summary>
    /// <returns>The tab that was removed, or null where the index named none.</returns>
    public TerminalTab? Remove(int at)
    {
        if (at < 0 || at >= _open.Count)
        {
            return null;
        }

        TerminalTab going = _open[at];

        _open.RemoveAt(at);
        _tabs.Items.RemoveAt(at);

        foreach (TerminalLeaf leaf in going.Leaves)
        {
            _terminal.Children.Remove(leaf.Pane);
        }

        // The window's closing question names what is open, and a tab is what open means.
        Sessions.Closed(going.Host);

        Strip();

        if (_open.Count == 0)
        {
            _watching?.Stop();
            _watching = null;
        }

        // The one after it, which is where every editor leaves the cursor — and the one before it
        // when the last tab went, because there is no one after.
        Active = Math.Min(at, _open.Count - 1);

        return going;
    }

    /// <summary>
    /// Rereads every tab's title, which changes without anything telling this window.
    ///
    /// <para>The host writes it through OSC while the parser is running, so there is no event to
    /// hang this on and no thread it would arrive on. It is asked for instead, on the same wake-up
    /// that redraws — cheap, and never wrong for longer than a frame.</para>
    /// </summary>
    public void Retitle()
    {
        for (int tab = 0; tab < _open.Count && tab < _tabs.Items.Count; tab++)
        {
            if (_tabs.Items[tab] is not TabItem item)
            {
                continue;
            }

            // The name is the title and only the title. A marker put into the header string lands
            // in the element's accessibility name too — so a screen reader would read the dot out,
            // and every case reading a tab would have to know about it. What activity gets instead
            // is weight, which is a property of how the tab is drawn and not of what it is called.
            if (!Equals(item.Header, _open[tab].Title))
            {
                item.Header = _open[tab].Title;
            }

            item.FontWeight = _open[tab].HasActivity ? FontWeights.Bold : FontWeights.Normal;
        }

        // The window keeps the client's name and does not take the session's.
        //
        // <b>Which means with one tab the title a host writes appears nowhere</b>, because QS46's
        // default installation hides the strip — and that is a real gap rather than an oversight.
        // It is QS161. What settled it here is that the alternative made the whole title a string
        // the machine decides: cmd writes its own full path through OSC within half a second, so a
        // window named for its session reads differently on every desk and is a thing no case can
        // check.
        Title = _recording ? "● recording — quickshell" : "quickshell";
    }

    /// <summary>Shows the strip once there is a choice to make, and hides it again when there is not.</summary>
    private void Strip() =>
        _tabs.Visibility = _tabs.Items.Count > 1 ? Visibility.Visible : Visibility.Collapsed;

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

    /// <summary>
    /// Who is asked to find something, or null while there is no terminal to search.
    ///
    /// <para>The needle, which way to look, and whether capitals matter; the answer is how many
    /// cells were matched, or null for nothing found.</para>
    /// </summary>
    public Func<string, bool, bool, int?>? Finding { get; set; }

    /// <summary>Who is asked to scroll, in lines. Negative goes back through the history.</summary>
    public Action<int>? Scrolling { get; set; }

    /// <summary>Whether the find bar is on screen, which only a user's chord makes true.</summary>
    public bool FindBarShowing => _find.Visibility == Visibility.Visible;

    /// <summary>What the find bar currently says, for a test and for nothing else.</summary>
    public string FoundSaying => _found.Text;

    /// <summary>
    /// Opens the find bar and puts the caret in it, or closes it and gives the keyboard back.
    ///
    /// <para><b>Closing hands focus back to the window</b>, and that is not tidiness: while a
    /// <see cref="TextBox"/> has focus every keystroke is the box's, so a find bar left open with
    /// the caret in it is a terminal that has stopped accepting typing.</para>
    /// </summary>
    public void ShowFindBar(bool showing)
    {
        _find.Visibility = showing ? Visibility.Visible : Visibility.Collapsed;

        if (showing)
        {
            _needle.Focus();
            _needle.SelectAll();

            return;
        }

        _found.Text = string.Empty;

        Keyboard.ClearFocus();
        Focus();
    }

    /// <summary>
    /// Looks for what the find bar holds, and says what happened where the user can read it.
    /// </summary>
    /// <param name="forward">Which way to look from the last match.</param>
    /// <returns>Whether anything was found.</returns>
    public bool FindNext(bool forward = true)
    {
        string needle = _needle.Text;

        if (needle.Length == 0)
        {
            _found.Text = string.Empty;

            return false;
        }

        // Capitals matter only where the user typed one, which is what every editor does and what
        // nobody has to be told: somebody hunting an error message is not thinking about case until
        // the moment they type a capital on purpose.
        bool exactly = needle.Any(char.IsUpper);

        if (Finding?.Invoke(needle, forward, exactly) is not { } cells)
        {
            _found.Text = "not found";

            return false;
        }

        _found.Text = Count(cells, "cell") + " matched";

        return true;
    }

    /// <summary>The find bar's own controls, built once and shown when somebody asks for them.</summary>
    private void BuildFindBar()
    {
        Button previous = new() { Content = "Previous", Margin = new Thickness(8, 0, 0, 0) };
        Button next = new() { Content = "Next", Margin = new Thickness(8, 0, 0, 0) };
        Button close = new() { Content = "Close", Margin = new Thickness(8, 0, 0, 0) };

        previous.Click += (_, _) => FindNext(forward: false);
        next.Click += (_, _) => FindNext();
        close.Click += (_, _) => ShowFindBar(showing: false);

        // Enter walks the matches and escape puts the keyboard back where a terminal expects it.
        // Handled on the box itself, because while it has focus nothing else is going to see them.
        _needle.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                FindNext(forward: (Keyboard.Modifiers & ModifierKeys.Shift) == 0);
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                ShowFindBar(showing: false);
                e.Handled = true;
            }
        };

        DockPanel.SetDock(close, Dock.Right);
        DockPanel.SetDock(next, Dock.Right);
        DockPanel.SetDock(previous, Dock.Right);

        _find.Children.Add(close);
        _find.Children.Add(next);
        _find.Children.Add(previous);
        _find.Children.Add(_needle);
        _find.Children.Add(_found);
    }

    /// <summary>
    /// What is selected in the terminal, or null while nothing can answer.
    ///
    /// <para>A delegate rather than a reference to the pane, because what is selected is the view's
    /// and the view does not exist until the window has been laid out — and this window is built
    /// before any of that on purpose.</para>
    /// </summary>
    public Func<string>? Selected { get; set; }

    /// <summary>Where a paste goes, or null while there is no session to send it to.</summary>
    public Func<string, ValueTask>? Pasting { get; set; }

    /// <summary>
    /// Whether the program has turned bracketed paste on, which decides whether this client asks.
    /// </summary>
    public Func<bool>? Bracketed { get; set; }

    /// <summary>
    /// How a risky paste is shown. A dialog carrying exactly what would be sent, unless a caller
    /// says otherwise — the same shape as <see cref="AskingToClose"/>, and for the same reason.
    /// </summary>
    public Func<string, bool>? AskingToPaste { get; set; }

    /// <summary>
    /// Puts the selection on the clipboard.
    ///
    /// <para><b>Nothing selected puts nothing on it.</b> A client that emptied somebody's clipboard
    /// because they pressed a chord with nothing highlighted would have destroyed something they
    /// were still going to use, and there is no undo for that.</para>
    /// </summary>
    /// <returns>What was copied, empty where nothing was.</returns>
    public string CopySelection()
    {
        string text = Selected?.Invoke() ?? string.Empty;

        if (text.Length == 0)
        {
            return string.Empty;
        }

        try
        {
            Clipboard.SetText(text);
        }
        catch (Exception)
        {
            // Another process holds the clipboard open, which happens and passes. Losing a copy is
            // not worth a dialog, and the selection is still on screen to try again with.
            return string.Empty;
        }

        return text;
    }

    /// <summary>
    /// Sends the clipboard to the host, having first made it safe to send.
    ///
    /// <para><b>This is the security half of QS30 and the order is the whole of it.</b> The text is
    /// cleaned of control characters first, because nothing legitimate pastes an escape sequence and
    /// a paste that could carry one could set a mode or answer a query on the user's behalf. Then it
    /// is either handed to a program that asked to be told it is a paste, or shown to the user — a
    /// newline is what makes pasted text run itself, and what somebody read on a web page and what
    /// their clipboard holds are not obliged to match.</para>
    /// </summary>
    /// <returns>What was sent, empty where nothing was.</returns>
    public string PasteFromClipboard()
    {
        string held;

        try
        {
            held = Clipboard.ContainsText() ? Clipboard.GetText() : string.Empty;
        }
        catch (Exception)
        {
            return string.Empty;
        }

        if (held.Length == 0 || Pasting is not { } sending)
        {
            return string.Empty;
        }

        char[] cleaned = new char[Paste.MeasureClean(held)];
        int written = Paste.Clean(held, cleaned);

        if (written <= 0)
        {
            return string.Empty;
        }

        string text = new(cleaned, 0, written);
        bool bracketed = Bracketed?.Invoke() ?? false;

        // Shown before it is sent, and the dialog carries the text itself rather than a count: a
        // user asked whether to paste "4 lines" has been told nothing they can act on.
        if (Paste.NeedsConfirming(text, bracketed) && !(AskingToPaste ?? AskedToPaste)(text))
        {
            return string.Empty;
        }

        string sent = bracketed ? Paste.Start + text + Paste.Finish : text;

        // Not awaited: this is the UI thread, and the ordering is the channel's. The same reasoning
        // as a keystroke's, and for the same reason it must not block a window's repaint.
        _ = sending(sent);

        return sent;
    }

    /// <summary>
    /// The default asking about a paste: the text, and a choice.
    ///
    /// <para>A window rather than a message box, because what matters here is that the user can read
    /// what is about to run — so it scrolls, it is monospaced, and it is not truncated into an
    /// ellipsis by a dialog that was built for one sentence.</para>
    /// </summary>
    private bool AskedToPaste(string text)
    {
        StackPanel body = new() { Margin = new Thickness(20) };

        body.Children.Add(new TextBlock
        {
            Text = Count(Lines(text), "line") + " would be sent, and this will run:",
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 10),
        });

        body.Children.Add(new ScrollViewer
        {
            MaxHeight = 220,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,

            // The carriage returns Paste.Clean settled on are what the host will see and what a
            // TextBlock renders as nothing at all, so they are shown as the line breaks they are.
            //
            // In the chrome's own face and not a monospaced one, which is not a preference: this
            // process runs with InvariantGlobalization, and naming a typeface here sends WPF down a
            // path whose static constructor builds a CultureInfo("en") and throws — which killed the
            // client the first time this dialog was opened. QS154 is that, and the monospace belongs
            // here once it is fixed.
            Content = new TextBlock { Text = text.Replace('\r', '\n') },
        });

        StackPanel buttons = new()
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 20, 0, 0),
        };

        Button stay = new() { Content = "Cancel", MinWidth = 88, IsCancel = true, IsDefault = true };
        Button send = new() { Content = "Paste", MinWidth = 88, Margin = new Thickness(8, 0, 0, 0) };

        buttons.Children.Add(stay);
        buttons.Children.Add(send);
        body.Children.Add(buttons);

        Window asking = new()
        {
            Title = "quickshell",
            Content = body,
            Owner = this,
            ThemeMode = ThemeMode,
            Width = 520,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };

        bool pasting = false;

        send.Click += (_, _) =>
        {
            pasting = true;
            asking.DialogResult = true;
        };

        asking.ShowDialog();

        return pasting;
    }

    /// <summary>
    /// How many lines the host would see.
    ///
    /// <para><b>A trailing break does not add a line, and getting that wrong is not cosmetic.</b>
    /// The number is the whole reason a user glances at this dialog before reading it, and a paste
    /// of two commands announced as three is a client saying there is something in the clipboard
    /// that the user cannot see.</para>
    /// </summary>
    public static int Lines(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (text.Length == 0)
        {
            return 0;
        }

        int breaks = 0;

        foreach (char character in text)
        {
            if (character is '\r' or '\n')
            {
                breaks++;
            }
        }

        return text[^1] is '\r' or '\n' ? breaks : breaks + 1;
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

    /// <summary>
    /// Who opens a tab when the user asks for one, or null while nothing can.
    ///
    /// <para>A delegate, because opening one means a device, a shell and a font size — all of which
    /// belong to whatever composed this client and none of which a window should know.</para>
    /// </summary>
    public Action? Opens { get; set; }

    /// <summary>
    /// Who ends a tab this window has finished with. Null leaves it to the garbage collector, which
    /// is what a test wants and what a client must never do.
    /// </summary>
    public Action<TerminalTab>? Ends { get; set; }

    /// <summary>The same, for one pane of a tab that is staying.</summary>
    public Action<TerminalLeaf>? EndsPane { get; set; }

    /// <summary>
    /// Closes the tab on screen, asking first where its session is still live.
    ///
    /// <para><b>A session that already ended is closed without a question.</b> Asking about a tab
    /// whose shell exited is asking permission to tidy up, and a client that does that is one whose
    /// questions stop being read — which is the same argument <see cref="CloseGuard"/> makes about
    /// the window.</para>
    /// </summary>
    /// <returns>Whether the tab went.</returns>
    public bool CloseTab()
    {
        if (Current is not { } going)
        {
            return false;
        }

        if (going.IsLive
            && Guard?.Ask([going.Title]) is { } question
            && !(AskingToClose ?? AskedToClose)(question).Close)
        {
            return false;
        }

        Ends?.Invoke(going);
        Remove(_active);

        // The last tab going closes the window, because a window with no terminal in it is not
        // something this client has a name for.
        if (_open.Count == 0)
        {
            Close();
        }

        return true;
    }

    /// <summary>The tab-opening binding's command.</summary>
    private sealed class Opening(MainWindow window) : ICommand
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
        public void Execute(object? parameter) => window.Opens?.Invoke();
    }

    /// <summary>The tab-closing binding's command.</summary>
    private sealed class Shutting(MainWindow window) : ICommand
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
        public void Execute(object? parameter) => window.CloseTab();
    }

    /// <summary>Next and previous, which wrap.</summary>
    private sealed class Step(MainWindow window, int by) : ICommand
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
        public void Execute(object? parameter) => window.Active += by;
    }

    /// <summary>
    /// One tab by its position.
    ///
    /// <para>An index past the last does nothing rather than wrapping, which is the one place the
    /// wrapping above would be wrong: Alt+7 in a window with three tabs is a mistake, and landing on
    /// the first would look like the chord did something else.</para>
    /// </summary>
    private sealed class Reach(MainWindow window, int at) : ICommand
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
        public void Execute(object? parameter)
        {
            if (at < window.Held.Count)
            {
                window.Active = at;
            }
        }
    }

    /// <summary>The splitting bindings' command.</summary>
    private sealed class Splitting(MainWindow window, Divide how) : ICommand
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
        public void Execute(object? parameter) => window.SplitPane(how);
    }

    /// <summary>The directional focus bindings' command.</summary>
    private sealed class Facing(MainWindow window, Toward way) : ICommand
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
        public void Execute(object? parameter) => window.FocusPane(way);
    }

    /// <summary>The zoom binding's command.</summary>
    private sealed class Zooming(MainWindow window) : ICommand
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
        public void Execute(object? parameter) => window.ZoomPane();
    }

    /// <summary>The equalise binding's command.</summary>
    private sealed class Equalising(MainWindow window) : ICommand
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
        public void Execute(object? parameter) => window.EqualisePanes();
    }

    /// <summary>The find binding's command: opens the bar, or closes one already open.</summary>
    private sealed class Find(MainWindow window) : ICommand
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
        public void Execute(object? parameter) => window.ShowFindBar(!window.FindBarShowing);
    }

    /// <summary>The scrollback bindings' command, one screenful at a time.</summary>
    private sealed class Scroll(MainWindow window, bool up) : ICommand
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
        public void Execute(object? parameter) => window.Scrolling?.Invoke(up ? -Page : Page);

        /// <summary>
        /// How far a page key moves.
        ///
        /// <para>A fixed number rather than the screen's height, because this window does not know
        /// how many rows the pane holds — the grid is the view's and the view is behind a delegate.
        /// Twenty-four is the height a terminal has meant since before either of us, and it is close
        /// enough to a screen that nobody counts.</para>
        /// </summary>
        private const int Page = 24;
    }

    /// <summary>The copy binding's command.</summary>
    private sealed class Copy(MainWindow window) : ICommand
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
        public void Execute(object? parameter) => window.CopySelection();
    }

    /// <summary>The paste binding's command.</summary>
    private sealed class PasteIn(MainWindow window) : ICommand
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
        public void Execute(object? parameter) => window.PasteFromClipboard();
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
