using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace Quickshell.App;

/// <summary>
/// The guided setup of an agent account on one server (QS340): the account's name and the commands it may
/// run as root, and the exact script that will make it, written again on every change so what the person
/// reads is what runs. Nothing runs from here: Run hands the plan back to the window, which runs it in the
/// person's own tab, under their own sudo.
/// </summary>
public sealed class AgentSetupDialog : Window
{
    private readonly string _host;
    private readonly string _publicKey;
    private readonly string _from;
    private readonly TextBox _account;
    private readonly TextBox _commands;
    private readonly TextBlock _script;
    private readonly TextBlock _refused;
    private readonly Button _run;

    /// <summary>Builds the dialog for one server, its list started from <paramref name="suggested"/>.</summary>
    /// <param name="host">The server, as the session names it.</param>
    /// <param name="publicKey">The agent key's public half.</param>
    /// <param name="from">The address the server sees this machine connect from.</param>
    /// <param name="suggested">Commands to start the list with: what the policy already granted as root.</param>
    public AgentSetupDialog(string host, string publicKey, string from, IReadOnlyList<IReadOnlyList<string>> suggested)
    {
        ArgumentNullException.ThrowIfNull(suggested);

        _host = host;
        _publicKey = publicKey;
        _from = from;

        Title = $"Set up an agent account on {host}";
        AutomationProperties.SetAutomationId(this, "AgentSetup");

        SizeToContent = SizeToContent.WidthAndHeight;
        MaxWidth = 860;
        MaxHeight = 820;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        StackPanel body = new() { Margin = new Thickness(18) };

        body.Children.Add(new TextBlock
        {
            Text = $"An account on {host} that the agent gateway signs in as, with a key of its own that only works from {from} "
                   + "and opens no terminal or forwarding. As root it may run the commands listed below, each exactly as written, "
                   + "and nothing else. The server enforces these limits, even when the agent policy in quickshell is turned off.",
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 640,
        });

        body.Children.Add(Label("Account"));
        _account = new TextBox { Text = "agent", Width = 220, HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetAutomationId(_account, "AgentAccount");
        AutomationProperties.SetName(_account, "Account");
        body.Children.Add(_account);

        body.Children.Add(Label("Commands it may run as root, one a line, program first (systemctl restart nginx)"));
        _commands = new TextBox
        {
            Text = string.Join("\n", suggested.Select(command => string.Join(' ', command))),
            AcceptsReturn = true,
            MinHeight = 72,
            Width = 640,
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        AutomationProperties.SetAutomationId(_commands, "AgentCommands");
        AutomationProperties.SetName(_commands, "Commands as root");
        body.Children.Add(_commands);

        body.Children.Add(Label("What will run on the server, as root, from your tab"));
        _script = new TextBlock { FontFamily = new FontFamily("Cascadia Mono, Consolas"), Margin = new Thickness(0, 4, 0, 0) };
        AutomationProperties.SetAutomationId(_script, "AgentScript");
        body.Children.Add(new ScrollViewer { Content = _script, MaxHeight = 360, Width = 800, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto });

        _refused = new TextBlock { TextWrapping = TextWrapping.Wrap, MaxWidth = 640, Margin = new Thickness(0, 8, 0, 0) };
        _refused.SetResourceReference(TextBlock.ForegroundProperty, "SystemFillColorCriticalBrush");
        AutomationProperties.SetAutomationId(_refused, "AgentRefused");
        body.Children.Add(_refused);

        StackPanel answers = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 0) };

        _run = new Button { Content = "Run it in my tab", Padding = new Thickness(14, 4, 14, 4), Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        AutomationProperties.SetAutomationId(_run, "RunAgentSetup");
        _run.Click += (_, _) => DialogResult = Plan is not null;
        answers.Children.Add(_run);

        Button cancel = new() { Content = "Cancel", Padding = new Thickness(14, 4, 14, 4), IsCancel = true };
        AutomationProperties.SetAutomationId(cancel, "CancelAgentSetup");
        answers.Children.Add(cancel);

        body.Children.Add(answers);
        Content = body;

        _account.TextChanged += (_, _) => Written();
        _commands.TextChanged += (_, _) => Written();
        Written();
    }

    /// <summary>The account named, as it stood when the plan was last written.</summary>
    public string Account => _account.Text.Trim();

    /// <summary>The plan as the dialog shows it, or null while what is typed cannot be written exactly.</summary>
    public AgentSetupPlan? Plan { get; private set; }

    /// <summary>The commands typed, each split into its words, blank lines left out.</summary>
    public IReadOnlyList<IReadOnlyList<string>> Commands =>
        [.. _commands.Text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                     .Select(line => (IReadOnlyList<string>)line.Split(' ', StringSplitOptions.RemoveEmptyEntries))];

    private void Written()
    {
        try
        {
            Plan = AgentAccountSetup.Plan(Account, _publicKey, _from, Commands);
            _script.Text = Plan.Script;
            _refused.Text = string.Empty;
        }
        catch (AgentSetupRefusal refusal)
        {
            Plan = null;
            _script.Text = string.Empty;
            _refused.Text = refusal.Message;
        }

        _run.IsEnabled = Plan is not null;
    }

    private static TextBlock Label(string text) => new() { Text = text, Margin = new Thickness(0, 12, 0, 4), FontWeight = FontWeights.SemiBold };

    /// <summary>The server this dialog sets up, for a title or a test.</summary>
    public string Host => _host;
}
