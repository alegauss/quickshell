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
    private readonly WrapPanel _cards;
    private readonly TextBlock _cardsHeading;

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

        body.Children.Add(new TextBlock { Text = "Connect to a server", FontSize = 24, FontWeight = FontWeights.SemiBold });

        TextBlock lead = new()
        {
            Text = "Type an address, open a session you saved, or bring yours over from MobaXterm.",
            FontSize = 13,
            Margin = new Thickness(0, 4, 0, 16),
            TextWrapping = TextWrapping.Wrap,
        };

        lead.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        body.Children.Add(lead);

        body.Children.Add(Card(quick, new Thickness(20, 8, 20, 20)));

        _cardsHeading = Heading(string.Empty);
        body.Children.Add(_cardsHeading);

        _cards = new WrapPanel();
        AutomationProperties.SetAutomationId(_cards, "HomeSessions");
        body.Children.Add(_cards);

        body.Children.Add(Heading("Ways to start"));

        UniformGrid ways = new() { Rows = 1, Columns = 3 };

        ways.Children.Add(Way("", "Import from MobaXterm", "Your sessions and their folders", "HomeImport", () => _window.ImportOrSay()));
        ways.Children.Add(Way("", "Open a local shell", "A Command Prompt on this computer", "HomeLocal", () => _window.Opens?.Invoke()));
        ways.Children.Add(Way("", "Browse files", "This computer, and a server's over SFTP", "HomeFiles", () => _window.BrowseFiles()));

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
        _cardsHeading.Visibility = cards.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

        foreach (SessionCard card in cards)
        {
            _cards.Children.Add(Session(card));
        }
    }

    /// <summary>One session's card: its name, its address, its folder and tags, and Connect.</summary>
    private Border Session(SessionCard card)
    {
        StackPanel inside = new();

        inside.Children.Add(new TextBlock { Text = card.Name, FontSize = 14, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });

        TextBlock address = new()
        {
            Text = card.Address,
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 2, 0, 0),
        };

        address.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        inside.Children.Add(address);

        string filed = string.Join(" · ", new[] { card.Folder }.Concat(card.Tags).Where(each => each.Length > 0));

        TextBlock meta = new() { Text = filed, FontSize = 11, Margin = new Thickness(0, 6, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };

        meta.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
        inside.Children.Add(meta);

        // When it last connected, where it is a recent one (QS279).
        if (card.When.Length > 0)
        {
            TextBlock when = new() { Text = card.When, FontSize = 12, Margin = new Thickness(0, 6, 0, 0) };

            when.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
            AutomationProperties.SetAutomationId(when, "When");
            inside.Children.Add(when);
        }

        Button connect = new() { Content = "Connect", Margin = new Thickness(0, 12, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };

        AutomationProperties.SetName(connect, $"Connect {card.Path}");
        connect.Click += (_, _) => _window.ConnectSession(card.Path);
        inside.Children.Add(connect);

        Border made = Card(inside, new Thickness(16));

        made.Width = 260;
        made.Margin = new Thickness(0, 0, 12, 12);
        AutomationProperties.SetName(made, card.Path);
        AutomationProperties.SetHelpText(made, card.Address);

        return made;
    }

    /// <summary>One way to start: a glyph, what it does, and a line on what that means.</summary>
    private static Button Way(string glyph, string title, string said, string id, Action run)
    {
        StackPanel inside = new() { Margin = new Thickness(4) };

        inside.Children.Add(new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 20 });
        inside.Children.Add(new TextBlock { Text = title, FontSize = 14, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 2) });

        TextBlock line = new() { Text = said, FontSize = 12, TextWrapping = TextWrapping.Wrap };

        line.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        inside.Children.Add(line);

        Button way = new()
        {
            Content = inside,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            VerticalContentAlignment = VerticalAlignment.Top,
            Padding = new Thickness(14),
            Margin = new Thickness(0, 0, 12, 0),
            MinHeight = 120,
        };

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
