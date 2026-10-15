using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace Quickshell.App;

/// <summary>One saved session as a card on the Home page: where it goes, where it is filed, and its tags.</summary>
/// <param name="Path">Its path in the store, which Connect opens.</param>
/// <param name="Name">Its own name.</param>
/// <param name="Address">user@host, or the host alone.</param>
/// <param name="Folder">The folder it is in; empty at the top.</param>
/// <param name="Tags">Its tags.</param>
public sealed record SessionCard(string Path, string Name, string Address, string Folder, IReadOnlyList<string> Tags)
{
    /// <summary>When it last connected, in words ("Connected now", "Yesterday"), or empty where it is not a recent one (QS279).</summary>
    public string When { get; init; } = string.Empty;

    /// <summary>How many tabs hold it now; above none, the card goes to one rather than opening another (QS313).</summary>
    public int Tabs { get; init; }
}

/// <summary>
/// The Home page (QS272): a way to a server on the first screen, so nobody takes this client for a
/// terminal emulator that happens to have a palette.
///
/// <para><b>Connect to a server</b> is quick connect (QS271); under it the sessions opened last as
/// cards, each with a Connect; then three ways to start: import from MobaXterm, a local shell, and
/// the file browser. The sidebar stays beside it.</para>
///
/// <para><b>Drawn by WPF, holding no device.</b> While Home is on screen no pane is shown and the
/// render loop draws none, so the page costs what a window of controls costs and nothing more.</para>
///
/// <para><b>A view, and nothing else</b>, as the sidebar is: every button is a method on
/// <see cref="MainWindow"/>, the one the palette runs.</para>
/// </summary>
public sealed class HomePage : Border
{
    private readonly MainWindow _window;
    /// <summary>The theme's quieter text: an address, a chip, a time.</summary>
    private const string Quiet = "TextFillColorSecondaryBrush";

    private readonly UniformGrid _cards;
    private readonly TextBlock _cardsHeading;
    private readonly Grid _cardsHead;

    /// <summary>The server glyph a card's tile holds: two stacked units with a light each, on a 24 grid, as the design draws it.</summary>
    private static readonly Geometry Server = Frozen(Geometry.Parse(
        "M4.5,4 H19.5 A1.5,1.5 0 0 1 21,5.5 V9.5 A1.5,1.5 0 0 1 19.5,11 H4.5 A1.5,1.5 0 0 1 3,9.5 V5.5 A1.5,1.5 0 0 1 4.5,4 Z "
        + "M4.5,13 H19.5 A1.5,1.5 0 0 1 21,14.5 V18.5 A1.5,1.5 0 0 1 19.5,20 H4.5 A1.5,1.5 0 0 1 3,18.5 V14.5 A1.5,1.5 0 0 1 4.5,13 Z "
        + "M7,7.5 L7.01,7.5 M7,16.5 L7.01,16.5"));

    /// <summary>Builds the page over a window.</summary>
    public HomePage(MainWindow window, QuickConnect quick)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(quick);

        _window = window;
        Quick = quick;

        AutomationProperties.SetAutomationId(this, "Home");
        AutomationProperties.SetName(this, "Home");
        SetResourceReference(BackgroundProperty, "SolidBackgroundFillColorBaseBrush");

        StackPanel body = new() { Margin = new Thickness(40, 32, 40, 32), MaxWidth = 880, HorizontalAlignment = HorizontalAlignment.Left };

        // The heading and its line, 6 apart, as the design sets them (QS280).
        body.Children.Add(new TextBlock { Text = "Connect to a server", FontSize = 26, FontWeight = FontWeights.Bold });

        TextBlock lead = new()
        {
            Text = "Type an address, or pick one of your saved sessions on the left.",
            FontSize = 14,
            Margin = new Thickness(0, 6, 0, 28),
            TextWrapping = TextWrapping.Wrap,
        };

        lead.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        body.Children.Add(lead);

