using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace Quickshell.App;

/// <summary>
/// The bar over a remote tab's terminal (QS269): what the session is called, where it goes, whether
/// it is up, which forwards it holds, and the three things a person does with a session from here.
///
/// <para><b>A view, and nothing else</b>, as the sidebar is. What it says is a
/// <see cref="ConnectionView"/> the window worked out; what a button does is a method on
/// <see cref="MainWindow"/>, the same one the palette runs.</para>
///
/// <para><b>Fluent's brushes and no colour of its own.</b> The state chip carries its word as well
/// as its colour: success while connected, caution while reconnecting, critical once it has gone.</para>
///
/// <para><b>Built the first time a remote tab is on screen, and not before.</b> A window that has
/// only ever held local shells has no element for it at all, which is what the default window's
/// own test reads.</para>
/// </summary>
public sealed class SessionBar : Border
{
    /// <summary>Its height, from the approved design.</summary>
    public const double BarHeight = 48;

    /// <summary>The face a route is written in, the terminal's own family first.</summary>
    private static readonly FontFamily Mono = new("Cascadia Mono, Consolas");

    private static readonly FontFamily Glyphs = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    private readonly MainWindow _window;
    private readonly TextBlock _name;
    private readonly TextBlock _address;
    private readonly Border _state;
    private readonly TextBlock _stateWord;
    private readonly StackPanel _forwards;

    /// <summary>Builds the bar over a window, empty until the window hands it a view.</summary>
    public SessionBar(MainWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);

        _window = window;

        Height = BarHeight;
        Padding = new Thickness(16, 0, 12, 0);
        BorderThickness = new Thickness(0, 0, 0, 1);
        SetResourceReference(BackgroundProperty, "CardBackgroundFillColorDefaultBrush");
        SetResourceReference(BorderBrushProperty, "DividerStrokeColorDefaultBrush");

        AutomationProperties.SetAutomationId(this, "SessionBar");
        AutomationProperties.SetName(this, "Session");

        _name = new TextBlock { FontWeight = FontWeights.SemiBold, FontSize = 14 };
        AutomationProperties.SetAutomationId(_name, "SessionName");

        _address = new TextBlock { FontFamily = Mono, FontSize = 12 };
        _address.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        AutomationProperties.SetAutomationId(_address, "SessionRoute");

        StackPanel named = new() { VerticalAlignment = VerticalAlignment.Center };

        named.Children.Add(_name);
        named.Children.Add(_address);

        _stateWord = new TextBlock { FontSize = 12 };

