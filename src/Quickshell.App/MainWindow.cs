using System.Windows;
using System.Windows.Controls;

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
    }

    /// <summary>How this window looks. The palette here is the terminal's and not the chrome's.</summary>
    public Appearance Appearance { get; }

    /// <summary>What this window shows.</summary>
    public Chrome Chrome { get; }

    /// <summary>How many tabs are open, which is what decides whether the strip is visible.</summary>
    public int Tabs => Math.Max(1, _tabs.Items.Count);

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

    /// <summary>Where this window is now, for remembering.</summary>
    public Placement Where() => new(
        (int)(WindowState == WindowState.Maximized ? RestoreBounds.Left : Left),
        (int)(WindowState == WindowState.Maximized ? RestoreBounds.Top : Top),
        (int)(WindowState == WindowState.Maximized ? RestoreBounds.Width : Width),
        (int)(WindowState == WindowState.Maximized ? RestoreBounds.Height : Height),
        WindowState == WindowState.Maximized);
}
