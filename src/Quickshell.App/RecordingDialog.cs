using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace Quickshell.App;

/// <summary>
/// The one question before a recording starts: what to call it (QS134).
///
/// <para><b>Said before it starts, all of it.</b> What is kept and what never is, the bound the file
/// stops at, and the folder it goes in, because a recording is a client writing somebody's session
/// to disk and they should learn the terms from this and not from the file. Starting is a decision
/// made once, in the open, which is why it is a dialog and not a chord; stopping is safe and needs
/// no question.</para>
///
/// <para><b>Named for what was run</b>, as the corpus names its streams, so a recording that shows a
/// defect becomes a regression test by moving one file. The default is the session and the time,
/// which is a name that still means something next week.</para>
/// </summary>
public sealed class RecordingDialog : Window
{
    private readonly TextBox _name = new() { MinWidth = 360, Padding = new Thickness(4) };

    /// <summary>Builds the dialog.</summary>
    /// <param name="suggested">The name offered, already selected so typing replaces it.</param>
    /// <param name="folder">Where the file goes.</param>
    /// <param name="limit">The bound on the compressed file.</param>
    public RecordingDialog(string suggested, string folder, long limit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(suggested);
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);

        Title = "Record a session";
        AutomationProperties.SetAutomationId(this, "Recording");

        SizeToContent = SizeToContent.WidthAndHeight;
        MaxWidth = 560;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        StackPanel body = new() { Margin = new Thickness(18) };

        TextBlock terms = new()
        {
            Text = Terms(folder, limit),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10),
        };

        AutomationProperties.SetAutomationId(terms, "Terms");
        AutomationProperties.SetName(terms, terms.Text);
        body.Children.Add(terms);

        _name.Text = suggested;
        AutomationProperties.SetName(_name, "Name");
        AutomationProperties.SetAutomationId(_name, "Name");
        body.Children.Add(_name);

        StackPanel row = new()
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0),
        };

        Button record = new() { Content = "Record", Padding = new Thickness(14, 4, 14, 4), IsDefault = true };
        Button decline = new()
        {
            Content = "Don't record",
            Padding = new Thickness(14, 4, 14, 4),
            Margin = new Thickness(8, 0, 0, 0),
            IsCancel = true,
        };

        AutomationProperties.SetAutomationId(record, "record");
        AutomationProperties.SetAutomationId(decline, "decline");

        record.Click += (_, _) =>
        {
            if (_name.Text.Trim() is { Length: > 0 } named)
            {
                Named = named;
                Close();
            }
        };

        decline.Click += (_, _) => Close();

        row.Children.Add(record);
        row.Children.Add(decline);
        body.Children.Add(row);

        Content = body;

        Loaded += (_, _) =>
        {
            _name.Focus();
            _name.SelectAll();
        };
    }

    /// <summary>The name chosen, or null where the person declined or closed the window.</summary>
    public string? Named { get; private set; }

    /// <summary>What a recording keeps, where, and up to what size, in one paragraph.</summary>
    public static string Terms(string folder, long limit)
    {
        string size = (limit / (1024 * 1024.0)).ToString("0.###", CultureInfo.InvariantCulture);

        return "A new session is opened and everything its host sends is kept, exactly as it arrived; "
               + $"nothing you type is. The file goes in {folder} and stops at {size} MB compressed. "
               + "Stop recording, in the palette, closes it and says where it is.";
    }
}
