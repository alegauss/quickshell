using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
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
/// accent at a tenth, and the Connected chip carries its word as well as its colour.</para>
///
/// <para><b>Open or a rail.</b> Folded, it is 52 pixels of buttons — the fold itself, the files of the
/// server on screen and its tunnels — and the terminal takes the rest. Which one it is lives in the
/// settings file (<c>sessionsSidebar</c>), so a window opens the way the last one was left.</para>
///
/// <para><b>Sizes are the design's</b> (<c>docs/design/friendly-shell/Main.dc.html</c> open,
/// <c>Connected.dc.html</c> folded), in device-independent pixels, and QS277's tests read them back off
/// the elements. The buttons other than New are ghosts: no fill until hovered, and the fold shows
/// whether it is on by the shape of the sidebar, not by an accent block.</para>
/// </summary>
public sealed class SessionsSidebar : Border
{
    /// <summary>Its width open, from the approved design.</summary>
    public const double OpenWidth = 292;

    /// <summary>Its width folded to the rail.</summary>
    public const double RailWidth = 52;

    /// <summary>A row's height, folder or session.</summary>
    public const double RowHeight = 32;

    /// <summary>How far in a top-level row starts.</summary>
    public const double Indent = 8;

    /// <summary>How much further in each level of folder goes, which puts a session in a folder at 30.</summary>
    public const double Step = 22;

    /// <summary>Segoe's icon faces, the newer first: what Windows itself draws its glyphs with.</summary>
    private static readonly FontFamily Glyphs = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    /// <summary>The key a selected row's fill is found by, set on this sidebar from the theme's accent.</summary>
    private const string SelectionKey = "SessionsSelectionBrush";

    /// <summary>The theme's quieter text, which every glyph and second line here is drawn in.</summary>
    private const string Secondary = "TextFillColorSecondaryBrush";

    // The templates are this sidebar's own and not shared: a template is sealed on the thread that first
    // uses it, and a second window on another thread would then be refused it.
    private readonly ControlTemplate _ghost = Templates.Ghost();
    private readonly ControlTemplate _row = Templates.Row(SelectionKey);
    private readonly ControlTemplate _bare = Templates.Bare();

    private readonly MainWindow _window;
    private readonly ToggleButton _fold;
    private readonly TextBlock _heading;
    private readonly Button _new;
    private readonly DockPanel _top;
    private readonly Border _search;
    private readonly TextBox _query;
    private readonly ListBox _list;
    private readonly TextBlock _empty;
    private readonly Border _footing;
    private readonly List<Button> _railOnly = [];

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

        // The theme is the window's, so the accent is read again once the sidebar is in it.
        Resources[SelectionKey] = Selection();
        Loaded += (_, _) => Resources[SelectionKey] = Selection();

        // The fold, first and in both shapes: it is how the rail becomes the sidebar again. Docked to
        // the left as a panel is, which is what it does to the sidebar.
        _fold = new ToggleButton
        {
            IsChecked = true,
            Width = 28,
            Height = 28,
            Padding = new Thickness(0),
            Template = _ghost,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Content = Glyph("\uE90C", 16),
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
            Margin = new Thickness(6, 0, 0, 0),
        };

        _heading.SetResourceReference(TextBlock.ForegroundProperty, Secondary);

        _new = NewButton();

        _top = new DockPanel { LastChildFill = false };

        DockPanel.SetDock(_fold, Dock.Left);
        DockPanel.SetDock(_heading, Dock.Left);
        DockPanel.SetDock(_new, Dock.Right);

        _top.Children.Add(_fold);
        _top.Children.Add(_heading);
        _top.Children.Add(_new);

        _query = new TextBox
        {
            Template = _bare,
            FontSize = 13,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            VerticalContentAlignment = VerticalAlignment.Center,
            ToolTip = "Search by name, host or tag",
        };

