using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace Quickshell.App;

/// <summary>
/// The saved sessions, down the left of the window, so nobody has to know a chord to see them (QS268).
///
/// <para><b>A view, and nothing else.</b> The rows are <see cref="SessionsView"/>'s, worked out by the
/// window from the store; what a click, a key or a menu entry does is a method on
/// <see cref="MainWindow"/>, the same one a test calls. Nothing here reads a file.</para>
///
/// <para><b>Fluent's brushes and no colour of its own</b>, as every surface of this client and its two
/// sibling apps: the sidebar sits on the layer brush with a divider beside it, the selected row is the
/// list's own selection, and the Connected chip carries its word as well as its colour.</para>
///
/// <para><b>Open or a rail.</b> Folded, it is 52 pixels of buttons — the fold itself, New, Import and
/// Settings — and the terminal takes the rest. Which one it is lives in the settings file
/// (<c>sessionsSidebar</c>), so a window opens the way the last one was left.</para>
/// </summary>
public sealed class SessionsSidebar : Border
{
    /// <summary>Its width open, from the approved design.</summary>
    public const double OpenWidth = 292;

    /// <summary>Its width folded to the rail.</summary>
    public const double RailWidth = 52;

    /// <summary>Segoe's icon faces, the newer first: what Windows itself draws its glyphs with.</summary>
    private static readonly FontFamily Glyphs = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    private readonly MainWindow _window;
    private readonly ToggleButton _fold;
    private readonly TextBlock _heading;
    private readonly Button _new;
    private readonly Grid _search;
    private readonly TextBox _query;
    private readonly ListBox _list;
    private readonly TextBlock _empty;
    private readonly List<TextBlock> _labels = [];

    private bool _loading;

    /// <summary>Builds the sidebar over a window, empty until the window hands it rows.</summary>
    public SessionsSidebar(MainWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);

        _window = window;

        Width = OpenWidth;
        BorderThickness = new Thickness(0, 0, 1, 0);
        SetResourceReference(BackgroundProperty, "LayerFillColorDefaultBrush");
        SetResourceReference(BorderBrushProperty, "DividerStrokeColorDefaultBrush");

        AutomationProperties.SetAutomationId(this, "Sessions");
        AutomationProperties.SetName(this, "Sessions");

        // The fold, first and in both shapes: it is how the rail becomes the sidebar again.
        _fold = new ToggleButton
        {
            IsChecked = true,
            Width = 32,
            Height = 32,
            Padding = new Thickness(0),
            Content = Glyph(""),
            ToolTip = "Fold the sessions sidebar",
        };

        AutomationProperties.SetAutomationId(_fold, "SessionsFold");
        AutomationProperties.SetName(_fold, "Sessions sidebar");
        AutomationProperties.SetHelpText(_fold, "Open: the saved sessions beside the terminal. Off: a narrow rail.");

        _fold.Checked += (_, _) => Folded(open: true);
        _fold.Unchecked += (_, _) => Folded(open: false);

        _heading = new TextBlock
        {
            Text = "Sessions",
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
        };

        _heading.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");

        _new = Action("", "New", "SessionsNew", "Make a saved session", () => _window.NewSessionOrSay());
        _new.SetResourceReference(StyleProperty, "AccentButtonStyle");
        _new.Height = 28;

        DockPanel top = new() { Margin = new Thickness(10, 12, 10, 8), LastChildFill = false };

        DockPanel.SetDock(_fold, Dock.Left);
        DockPanel.SetDock(_heading, Dock.Left);
        DockPanel.SetDock(_new, Dock.Right);

        top.Children.Add(_fold);
        top.Children.Add(_heading);
        top.Children.Add(_new);

        _query = new TextBox
        {
            Height = 32,
            VerticalContentAlignment = VerticalAlignment.Center,
            ToolTip = "Search by name, host or tag",
        };

        AutomationProperties.SetAutomationId(_query, "SessionsSearch");
        AutomationProperties.SetName(_query, "Search sessions");
        AutomationProperties.SetHelpText(_query, "Search by name, host or tag");

