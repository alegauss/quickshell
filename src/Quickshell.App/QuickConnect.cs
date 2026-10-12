using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace Quickshell.App;

/// <summary>
/// Quick connect (QS271): an address, how to sign in, whether to keep it, and Connect.
///
/// <para><b>The first thing a new user tries.</b> Every other client takes <c>user@host</c> and
/// connects; until this, a host nobody had saved meant the session dialog, filled and saved first.
/// The address parses into the session the store would hold (<see cref="QuickAddress"/>) and opens
/// by the path a saved one takes, so the host-key question, the sign-in prompts and the trace are
/// the same.</para>
///
/// <para><b>A control and not a window</b>, so the Home tab can hold it (QS272); the palette's
/// <b>Connect to an address</b> puts it in a small window of its own until then.</para>
/// </summary>
public sealed class QuickConnect : Border
{
    private static readonly FontFamily Mono = new("Cascadia Mono, Consolas");

    private static readonly FontFamily Glyphs = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    /// <summary>The sign-in ways in the drop-down's order, each as it is said there.</summary>
    private static readonly (SignInMethod Way, string Said)[] Ways =
    [
        (SignInMethod.KeyOrAgent, "Key or agent (automatic)"),
        (SignInMethod.Password, "Password"),
        (SignInMethod.KeyFile, "A specific key file…"),
    ];

    private readonly MainWindow _window;
    private readonly ComboBox _address;
    private readonly ComboBox _signIn;
    private readonly TextBox _key;
    private readonly FrameworkElement _keyRow;
    private readonly CheckBox _save;
    private readonly TextBlock _refused;

    // Set while the drop-down is changed from code, so only a person's choice opens the file picker.
    private bool _setting;

    /// <summary>Builds it over a window, which connects what it is given.</summary>
    /// <param name="window">Who connects.</param>
    /// <param name="aliases">The ssh_config hosts it offers as you type.</param>
    public QuickConnect(MainWindow window, IReadOnlyList<string> aliases)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(aliases);

        _window = window;

        AutomationProperties.SetAutomationId(this, "QuickConnect");
        AutomationProperties.SetName(this, "Quick connect");

        // Editable, so it is a box first and a list of the config's hosts second: typing the start of
        // one completes it, and the arrow lists them all. 40 high, monospace 14, with a terminal glyph
        // 12 in from its left and the placeholder the design gives it (QS280).
        _address = new ComboBox
        {
            IsEditable = true,
            IsTextSearchEnabled = true,
            Height = 40,
            FontFamily = Mono,
            FontSize = 14,
            Padding = new Thickness(36, 0, 8, 0),
            VerticalContentAlignment = VerticalAlignment.Center,
            Name = "Address",
            ToolTip = "user@host, host, user@host:port, or a host from ~/.ssh/config",
        };

        foreach (string alias in aliases)
        {
            _address.Items.Add(alias);
        }

        AutomationProperties.SetAutomationId(_address, "Address");
        AutomationProperties.SetName(_address, "Address");
        AutomationProperties.SetHelpText(_address, "user@host, host, user@host:port, or a host from ~/.ssh/config");

        System.Windows.Controls.Viewbox prompt = Templates.Terminal(16, "TextFillColorSecondaryBrush");

        prompt.Margin = new Thickness(12, 0, 0, 0);
        prompt.HorizontalAlignment = HorizontalAlignment.Left;
        prompt.IsHitTestVisible = false;