        _query.SetResourceReference(Control.ForegroundProperty, "TextFillColorPrimaryBrush");
        _query.SetResourceReference(TextBoxBase.CaretBrushProperty, "TextFillColorPrimaryBrush");

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
            FontSize = 13,
            IsHitTestVisible = false,
            VerticalAlignment = VerticalAlignment.Center,
        };

        hint.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
        _query.TextChanged += (_, _) => hint.Visibility = _query.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

        TextBlock magnifier = Glyph("\uE721", 14);

        magnifier.Margin = new Thickness(0, 0, 8, 0);
        magnifier.SetResourceReference(TextBlock.ForegroundProperty, Secondary);

        Grid field = new();

        field.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        field.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(_query, 1);
        Grid.SetColumn(hint, 1);
        field.Children.Add(magnifier);
        field.Children.Add(_query);
        field.Children.Add(hint);

        // The frame is a border of its own, 32 high and rounded at 6, with the magnifier 10 in; the
        // stroke takes the accent while the box has the keyboard, as a Fluent field's does.
        _search = new Border
        {
            Height = 32,
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 0, 10, 0),
            Margin = new Thickness(0, 10, 0, 0),
            Child = field,
        };

        _search.SetResourceReference(BackgroundProperty, "ControlFillColorDefaultBrush");
        _search.SetResourceReference(BorderBrushProperty, "ControlStrokeColorDefaultBrush");
        _query.IsKeyboardFocusWithinChanged += (_, _) => _search.SetResourceReference(
            BorderBrushProperty, _query.IsKeyboardFocusWithin ? "AccentFillColorDefaultBrush" : "ControlStrokeColorDefaultBrush");

        StackPanel head = new() { Margin = new Thickness(16, 16, 16, 10) };

        head.Children.Add(_top);
        head.Children.Add(_search);

        // 4 above and 8 to the sides, the right 8 being what keeps a row clear of the scrollbar.
        _list = new ListBox
        {
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Padding = new Thickness(8, 4, 8, 4),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };

        // Several rows at once (QS314): Ctrl+click adds or removes one, Shift+click takes a range in the
        // order shown, and Ctrl+A every row listed, which is the list's own extended selection.
        _list.SelectionMode = SelectionMode.Extended;

        ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
        AutomationProperties.SetAutomationId(_list, "SessionsList");
        AutomationProperties.SetName(_list, "Saved sessions");

        _list.KeyDown += Pressed;

        // One menu for the list, made for the row it opens on (QS288): a menu built for every row up
        // front was a cost every start paid for a gesture most starts never make.
        _list.ContextMenu = new ContextMenu();
        _list.ContextMenuOpening += (_, opening) =>
        {
            if (ItemsControl.ContainerFromElement(_list, (DependencyObject)opening.OriginalSource) is ListBoxItem { Tag: SessionRow row } item)
            {
                // A right click inside a set keeps the set (QS314); anywhere else it is one row.
                if (item.IsSelected && _list.SelectedItems.Count > 1)
                {
                    _list.ContextMenu = SetMenu();

                    return;
                }

                _list.SelectedItem = item;
                _list.ContextMenu = Menu(row);
            }
            else
            {
                opening.Handled = true;
            }
        };

        _empty = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(16, 8, 16, 8),
            Visibility = Visibility.Collapsed,
        };

        _empty.SetResourceReference(TextBlock.ForegroundProperty, Secondary);
        AutomationProperties.SetAutomationId(_empty, "SessionsEmpty");

        StackPanel foot = new() { Margin = new Thickness(16, 10, 16, 14) };

        foot.Children.Add(Footer("\uE896", "Import sessions", "SessionsImport", "Bring sessions in from MobaXterm",
                                 () => _window.ImportOrSay()));
        foot.Children.Add(Footer("\uE713", "Settings", "SessionsSettings", "Open the settings window",
                                 () => _window.ShowSettings()));

        _footing = new Border { BorderThickness = new Thickness(0, 1, 0, 0), Child = foot };

        _footing.SetResourceReference(BorderBrushProperty, "DividerStrokeColorDefaultBrush");

        // Only on the rail: what a remote tab has besides its terminal, a press away while the sidebar
        // is out of the way. Each acts on the tab on screen, as its palette entry does.
        _railOnly.Add(RailButton("\uE8B7", "Files on this server", "SessionsFiles", "Browse the files of the server on screen",
                                 () => _window.BrowseFiles()));
        _railOnly.Add(RailButton("\uE8AB", "Tunnels", "SessionsTunnels", "Show the forwards every session has open",
                                 () => _window.ShowForwards()));

        foreach (Button only in _railOnly)
        {
            DockPanel.SetDock(only, Dock.Top);
            only.Visibility = Visibility.Collapsed;
            _top.Children.Add(only);
        }

        DockPanel body = new();

        DockPanel.SetDock(head, Dock.Top);
        DockPanel.SetDock(_footing, Dock.Bottom);
        DockPanel.SetDock(_empty, Dock.Top);

        body.Children.Add(head);
        body.Children.Add(_footing);
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

    /// <summary>The row selected in the list, or null; the one focused where several are (QS314).</summary>
    public SessionRow? Selected =>
        (Keyboard.FocusedElement is ListBoxItem { IsSelected: true, Tag: SessionRow focused } && _list.Items.Contains(Keyboard.FocusedElement)
            ? focused
            : null)
        ?? (_list.SelectedItem as ListBoxItem)?.Tag as SessionRow;

    /// <summary>Every row selected, in the order the list shows them (QS314).</summary>
    public IReadOnlyList<SessionRow> SelectedRows =>
        [.. _list.Items.OfType<ListBoxItem>().Where(item => item.IsSelected).Select(item => (SessionRow)item.Tag)];

    /// <summary>Selects the rows at these paths and no others, as clicks would; what a test does (QS314).</summary>
    public void SelectRows(IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        HashSet<string> wanted = new(paths, StringComparer.OrdinalIgnoreCase);

        _list.SelectedItems.Clear();

        foreach (ListBoxItem item in _list.Items.OfType<ListBoxItem>())
        {
            if (item.Tag is SessionRow row && wanted.Contains(row.Path))
            {
                _list.SelectedItems.Add(item);
            }
        }
    }

    /// <summary>The menu over a set of rows: only what takes a set, which is the document of them (QS315).</summary>
    private ContextMenu SetMenu()
    {
        ContextMenu menu = new();
        int sessions = _window.SelectedSessions().Count;

        menu.Items.Add(new MenuItem { Header = $"{sessions} sessions selected", IsEnabled = false });
        menu.Items.Add(new Separator());
        menu.Items.Add(Entry("Document as PDF…", () => _window.DocumentSessions()));

        return menu;
    }

    /// <summary>Puts the keyboard in the search box, as Home's Show all sessions does (QS279).</summary>
    public void FocusSearch() => _query.Focus();

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

        // Across the top while open. On the rail the fold is the first of a column of 36-pixel buttons,
        // 4 apart from 10 down, centred in the 52.
        DockPanel.SetDock(_fold, open ? Dock.Left : Dock.Top);
        _fold.Width = _fold.Height = open ? 28 : 36;
        ((TextBlock)_fold.Content).FontSize = open ? 16 : 18;
        ((FrameworkElement)_top.Parent).Margin = open ? new Thickness(16, 16, 16, 10) : new Thickness(8, 10, 8, 0);

        Visibility shown = open ? Visibility.Visible : Visibility.Collapsed;
        Visibility railed = open ? Visibility.Collapsed : Visibility.Visible;

        _heading.Visibility = shown;
        _new.Visibility = shown;
        _search.Visibility = shown;
        _list.Visibility = shown;
        _footing.Visibility = shown;
        _empty.Visibility = open && _list.Items.Count == 0 && _empty.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        foreach (Button only in _railOnly)
        {
            only.Visibility = railed;
        }

        _fold.ToolTip = open ? "Fold the sessions sidebar" : "Show sessions";
    }

    /// <summary>
    /// Shows these rows, keeping the selection where the same path is still listed.
    /// </summary>
    /// <param name="rows">What <see cref="SessionsView.Rows"/> gave.</param>
    /// <param name="empty">What to say where there are none: no sessions yet, a search that found nothing, or a store that would not read.</param>
    public void Show(IReadOnlyList<SessionRow> rows, string empty)
    {
        ArgumentNullException.ThrowIfNull(rows);

        HashSet<string> selected = new(SelectedRows.Select(row => row.Path), StringComparer.OrdinalIgnoreCase);

        // A row that is the same as before keeps the item it had (QS288): a tab opening changes one
        // row's Connected mark, and rebuilding every row for it cost a start a sizeable share of the
        // time between the window appearing and its first pane.
        Dictionary<SessionRow, ListBoxItem> had = [];

        foreach (ListBoxItem kept in _list.Items.OfType<ListBoxItem>())
        {
            had.TryAdd((SessionRow)kept.Tag, kept);
        }

        _list.Items.Clear();

        foreach (SessionRow row in rows)
        {
            ListBoxItem item = had.Remove(row, out ListBoxItem? same) ? same : Item(row);

            _list.Items.Add(item);

            if (selected.Contains(row.Path))
            {
                _list.SelectedItems.Add(item);
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

    /// <summary>What Enter does, and a double-click on a session: a session connects, a folder folds or unfolds.</summary>
    private void Activate(SessionRow row)
    {
        if (row.IsFolder)
        {
            _window.ToggleFolder(row.Path);

            return;
        }

        // To the tab already holding it, where one does, as the row's Connected chip promises (QS313).
        _window.GoToOrConnect(row.Path);
    }

    /// <summary>
    /// One row: a surface rather than a form, as the design has it. A folder is a chevron, its icon,
    /// its name and what it hands down; a session is its terminal glyph, its name over its address, and
    /// the Connected chip where a tab holds it. Every row is 32 high, 8 apart inside, and 22 further in
    /// per folder.
    /// </summary>
    private ListBoxItem Item(SessionRow row)
    {
        Grid line = new();

        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        StackPanel marks = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

        if (row.IsFolder)
        {
            // One chevron, turned a quarter when the folder is open, as the design turns it.
            TextBlock chevron = Glyph("\uE76C", 10);

            chevron.Width = 10;
            chevron.Margin = new Thickness(0, 0, 8, 0);
            chevron.RenderTransformOrigin = new Point(0.5, 0.5);
            chevron.RenderTransform = new RotateTransform(row.Expanded ? 90 : 0);
            chevron.SetResourceReference(TextBlock.ForegroundProperty, Secondary);
            AutomationProperties.SetAutomationId(chevron, "Chevron");
            marks.Children.Add(chevron);

            TextBlock folder = Glyph("\uE8B7", 15);

            folder.Width = 15;
            folder.SetResourceReference(TextBlock.ForegroundProperty, Secondary);
            AutomationProperties.SetAutomationId(folder, "Icon");
            marks.Children.Add(folder);
        }
        else
        {
            marks.Children.Add(TerminalGlyph());
        }

        line.Children.Add(marks);

        TextBlock name = new()
        {
            Text = row.Name,
            FontSize = 13,
            FontWeight = row.IsFolder ? FontWeights.SemiBold : FontWeights.Normal,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };

        FrameworkElement words = name;

        if (row.IsFolder)
        {
            if (row.Meta.Length > 0)
            {
                // What a folder hands down sits at the right of its row, not under its name.
                TextBlock handed = new()
                {
                    Text = row.Meta,
                    FontSize = 11,
                    Margin = new Thickness(8, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                };

                handed.SetResourceReference(TextBlock.ForegroundProperty, Secondary);
                Grid.SetColumn(handed, 2);
                line.Children.Add(handed);
            }
        }
        else if (row.Meta.Length > 0)
        {
            // Name over address, at a line-height of 1.2 each, so two lines fit the 32 of a row.
            name.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
            name.LineHeight = 13 * 1.2;

            TextBlock meta = new()
            {
                Text = row.Meta,
                FontSize = 11,
                LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                LineHeight = 11 * 1.2,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };

            meta.SetResourceReference(TextBlock.ForegroundProperty, Secondary);

            StackPanel stacked = new() { VerticalAlignment = VerticalAlignment.Center };

            stacked.Children.Add(name);
            stacked.Children.Add(meta);
            words = stacked;
        }

        words.Margin = new Thickness(8, 0, 0, 0);
        Grid.SetColumn(words, 1);
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
                Margin = new Thickness(8, 0, 0, 0),
            };

            chip.SetResourceReference(BackgroundProperty, "SystemFillColorSuccessBackgroundBrush");
            chip.SetResourceReference(BorderBrushProperty, "SystemFillColorSuccessBrush");
            AutomationProperties.SetAutomationId(chip, "Connected");

            Grid.SetColumn(chip, 2);
            line.Children.Add(chip);
        }

        ListBoxItem item = new()
        {
            Content = line,
            Tag = row,
            Template = _row,
            Height = RowHeight,
            Margin = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(Indent + (row.Depth * Step), 0, 8, 0),
        };

        // The path is the name a screen reader says and a case finds it by: unique in the store, where
        // a session's own name is only unique in its folder. Its address is the help beside it, and
        // being open in a tab is said in words in the status.
        AutomationProperties.SetName(item, row.Path);
        AutomationProperties.SetHelpText(item, row.Meta);
        AutomationProperties.SetItemStatus(item, row.Connected ? "Connected" : string.Empty);

        if (row.IsFolder)
        {
            // A folder folds on one click, as a tree in Explorer's pane does: a folder has nothing to
            // connect, so the second click a double-click asks for was only a cost. The second press of
            // a double-click is not another toggle, or a double-click out of habit would open the folder
            // and shut it again. After the press, so the list selects the row before it is rebuilt.
            item.PreviewMouseLeftButtonDown += (_, pressed) =>
            {
                // Ctrl or Shift is choosing the folder for a set (QS314), not folding it.
                if (pressed.ClickCount == 1 && (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) == 0)
                {
                    Dispatcher.BeginInvoke(() => _window.ToggleFolder(row.Path));
                }
            };
        }
        else
        {
            item.MouseDoubleClick += (_, clicked) =>
            {
                Activate(row);
                clicked.Handled = true;
            };
        }

        return item;
    }

    /// <summary>
    /// What a right-click offers. A session connects, is edited, duplicated, moved to another folder or
    /// deleted; a folder takes a new session.
    /// </summary>
    public ContextMenu Menu(SessionRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        ContextMenu menu = new();

        if (row.IsFolder)
        {
            menu.Items.Add(Entry("New session here", () => _window.NewSessionOrSay(row.Path)));
            menu.Items.Add(Entry("Document as PDF…", () => _window.DocumentSessionsAt(row.Path)));

            return menu;
        }

        // Go to tab first where a tab holds it, and a second connection beside it (QS313).
        if (_window.TabsHolding(row.Path) > 0)
        {
            menu.Items.Add(Entry("Go to tab", () => _window.GoToSession(row.Path)));
            menu.Items.Add(Entry("New connection", () => _window.ConnectSession(row.Path)));
        }
        else
        {
            menu.Items.Add(Entry("Connect", () => _window.ConnectSession(row.Path)));
        }

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
        menu.Items.Add(Entry("Document as PDF…", () => _window.DocumentSessionsAt(row.Path)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Entry("Delete…", () => _window.DeleteSessionOrSay(row.Path)));

        return menu;
    }

    private static MenuItem Entry(string header, Action run)
    {
        MenuItem entry = new() { Header = header };

        AutomationProperties.SetName(entry, header);

        entry.Click += (_, _) => run();

        return entry;
    }

    /// <summary>New: the one filled button, 28 high, a 12-pixel plus 6 before a 12-pixel word.</summary>
    private Button NewButton()
    {
        TextBlock plus = Glyph("\uE710", 12);
        TextBlock word = new() { Text = "New", FontSize = 12, FontWeight = FontWeights.SemiBold, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };

        StackPanel inside = new() { Orientation = Orientation.Horizontal };

        inside.Children.Add(plus);
        inside.Children.Add(word);

        Button button = new()
        {
            Content = inside,
            Height = 28,
            Padding = new Thickness(10, 0, 10, 0),
            ToolTip = "Make a saved session",
            VerticalAlignment = VerticalAlignment.Center,
        };

        button.SetResourceReference(StyleProperty, "AccentButtonStyle");
        AutomationProperties.SetAutomationId(button, "SessionsNew");
        AutomationProperties.SetName(button, "New");
        AutomationProperties.SetHelpText(button, "Make a saved session");

        button.Click += (_, _) => _window.NewSessionOrSay();

        return button;
    }

    /// <summary>A footer button: a ghost 32 high, a 15-pixel glyph 10 before a 13-pixel word.</summary>
    private Button Footer(string glyph, string label, string id, string help, Action run)
    {
        TextBlock word = new() { Text = label, FontSize = 13, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        TextBlock mark = Glyph(glyph, 15);

        mark.SetResourceReference(TextBlock.ForegroundProperty, Secondary);

        StackPanel inside = new() { Orientation = Orientation.Horizontal };

        inside.Children.Add(mark);
        inside.Children.Add(word);

        Button button = new()
        {
            Content = inside,
            Template = _ghost,
            Height = 32,
            Padding = new Thickness(8, 0, 8, 0),
            Margin = new Thickness(0, 0, 0, 2),
            HorizontalContentAlignment = HorizontalAlignment.Left,
        };

        return Named(button, label, id, help, run);
    }

    /// <summary>A rail button: a ghost 36 square with an 18-pixel glyph, 4 below the one above.</summary>
    private Button RailButton(string glyph, string label, string id, string help, Action run)
    {
        TextBlock mark = Glyph(glyph, 18);

        mark.SetResourceReference(TextBlock.ForegroundProperty, Secondary);

        Button button = new()
        {
            Content = mark,
            Template = _ghost,
            Width = 36,
            Height = 36,
            Padding = new Thickness(0),
            Margin = new Thickness(0, 4, 0, 0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };

        return Named(button, label, id, help, run);
    }

    private static Button Named(Button button, string label, string id, string help, Action run)
    {
        button.ToolTip = label;
        AutomationProperties.SetAutomationId(button, id);
        AutomationProperties.SetName(button, label);
        AutomationProperties.SetHelpText(button, help);

        button.Click += (_, _) => run();

        return button;
    }

    /// <summary>The session row's glyph: the drawn terminal at 15, in the quieter text colour.</summary>
    private static Viewbox TerminalGlyph()
    {
        Viewbox glyph = Templates.Terminal(15, Secondary);

        AutomationProperties.SetAutomationId(glyph, "Terminal");

        return glyph;
    }

    private static TextBlock Glyph(string glyph, double size) => new()
    {
        Text = glyph,
        FontFamily = Glyphs,
        FontSize = size,
        VerticalAlignment = VerticalAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Center,
    };

    /// <summary>
    /// The accent at a tenth, from the theme this sidebar is in, or the system's highlight where there is
    /// none. No colour of its own: Brand is the one place this client writes one (QS83).
    /// </summary>
    private SolidColorBrush Selection()
    {
        Color accent = SystemColors.HighlightColor;

        if (TryFindResource("SystemAccentColor") is Color system)
        {
            accent = system;
        }
        else if (TryFindResource("AccentFillColorDefaultBrush") is SolidColorBrush fill)
        {
            accent = fill.Color;
        }

        SolidColorBrush brush = new(accent) { Opacity = 0.1 };

        brush.Freeze();

        return brush;
    }


}
