using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace Quickshell.App;

/// <summary>
/// The person's answer to what an agent asks to run (QS337, the design of QS324): drawn by quickshell, not
/// by the agent's terminal, where the agent controls what is printed.
///
/// <para><b>Every call as it will run.</b> Its session, address and tags, prod in the danger colour; a
/// command as the words it runs, each in its own marks so quoting cannot hide a second one; an edit as
/// its diff; and the batch's short hash, which the agent's request carries too.</para>
///
/// <para><b>Three answers.</b> Run it, bound to the hash. Run it and let the same commands run on the
/// same sessions without asking for a time chosen from a short list, never open-ended, which is written
/// into the policy as a grant. Or refuse, with a note the agent receives.</para>
/// </summary>
public sealed class ApprovalDialog : Window
{
    /// <summary>The times a grant may be given for.</summary>
    public static readonly IReadOnlyList<(string Label, int Minutes)> GrantTimes = [("30 minutes", 30), ("1 hour", 60), ("4 hours", 240)];

    private readonly ApprovalRequest _request;
    private readonly TextBox _note = new() { MinWidth = 300, Padding = new Thickness(4), Margin = new Thickness(0, 0, 8, 0) };
    private readonly ComboBox _for = new() { MinWidth = 110, Margin = new Thickness(8, 0, 0, 0) };