        body.Children.Add(Card(quick, new Thickness(20)));

        // "Recent", with Show all sessions at its right (QS279): the cards are three, and the rest are
        // in the sidebar, which the link opens with the keyboard in its search.
        _cardsHeading = Heading(string.Empty);
        _cardsHeading.Margin = new Thickness(0);

        Button all = new()
        {
            Content = new TextBlock { Text = "Show all sessions", FontSize = 12 },
            Template = Templates.Plain(),
            Cursor = System.Windows.Input.Cursors.Hand,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
        };

        all.SetResourceReference(Control.ForegroundProperty, "AccentTextFillColorPrimaryBrush");
        AutomationProperties.SetAutomationId(all, "ShowAllSessions");
        AutomationProperties.SetName(all, "Show all sessions");
        all.Click += (_, _) => _window.ShowAllSessions();

        _cardsHead = new Grid { Margin = new Thickness(0, 28, 0, 12) };
        _cardsHead.Children.Add(_cardsHeading);
        _cardsHead.Children.Add(all);
        body.Children.Add(_cardsHead);

        // Three equal columns, 12 apart: each card carries 12 at its right and the grid gives the last one's back.
        _cards = new UniformGrid { Rows = 1, Columns = 3, Margin = new Thickness(0, 0, -12, 0) };
        AutomationProperties.SetAutomationId(_cards, "HomeSessions");
        body.Children.Add(_cards);

        // The ways to start, 28 under what is above them: three equal cards, 12 apart.
        UniformGrid ways = new() { Rows = 1, Columns = 3, Margin = new Thickness(0, 28, -12, 0) };

        ways.Children.Add(Way("", "Bring your MobaXterm sessions", "Previewed first; nothing is written until you agree.", "HomeImport", () => _window.ImportOrSay()));
        ways.Children.Add(Way("", "Open a local shell", "Command Prompt, PowerShell or WSL in a new tab.", "HomeLocal", () => _window.Opens?.Invoke()));
        ways.Children.Add(Way("", "Browse files", "Drag files to and from a server over SFTP.", "HomeFiles", () => _window.BrowseFiles()));

        body.Children.Add(ways);

        Child = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    /// <summary>The quick-connect control on it.</summary>
    public QuickConnect Quick { get; }

    /// <summary>The cards on screen, in order, which is what a test reads.</summary>
    public IReadOnlyList<string> Cards =>
        [.. _cards.Children.OfType<FrameworkElement>().Select(card => AutomationProperties.GetName(card))];

    /// <summary>What the cards are headed, which says whether they are recent or merely saved.</summary>
    public string CardsHeading => _cardsHeading.Text;

    /// <summary>Shows these sessions as cards.</summary>
    /// <param name="cards">At most three, newest first.</param>
    /// <param name="recent">Whether they were opened recently, or are simply the store's first.</param>
    public void Show(IReadOnlyList<SessionCard> cards, bool recent)
    {
        ArgumentNullException.ThrowIfNull(cards);

        _cards.Children.Clear();
        _cardsHeading.Text = cards.Count == 0 ? string.Empty : recent ? "Recent" : "Saved sessions";
        _cardsHead.Visibility = cards.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

        foreach (SessionCard card in cards)
        {
            _cards.Children.Add(Session(card));
        }
    }

