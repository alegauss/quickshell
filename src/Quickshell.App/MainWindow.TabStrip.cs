using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Quickshell.App;

/// <summary>
/// The tab strip as the friendly-shell design draws it (QS278): one row, 42 high, with Home first, each
/// tab's state as a dot before its title and a close button after it, the tab on screen underlined in
/// the accent, a New tab button after the last, and Commands at the far right.
///
/// <para><b>Still a tab control.</b> Selection, the drag that reorders (QS250), the pull that detaches
/// (QS160) and the accessibility tree's TabItems all stay the control's; what changed is how each item
/// is drawn. The look of a tab is decided here in code and not by a selection trigger, because while
/// Home is on screen the strip's selection is still the last tab, and that tab must not look chosen.</para>
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>The strip's row height, a tab's height in it, and the underline's thickness.</summary>
    public const double StripHeight = 42, TabHeight = 38, Underline = 3;

    /// <summary>The icon faces the strip's glyphs come from, as the sidebar's do.</summary>
    private static readonly FontFamily StripGlyphs = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    /// <summary>The theme's two text brushes: what is on screen, and what is behind it.</summary>
    private const string PlainText = "TextFillColorPrimaryBrush", QuietText = "TextFillColorSecondaryBrush";

    // The row around the strip: 0 12 of padding and the divider under it.
    private readonly Border _stripFrame = new() { Height = StripHeight, Padding = new Thickness(12, 0, 12, 0), BorderThickness = new Thickness(0, 0, 0, 1) };

    /// <summary>What each tab's header is made of, so a redraw changes the parts that changed and nothing else.</summary>
    private sealed record TabLook(Ellipse Dot, TextBlock Number, TextBlock Title, Border Bar, Button Close);

    /// <summary>Builds the row: frame, the strip's own template, New tab and Commands. Called once, from the constructor.</summary>
    private void BuildStrip()
    {
        _stripFrame.SetResourceReference(Border.BorderBrushProperty, "DividerStrokeColorDefaultBrush");
        AutomationProperties.SetAutomationId(_stripFrame, "TabStrip");

        _tabs.Template = Templates.Strip();
        _tabs.VerticalAlignment = VerticalAlignment.Bottom;

        Button adding = StripButton(Glyphed("", 14), "NewTab", "New tab", "Open a new tab (Ctrl+Shift+T)", () => Opens?.Invoke());

        adding.Width = 30;
        adding.Height = 28;

        TextBlock word = new() { Text = "Commands", FontSize = 12, Margin = new Thickness(8, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        TextBlock keys = new() { Text = "Ctrl+Shift+P", FontSize = 11 };

        keys.SetResourceReference(TextBlock.ForegroundProperty, QuietText);

        Border cap = new() { Child = keys, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), Padding = new Thickness(5, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center };

        cap.SetResourceReference(Border.BorderBrushProperty, "DividerStrokeColorDefaultBrush");

        StackPanel inside = new() { Orientation = Orientation.Horizontal };

        inside.Children.Add(Glyphed("", 14));
        inside.Children.Add(word);
        inside.Children.Add(cap);

        Button commands = StripButton(inside, "Commands", "Commands", "Every action this client has, by typing part of its name", () => ShowPalette());

        commands.Height = 28;
        commands.Padding = new Thickness(10, 0, 10, 0);
        AutomationProperties.SetAcceleratorKey(commands, "Ctrl+Shift+P");

        // Home, the tabs and New tab one after the other from the left; Commands alone at the right.
        _strip.LastChildFill = false;
        DockPanel.SetDock(_tabs, Dock.Left);
        DockPanel.SetDock(adding, Dock.Left);
        DockPanel.SetDock(commands, Dock.Right);
        _strip.Children.Add(adding);
        _strip.Children.Add(commands);

        _stripFrame.Child = _strip;
    }

    /// <summary>A ghost button on the strip, 5 up from its bottom edge as the design sets them.</summary>
    private static Button StripButton(object content, string id, string name, string help, Action run)
    {
        Button button = new()
        {
            Content = content,
            Template = Templates.Ghost(),
            Padding = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(2, 0, 0, 5),
            ToolTip = help,
        };

        button.SetResourceReference(Control.ForegroundProperty, QuietText);
        AutomationProperties.SetAutomationId(button, id);
        AutomationProperties.SetName(button, name);
        AutomationProperties.SetHelpText(button, help);
        button.Click += (_, _) => run();

        return button;
    }

    /// <summary>One tab's item: its parts made once, dressed by <see cref="Dress"/> from then on.</summary>
    private TabItem TabFor(TerminalTab tab)
    {
        Ellipse dot = new() { Width = 8, Height = 8, VerticalAlignment = VerticalAlignment.Center };

        AutomationProperties.SetAutomationId(dot, "TabState");

        // The tab's place on the strip, which is the Alt chord that reaches it (QS319), in the quieter colour
        // so the title still reads first.
        TextBlock number = new() { Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };

        number.SetResourceReference(TextBlock.ForegroundProperty, QuietText);
        AutomationProperties.SetAutomationId(number, "TabNumber");

        TextBlock title = new() { Text = tab.Title, VerticalAlignment = VerticalAlignment.Center };

        TabItem item = new() { Template = Templates.Tab(), Height = TabHeight, Margin = new Thickness(0, 0, 2, 0), VerticalAlignment = VerticalAlignment.Bottom };

        // 10 points, and no padding (QS312). The theme pads a button 11 a side, which on one 22 wide
        // left the glyph no room at all: the button was in the tree, laid out, and drew nothing.
        TextBlock cross = Glyphed("", 10);

        Button close = new()
        {
            Content = cross,
            Template = Templates.Ghost(),
            Width = 22,
            Height = 22,
            Padding = new Thickness(0),
            Margin = new Thickness(8, 0, 0, 0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Focusable = false,
        };

        close.SetResourceReference(Control.ForegroundProperty, QuietText);
        AutomationProperties.SetAutomationId(close, "CloseTab");
        close.Click += (_, clicked) =>
        {
            clicked.Handled = true;

            // The tab this button is on, wherever the strip has moved it since.
            if (_tabs.Items.IndexOf(item) is var at and >= 0)
            {
                Active = at;
                CloseTab();
            }
        };

        // A middle click closes this tab, as it does in every browser and terminal on Windows (QS310):
        // pressed and released on the same tab, and taken before the strip's drag, which is the left
        // button's alone.
        bool middlePressed = false;

        item.PreviewMouseDown += (_, pressed) =>
        {
            if (pressed.ChangedButton == System.Windows.Input.MouseButton.Middle)
            {
                middlePressed = true;
                pressed.Handled = true;
            }
        };

        item.PreviewMouseUp += (_, released) =>
        {
            if (released.ChangedButton != System.Windows.Input.MouseButton.Middle)
            {
                return;
            }

            released.Handled = true;

            if (middlePressed && _tabs.Items.IndexOf(item) is var at and >= 0)
            {
                CloseTab(at);
            }

            middlePressed = false;
        };

        item.MouseLeave += (_, _) => middlePressed = false;

        // A right click, or Shift+F10 on the tab, opens what a tab can do (QS311). Made as it opens,
        // so the entries say what is possible for this tab among the tabs there are now.
        item.ContextMenu = new ContextMenu();
        item.ContextMenuOpening += (_, _) => item.ContextMenu = TabMenu(_tabs.Items.IndexOf(item));

        StackPanel row = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(16, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };

        row.Children.Add(dot);
        row.Children.Add(number);
        row.Children.Add(title);
        row.Children.Add(close);

        Border line = Underlined();
        Grid header = new() { Height = TabHeight };

        header.Children.Add(row);
        header.Children.Add(line);

        item.Header = header;
        item.Tag = new TabLook(dot, number, title, line, close);

        Dress(item, tab, selected: false);

        return item;
    }

    /// <summary>
    /// A tab's menu: Close, Close other tabs and Close tabs to the right, each acting on this tab and not
    /// the one on screen, the last two unavailable where there is nothing for them to close (QS311).
    /// </summary>
    public ContextMenu TabMenu(int index)
    {
        TerminalTab? tab = index >= 0 && index < _open.Count ? _open[index] : null;
        ContextMenu menu = new();

        AutomationProperties.SetAutomationId(menu, "TabMenu");

        // Each entry finds its tab again when chosen, since the strip may have moved it meanwhile.
        menu.Items.Add(TabMenuEntry("Close", "CloseThisTab", tab is not null, () => CloseTab(_open.IndexOf(tab!))));
        menu.Items.Add(TabMenuEntry("Close other tabs", "CloseOtherTabs", tab is not null && _open.Count > 1,
                                    () => CloseOtherTabs(_open.IndexOf(tab!))));
        menu.Items.Add(TabMenuEntry("Close tabs to the right", "CloseTabsToTheRight", tab is not null && index < _open.Count - 1,
                                    () => CloseTabsToTheRight(_open.IndexOf(tab!))));

        return menu;
    }

    private static MenuItem TabMenuEntry(string header, string id, bool enabled, Action run)
    {
        MenuItem entry = new() { Header = header, IsEnabled = enabled };

        AutomationProperties.SetAutomationId(entry, id);
        AutomationProperties.SetName(entry, header);
        entry.Click += (_, _) => run();

        return entry;
    }

    /// <summary>
    /// A tab's look from its state: the dot (filled while connected or reconnecting, a hollow ring for a
    /// local shell or one that ended), its title, and the underline and weight while it is on screen.
    /// The state is also its item status, which is how a case or a screen reader reads the dot.
    /// </summary>
    private static void Dress(TabItem item, TerminalTab tab, bool selected)
    {
        if (item.Tag is not TabLook look)
        {
            return;
        }

        Dot(item, look.Dot, ConnectionView.Of(tab.Focused)?.State);

        string title = tab.Title;

        if (!string.Equals(look.Title.Text, title, StringComparison.Ordinal))
        {
            look.Title.Text = title;
        }

        // The name is the title and only the title: a case reads a tab by it, and a screen reader says it.
        if (!string.Equals(AutomationProperties.GetName(item), title, StringComparison.Ordinal))
        {
            AutomationProperties.SetName(item, title);
            AutomationProperties.SetName(look.Close, $"Close {title}");
        }

        // On screen it is SemiBold in the primary text colour and underlined; behind, Regular in the
        // secondary one, unless something was written to it since it was last seen, which is said by
        // weight (the title stays the title, so a screen reader does not read a marker out).
        FontWeight weight = tab.HasActivity ? FontWeights.Bold : FontWeights.Normal;

        look.Bar.Visibility = selected ? Visibility.Visible : Visibility.Hidden;
        look.Title.FontWeight = selected ? FontWeights.SemiBold : weight;
        look.Title.SetResourceReference(TextBlock.ForegroundProperty, selected || tab.HasActivity ? PlainText : QuietText);
    }

    /// <summary>
    /// The dot for a state, redrawn only when the state changed: the success fill while connected, the
    /// caution fill while reconnecting, and a 1.5 ring in the tertiary text colour for a local shell or
    /// one that ended. The state's word is the item's status.
    /// </summary>
    private static void Dot(TabItem item, Ellipse dot, ConnectionState? state)
    {
        string status = state is { } known ? ConnectionView.Word(known) : "Local";

        if (Equals(AutomationProperties.GetItemStatus(item), status))
        {
            return;
        }

        AutomationProperties.SetItemStatus(item, status);

        switch (state)
        {
            case ConnectionState.Connected:
                dot.StrokeThickness = 0;
                dot.SetResourceReference(Shape.FillProperty, "SystemFillColorSuccessBrush");
                break;

            case ConnectionState.Reconnecting:
                dot.StrokeThickness = 0;
                dot.SetResourceReference(Shape.FillProperty, "SystemFillColorCautionBrush");
                break;

            default:
                dot.StrokeThickness = 1.5;
                dot.Fill = Brushes.Transparent;
                dot.SetResourceReference(Shape.StrokeProperty, "TextFillColorTertiaryBrush");
                break;
        }
    }

    /// <summary>
    /// Home's place on the strip: a tab to the eye — the house, the word, the underline while it is on
    /// screen — and a button to everything else, since it is not one of the window's tabs.
    /// </summary>
    private static Button HomeTab()
    {
        TextBlock house = Glyphed("", 15);
        TextBlock word = new() { Text = "Home", Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };

        StackPanel row = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(16, 0, 16, 0), VerticalAlignment = VerticalAlignment.Center };

        row.Children.Add(house);
        row.Children.Add(word);

        Border line = Underlined();
        Grid header = new() { Height = TabHeight };

        header.Children.Add(row);
        header.Children.Add(line);

        Button button = new()
        {
            Content = header,
            Template = Templates.Plain(),
            Height = TabHeight,
            Margin = new Thickness(0, 0, 2, 0),
            VerticalAlignment = VerticalAlignment.Bottom,
            ToolTip = "Connect to a server, open a recent session, or start another way",
            Tag = (word, line),
        };

        return button;
    }

    /// <summary>
    /// Numbers every tab by its place on the strip (QS319): "1. " before the first title, and so on, so
    /// two tabs on one host are told apart and the number is the Alt chord that reaches the tab. Run on
    /// every change to the strip, so a close or a move renumbers the rest at once; a tenth tab has its
    /// number too, with no chord behind it. The tab's name stays its bare title; the number is in its help.
    /// </summary>
    private void Number()
    {
        for (int at = 0; at < _tabs.Items.Count; at++)
        {
            if (_tabs.Items[at] is not TabItem { Tag: TabLook look } item)
            {
                continue;
            }

            string said = (at + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) + ". ";

            if (!string.Equals(look.Number.Text, said, StringComparison.Ordinal))
            {
                look.Number.Text = said;
                AutomationProperties.SetHelpText(item, at < 9 ? $"Tab {at + 1}, Alt+{at + 1}" : $"Tab {at + 1}");
            }
        }
    }

    /// <summary>Home's tab, dressed as on screen or not, the way a tab is.</summary>
    private void DressHome()
    {
        if (_homeSlot.Content is not Button { Tag: (TextBlock word, Border line) })
        {
            return;
        }

        line.Visibility = _homeShowing ? Visibility.Visible : Visibility.Hidden;
        word.FontWeight = _homeShowing ? FontWeights.SemiBold : FontWeights.Normal;
        word.SetResourceReference(TextBlock.ForegroundProperty, _homeShowing ? PlainText : QuietText);
    }

    /// <summary>Every tab dressed for whether it is the one on screen, which no tab is while Home is.</summary>
    private void DressTabs()
    {
        for (int tab = 0; tab < _open.Count && tab < _tabs.Items.Count; tab++)
        {
            if (_tabs.Items[tab] is TabItem item)
            {
                Dress(item, _open[tab], selected: !_homeShowing && tab == _active);
            }

            // A saved session's first live connection in this tab is what puts it in the recent list
            // (QS279): read here because this is where each tab's state is read, twice a second.
            if (_open[tab].Saved is { Length: > 0 } saved
                && ConnectionView.Of(_open[tab].Focused)?.State == ConnectionState.Connected
                && _wentLive.Add(_open[tab]))
            {
                SessionWentLive(saved);
            }
        }

        Number();
        DressHome();
    }

    /// <summary>The accent underline: 3 high, rounded at 2, 10 in from each side and 1 over the divider.</summary>
    private static Border Underlined()
    {
        Border line = new()
        {
            Height = Underline,
            CornerRadius = new CornerRadius(2),
            Margin = new Thickness(10, 0, 10, -1),
            VerticalAlignment = VerticalAlignment.Bottom,
            Visibility = Visibility.Hidden,
        };

        line.SetResourceReference(Border.BackgroundProperty, "AccentFillColorDefaultBrush");
        AutomationProperties.SetAutomationId(line, "Underline");

        return line;
    }

    private static TextBlock Glyphed(string glyph, double size) => new()
    {
        Text = glyph,
        FontFamily = StripGlyphs,
        FontSize = size,
        VerticalAlignment = VerticalAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Center,
    };
}
