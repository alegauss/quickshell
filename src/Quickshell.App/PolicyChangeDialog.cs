using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace Quickshell.App;

/// <summary>
/// The agent policy changed outside quickshell's own window (QS339): the change shown as a diff, and the
/// person accepts it or rejects it. Until they answer, the gateway refuses every call.
/// </summary>
public sealed class PolicyChangeDialog : Window
{
    /// <summary>Builds the dialog for the change waiting on <paramref name="policyFile"/>.</summary>
    public PolicyChangeDialog(string policyFile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(policyFile);

        Title = "The agent policy changed";
        AutomationProperties.SetAutomationId(this, "PolicyChanged");

        SizeToContent = SizeToContent.WidthAndHeight;
        MaxWidth = 820;
        MaxHeight = 760;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Topmost = true;

        StackPanel body = new() { Margin = new Thickness(18) };

        TextBlock said = new()
        {
            Text = "The agent policy was changed outside quickshell. Until you accept or reject the change, every call an agent "
                   + "makes is refused. An agent that can write files could have made it.",
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 640,
        };

        AutomationProperties.SetAutomationId(said, "PolicyChangedSaid");
        body.Children.Add(said);

        TextBlock diff = new()
        {
            Text = PolicyApproval.Change(policyFile),
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            Margin = new Thickness(0, 10, 0, 0),
        };

        AutomationProperties.SetAutomationId(diff, "PolicyDiff");
        body.Children.Add(new ScrollViewer { Content = diff, MaxHeight = 480, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto });

        StackPanel answers = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 0) };

        answers.Children.Add(Action("Reject the change", "RejectPolicy", () => PolicyApproval.Reject(policyFile)));
        answers.Children.Add(Action("Accept the change", "AcceptPolicy", () => PolicyApproval.Accept(policyFile)));
        body.Children.Add(answers);

        Content = body;
    }

    private Button Action(string label, string id, Action act)
    {
        Button button = new() { Content = label, Padding = new Thickness(14, 4, 14, 4), Margin = new Thickness(0, 0, 8, 0) };

        AutomationProperties.SetAutomationId(button, id);
        button.Click += (_, _) =>
        {
            act();
            DialogResult = true;
        };

        return button;
    }
}