    /// <summary>
    /// One session's card, as the design draws it (QS279): a 34-pixel tile with the server glyph beside
    /// its name over its address; its folder and tags as chips, the folder first; and at the bottom when
    /// it last connected, beside a 28-high Connect. Padding 16, 12 between the three, radius 8.
    /// </summary>
    private Border Session(SessionCard card)
    {
        StackPanel inside = new();

        Grid top = new();

        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        top.Children.Add(Tile());

        StackPanel named = new() { Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };

        named.Children.Add(new TextBlock { Text = card.Name, FontSize = 14, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });

        TextBlock address = new()
        {
            Text = card.Address,
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        address.SetResourceReference(TextBlock.ForegroundProperty, Quiet);
        named.Children.Add(address);
        Grid.SetColumn(named, 1);
        top.Children.Add(named);
        inside.Children.Add(top);

        WrapPanel chips = new() { Margin = new Thickness(0, 12, 0, 0) };

        AutomationProperties.SetAutomationId(chips, "Chips");

        foreach (string label in new[] { card.Folder }.Concat(card.Tags).Where(each => each.Length > 0))
        {
            chips.Children.Add(Chip(label));
        }

        if (chips.Children.Count > 0)
        {
            inside.Children.Add(chips);
        }

        Grid bottom = new() { Margin = new Thickness(0, 12, 0, 0) };

        // When it last connected, where it is a recent one.
        if (card.When.Length > 0)
        {
            TextBlock when = new() { Text = card.When, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };

            when.SetResourceReference(TextBlock.ForegroundProperty, Quiet);
            AutomationProperties.SetAutomationId(when, "When");
            bottom.Children.Add(when);
        }

        // A card whose session a tab holds goes to that tab, which is what Connected promises (QS313);
        // a second shell on the host is the quieter New connection beside it.
        StackPanel actions = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        Action main;

        if (card.Tabs > 0)
        {
            Button another = CardButton("New connection", "CardNewConnection", $"New connection to {card.Path}",
                                        () => _window.ConnectSession(card.Path));
            Button go = CardButton("Go to tab", "CardGoTo", $"Go to tab {card.Path}", () => _window.GoToSession(card.Path));

            another.Margin = new Thickness(0, 0, 6, 0);
            go.SetResourceReference(StyleProperty, "AccentButtonStyle");
            actions.Children.Add(another);
            actions.Children.Add(go);
            main = () => _window.GoToSession(card.Path);
        }
        else
        {
            actions.Children.Add(CardButton("Connect", "CardConnect", $"Connect {card.Path}", () => _window.ConnectSession(card.Path)));
            main = () => _window.ConnectSession(card.Path);
        }

        bottom.Children.Add(actions);
        inside.Children.Add(bottom);

        Border made = Card(inside, new Thickness(16));

        // A double click on the card does what its main button does.
        made.MouseLeftButtonDown += (_, pressed) =>
        {
            if (pressed.ClickCount == 2)
            {
                main();
                pressed.Handled = true;
            }
        };

        made.Margin = new Thickness(0, 0, 12, 0);
        AutomationProperties.SetName(made, card.Path);
        AutomationProperties.SetHelpText(made, card.Address);

        // The stroke one step stronger under the pointer.
        made.MouseEnter += (_, _) => made.SetResourceReference(BorderBrushProperty, "ControlStrokeColorSecondaryBrush");
        made.MouseLeave += (_, _) => made.SetResourceReference(BorderBrushProperty, "CardStrokeColorDefaultBrush");

        return made;
    }

    /// <summary>One of a card's buttons: 28 high, 12-pixel SemiBold words, 12 either side.</summary>
    private static Button CardButton(string words, string id, string name, Action run)
    {
        Button button = new()
        {
            Content = words,
            Height = 28,
            Padding = new Thickness(12, 0, 12, 0),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
        };

        AutomationProperties.SetAutomationId(button, id);
        AutomationProperties.SetName(button, name);
        button.Click += (_, _) => run();

        return button;
    }

    /// <summary>The 34-pixel tile, radius 8, on the layer fill, holding the server glyph at 17 in the accent.</summary>
    private static Border Tile()
    {
        System.Windows.Shapes.Path glyph = new()
        {
            Data = Server,
            StrokeThickness = 2,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
        };

        glyph.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "AccentFillColorDefaultBrush");

        Border tile = new()
        {
            Width = 34,
            Height = 34,
            CornerRadius = new CornerRadius(8),
            Child = new Viewbox { Width = 17, Height = 17, Child = new Canvas { Width = 24, Height = 24, Children = { glyph } } },
        };

        tile.SetResourceReference(BackgroundProperty, "LayerFillColorDefaultBrush");
        AutomationProperties.SetAutomationId(tile, "Tile");

        return tile;
    }