        TextBlock hint = new()
        {
            Text = "user@host or user@host:port",
            FontFamily = Mono,
            FontSize = 14,
            Margin = new Thickness(38, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
        };

        hint.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");

        _address.AddHandler(System.Windows.Controls.Primitives.TextBoxBase.TextChangedEvent,
                            new TextChangedEventHandler((_, _) => hint.Visibility = _address.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed));

        Grid box = new();

        box.Children.Add(_address);
        box.Children.Add(prompt);
        box.Children.Add(hint);

        _signIn = new ComboBox { Height = 40, Width = 230, Name = "SignIn", VerticalContentAlignment = VerticalAlignment.Center };

        foreach ((SignInMethod way, string said) in Ways)
        {
            ComboBoxItem item = new() { Content = said, Tag = way, Name = $"Quick{way}" };

            AutomationProperties.SetName(item, said);
            _signIn.Items.Add(item);
        }

        _signIn.SelectedIndex = 0;
        AutomationProperties.SetAutomationId(_signIn, "SignIn");
        AutomationProperties.SetName(_signIn, "Sign in with");
        _signIn.SelectionChanged += (_, _) => Chose();

        StackPanel arrowed = new() { Orientation = Orientation.Horizontal };

        arrowed.Children.Add(new TextBlock { Text = "Connect", FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });

        TextBlock arrow = Glyph("", 14);

        arrow.Margin = new Thickness(8, 0, 0, 0);
        arrowed.Children.Add(arrow);

        Button connect = new()
        {
            Content = arrowed,
            IsDefault = true,
            Height = 40,
            Padding = new Thickness(22, 0, 22, 0),
            VerticalAlignment = VerticalAlignment.Bottom,
            Name = "Connect",
        };

        connect.SetResourceReference(StyleProperty, "AccentButtonStyle");
        AutomationProperties.SetName(connect, "Connect");
        connect.Click += (_, _) => Go();

        // One row, bottom-aligned, 12 apart: the address, how to sign in, and Connect.
        Grid row = new();

        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        FrameworkElement address = Labelled("Address", box);
        FrameworkElement signIn = Labelled("Sign in with", _signIn);

        signIn.Margin = new Thickness(12, 0, 0, 0);
        connect.Margin = new Thickness(12, 0, 0, 0);
        Grid.SetColumn(signIn, 1);
        Grid.SetColumn(connect, 2);
        row.Children.Add(address);
        row.Children.Add(signIn);
        row.Children.Add(connect);

        _refused = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0), Name = "AddressRefused" };
        _refused.SetResourceReference(TextBlock.ForegroundProperty, "SystemFillColorCriticalBrush");
        AutomationProperties.SetAutomationId(_refused, "AddressRefused");

        _key = new TextBox { Height = 32, FontFamily = Mono, Name = "QuickKey" };
        AutomationProperties.SetName(_key, "Key file");

        _keyRow = Labelled("Key file", _key);
        _keyRow.Margin = new Thickness(0, 12, 0, 0);

        _save = new CheckBox
        {
            Content = "Save as a session",
            IsChecked = true,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Name = "SaveAddress",
            ToolTip = "Keeps it in the sessions sidebar, under no folder, named after the host",
        };

        AutomationProperties.SetName(_save, "Save as a session");

        TextBlock offered = new()
        {
            Text = "Hosts in ~/.ssh/config are offered as you type.",
            FontSize = 12,
            Margin = new Thickness(16, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };

        offered.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");

        StackPanel under = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };

        under.Children.Add(_save);
        under.Children.Add(offered);

        StackPanel body = new();

        body.Children.Add(row);
        body.Children.Add(_refused);
        body.Children.Add(_keyRow);
        body.Children.Add(under);

        Child = body;

        Shape();
    }

    /// <summary>What the box holds.</summary>
    public string Address
    {
        get => _address.Text;
        set => _address.Text = value;
    }

    /// <summary>How it signs in.</summary>
    public SignInMethod SignIn
    {
        get => _signIn.SelectedItem is ComboBoxItem { Tag: SignInMethod way } ? way : SignInMethod.KeyOrAgent;
        set
        {
            _setting = true;

            try
            {
                _signIn.SelectedIndex = Array.FindIndex(Ways, each => each.Way == value);
            }
            finally
            {
                _setting = false;
            }

            Shape();
        }
    }

    /// <summary>The ways the drop-down offers, as it says them, which is what a test reads.</summary>
    public IReadOnlyList<string> SignInWays => [.. _signIn.Items.OfType<ComboBoxItem>().Select(item => (string)item.Content)];

    /// <summary>The key file, where it signs in with one.</summary>
    public string KeyFile
    {
        get => _key.Text;
        set => _key.Text = value;
    }

    /// <summary>Whether Connect also saves it.</summary>
    public bool Saving
    {
        get => _save.IsChecked == true;
        set => _save.IsChecked = value;
    }

    /// <summary>
    /// What choosing <b>A specific key file…</b> asks: a file picker, unless a caller says otherwise —
    /// which is how a test answers. Null back is a picker left without a file.
    /// </summary>
    public Func<string?>? PicksKey { get; set; }

    /// <summary>Puts the keyboard in the address box, which is where a person starts.</summary>
    public void FocusAddress() => _address.Focus();

    /// <summary>Why the last address did not connect, or empty.</summary>
    public string Refused => _refused.Text;

    /// <summary>Raised once an address has connected, which is when a window of its own closes.</summary>
    public event Action? Connected;

    /// <summary>Connects what is typed, or says under the box why it cannot.</summary>
    public bool Go()
    {
        string refused = _window.ConnectAddress(Address, SignIn, KeyFile, Saving);

        _refused.Text = refused;

        if (refused.Length > 0)
        {
            return false;
        }

        Connected?.Invoke();

        return true;
    }

    /// <summary>A person chose a way: a key file is asked for there and then, and the key's box shows.</summary>
    private void Chose()
    {
        Shape();

        if (_setting || SignIn != SignInMethod.KeyFile)
        {
            return;
        }

        if ((PicksKey ?? Picked)() is { Length: > 0 } file)
        {
            _key.Text = file;
        }
    }

    private string? Picked()
    {
        Microsoft.Win32.OpenFileDialog dialog = new()
        {
            Title = "A specific key file",
            Filter = "Private keys|*.*",
            InitialDirectory = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh"),
        };

        return dialog.ShowDialog(Window.GetWindow(this)) == true ? dialog.FileName : null;
    }

    private void Shape() =>
        _keyRow.Visibility = SignIn == SignInMethod.KeyFile ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>A 12-pixel SemiBold label 6 above what it names.</summary>
    private static StackPanel Labelled(string text, FrameworkElement named)
    {
        TextBlock label = new() { Text = text, FontSize = 12, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) };

        label.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");

        StackPanel labelled = new() { VerticalAlignment = VerticalAlignment.Bottom };

        labelled.Children.Add(label);
        labelled.Children.Add(named);

        return labelled;
    }

    private static TextBlock Glyph(string glyph, double size) => new()
    {
        Text = glyph,
        FontFamily = Glyphs,
        FontSize = size,
        VerticalAlignment = VerticalAlignment.Center,
    };
}
/// <summary>Quick connect in a small window of its own, from the palette's <b>Connect to an address</b> (QS271).</summary>
public sealed class QuickConnectWindow : Window
{
    /// <summary>A window around one quick connect, which closes once it has connected.</summary>
    public QuickConnectWindow(QuickConnect quick)
    {
        ArgumentNullException.ThrowIfNull(quick);

        Quick = quick;
        Title = "Connect to an address";
        Width = 760;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        AutomationProperties.SetAutomationId(this, "QuickConnectWindow");

        quick.Margin = new Thickness(20, 8, 20, 20);
        Content = quick;

        quick.Connected += Close;

        Loaded += (_, _) => quick.FocusAddress();

        // Escape leaves without connecting, as every small window does.
        PreviewKeyDown += (_, pressed) =>
        {
            if (pressed.Key == System.Windows.Input.Key.Escape)
            {
                pressed.Handled = true;
                Close();
            }
        };
    }

    /// <summary>What it holds.</summary>
    public QuickConnect Quick { get; }
}
