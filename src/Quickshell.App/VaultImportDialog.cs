using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace Quickshell.App;

/// <summary>What the import dialog was answered with: the vault, what opens it, and a key file where it needs one.</summary>
/// <param name="Path">The vault.</param>
/// <param name="Passphrase">Its passphrase.</param>
/// <param name="KeyFile">Its key file, or null.</param>
public sealed record VaultImportAnswer(string Path, string Passphrase, string? KeyFile);

/// <summary>Which vault to read saved passwords from, and how to open it (QS308).</summary>
public sealed class VaultImportDialog : Window
{
    private readonly TextBox _path = new() { MinWidth = 380, Padding = new Thickness(4) };
    private readonly PasswordBox _passphrase = new() { MinWidth = 380, Padding = new Thickness(4) };
    private readonly TextBox _keyFile = new() { MinWidth = 380, Padding = new Thickness(4) };

    /// <summary>Builds the dialog.</summary>
    /// <param name="suggested">The file offered, which may be empty.</param>
    public VaultImportDialog(string suggested)
    {
        Title = "Import saved passwords";
        AutomationProperties.SetAutomationId(this, "ImportVault");

        SizeToContent = SizeToContent.WidthAndHeight;
        MaxWidth = 560;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        StackPanel body = new() { Margin = new Thickness(18) };

        TextBlock terms = new()
        {
            Text = Terms,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 420,
            Margin = new Thickness(0, 0, 0, 12),
        };

        AutomationProperties.SetAutomationId(terms, "Terms");
        AutomationProperties.SetName(terms, terms.Text);
        body.Children.Add(terms);

        body.Children.Add(Label("File"));
        _path.Text = suggested;
        body.Children.Add(Browsing(_path, "File", "browse", choose =>
        {
            choose.Filter = "KeePass database (*.kdbx)|*.kdbx|All files (*.*)|*.*";
        }));

        body.Children.Add(Label("Passphrase", top: 12));
        AutomationProperties.SetName(_passphrase, "Passphrase");
        AutomationProperties.SetAutomationId(_passphrase, "Passphrase");
        body.Children.Add(_passphrase);

        body.Children.Add(Label("Key file, if the vault has one", top: 12));
        body.Children.Add(Browsing(_keyFile, "Key file", "browse-key", _ => { }));

        StackPanel row = new()
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0),
        };

        Button open = new() { Content = "Open", Padding = new Thickness(14, 4, 14, 4), IsDefault = true };
        Button decline = new()
        {
            Content = "Don't import",
            Padding = new Thickness(14, 4, 14, 4),
            Margin = new Thickness(8, 0, 0, 0),
            IsCancel = true,
        };

        AutomationProperties.SetAutomationId(open, "open");
        AutomationProperties.SetAutomationId(decline, "decline");

        open.Click += (_, _) =>
        {
            if (_path.Text.Trim() is { Length: > 0 } path)
            {
                Answered = new VaultImportAnswer(path, _passphrase.Password,
                                                 _keyFile.Text.Trim() is { Length: > 0 } key ? key : null);
                Close();
            }
        };

        decline.Click += (_, _) => Close();

        row.Children.Add(open);
        row.Children.Add(decline);
        body.Children.Add(row);

        Content = body;

        Loaded += (_, _) =>
        {
            if (_path.Text.Length > 0)
            {
                _passphrase.Focus();
            }
            else
            {
                _path.Focus();
            }
        };
    }

    /// <summary>What the import tells a person before they choose anything.</summary>
    public const string Terms =
        "The passwords in a KeePass vault are saved for the sessions they belong to, matched by each entry's "
        + "ssh:// URL, or its host and user name. You see what would change before anything is saved, and a "
        + "password already saved is never replaced unless you tick it.";

    /// <summary>What was answered, or null where the person declined or closed the window.</summary>
    public VaultImportAnswer? Answered { get; private set; }

    private DockPanel Browsing(TextBox field, string name, string id, Action<Microsoft.Win32.OpenFileDialog> shape)
    {
        DockPanel where = new();
        Button browse = new() { Content = "Browse…", Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(6, 0, 0, 0) };

        AutomationProperties.SetAutomationId(browse, id);
        AutomationProperties.SetName(field, name);
        AutomationProperties.SetAutomationId(field, name.Replace(" ", string.Empty, StringComparison.Ordinal));

        browse.Click += (_, _) =>
        {
            Microsoft.Win32.OpenFileDialog choose = new() { Title = name, CheckFileExists = true };

            shape(choose);

            if (Path.GetDirectoryName(field.Text) is { Length: > 0 } folder && Directory.Exists(folder))
            {
                choose.InitialDirectory = folder;
            }

            if (choose.ShowDialog(this) == true)
            {
                field.Text = choose.FileName;
            }
        };

        DockPanel.SetDock(browse, Dock.Right);
        where.Children.Add(browse);
        where.Children.Add(field);

        return where;
    }

    private static TextBlock Label(string text, double top = 0) =>
        new() { Text = text, Margin = new Thickness(0, top, 0, 4), Opacity = 0.8 };
}