    /// <summary>A tag chip: 11-pixel words, padding 1 by 7, radius 5, a one-pixel stroke, 6 from the next.</summary>
    private static Border Chip(string label)
    {
        TextBlock said = new() { Text = label, FontSize = 11 };

        said.SetResourceReference(TextBlock.ForegroundProperty, Quiet);

        Border chip = new()
        {
            Child = said,
            Padding = new Thickness(7, 1, 7, 1),
            CornerRadius = new CornerRadius(5),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 0, 6, 6),
        };

        chip.SetResourceReference(BackgroundProperty, "SubtleFillColorSecondaryBrush");
        chip.SetResourceReference(BorderBrushProperty, "CardStrokeColorDefaultBrush");

        return chip;
    }

    private static Geometry Frozen(Geometry geometry)
    {
        geometry.Freeze();

        return geometry;
    }

    /// <summary>
    /// One way to start, as the design draws it (QS280): a card on the subtle fill, padding 14 by 16,
    /// radius 8, an 18-pixel accent glyph at its left beside what it does in SemiBold over a 12-pixel
    /// line on what that means.
    /// </summary>
    private static Button Way(string glyph, string title, string said, string id, Action run)
    {
        TextBlock mark = new() { Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 18, Margin = new Thickness(0, 1, 12, 0) };

        mark.SetResourceReference(TextBlock.ForegroundProperty, "AccentTextFillColorPrimaryBrush");

        StackPanel words = new();

        words.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });

        TextBlock line = new() { Text = said, FontSize = 12, Margin = new Thickness(0, 2, 0, 0), TextWrapping = TextWrapping.Wrap };

        line.SetResourceReference(TextBlock.ForegroundProperty, Quiet);
        words.Children.Add(line);

        Grid inside = new();

        inside.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        inside.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        inside.Children.Add(mark);
        Grid.SetColumn(words, 1);
        inside.Children.Add(words);

        Border card = new()
        {
            Child = inside,
            Padding = new Thickness(16, 14, 16, 14),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
        };

        card.SetResourceReference(BackgroundProperty, "CardBackgroundFillColorSecondaryBrush");
        card.SetResourceReference(BorderBrushProperty, "CardStrokeColorDefaultBrush");

        Button way = new()
        {
            Content = card,
            Template = Templates.Plain(),
            Margin = new Thickness(0, 0, 12, 0),
            Cursor = System.Windows.Input.Cursors.Hand,
        };

        way.MouseEnter += (_, _) => card.SetResourceReference(BorderBrushProperty, "ControlStrokeColorSecondaryBrush");
        way.MouseLeave += (_, _) => card.SetResourceReference(BorderBrushProperty, "CardStrokeColorDefaultBrush");

        AutomationProperties.SetAutomationId(way, id);
        AutomationProperties.SetName(way, title);
        AutomationProperties.SetHelpText(way, said);
        way.Click += (_, _) => run();

        return way;
    }

    /// <summary>A flat card: the layer brush, a one-pixel border, eight pixels of radius.</summary>
    private static Border Card(UIElement inside, Thickness padding)
    {
        Border card = new()
        {
            Child = inside,
            Padding = padding,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
        };

        card.SetResourceReference(BackgroundProperty, "CardBackgroundFillColorDefaultBrush");
        card.SetResourceReference(BorderBrushProperty, "CardStrokeColorDefaultBrush");

        return card;
    }

    private static TextBlock Heading(string text) => new()
    {
        Text = text,
        FontSize = 16,
        FontWeight = FontWeights.SemiBold,
        Margin = new Thickness(0, 28, 0, 12),
    };
}