    /// <summary>Builds the dialog for <paramref name="request"/>.</summary>
    public ApprovalDialog(ApprovalRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        _request = request;
        Answer = new ApprovalAnswer(Decision.Refuse, request.Hash, "closed without an answer");

        Title = "An agent asks to run this";
        AutomationProperties.SetAutomationId(this, "AgentApproval");

        SizeToContent = SizeToContent.WidthAndHeight;
        MaxWidth = 760;
        MaxHeight = 760;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = true;
        Topmost = true;

        StackPanel body = new() { Margin = new Thickness(18) };

        TextBlock heading = new()
        {
            Text = request.Calls.Count == 1 ? "An agent asks to run this" : $"An agent asks to run these {request.Calls.Count}, in this order",
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
        };

        AutomationProperties.SetAutomationId(heading, "AskHeading");
        body.Children.Add(heading);

        TextBlock hash = new() { Text = $"Plan {request.ShortHash}", Margin = new Thickness(0, 2, 0, 10), Opacity = 0.75 };

        AutomationProperties.SetAutomationId(hash, "PlanHash");
        body.Children.Add(hash);

        StackPanel calls = new();

        foreach (AskedCall call in request.Calls)
        {
            calls.Children.Add(Call(call));
        }

        body.Children.Add(new ScrollViewer { Content = calls, MaxHeight = 460, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });

        // Run it, or run it and allow for a while.
        StackPanel run = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 0) };
        Button approve = Action("Run it", "Approve", () => Answered(new ApprovalAnswer(Decision.Approve, request.Hash)));

        approve.IsDefault = false;
        run.Children.Add(approve);

        foreach ((string label, int _) in GrantTimes)
        {
            _for.Items.Add(label);
        }

        _for.SelectedIndex = 0;
        AutomationProperties.SetAutomationId(_for, "GrantFor");
        AutomationProperties.SetName(_for, "Allow for");

        Button grant = Action("Run it and allow these for", "Grant", () =>
            Answered(new ApprovalAnswer(Decision.Grant, request.Hash, GrantMinutes: GrantTimes[Math.Max(0, _for.SelectedIndex)].Minutes)));

        // A grant names commands whole, so a batch holding an edit cannot become one.
        bool grantable = request.Calls.All(call => call.Tool is "exec" or "job_start");

        grant.IsEnabled = grantable;
        _for.IsEnabled = grantable;
        grant.Margin = new Thickness(8, 0, 0, 0);
        run.Children.Add(grant);
        run.Children.Add(_for);
        body.Children.Add(run);

        // Or refuse, saying why.
        StackPanel refuse = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };

        AutomationProperties.SetAutomationId(_note, "RefuseNote");
        AutomationProperties.SetName(_note, "What to tell the agent");
        refuse.Children.Add(_note);
        refuse.Children.Add(Action("Refuse", "Refuse", () => Answered(new ApprovalAnswer(Decision.Refuse, request.Hash, _note.Text.Trim()))));
        body.Children.Add(refuse);

        Content = body;
    }

    /// <summary>What the person answered; a refusal until they answer, so closing the window refuses.</summary>
    public ApprovalAnswer Answer { get; private set; }

    private void Answered(ApprovalAnswer answer)
    {
        Answer = answer;
        DialogResult = true;
    }

    private static Border Call(AskedCall call)
    {
        StackPanel inside = new();

        StackPanel where = new() { Orientation = Orientation.Horizontal };
        TextBlock session = new() { Text = $"{call.Session}   {call.Address}", FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };

        AutomationProperties.SetAutomationId(session, "AskSession");
        where.Children.Add(session);

        foreach (string tag in call.Tags)
        {
            bool prod = string.Equals(tag, "prod", StringComparison.OrdinalIgnoreCase);
            TextBlock word = new() { Text = tag, FontSize = 11, FontWeight = FontWeights.SemiBold };
            Border chip = new()
            {
                Child = word,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(5, 0, 5, 0),
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };

            string brush = prod ? "SystemFillColorCriticalBrush" : "TextFillColorSecondaryBrush";

            word.SetResourceReference(TextBlock.ForegroundProperty, brush);
            chip.SetResourceReference(Border.BorderBrushProperty, brush);
            AutomationProperties.SetAutomationId(word, prod ? "AskProd" : "AskTag");
            AutomationProperties.SetName(word, tag);
            where.Children.Add(chip);
        }

        inside.Children.Add(where);

        TextBlock what = new()
        {
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            Margin = new Thickness(0, 6, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            Text = call.Tool == "edit_file"
                ? $"edit {call.Path}\n{call.Diff}"
                : (call.Tool == "job_start" ? "job: " : string.Empty) + string.Join(' ', call.Words.Select(word => $"‹{word}›")),
        };

        AutomationProperties.SetAutomationId(what, call.Tool == "edit_file" ? "AskDiff" : "AskCommand");
        inside.Children.Add(what);

        TextBlock rule = new() { Text = call.Rule, FontSize = 11, Opacity = 0.7, Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap };

        AutomationProperties.SetAutomationId(rule, "AskRule");
        inside.Children.Add(rule);

        Border card = new()
        {
            Child = inside,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10),
            Margin = new Thickness(0, 0, 0, 8),
        };

        card.SetResourceReference(Border.BorderBrushProperty, "ControlStrokeColorDefaultBrush");

        return card;
    }

    private static Button Action(string label, string id, Action act)
    {
        Button button = new() { Content = label, Padding = new Thickness(14, 4, 14, 4) };

        AutomationProperties.SetAutomationId(button, id);
        button.Click += (_, _) => act();

        return button;
    }

    /// <summary>
    /// Writes a grant the person gave from this dialog into the policy file: the batch's commands on the
    /// tags of the sessions they ran on, or the sessions themselves where untagged, until the time chosen.
    /// A policy created here keeps what no file meant: every session at operate, prod at observe.
    /// </summary>
    public static void WriteGrant(string policyFile, ApprovalRequest request, int minutes, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(request);

        JsonObject policy = File.Exists(policyFile)
            ? JsonNode.Parse(File.ReadAllText(policyFile), documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject
              ?? throw new InvalidDataException($"{policyFile} is not a policy")
            : new JsonObject
            {
                ["schema"] = 1,
                ["default"] = "operate",
                ["rules"] = new JsonArray(new JsonObject { ["tags"] = new JsonArray("prod"), ["level"] = "observe" }),
            };

        JsonArray grants = policy["grants"] as JsonArray ?? [];

        policy["grants"] = grants;

        foreach (IGrouping<string, AskedCall> on in request.Calls.GroupBy(call => call.Session, StringComparer.OrdinalIgnoreCase))
        {
            AskedCall first = on.First();
            JsonObject grant = first.Tags.Count > 0
                ? new JsonObject { ["tags"] = new JsonArray([.. first.Tags.Select(tag => (JsonNode?)JsonValue.Create(tag))]) }
                : new JsonObject { ["sessions"] = new JsonArray(first.Session) };

            grant["commands"] = new JsonArray([.. on.Select(call => (JsonNode?)new JsonArray([.. call.Words.Select(word => (JsonNode?)JsonValue.Create(word))]))]);
            grant["until"] = now.AddMinutes(minutes).ToString("O", CultureInfo.InvariantCulture);
            grant["why"] = $"approved in quickshell, plan {request.ShortHash}";
            grants.Add(grant);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(policyFile)!);
        File.WriteAllText(policyFile, policy.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }
}
