using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace Quickshell.App;

/// <summary>What the export dialog was answered with: where the vault goes, and what opens it.</summary>
/// <param name="Path">The file to write.</param>
/// <param name="Passphrase">The passphrase, which the caller turns into a <see cref="Quickshell.Transport.Secret"/> at once.</param>
/// <param name="Keep">Whether the file is to be kept up to date from now on (QS307).</param>
/// <param name="Keys">Whether the key files sessions name go in as attachments (QS345).</param>
public sealed record VaultExportAnswer(string Path, string Passphrase, bool Keep = false, bool Keys = false);

/// <summary>
/// Where to write the vault of saved passwords, and the passphrase it is written under (QS306).
///
/// <para><b>The passphrase is the protection</b>, so the dialog offers a generated one, six words
/// shown in the clear so they can be written down, and refuses a typed one whose estimate is low,
/// saying why (<see cref="Passphrase"/>). Export stays unavailable until the passphrase is accepted
/// and typed twice alike, or generated.</para>
/// </summary>
public sealed class VaultExportDialog : Window
{
    private readonly TextBox _path = new() { MinWidth = 380, Padding = new Thickness(4) };
    private readonly PasswordBox _typed = new() { MinWidth = 380, Padding = new Thickness(4) };
    private readonly PasswordBox _again = new() { MinWidth = 380, Padding = new Thickness(4), Margin = new Thickness(0, 6, 0, 0) };
    private readonly TextBox _generated = new() { MinWidth = 380, Padding = new Thickness(4), IsReadOnly = true, Visibility = Visibility.Collapsed };
    private readonly TextBlock _verdict = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0), MaxWidth = 420 };
    private readonly Button _export = new() { Content = "Export", Padding = new Thickness(14, 4, 14, 4), IsDefault = true, IsEnabled = false };
    private readonly CheckBox _keep = new() { Content = "Keep this file up to date", Margin = new Thickness(0, 12, 0, 0) };
    private readonly CheckBox _keys = new() { Content = "Include the key files sessions name", Margin = new Thickness(0, 12, 0, 0) };

    /// <summary>What including the key files costs, said beside the box that turns it on (QS345).</summary>
    public const string KeysTerms =
        "Each private key a session names goes in once, with its .pub beside it. A key that has no passphrase of "
        + "its own is then protected by the vault's passphrase alone.";

    /// <summary>What keeping the file up to date does, said beside the box that turns it on (QS307).</summary>
    public const string KeepTerms =
        "It is written again whenever a saved password or a session changes, without asking for the passphrase: "
        + "its key is kept for this Windows account. In a synced folder such as OneDrive it reaches your other "
        + "devices, where the passphrase alone protects it.";

    /// <summary>Builds the dialog.</summary>
    /// <param name="suggested">The file offered.</param>
    /// <param name="sessions">How many sessions the store holds, which the terms say.</param>
    public VaultExportDialog(string suggested, int sessions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(suggested);

        Title = "Export saved passwords";
        AutomationProperties.SetAutomationId(this, "ExportVault");

        SizeToContent = SizeToContent.WidthAndHeight;
        MaxWidth = 560;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        StackPanel body = new() { Margin = new Thickness(18) };

        TextBlock terms = new()
        {
            Text = Terms(sessions),
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 420,
            Margin = new Thickness(0, 0, 0, 12),
        };

        AutomationProperties.SetAutomationId(terms, "Terms");
        AutomationProperties.SetName(terms, terms.Text);
        body.Children.Add(terms);

        body.Children.Add(Label("File"));

        DockPanel where = new();
        Button browse = new() { Content = "Browse…", Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(6, 0, 0, 0) };

        AutomationProperties.SetAutomationId(browse, "browse");
        browse.Click += (_, _) => Browse();
        DockPanel.SetDock(browse, Dock.Right);
        where.Children.Add(browse);

        _path.Text = suggested;
        AutomationProperties.SetName(_path, "File");
        AutomationProperties.SetAutomationId(_path, "File");
        where.Children.Add(_path);
        body.Children.Add(where);

        TextBlock passphrase = Label("Passphrase");
        passphrase.Margin = new Thickness(0, 12, 0, 4);
        body.Children.Add(passphrase);

        AutomationProperties.SetName(_typed, "Passphrase");
        AutomationProperties.SetAutomationId(_typed, "Passphrase");
        AutomationProperties.SetName(_again, "Passphrase again");
        AutomationProperties.SetAutomationId(_again, "PassphraseAgain");
        AutomationProperties.SetName(_generated, "Generated passphrase");
        AutomationProperties.SetAutomationId(_generated, "Generated");

        _typed.PasswordChanged += (_, _) => Judge();
        _again.PasswordChanged += (_, _) => Judge();

        body.Children.Add(_typed);
        body.Children.Add(_again);
        body.Children.Add(_generated);

        Button generate = new()
        {
            Content = "Generate a passphrase",
            Padding = new Thickness(10, 2, 10, 2),
            Margin = new Thickness(0, 8, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
        };

        AutomationProperties.SetAutomationId(generate, "generate");
        generate.Click += (_, _) => Generate();
        body.Children.Add(generate);

        AutomationProperties.SetAutomationId(_verdict, "Verdict");
        body.Children.Add(_verdict);

        AutomationProperties.SetAutomationId(_keys, "Keys");
        body.Children.Add(_keys);
        body.Children.Add(new TextBlock
        {
            Text = KeysTerms,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 400,
            Margin = new Thickness(20, 4, 0, 0),
            Opacity = 0.7,
            FontSize = 11,
        });

        AutomationProperties.SetAutomationId(_keep, "Keep");
        body.Children.Add(_keep);
        body.Children.Add(new TextBlock
        {
            Text = KeepTerms,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 400,
            Margin = new Thickness(20, 4, 0, 0),
            Opacity = 0.7,
            FontSize = 11,
        });

        StackPanel row = new()
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0),
        };

        Button decline = new()
        {
            Content = "Don't export",
            Padding = new Thickness(14, 4, 14, 4),
            Margin = new Thickness(8, 0, 0, 0),
            IsCancel = true,
        };

        AutomationProperties.SetAutomationId(_export, "export");
        AutomationProperties.SetAutomationId(decline, "decline");

        _export.Click += (_, _) =>
        {
            if (Chosen() is { } chosen && _path.Text.Trim() is { Length: > 0 } path)
            {
                Answered = new VaultExportAnswer(path, chosen, _keep.IsChecked == true, _keys.IsChecked == true);
                Close();
            }
        };

        decline.Click += (_, _) => Close();

        row.Children.Add(_export);
        row.Children.Add(decline);
        body.Children.Add(row);

        Content = body;

        Judge();

        Loaded += (_, _) => _typed.Focus();
    }

    /// <summary>What was answered, or null where the person declined or closed the window.</summary>
    public VaultExportAnswer? Answered { get; private set; }

    /// <summary>Whether Export can be pressed now, which a test reads.</summary>
    public bool CanExport => _export.IsEnabled;

    /// <summary>What the dialog says about the passphrase now.</summary>
    public string Verdict => _verdict.Text;

    /// <summary>The generated passphrase on show, or null where none was generated.</summary>
    public string? GeneratedPassphrase => _generated.Visibility == Visibility.Visible ? _generated.Text : null;

    /// <summary>Puts a typed passphrase in both boxes, as a person would; what a test does.</summary>
    public void Type(string passphrase, string? again = null)
    {
        _typed.Password = passphrase;
        _again.Password = again ?? passphrase;
    }

    /// <summary>Generates six words and shows them, replacing whatever was typed.</summary>
    public void Generate()
    {
        _generated.Text = Passphrase.Generate();
        _generated.Visibility = Visibility.Visible;
        _typed.Visibility = Visibility.Collapsed;
        _again.Visibility = Visibility.Collapsed;
        _typed.Clear();
        _again.Clear();

        Judge();
    }

    /// <summary>What the export dialog tells a person before they choose anything.</summary>
    public static string Terms(int sessions) =>
        $"Every saved session, {sessions} of them, goes into a KeePass vault with its host, port, login and saved password, "
        + "so you can look one up in KeePassXC or any KeePass app when you forget it. "
        + "Windows Hello asks who you are before the passwords are read. "
        + "Anyone who copies the file can try passphrases against it for as long as it exists, so the passphrase is what protects it.";

    /// <summary>The passphrase that would be used now, or null where none is acceptable.</summary>
    private string? Chosen()
    {
        if (GeneratedPassphrase is { } generated)
        {
            return generated;
        }

        return _typed.Password.Length > 0 && _typed.Password == _again.Password && Passphrase.Refusal(_typed.Password) is null
            ? _typed.Password
            : null;
    }

    private void Judge()
    {
        if (GeneratedPassphrase is not null)
        {
            _verdict.Text = "Write these six words down before you export: they are the only way into the vault.";
        }
        else if (_typed.Password.Length == 0)
        {
            _verdict.Text = $"Type a passphrase of at least {Passphrase.RequiredBits} bits' estimated strength, or generate one.";
        }
        else if (Passphrase.Refusal(_typed.Password) is { } refused)
        {
            _verdict.Text = refused;
        }
        else if (_typed.Password != _again.Password)
        {
            _verdict.Text = "The two passphrases are not the same.";
        }
        else
        {
            _verdict.Text = "This passphrase is strong enough.";
        }

        AutomationProperties.SetName(_verdict, _verdict.Text);
        _export.IsEnabled = Chosen() is not null;
    }

    private void Browse()
    {
        Microsoft.Win32.SaveFileDialog choose = new()
        {
            Title = "Export saved passwords",
            Filter = "KeePass database (*.kdbx)|*.kdbx",
            DefaultExt = ".kdbx",
            FileName = Path.GetFileName(_path.Text),
            InitialDirectory = Path.GetDirectoryName(_path.Text) is { Length: > 0 } folder && Directory.Exists(folder) ? folder : string.Empty,
        };

        if (choose.ShowDialog(this) == true)
        {
            _path.Text = choose.FileName;
        }
    }

    private static TextBlock Label(string text) => new() { Text = text, Margin = new Thickness(0, 0, 0, 4), Opacity = 0.8 };
}
