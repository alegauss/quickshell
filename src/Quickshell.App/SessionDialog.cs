using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;

namespace Quickshell.App;

/// <summary>
/// Where a session is made, and the highest-traffic settings surface this client has.
///
/// <para><b>What is on screen before a group is opened is the whole argument.</b> A host and its
/// port, a name and a folder, who it signs in as and how — because everything else can be inherited
/// from the folder or defaulted, and asking for it anyway would be asking the user to retype what the
/// store already knows. The rest is folded into named groups, each saying in one line what it holds,
/// and all of them start shut (QS270, the friendly-shell design approved on 2026-10-11).</para>
///
/// <para><b>How it signs in is a choice, not a consequence.</b> Key or agent, password, or key
/// file, as three segments: until QS270 it was decided by which of two text boxes among eight held
/// something, and a mistake showed only on connect. The choice writes the same Key and Credential
/// fields it always did.</para>
///
/// <para><b>The window arranges; <see cref="SessionEditor"/> decides.</b> Which field is inherited,
/// which folder said so, what is refused and why — none of that is here. This builds controls over
/// the editor's fields and shows what the editor says about them, so the rules can be tested against
/// the rules rather than against a visual tree.</para>
/// </summary>
public sealed class SessionDialog : Window
{
    private static readonly FontFamily Mono = new("Cascadia Mono, Consolas");

    private SessionEditor _editor;
    private readonly TextBox _host;
    private readonly TextBox _name;
    private readonly ComboBox _folder;
    private readonly TextBox _postLogin;
    private readonly TextBlock _warns;
    private readonly TextBlock _signInSays;
    private readonly FrameworkElement _keyRow;
    private readonly FrameworkElement _credentialRow;
    private readonly Dictionary<SignInMethod, RadioButton> _ways = [];
    private readonly Dictionary<SessionGroupName, TextBlock> _summaries = [];
    private readonly TextBlock _complaints;
    private readonly TextBlock _tested;
    private readonly Button _test;
    private readonly Button _save;
    private readonly List<Row> _rows = [];

    private bool _reading;

    /// <summary>A dialog over one editor.</summary>
    public SessionDialog(SessionEditor editor)
    {
        ArgumentNullException.ThrowIfNull(editor);

        _editor = editor;

        Title = editor.IsNew ? "New session" : $"Session: {editor.Path}";
        SizeToContent = SizeToContent.Height;
        Width = 620;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        AutomationProperties.SetAutomationId(this, "SessionDialog");

        StackPanel body = new() { Margin = new Thickness(24, 16, 24, 0) };

        TextBlock lead = new()
        {
            Text = "Only the host is needed. Everything else has a sensible default.",
            FontSize = 13,
            Margin = new Thickness(0, 0, 0, 8),
            TextWrapping = TextWrapping.Wrap,
        };

        lead.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        body.Children.Add(lead);

        // Host and its port, side by side: the two things that say where it goes.
        Grid where = Columns(3, 1);

        _host = Asked("Host", editor.Host, where, column: 0);
        _host.FontFamily = Mono;
        Inheritable(nameof(SessionSettings.Port), where, column: 1);
        body.Children.Add(where);

        // Its name and the folder it goes in, whose values it inherits.
        Grid called = Columns(1, 1);

        _name = Asked("Name", editor.Name, called, column: 0);

        _folder = new ComboBox { Name = "Folder", IsEnabled = editor.IsNew, Height = 32 };

        foreach (string folder in editor.Folders)
        {
            _folder.Items.Add(new ComboBoxItem { Content = folder.Length == 0 ? "No folder" : folder, Tag = folder });
        }

        _folder.SelectedItem = _folder.Items.OfType<ComboBoxItem>()
                                      .FirstOrDefault(item => string.Equals((string)item.Tag, editor.Folder, StringComparison.OrdinalIgnoreCase));
        _folder.ToolTip = editor.IsNew ? "Where it goes, and what it inherits" : "Moved from the sessions sidebar";
        AutomationProperties.SetName(_folder, "Folder");
        _folder.SelectionChanged += (_, _) => Refile();

        Labelled("Folder", _folder, called, column: 1);
        body.Children.Add(called);

        Grid who = Columns(1);

        Inheritable(nameof(SessionSettings.User), who, column: 0);
        body.Children.Add(who);

        // How it signs in: three segments over Key and Credential, and the field the choice needs.
        body.Children.Add(Label("Sign in with"));

        UniformGrid ways = new() { Rows = 1, Columns = 3 };

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
                GroupName = "SignIn",
                Name = $"SignIn{way}",
                Height = 34,
                Margin = new Thickness(1),
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                IsChecked = editor.SignIn == way,
            };