/// <summary>
/// What an import would save, before it saves anything (QS308): how many passwords are new, how many
/// are already there, and each one that differs from what is saved, which is replaced only if ticked.
/// </summary>
public sealed class VaultImportPreview : Window
{
    private readonly List<(string Label, CheckBox Box)> _conflicts = [];

    /// <summary>Builds the preview over a plan.</summary>
    public VaultImportPreview(VaultImport plan, string path)
    {
        ArgumentNullException.ThrowIfNull(plan);

        Title = "Import saved passwords";
        AutomationProperties.SetAutomationId(this, "ImportPreview");

        SizeToContent = SizeToContent.WidthAndHeight;
        MaxWidth = 560;
        MaxHeight = 640;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        StackPanel body = new() { Margin = new Thickness(18) };

        TextBlock summary = new()
        {
            Text = Summary(plan, path),
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 420,
        };

        AutomationProperties.SetAutomationId(summary, "Summary");
        AutomationProperties.SetName(summary, summary.Text);
        body.Children.Add(summary);

        foreach (ImportItem conflict in plan.Items.Where(item => item.State == ImportState.Differs))
        {
            CheckBox box = new()
            {
                Content = $"Replace the saved password for {conflict.Label}",
                Margin = new Thickness(0, 8, 0, 0),
            };

            AutomationProperties.SetName(box, (string)box.Content);
            _conflicts.Add((conflict.Label, box));
            body.Children.Add(box);
        }

        StackPanel row = new()
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0),
        };

        Button import = new() { Content = "Import", Padding = new Thickness(14, 4, 14, 4), IsDefault = true };
        Button decline = new()
        {
            Content = "Don't import",
            Padding = new Thickness(14, 4, 14, 4),
            Margin = new Thickness(8, 0, 0, 0),
            IsCancel = true,
        };

        AutomationProperties.SetAutomationId(import, "import");
        AutomationProperties.SetAutomationId(decline, "decline");

        import.Click += (_, _) =>
        {
            Replace = new HashSet<string>(_conflicts.Where(one => one.Box.IsChecked == true).Select(one => one.Label),
                                          StringComparer.Ordinal);
            Close();
        };

        decline.Click += (_, _) => Close();

        row.Children.Add(import);
        row.Children.Add(decline);
        body.Children.Add(row);

        Content = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    /// <summary>The conflicts the person chose to replace, or null where they declined the import.</summary>
    public IReadOnlySet<string>? Replace { get; private set; }

    /// <summary>What the preview says, in one paragraph.</summary>
    public static string Summary(VaultImport plan, string path)
    {
        ArgumentNullException.ThrowIfNull(plan);

        int fresh = plan.Items.Count(item => item.State == ImportState.New);
        int same = plan.Items.Count(item => item.State == ImportState.Same);
        int differs = plan.Items.Count(item => item.State == ImportState.Differs);

        string said = $"{path} has passwords for {plan.Items.Count} of your saved sessions' accounts: {fresh} not saved yet, "
                      + $"which will be, {same} already saved as they are, and {differs} saved with a different password, "
                      + "which stay as they are unless ticked below.";

        if (plan.Unmatched.Count > 0)
        {
            said += $" {plan.Unmatched.Count} entries match no saved session and are left: "
                    + string.Join(", ", plan.Unmatched.Take(8)) + (plan.Unmatched.Count > 8 ? ", …" : string.Empty) + ".";
        }

        return said;
    }
}
