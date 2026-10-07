using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace Quickshell.App;

/// <summary>One answer a <see cref="Choice"/> offers: the words on its button, and what it means.</summary>
/// <param name="Label">What the button says — the act, never "Yes".</param>
/// <param name="Means">What the caller is told was chosen.</param>
public readonly record struct ChoiceButton(string Label, string Means)
{
    /// <summary>
    /// Whether this is the way out — what Escape and closing the window choose. Where no button says
    /// so, the last one is. A question whose safe answer is its first button marks it, so Escape
    /// can never be the destructive act.
    /// </summary>
    public bool IsWayOut { get; init; }
}

/// <summary>
/// A question whose buttons say what they do, in this client's own words (QS131).
///
/// <para><b>Not a <c>MessageBox</c>, for two reasons the crash dialog showed.</b> A message box takes
/// its button captions from Windows, so on a Portuguese desk the sentence was this client's English
/// and the buttons "Sim" and "Não" — a client that reads unfinished. And "Yes/No" names neither
/// thing that happens: the question was "Open the report now?", and a person answering it should be
/// able to read the act off the button without reading the sentence above it again.</para>
///
/// <para><b>Built to survive being shown from a client that is failing.</b> It loads no styles,
/// reaches no session state, and is plain controls in a plain window — and where even that cannot be
/// built (no dispatcher on this thread, the application already torn down), it falls back to
/// <c>MessageBox</c>, because a dialog that fails to appear is worse than a bilingual one.</para>
/// </summary>
public sealed class Choice : Window
{
    private Choice(string title, string message, IReadOnlyList<ChoiceButton> buttons)
    {
        Title = title;

        // Found by these, in every language, by a UI case reading the accessibility tree.
        AutomationProperties.SetAutomationId(this, "Choice");

        SizeToContent = SizeToContent.WidthAndHeight;
        MaxWidth = 560;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        StackPanel body = new() { Margin = new Thickness(18) };

        body.Children.Add(new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 16),
            Name = "Message",
        });

        StackPanel row = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };

        bool marked = buttons.Any(button => button.IsWayOut);

        for (int index = 0; index < buttons.Count; index++)
        {
            ChoiceButton offered = buttons[index];

            Button button = new()
            {
                Content = offered.Label,
                Padding = new Thickness(14, 4, 14, 4),
                Margin = new Thickness(8, 0, 0, 0),

                // The first is what Enter does. Escape does the way out: the button marked as one,
                // or the last where none is — the order most questions come in, the act asked about
                // and then the way out.
                IsDefault = index == 0,
                IsCancel = marked ? offered.IsWayOut : index == buttons.Count - 1,
            };

            AutomationProperties.SetName(button, offered.Label);
            AutomationProperties.SetAutomationId(button, offered.Means);

            button.Click += (_, _) =>
            {
                Chosen = offered.Means;
                Close();
            };

            row.Children.Add(button);
        }

        body.Children.Add(row);
        Content = body;
    }

    /// <summary>What was chosen, or null where the window was closed some other way.</summary>
    public string? Chosen { get; private set; }

    /// <summary>
    /// Asks, and answers with the chosen button's meaning — or null where it was closed, which every
    /// caller reads as the way out.
    /// </summary>
    /// <param name="owner">The window to sit over, or null where there is none.</param>
    /// <param name="title">The caption.</param>
    /// <param name="message">The question, in full.</param>
    /// <param name="buttons">The act first, the way out last.</param>
    public static string? Ask(Window? owner, string title, string message, params ChoiceButton[] buttons)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(buttons.Length, 1);

        if (Showing is { } shown)
        {
            return shown(Build(title, message, buttons));
        }

        try
        {
            Choice choice = Build(title, message, buttons);

            // Owned, as a message box is by whatever window is active: it sits over the client and
            // stays above it, and the accessibility tree finds it under the window that asked.
            if ((owner ?? Active()) is { IsLoaded: true } over && !ReferenceEquals(over, choice))
            {
                choice.Owner = over;
            }

            choice.ShowDialog();

            return choice.Chosen;
        }
        catch (Exception unbuildable) when (unbuildable is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // No dispatcher here, or nothing left to own a window: the operating system's box, which
            // is bilingual and still better than nothing on screen.
            MessageBoxResult answer = MessageBox.Show(
                $"{message}\n\n{buttons[0].Label}?", title,
                buttons.Length > 1 ? MessageBoxButton.YesNo : MessageBoxButton.OK);

            return answer is MessageBoxResult.Yes or MessageBoxResult.OK ? buttons[0].Means : null;
        }
    }

    /// <summary>
    /// Who shows a choice instead of a person answering one — how a test reads the buttons a
    /// question offers and picks one. Null shows it.
    /// </summary>
    public static Func<Choice, string?>? Showing { get; set; }

    /// <summary>The buttons' words, in order, for whoever needs to read them.</summary>
    public IReadOnlyList<string> Labels =>
        [.. LogicalTreeHelper.GetChildren((StackPanel)((StackPanel)Content).Children[1])
                             .OfType<Button>().Select(button => (string)button.Content)];

    /// <summary>Presses the button with these words, as a person would.</summary>
    public void Press(string label)
    {
        Button button = LogicalTreeHelper.GetChildren((StackPanel)((StackPanel)Content).Children[1])
                                         .OfType<Button>()
                                         .Single(each => (string)each.Content == label);

        button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
    }

    /// <summary>The window a message box would have taken for its owner, or null where there is none here.</summary>
    private static Window? Active()
    {
        if (Application.Current is not { } application || !application.Dispatcher.CheckAccess())
        {
            return null;
        }

        return application.Windows.OfType<Window>().FirstOrDefault(window => window.IsActive)
               ?? application.MainWindow;
    }

    private static Choice Build(string title, string message, IReadOnlyList<ChoiceButton> buttons) =>
        new(title, message, buttons);
}
