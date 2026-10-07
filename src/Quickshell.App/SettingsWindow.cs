using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using Quickshell.Terminal;

namespace Quickshell.App;

/// <summary>
/// A window over the settings file, with the file still in charge (QS170).
///
/// <para><b>Every change is a write, and every write starts from the file.</b> There is no OK button
/// and no copy held here: a control that changes reads the file as it is now, changes its one key,
/// and writes it back through <see cref="SettingsFile.WriteTo"/>, which edits the value where it sits
/// and leaves the user's comments alone. The watch that already applies a hand edit applies this one,
/// so the window is a second way of typing into the file and never a second source of truth.</para>
///
/// <para><b>One control per documented key, said in the reference's own words.</b> What each key
/// means is a sentence out of <c>docs/SETTINGS.md</c>, and a test holds the two together; new prose
/// here would be a second description to drift from the first. The window is also not where a new
/// setting appears — a key comes first, and a control follows it.</para>
///
/// <para>A file that does not parse is not written over. It reads as the defaults, and writing those
/// back would replace somebody's half-finished edit with nothing of theirs, so the window says so
/// and leaves the file to be fixed by hand.</para>
/// </summary>
public sealed class SettingsWindow : Window
{
    /// <summary>What each key means, in <c>docs/SETTINGS.md</c>'s words, in the order it lists them.</summary>
    public static readonly IReadOnlyList<(string Key, string Label, string Means)> Keys =
    [
        ("theme", "Theme", "How the chrome is painted — the title bar, the tab strip, the dialogs."),
        ("fontFamily", "Font", "The terminal's typeface, by name. Anything installed on the machine."),
        ("fontSize", "Font size", "Its size in points. The grid follows: a bigger font in the same window is fewer columns, and the program on the far end is told."),
        ("scrollback", "Scrollback", "How many lines each session keeps once they have left the screen. Ten thousand by default."),
        ("ligatures", "Ligatures", "Whether the font's ligatures are formed — whether != is drawn as two characters or as one."),
        ("cursor", "Cursor", "Block, Underline or Bar."),
        ("cursorBlink", "Cursor blinks", "Whether the cursor blinks."),
        ("warnOnPaste", "Warn on paste", "Whether a paste carrying a newline is shown before any of it is sent."),
        ("colourScheme", "Colour scheme", "A path to a scheme file. Empty — the default — is the built-in scheme."),
    ];