            // A segment and not a dot: a toggle button's look over a radio button's behaviour.
            segment.SetResourceReference(StyleProperty, typeof(ToggleButton));
            AutomationProperties.SetName(segment, said);

            segment.Checked += (_, _) =>
            {
                if (!_reading)
                {
                    _editor.SignIn = way;
                    Say();
                }
            };

            _ways[way] = segment;
            ways.Children.Add(segment);
        }

        body.Children.Add(ways);

        _signInSays = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0), Name = "SignInSays" };
        _signInSays.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        body.Children.Add(_signInSays);

        Grid key = Columns(1);

        Inheritable(nameof(SessionSettings.Key), key, column: 0);
        _keyRow = key;
        body.Children.Add(key);

        Grid credential = Columns(1);

        Inheritable(nameof(SessionSettings.Credential), credential, column: 0);
        _credentialRow = credential;
        body.Children.Add(credential);

        // The rest, folded, each group saying what it holds while it is shut.
        StackPanel groups = new() { Margin = new Thickness(0, 16, 0, 0) };

        Grid jump = Columns(1);

        Inheritable(nameof(SessionSettings.JumpHost), jump, column: 0);
        groups.Children.Add(Group(SessionGroupName.JumpHost, "Jump host", jump));

        TextBlock tunnels = new()
        {
            Text = editor.Forwards.Count == 0
                ? "This session starts no forwards."
                : string.Join(Environment.NewLine, editor.Forwards.Select(forward => forward.ToString())),
            FontFamily = editor.Forwards.Count == 0 ? FontFamily : Mono,
            TextWrapping = TextWrapping.Wrap,
        };

        TextBlock tunnelsHow = new()
        {
            Text = "Forwards are kept in the sessions file, under Forwards (docs\\SESSIONS.md). Show forwards lists them while they run.",
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0),
        };

        tunnelsHow.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");

        StackPanel forwards = new();

        forwards.Children.Add(tunnels);
        forwards.Children.Add(tunnelsHow);
        groups.Children.Add(Group(SessionGroupName.Tunnels, "Tunnels", forwards));

        StackPanel after = new();

        _postLogin = Asked("After login", editor.PostLogin ?? string.Empty, after);
        _postLogin.FontFamily = Mono;

        _warns = new TextBlock
        {
            Text = editor.PostLoginWarning,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Margin = new Thickness(0, 4, 0, 0),
            Name = "PostLoginWarning",
        };

        _warns.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        after.Children.Add(_warns);
        groups.Children.Add(Group(SessionGroupName.AfterLogin, "After login", after));

        StackPanel terminal = new();

        foreach (string name in (string[])[nameof(SessionSettings.Scheme), nameof(SessionSettings.TerminalType),
                                           nameof(SessionSettings.Scrollback), nameof(SessionSettings.Reconnect)])
        {
            Grid row = Columns(1);

            Inheritable(name, row, column: 0);
            terminal.Children.Add(row);
        }

        groups.Children.Add(Group(SessionGroupName.Terminal, "Terminal", terminal));

        body.Children.Add(groups);

        _complaints = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 12, 0, 0),
            Name = "Complaints",
        };

        _complaints.SetResourceReference(TextBlock.ForegroundProperty, "SystemFillColorCriticalBrush");
        body.Children.Add(_complaints);

        // The footer: test it, or leave, or keep it.
        _test = new Button { Content = "Test connection", Name = "Test", Height = 34, Padding = new Thickness(12, 0, 12, 0) };
        _test.ToolTip = "Connects, signs in, says what it reached, and closes again";
        _test.Click += async (_, _) => await TestAsync();

        _tested = new TextBlock
        {
            Name = "Tested",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 10, 0),
        };

        Button cancel = new() { Content = "Cancel", Name = "Cancel", IsCancel = true, Height = 34, Padding = new Thickness(16, 0, 16, 0) };

        _save = new Button
        {
            Content = editor.IsNew ? "Save and connect" : "Save",
            IsDefault = true,
            Height = 34,
            Padding = new Thickness(18, 0, 18, 0),
            Margin = new Thickness(10, 0, 0, 0),
            Name = "Save",
        };

        _save.SetResourceReference(StyleProperty, "AccentButtonStyle");
        _save.Click += (_, _) => Commit();

        DockPanel footer = new() { Margin = new Thickness(24, 16, 24, 16) };

        DockPanel.SetDock(_test, Dock.Left);
        DockPanel.SetDock(_save, Dock.Right);
        DockPanel.SetDock(cancel, Dock.Right);

        footer.Children.Add(_test);
        footer.Children.Add(_save);
        footer.Children.Add(cancel);
        footer.Children.Add(_tested);

        Border foot = new() { BorderThickness = new Thickness(0, 1, 0, 0), Margin = new Thickness(0, 20, 0, 0), Child = footer };

        foot.SetResourceReference(BorderBrushProperty, "DividerStrokeColorDefaultBrush");

        StackPanel whole = new();

        whole.Children.Add(body);
        whole.Children.Add(foot);

        Content = whole;

        _host.TextChanged += (_, _) => Read();
        _name.TextChanged += (_, _) => Read();
        _postLogin.TextChanged += (_, _) => Read();

        Read();
    }

    /// <summary>The tree as saved, or null where the dialog was closed without saving.</summary>
    public SessionTree? Saved { get; private set; }

    /// <summary>The editor the dialog ends on, which a change of folder replaces (QS270).</summary>
    public SessionEditor Editor => _editor;

    /// <summary>
    /// Connects a session, signs in, and answers what it reached or why it did not, then closes it
    /// again (QS270). Null hides Test connection, as where nothing could connect.
    /// </summary>
    public Func<ResolvedSession, CancellationToken, Task<string>>? Tests
    {
        get;

        init
        {
            field = value;
            _test.Visibility = value is null ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    /// <summary>What the last test said, which is what a test of the dialog reads.</summary>
    public string TestSaid => _tested.Text;

    /// <summary>
    /// Runs Test connection on the session as it stands on screen, unsaved.
    /// </summary>
    public async Task TestAsync()
    {
        Read();

        if (Tests is not { } test || !_editor.CanSave)
        {
            return;
        }

        // The session as it would be saved, resolved in a copy of the store: nothing is written.
        SessionTree preview = _editor.Save();

        if (preview.Session(_editor.SavedAt) is not { } session)
        {
            return;
        }

        _test.IsEnabled = false;
        _tested.Text = $"Connecting to {session.Host}…";

        try
        {
            _tested.Text = await test(session, CancellationToken.None);
        }
        finally
        {
            _test.IsEnabled = true;
        }
    }

    /// <summary>
    /// Pulls what is on screen back into the editor and shows what it says about it.
    ///
    /// <para>A field is overridden only where its box holds something. Clearing a box gives the
    /// field back to whatever it inherits, which is how a user undoes an override without needing to
    /// know the word.</para>
    /// </summary>
    private void Read()
    {
        if (_reading)
        {
            return;
        }

        _editor.Host = _host.Text;
        _editor.Name = _name.Text;
        _editor.PostLogin = _postLogin.Text.Length == 0 ? null : _postLogin.Text;

        foreach (Row row in _rows)
        {
            EditableField field = _editor.Field(row.Name);

            if (row.Box.Text.Length == 0)
            {
                field.Inherit();
            }
            else if (row.Box.Text != field.Own)
            {
                field.Override(row.Box.Text);
            }
        }

        Say();
    }

    /// <summary>Says what the editor says now, without reading anything back from the screen.</summary>
    private void Say()
    {
        _reading = true;

        try
        {
            foreach (Row row in _rows)
            {
                EditableField field = _editor.Field(row.Name);

                if (row.Box.Text != (field.IsOverridden ? field.Own : string.Empty))
                {
                    row.Box.Text = field.IsOverridden ? field.Own ?? string.Empty : string.Empty;
                }

                row.Says.Text = field.Explains;
                row.Hint.Text = field.IsInherited ? field.Inherited!.Value.Value : string.Empty;
                row.Hint.Visibility = row.Box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

                // The way out of whichever state it is in, beside what it says.
                row.Switch.Inlines.Clear();

                if (field.IsInherited)
                {
                    row.Switch.Inlines.Add(Link("set here instead", () =>
                    {
                        field.Override(field.Inherited!.Value.Value);
                        Say();
                        row.Box.Focus();
                        row.Box.SelectAll();
                    }));
                }
                else if (field.IsOverridden && field.Inherited is { } above)
                {
                    row.Switch.Inlines.Add(Link($"use {above.From}'s instead", () =>
                    {
                        field.Inherit();
                        Say();
                    }));
                }
            }

            foreach ((SignInMethod way, RadioButton segment) in _ways)
            {
                segment.IsChecked = _editor.SignIn == way;
            }

            _signInSays.Text = _editor.SignInSays;
            _keyRow.Visibility = _editor.SignIn == SignInMethod.KeyFile ? Visibility.Visible : Visibility.Collapsed;
            _credentialRow.Visibility = _editor.SignIn == SignInMethod.Password ? Visibility.Visible : Visibility.Collapsed;

            foreach ((SessionGroupName group, TextBlock summary) in _summaries)
            {
                summary.Text = _editor.Summary(group);
            }

            _warns.Text = _editor.PostLoginWarning;
            _complaints.Text = string.Join(Environment.NewLine, _editor.Complaints);
            _save.IsEnabled = _editor.CanSave;
        }
        finally
        {
            _reading = false;
        }
    }

    /// <summary>A new session moved to another folder before it was saved, inheriting from there.</summary>
    private void Refile()
    {
        if (_reading || !_editor.IsNew || _folder.SelectedItem is not ComboBoxItem { Tag: string folder }
            || string.Equals(folder, _editor.Folder, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        Read();

        _editor = _editor.InFolder(folder);

        Say();
    }

    private void Commit()
    {
        Read();

        if (!_editor.CanSave)
        {
            return;
        }

        Saved = _editor.Save();

        // Only a dialog shown modally has a result to set; one a test fills in without showing it
        // has been saved all the same.
        if (IsVisible)
        {
            DialogResult = true;
        }
    }

    /// <summary>A field the dialog asks for outright, with no inheritance behind it.</summary>
    private static TextBox Asked(string label, string value, Panel into, int column = 0)
    {
        TextBox box = new() { Text = value, Name = label.Replace(" ", string.Empty, StringComparison.Ordinal), Height = 32 };

        AutomationProperties.SetName(box, label);
        Labelled(label, box, into, column);

        return box;
    }

    /// <summary>A label over a control, in one column of a row.</summary>
    private static void Labelled(string label, FrameworkElement control, Panel into, int column)
    {
        StackPanel cell = new() { Margin = new Thickness(column == 0 ? 0 : 12, 0, 0, 0) };

        cell.Children.Add(Label(label));
        cell.Children.Add(control);

        Grid.SetColumn(cell, column);
        into.Children.Add(cell);
    }

    private static TextBlock Label(string text)
    {
        TextBlock label = new() { Text = text, FontSize = 12, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 6) };

        label.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");

        return label;
    }

    /// <summary>
    /// A field with a value behind it, shown empty where it is inherited, with the inherited value
    /// written faintly inside the box and where it came from beside the label.
    ///
    /// <para>Empty and not pre-filled: a box carrying the inherited value would become an override
    /// the moment anybody pressed save, and a folder's one default would quietly become a copy on
    /// every session under it. <b>Set here instead</b> is the act that takes it over.</para>
    /// </summary>
    private void Inheritable(string name, Panel into, int column)
    {
        EditableField field = _editor.Field(name);

        TextBox box = new() { Text = field.IsOverridden ? field.Own : string.Empty, Name = field.Name, Height = 32 };

        AutomationProperties.SetName(box, field.Label);

        TextBlock hint = new()
        {
            IsHitTestVisible = false,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0),
        };

        hint.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");

        Grid boxed = new();

        boxed.Children.Add(box);
        boxed.Children.Add(hint);

        TextBlock says = new() { FontSize = 11, Name = $"{field.Name}Explains" };

        says.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");

        TextBlock switching = new() { FontSize = 11, Margin = new Thickness(6, 0, 0, 0) };

        StackPanel side = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 6) };

        side.Children.Add(says);
        side.Children.Add(switching);

        DockPanel head = new();
        TextBlock label = Label(field.Label);

        DockPanel.SetDock(side, Dock.Right);
        head.Children.Add(side);
        head.Children.Add(label);

        StackPanel cell = new() { Margin = new Thickness(column == 0 ? 0 : 12, 0, 0, 0) };

        cell.Children.Add(head);
        cell.Children.Add(boxed);

        Grid.SetColumn(cell, column);
        into.Children.Add(cell);

        box.TextChanged += (_, _) => Read();

        _rows.Add(new Row(field.Name, box, says, hint, switching));
    }

    /// <summary>A group of the less-asked fields, shut, its header saying what it holds.</summary>
    private Expander Group(SessionGroupName group, string title, UIElement inside)
    {
        TextBlock summary = new() { FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, Name = $"{group}Summary" };

        summary.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        _summaries[group] = summary;

        DockPanel header = new() { LastChildFill = true };
        TextBlock named = new() { Text = title, FontSize = 13, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 12, 0) };

        DockPanel.SetDock(named, Dock.Left);
        header.Children.Add(named);
        header.Children.Add(summary);

        Expander expander = new()
        {
            Header = header,
            Content = inside,
            IsExpanded = false,
            Name = $"{group}Group",
            Margin = new Thickness(0, 0, 0, 2),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };

        AutomationProperties.SetName(expander, title);

        return expander;
    }

    private static Hyperlink Link(string text, Action run)
    {
        Hyperlink link = new(new Run(text));

        link.Click += (_, _) => run();

        return link;
    }

    private static Grid Columns(params int[] stars)
    {
        Grid grid = new();

        foreach (int star in stars)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(star, GridUnitType.Star) });
        }

        return grid;
    }

    /// <summary>One inheritable field on screen.</summary>
    private sealed record Row(string Name, TextBox Box, TextBlock Says, TextBlock Hint, TextBlock Switch);
}
