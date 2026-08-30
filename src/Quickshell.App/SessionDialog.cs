using System.Windows;
using System.Windows.Controls;

namespace Quickshell.App;

/// <summary>
/// Where a session is made, and the highest-traffic settings surface this client has.
///
/// <para><b>What is on screen before the disclosure is opened is the whole argument.</b> A host, a
/// name, and the button — because everything else can be inherited from the folder or defaulted, and
/// asking for it anyway would be asking the user to retype what the store already knows. The
/// disclosure holds the rest and starts shut.</para>
///
/// <para><b>The window arranges; <see cref="SessionEditor"/> decides.</b> Which field is inherited,
/// which folder said so, what is refused and why — none of that is here. This builds controls over
/// the editor's fields and shows what the editor says about them, so the rules can be tested against
/// the rules rather than against a visual tree.</para>
/// </summary>
public sealed class SessionDialog : Window
{
    private readonly SessionEditor _editor;
    private readonly TextBox _host;
    private readonly TextBox _name;
    private readonly TextBox _postLogin;
    private readonly TextBlock _complaints;
    private readonly Button _save;
    private readonly List<(EditableField Field, TextBox Box, TextBlock Says)> _rows = [];

    /// <summary>A dialog over one editor.</summary>
    public SessionDialog(SessionEditor editor)
    {
        ArgumentNullException.ThrowIfNull(editor);

        _editor = editor;

        Title = editor.IsNew ? "New session" : $"Session: {editor.Path}";
        SizeToContent = SizeToContent.Height;
        Width = 460;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        StackPanel body = new() { Margin = new Thickness(16) };

        _host = Asked("Host", editor.Host, body);
        _name = Asked("Name", editor.Name, body);

        // Shut. A dialog that opened with everything showing would be the twelve-field dialog with
        // an extra click in it.
        Expander more = new()
        {
            Header = "Everything else",
            IsExpanded = false,
            Margin = new Thickness(0, 12, 0, 0),
            Name = "More",
        };

        StackPanel rest = new();

        foreach (EditableField field in editor.Fields)
        {
            _rows.Add(Row(field, rest));
        }

        _postLogin = Asked("After login", editor.PostLogin ?? string.Empty, rest);

        TextBlock warns = new()
        {
            Text = editor.PostLoginWarning,
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.8,
            Margin = new Thickness(0, 2, 0, 0),
            Name = "PostLoginWarning",
        };

        rest.Children.Add(warns);

        more.Content = rest;
        body.Children.Add(more);

        _complaints = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 12, 0, 0),
            Name = "Complaints",
        };

        body.Children.Add(_complaints);

        _save = new Button
        {
            Content = "Save",
            IsDefault = true,
            Padding = new Thickness(16, 4, 16, 4),
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0),
            Name = "Save",
        };

        _save.Click += (_, _) => Commit();

        body.Children.Add(_save);

        Content = body;

        _host.TextChanged += (_, _) => Read();
        _name.TextChanged += (_, _) => Read();
        _postLogin.TextChanged += (_, _) => Read();

        Read();
    }

    /// <summary>The tree as saved, or null where the dialog was closed without saving.</summary>
    public SessionTree? Saved { get; private set; }

    /// <summary>
    /// Pulls what is on screen back into the editor and shows what it says about it.
    ///
    /// <para>A field is overridden only where its box holds something. Clearing a box gives the
    /// field back to whatever it inherits, which is how a user undoes an override without needing to
    /// know the word.</para>
    /// </summary>
    private void Read()
    {
        _editor.Host = _host.Text;
        _editor.Name = _name.Text;
        _editor.PostLogin = _postLogin.Text.Length == 0 ? null : _postLogin.Text;

        foreach ((EditableField field, TextBox box, TextBlock says) in _rows)
        {
            if (box.Text.Length == 0)
            {
                field.Inherit();
            }
            else if (box.Text != field.Own)
            {
                field.Override(box.Text);
            }

            says.Text = field.Explains;
        }

        _complaints.Text = string.Join(Environment.NewLine, _editor.Complaints);
        _save.IsEnabled = _editor.CanSave;
    }

    private void Commit()
    {
        Read();

        if (!_editor.CanSave)
        {
            return;
        }

        Saved = _editor.Save();
        DialogResult = true;
    }

    /// <summary>A field the dialog asks for outright, with no inheritance behind it.</summary>
    private static TextBox Asked(string label, string value, Panel into)
    {
        into.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 8, 0, 2) });

        TextBox box = new() { Text = value, Name = label.Replace(" ", string.Empty, StringComparison.Ordinal) };

        into.Children.Add(box);

        return box;
    }

    /// <summary>
    /// A field with a value behind it, shown empty where it is inherited.
    ///
    /// <para>Empty and not pre-filled: a box carrying the inherited value would become an override
    /// the moment anybody pressed save, and a folder's one default would quietly become a copy on
    /// every session under it. What the field inherits is said beside it instead.</para>
    /// </summary>
    private static (EditableField, TextBox, TextBlock) Row(EditableField field, Panel into)
    {
        into.Children.Add(new TextBlock { Text = field.Label, Margin = new Thickness(0, 8, 0, 2) });

        TextBox box = new() { Text = field.IsOverridden ? field.Own : string.Empty, Name = field.Name };

        into.Children.Add(box);

        TextBlock says = new()
        {
            Text = field.Explains,
            Opacity = 0.7,
            Margin = new Thickness(0, 2, 0, 0),
            Name = $"{field.Name}Explains",
        };

        into.Children.Add(says);

        return (field, box, says);
    }
}