    private readonly string _path;
    private readonly Dictionary<string, FrameworkElement> _controls = new(StringComparer.Ordinal);
    private readonly TextBlock _refused = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) };

    private bool _loading;

    /// <summary>Opens the window over a settings file.</summary>
    /// <param name="path">The file, which need not exist yet.</param>
    public SettingsWindow(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        _path = path;

        Title = "Settings";
        Width = 560;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        AutomationProperties.SetAutomationId(this, "Settings");

        StackPanel body = new() { Margin = new Thickness(18) };

        body.Children.Add(new TextBlock
        {
            Text = $"These are written to {path} as you change them, and applied at once. The file can "
                   + "also be edited by hand; it is the setting, and this is a way of typing into it.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 14),
        });

        body.Children.Add(_refused);

        foreach ((string key, string label, string means) in Keys)
        {
            FrameworkElement control = Control(key);

            AutomationProperties.SetAutomationId(control, key);
            AutomationProperties.SetName(control, label);
            AutomationProperties.SetHelpText(control, means);

            _controls[key] = control;

            body.Children.Add(new TextBlock { Text = label, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 6, 0, 2) });
            body.Children.Add(control);
            body.Children.Add(new TextBlock { Text = means, TextWrapping = TextWrapping.Wrap, Opacity = 0.75, Margin = new Thickness(0, 2, 0, 6) });
        }

        Content = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 720 };

        Reload();
    }

    /// <summary>The control for one key, for a caller that reads or changes it.</summary>
    public FrameworkElement ControlFor(string key) => _controls[key];

    /// <summary>
    /// Shows the file as it is now — what a person reopening the window, or a hand edit landing
    /// while it is open, should see.
    /// </summary>
    public void Reload()
    {
        bool readable = SettingsFile.Readable(_path);

        _refused.Text = readable
            ? string.Empty
            : "The file does not parse, so nothing here will be written over it. Fix it by hand — "
              + "the last thing written to it was probably mid-edit — and reopen this window.";
        _refused.Visibility = readable ? Visibility.Collapsed : Visibility.Visible;

        Settings now = SettingsFile.ReadFrom(_path);

        _loading = true;

        try
        {
            ((ComboBox)_controls["theme"]).SelectedItem = now.Theme;
            ((TextBox)_controls["fontFamily"]).Text = now.FontFamily;
            ((TextBox)_controls["fontSize"]).Text = now.FontSize.ToString(CultureInfo.InvariantCulture);
            ((TextBox)_controls["scrollback"]).Text = now.Scrollback.ToString(CultureInfo.InvariantCulture);
            ((CheckBox)_controls["ligatures"]).IsChecked = now.Ligatures;
            ((ComboBox)_controls["cursor"]).SelectedItem = now.Cursor;
            ((CheckBox)_controls["cursorBlink"]).IsChecked = now.CursorBlink;
            ((CheckBox)_controls["warnOnPaste"]).IsChecked = now.WarnOnPaste;
            ((TextBox)_controls["colourScheme"]).Text = now.Scheme;
        }
        finally
        {
            _loading = false;
        }

        foreach (FrameworkElement control in _controls.Values)
        {
            control.IsEnabled = readable;
        }
    }

    /// <summary>
    /// Reads the file, changes one key, and writes it back — the whole of what a control does.
    /// </summary>
    /// <returns>Whether anything was written: not while loading, and not over a file that does not parse.</returns>
    public bool Change(Func<Settings, Settings> edit)
    {
        ArgumentNullException.ThrowIfNull(edit);

        if (_loading || !SettingsFile.Readable(_path))
        {
            return false;
        }

        SettingsFile.WriteTo(_path, edit(SettingsFile.ReadFrom(_path)));

        return true;
    }

    private FrameworkElement Control(string key) => key switch
    {
        "theme" => Choice(Enum.GetValues<ChromeTheme>().Cast<object>(),
                          value => Change(now => now with { Theme = (ChromeTheme)value })),
        "cursor" => Choice([CursorShape.Block, CursorShape.Underline, CursorShape.Bar],
                           value => Change(now => now with { Cursor = (CursorShape)value })),
        "ligatures" => Toggle(on => Change(now => now with { Ligatures = on })),
        "cursorBlink" => Toggle(on => Change(now => now with { CursorBlink = on })),
        "warnOnPaste" => Toggle(on => Change(now => now with { WarnOnPaste = on })),
        "fontFamily" => Field(text => text.Trim().Length > 0 && Change(now => now with { FontFamily = text.Trim() })),
        "fontSize" => Field(text => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double size)
                                    && size > 0 && Change(now => now with { FontSize = size })),
        "scrollback" => Field(text => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int lines)
                                      && lines >= 0 && Change(now => now with { Scrollback = lines })),
        "colourScheme" => Field(text => Change(now => now with { Scheme = text.Trim() })),
        _ => throw new ArgumentOutOfRangeException(nameof(key), key, "no control for this key"),
    };

    private static ComboBox Choice(IEnumerable<object> values, Action<object> chosen)
    {
        ComboBox box = new() { ItemsSource = values.ToArray() };

        box.SelectionChanged += (_, _) =>
        {
            if (box.SelectedItem is { } value)
            {
                chosen(value);
            }
        };

        return box;
    }

    private static CheckBox Toggle(Action<bool> toggled)
    {
        CheckBox box = new();

        box.Checked += (_, _) => toggled(true);
        box.Unchecked += (_, _) => toggled(false);

        return box;
    }

    /// <summary>
    /// A text field, written when it is left or Enter is pressed rather than on every keystroke: a
    /// font name typed a letter at a time is eleven fonts that do not exist on the way to one that
    /// does. A value that does not make sense is not written, and the field keeps it for fixing.
    /// </summary>
    private static TextBox Field(Func<string, bool> committed)
    {
        TextBox box = new();

        box.LostKeyboardFocus += (_, _) => committed(box.Text);
        box.KeyDown += (_, pressed) =>
        {
            if (pressed.Key == System.Windows.Input.Key.Enter)
            {
                committed(box.Text);
            }
        };

        return box;
    }
}
