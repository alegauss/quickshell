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

    private readonly MainWindow _window;
    private readonly ComboBox _address;
    private readonly Dictionary<SignInMethod, RadioButton> _ways = [];
    private readonly TextBox _key;
    private readonly FrameworkElement _keyRow;
    private readonly CheckBox _save;
    private readonly TextBlock _refused;

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
        // one completes it, and the arrow lists them all.
        _address = new ComboBox
        {
            IsEditable = true,
            IsTextSearchEnabled = true,
            Height = 36,
            FontFamily = Mono,
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

        Button connect = new()
        {
            Content = "Connect",
            IsDefault = true,
            Height = 36,
            Padding = new Thickness(18, 0, 18, 0),
            Margin = new Thickness(10, 0, 0, 0),
            Name = "Connect",
        };

        connect.SetResourceReference(StyleProperty, "AccentButtonStyle");
        AutomationProperties.SetName(connect, "Connect");
        connect.Click += (_, _) => Go();

        DockPanel line = new();

        DockPanel.SetDock(connect, Dock.Right);
        line.Children.Add(connect);
        line.Children.Add(_address);

        UniformGrid ways = new() { Rows = 1, Columns = 3, Margin = new Thickness(0, 6, 0, 0) };

        foreach ((SignInMethod way, string said) in new[]
                 {
                     (SignInMethod.KeyOrAgent, "Key or agent"),
                     (SignInMethod.Password, "Password"),
                     (SignInMethod.KeyFile, "Key file"),
                 })
        {
            RadioButton segment = new()
            {
                Content = said,
                GroupName = "QuickSignIn",
                Name = $"Quick{way}",
                Height = 32,
                Margin = new Thickness(1),
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                IsChecked = way == SignInMethod.KeyOrAgent,
            };

            segment.SetResourceReference(StyleProperty, typeof(ToggleButton));
            AutomationProperties.SetName(segment, said);
            segment.Checked += (_, _) => Shape();

            _ways[way] = segment;
            ways.Children.Add(segment);
        }

        _key = new TextBox { Height = 32, FontFamily = Mono, Name = "QuickKey" };
        AutomationProperties.SetName(_key, "Key file");

        StackPanel keyRow = new() { Margin = new Thickness(0, 6, 0, 0) };

        keyRow.Children.Add(Small("Key file"));
        keyRow.Children.Add(_key);
        _keyRow = keyRow;

        _save = new CheckBox
        {
            Content = "Save as a session",
            IsChecked = true,
            Margin = new Thickness(0, 10, 0, 0),
            Name = "SaveAddress",
            ToolTip = "Keeps it in the sessions sidebar, under no folder, named after the host",
        };

        AutomationProperties.SetName(_save, "Save as a session");

        _refused = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0), Name = "AddressRefused" };
        _refused.SetResourceReference(TextBlock.ForegroundProperty, "SystemFillColorCriticalBrush");
        AutomationProperties.SetAutomationId(_refused, "AddressRefused");

        StackPanel body = new();

        body.Children.Add(Small("Address"));
        body.Children.Add(line);
        body.Children.Add(_refused);
        body.Children.Add(Small("Sign in with"));
        body.Children.Add(ways);
        body.Children.Add(keyRow);
        body.Children.Add(_save);

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
        get => _ways.First(way => way.Value.IsChecked == true).Key;
        set => _ways[value].IsChecked = true;
    }

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

    private void Shape() =>
        _keyRow.Visibility = _ways.TryGetValue(SignInMethod.KeyFile, out RadioButton? file) && file.IsChecked == true
            ? Visibility.Visible
            : Visibility.Collapsed;

    private static TextBlock Small(string text)
    {
        TextBlock label = new() { Text = text, FontSize = 12, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 6) };

        label.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");

        return label;
    }
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
        Width = 480;
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