        _query.TextChanged += (_, _) =>
        {
            if (!_loading)
            {
                _window.SearchSessions(_query.Text);
            }
        };

        // A hint where the box is empty, which WPF's text box has no property for. It does not take
        // the mouse, so a click lands in the box under it.
        TextBlock hint = new()
        {
            Text = "Search by name, host or tag",
            IsHitTestVisible = false,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0),
        };

        hint.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
        _query.TextChanged += (_, _) => hint.Visibility = _query.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

        _search = new Grid { Margin = new Thickness(12, 0, 12, 8) };
        _search.Children.Add(_query);
        _search.Children.Add(hint);

        _list = new ListBox
        {
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Margin = new Thickness(4, 0, 4, 0),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };

        ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
        AutomationProperties.SetAutomationId(_list, "SessionsList");
        AutomationProperties.SetName(_list, "Saved sessions");

        _list.KeyDown += Pressed;

        _empty = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(16, 8, 16, 8),
            Visibility = Visibility.Collapsed,
        };

        _empty.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        AutomationProperties.SetAutomationId(_empty, "SessionsEmpty");

        StackPanel foot = new() { Margin = new Thickness(8, 8, 8, 12) };

        foot.Children.Add(Action("", "Import sessions", "SessionsImport", "Bring sessions in from MobaXterm",
                                 () => _window.ImportOrSay(), ghost: true));
        foot.Children.Add(Action("", "Settings", "SessionsSettings", "Open the settings window",
                                 () => _window.ShowSettings(), ghost: true));

        Border footing = new() { BorderThickness = new Thickness(0, 1, 0, 0), Child = foot };

        footing.SetResourceReference(BorderBrushProperty, "DividerStrokeColorDefaultBrush");

        DockPanel body = new();

        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(_search, Dock.Top);
        DockPanel.SetDock(footing, Dock.Bottom);
        DockPanel.SetDock(_empty, Dock.Top);

        body.Children.Add(top);
        body.Children.Add(_search);
        body.Children.Add(footing);
        body.Children.Add(_empty);
        body.Children.Add(_list);

        Child = body;
    }

    /// <summary>Whether it is open, rather than folded to its rail.</summary>
    public bool IsOpen => _fold.IsChecked == true;

    /// <summary>What the search box holds.</summary>
    public string Query => _query.Text;

    /// <summary>The rows on screen, top to bottom, which is what a test reads.</summary>
    public IReadOnlyList<SessionRow> Rows => [.. _list.Items.OfType<ListBoxItem>().Select(item => (SessionRow)item.Tag)];

    /// <summary>The row selected in the list, or null.</summary>
    public SessionRow? Selected => (_list.SelectedItem as ListBoxItem)?.Tag as SessionRow;

    /// <summary>Selects the row at a path, as a click would; false where none is listed.</summary>
    public bool Select(string path)
    {
        foreach (ListBoxItem item in _list.Items.OfType<ListBoxItem>())
        {
            if (item.Tag is SessionRow row && string.Equals(row.Path, path, StringComparison.OrdinalIgnoreCase))
            {
                _list.SelectedItem = item;

                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Opens it or folds it to the rail, without writing anything: what the settings file said, applied
    /// to a window. Only a person pressing the fold writes the file.
    /// </summary>
    public void Shape(bool open)
    {
        _loading = true;

        try
        {
            _fold.IsChecked = open;
        }
        finally
        {
            _loading = false;
        }

        Width = open ? OpenWidth : RailWidth;

        // Across the top while open; one above the other on the rail, where there is room for one.
        DockPanel.SetDock(_fold, open ? Dock.Left : Dock.Top);
        DockPanel.SetDock(_new, open ? Dock.Right : Dock.Top);
        _new.Margin = open ? new Thickness(0) : new Thickness(0, 6, 0, 0);

        Visibility shown = open ? Visibility.Visible : Visibility.Collapsed;

        _heading.Visibility = shown;
        _search.Visibility = shown;
        _list.Visibility = shown;
        _empty.Visibility = open && _list.Items.Count == 0 && _empty.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        foreach (TextBlock label in _labels)
        {
            label.Visibility = shown;
        }

        _fold.ToolTip = open ? "Fold the sessions sidebar" : "Open the sessions sidebar";
    }

    /// <summary>
    /// Shows these rows, keeping the selection where the same path is still listed.
    /// </summary>
    /// <param name="rows">What <see cref="SessionsView.Rows"/> gave.</param>
    /// <param name="empty">What to say where there are none: no sessions yet, a search that found nothing, or a store that would not read.</param>
    public void Show(IReadOnlyList<SessionRow> rows, string empty)
    {
        ArgumentNullException.ThrowIfNull(rows);

        string? selected = Selected?.Path;

        _list.Items.Clear();

        foreach (SessionRow row in rows)
        {
            ListBoxItem item = Item(row);

            _list.Items.Add(item);

            if (string.Equals(row.Path, selected, StringComparison.OrdinalIgnoreCase))
            {
                _list.SelectedItem = item;
            }
        }

        _empty.Text = rows.Count == 0 ? empty : string.Empty;
        _empty.Visibility = IsOpen && rows.Count == 0 && empty.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>The fold was pressed. Written to the settings file by the window, so every window follows.</summary>
    private void Folded(bool open)
    {
        if (_loading)
        {
            return;
        }

        _window.FoldSidebar(open);
    }

    /// <summary>Enter connects or folds the row, and the arrows across fold and unfold a folder, as a tree's do.</summary>
    private void Pressed(object sender, KeyEventArgs e)
    {
        if (Selected is not { } row)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Enter:
                Activate(row);
                e.Handled = true;
                break;

            case Key.Left when row is { IsFolder: true, Expanded: true }:
            case Key.Right when row is { IsFolder: true, Expanded: false }:
                _window.ToggleFolder(row.Path);
                e.Handled = true;
                break;

            case Key.Delete when !row.IsFolder:
                _window.DeleteSessionOrSay(row.Path);
                e.Handled = true;
                break;
        }
    }

    /// <summary>What a double-click or Enter does: a session connects, a folder folds or unfolds.</summary>
    private void Activate(SessionRow row)
    {
        if (row.IsFolder)
        {
            _window.ToggleFolder(row.Path);

            return;
        }

        _window.ConnectSession(row.Path);
    }

    /// <summary>
    /// One row: a surface rather than a form, as the design has it. A folder is a chevron, its icon,
    /// its name and what it hands down; a session is its icon, its name over its address, and the
    /// Connected chip where a tab holds it.
    /// </summary>
    private ListBoxItem Item(SessionRow row)
    {
        Grid line = new() { Margin = new Thickness(row.Depth * 16, 0, 0, 0) };

        line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        if (row.IsFolder)
        {
            TextBlock chevron = Glyph(row.Expanded ? "" : "");

            chevron.FontSize = 10;
            line.Children.Add(chevron);
        }

        TextBlock icon = Glyph(row.IsFolder ? "" : "");

        Grid.SetColumn(icon, 1);
        line.Children.Add(icon);

        StackPanel words = new() { VerticalAlignment = VerticalAlignment.Center };

        words.Children.Add(new TextBlock
        {
            Text = row.Name,
            FontSize = 13,
            FontWeight = row.IsFolder ? FontWeights.SemiBold : FontWeights.Normal,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });

        if (row.Meta.Length > 0)
        {
            TextBlock meta = new()
            {
                Text = row.Meta,
                FontSize = 11,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };

            meta.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
            words.Children.Add(meta);
        }

        Grid.SetColumn(words, 2);
        line.Children.Add(words);

        if (row.Connected)
        {
            TextBlock said = new() { Text = "Connected", FontSize = 11 };

            said.SetResourceReference(TextBlock.ForegroundProperty, "SystemFillColorSuccessBrush");

            Border chip = new()
            {
                Child = said,
                Padding = new Thickness(7, 1, 7, 1),
                CornerRadius = new CornerRadius(5),
                BorderThickness = new Thickness(1),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 0, 0),
            };

            chip.SetResourceReference(BackgroundProperty, "SystemFillColorSuccessBackgroundBrush");
            chip.SetResourceReference(BorderBrushProperty, "SystemFillColorSuccessBrush");
            AutomationProperties.SetAutomationId(chip, "Connected");

            Grid.SetColumn(chip, 3);
            line.Children.Add(chip);
        }

        ListBoxItem item = new()
        {
            Content = line,
            Tag = row,
            MinHeight = row.IsFolder || row.Meta.Length == 0 ? 32 : 40,
            Padding = new Thickness(6, 2, 8, 2),
            ContextMenu = Menu(row),
        };

        // The path is the name a screen reader says and a case finds it by: unique in the store, where
        // a session's own name is only unique in its folder. Its address is the help beside it, and
        // being open in a tab is said in words in the status.
        AutomationProperties.SetName(item, row.Path);
        AutomationProperties.SetHelpText(item, row.Meta);
        AutomationProperties.SetItemStatus(item, row.Connected ? "Connected" : string.Empty);

        item.MouseDoubleClick += (_, clicked) =>
        {
            Activate(row);
            clicked.Handled = true;
        };

        return item;
    }

    /// <summary>
    /// What a right-click offers. A session connects, is edited, duplicated, moved to another folder or
    /// deleted; a folder takes a new session.
    /// </summary>
    private ContextMenu Menu(SessionRow row)
    {
        ContextMenu menu = new();

        if (row.IsFolder)
        {
            menu.Items.Add(Entry("New session here", () => _window.NewSessionOrSay(row.Path)));

            return menu;
        }

        menu.Items.Add(Entry("Connect", () => _window.ConnectSession(row.Path)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Entry("Edit…", () => _window.EditSessionOrSay(row.Path)));
        menu.Items.Add(Entry("Duplicate", () => _window.DuplicateSessionOrSay(row.Path)));

        // Filled when it opens, so the folders listed are the store's as it is then.
        MenuItem moving = new() { Header = "Move to" };

        moving.Items.Add(new MenuItem());
        moving.SubmenuOpened += (_, _) =>
        {
            moving.Items.Clear();

            foreach (string folder in _window.FoldersBesides(row.Path))
            {
                string into = folder;

                moving.Items.Add(Entry(into.Length == 0 ? "The top level" : into,
                                       () => _window.MoveSessionOrSay(row.Path, into)));
            }
        };

        menu.Items.Add(moving);
        menu.Items.Add(new Separator());
        menu.Items.Add(Entry("Delete…", () => _window.DeleteSessionOrSay(row.Path)));

        return menu;
    }

    private static MenuItem Entry(string header, Action run)
    {
        MenuItem entry = new() { Header = header };

        entry.Click += (_, _) => run();

        return entry;
    }

    /// <summary>A button with a glyph and a word, the word hidden on the rail where the glyph alone fits.</summary>
    private Button Action(string glyph, string label, string id, string help, Action run, bool ghost = false)
    {
        TextBlock word = new() { Text = label, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };

        _labels.Add(word);

        StackPanel inside = new() { Orientation = Orientation.Horizontal };

        inside.Children.Add(Glyph(glyph));
        inside.Children.Add(word);

        Button button = new()
        {
            Content = inside,
            ToolTip = help,
            Padding = new Thickness(8, 4, 10, 4),
            Margin = new Thickness(0, 1, 0, 1),
            HorizontalContentAlignment = HorizontalAlignment.Left,
        };

        if (ghost)
        {
            button.HorizontalAlignment = HorizontalAlignment.Stretch;
            button.Background = Brushes.Transparent;
            button.BorderThickness = new Thickness(0);
        }

        AutomationProperties.SetAutomationId(button, id);
        AutomationProperties.SetName(button, label);
        AutomationProperties.SetHelpText(button, help);

        button.Click += (_, _) => run();

        return button;
    }

    private static TextBlock Glyph(string glyph) => new()
    {
        Text = glyph,
        FontFamily = Glyphs,
        FontSize = 14,
        VerticalAlignment = VerticalAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Center,
    };
}