        _state = new Border
        {
            Child = _stateWord,
            Padding = new Thickness(8, 2, 8, 2),
            CornerRadius = new CornerRadius(5),
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0),
        };

        AutomationProperties.SetAutomationId(_state, "ConnectionState");

        _forwards = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

        StackPanel acts = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

        acts.Children.Add(Act("", "Files", "SessionFiles", "Browse this server's files",
                              () => _window.BrowseFiles()));
        acts.Children.Add(Act("", "Split", "SessionSplit", "Split this pane, the new one beside it",
                              () => _window.SplitPane(Divide.Beside)));
        acts.Children.Add(More());

        DockPanel line = new() { LastChildFill = false };

        DockPanel.SetDock(named, Dock.Left);
        DockPanel.SetDock(_state, Dock.Left);
        DockPanel.SetDock(_forwards, Dock.Left);
        DockPanel.SetDock(acts, Dock.Right);

        line.Children.Add(named);
        line.Children.Add(_state);
        line.Children.Add(_forwards);
        line.Children.Add(acts);

        Child = line;
    }

    /// <summary>What it is showing, or null before the first view.</summary>
    public ConnectionView? Showing { get; private set; }

    /// <summary>The state chip's word, which is what a test and a case read.</summary>
    public string StateSaid => _stateWord.Text;

    /// <summary>The forward chips' words, left to right.</summary>
    public IReadOnlyList<string> ForwardChips =>
        [.. _forwards.Children.OfType<Button>().Select(chip => AutomationProperties.GetName(chip))];

    /// <summary>Says this, touching only what differs from what it said last.</summary>
    public void Show(ConnectionView view)
    {
        ArgumentNullException.ThrowIfNull(view);

        if (view.Equals(Showing))
        {
            return;
        }

        ConnectionView? was = Showing;

        Showing = view;

        _name.Text = view.Name;
        _address.Text = view.Address;

        if (was?.State != view.State)
        {
            _stateWord.Text = view.Said;
            AutomationProperties.SetName(_state, view.Said);

            (string fill, string edge) = view.State switch
            {
                ConnectionState.Connected => ("SystemFillColorSuccessBackgroundBrush", "SystemFillColorSuccessBrush"),
                ConnectionState.Reconnecting => ("SystemFillColorCautionBackgroundBrush", "SystemFillColorCautionBrush"),
                _ => ("SystemFillColorCriticalBackgroundBrush", "SystemFillColorCriticalBrush"),
            };

            _state.SetResourceReference(BackgroundProperty, fill);
            _state.SetResourceReference(BorderBrushProperty, edge);
            _stateWord.SetResourceReference(TextBlock.ForegroundProperty, edge);
        }

        if (was is null || !was.Forwards.SequenceEqual(view.Forwards))
        {
            _forwards.Children.Clear();

            foreach (string forward in view.Forwards)
            {
                _forwards.Children.Add(Chip(forward));
            }
        }
    }

    /// <summary>One running forward, as a chip that opens the forwards window.</summary>
    private Button Chip(string said)
    {
        Button chip = new()
        {
            Content = new TextBlock { Text = said, FontSize = 12 },
            Padding = new Thickness(8, 2, 8, 2),
            Margin = new Thickness(8, 0, 0, 0),
            MinHeight = 0,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "Show every forward",
        };

        AutomationProperties.SetAutomationId(chip, "Forward");
        AutomationProperties.SetName(chip, said);
        AutomationProperties.SetHelpText(chip, "Show every forward");

        chip.Click += (_, _) => _window.ShowForwards();

        return chip;
    }

    /// <summary>A button with a glyph and a word.</summary>
    private static Button Act(string glyph, string label, string id, string help, Action run)
    {
        StackPanel inside = new() { Orientation = Orientation.Horizontal };

        inside.Children.Add(Glyph(glyph));
        inside.Children.Add(new TextBlock { Text = label, FontSize = 12, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });

        Button button = new()
        {
            Content = inside,
            Height = 30,
            Padding = new Thickness(10, 0, 10, 0),
            Margin = new Thickness(8, 0, 0, 0),
            ToolTip = help,
        };

        AutomationProperties.SetAutomationId(button, id);
        AutomationProperties.SetName(button, label);
        AutomationProperties.SetHelpText(button, help);

        button.Click += (_, _) => run();

        return button;
    }

    /// <summary>
    /// The menu of what the palette offers for a session, filled when it opens so each entry is
    /// offered only where it can run then.
    /// </summary>
    private Button More()
    {
        Button more = new()
        {
            Content = Glyph(""),
            Width = 30,
            Height = 30,
            Padding = new Thickness(0),
            Margin = new Thickness(8, 0, 0, 0),
            ToolTip = "More for this session",
        };

        AutomationProperties.SetAutomationId(more, "SessionMore");
        AutomationProperties.SetName(more, "More for this session");

        ContextMenu menu = new() { PlacementTarget = more, Placement = PlacementMode.Bottom };

        more.Click += (_, _) =>
        {
            menu.Items.Clear();

            foreach ((string header, Action run) in _window.SessionActions())
            {
                MenuItem entry = new() { Header = header };

                entry.Click += (_, _) => run();
                menu.Items.Add(entry);
            }

            menu.IsOpen = true;
        };

        return more;
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
