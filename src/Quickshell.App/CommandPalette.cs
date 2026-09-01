using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Quickshell.App;

/// <summary>
/// The list of everything this client does, found by typing part of its name.
///
/// <para><b>QS52: this is the surface that lets the other surfaces stay small.</b> An action does not
/// need a button in order to be findable, which is what makes the non-goal about toolbars affordable
/// — the window stays as empty as QS46 promised and nothing is lost.</para>
///
/// <para><b>Every entry shows its chord.</b> That turns the palette into how chords are learnt,
/// rather than something a user has to read a reference to discover. Somebody who finds "Split pane
/// right" here twice has seen <c>Ctrl+Shift+\</c> twice, and the third time they will type it.</para>
///
/// <para><b>It has no configuration of its own</b>, closes on escape, and takes the keyboard only
/// because it was asked for.</para>
/// </summary>
public sealed class CommandPalette : Window
{
    private readonly IReadOnlyList<Command> _all;
    private readonly IReadOnlyList<string> _recent;
    private readonly TextBox _typed = new()
    {
        Margin = new Thickness(8),
        Padding = new Thickness(6, 4, 6, 4),
        FontSize = 15,
    };

    private readonly ListBox _showing = new()
    {
        Margin = new Thickness(8, 0, 8, 8),
        MaxHeight = 320,
        BorderThickness = new Thickness(0),
    };

    /// <summary>
    /// Builds the palette over a list of actions.
    /// </summary>
    /// <param name="all">Everything it can offer, from <see cref="Commands.From"/>.</param>
    /// <param name="recent">The names most recently run, newest first.</param>
    public CommandPalette(IReadOnlyList<Command> all, IReadOnlyList<string>? recent = null)
    {
        ArgumentNullException.ThrowIfNull(all);

        _all = all;
        _recent = recent ?? [];

        Title = "Command palette";
        Width = 560;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        AutomationProperties.SetName(_typed, "Command");
        AutomationProperties.SetName(_showing, "Commands");

        _showing.ItemTemplate = Row();
        _showing.ItemContainerStyle = Announcing();

        StackPanel body = new();

        body.Children.Add(_typed);
        body.Children.Add(_showing);

        Content = body;

        _typed.TextChanged += (_, _) => Filter();

        // On the box and not on the window: the list never takes the keyboard, so every key a user
        // presses while this is open belongs to what they are typing, including the ones that move
        // the selection.
        _typed.PreviewKeyDown += Steering;

        Filter();

        // The keyboard goes to the box the moment there is one, because a palette a user has to
        // click into is a palette they stop opening.
        Loaded += (_, _) => _typed.Focus();
    }

    /// <summary>What the user picked, or null where they pressed escape.</summary>
    public Command? Chosen { get; private set; }

    /// <summary>What the list is showing now, which is what a test reads.</summary>
    public IReadOnlyList<Command> Showing => [.. _showing.Items.OfType<Command>()];

    /// <summary>Types into the palette, for a caller with no keyboard.</summary>
    /// <param name="query">What to type.</param>
    public void Type(string query)
    {
        _typed.Text = query ?? string.Empty;

        Filter();
    }

    /// <summary>Takes what is selected and closes, which is what Enter does.</summary>
    public void Take()
    {
        Chosen = _showing.SelectedItem as Command;

        // Only where something was picked. Closing with nothing selected would report escape, and
        // a palette that ran nothing should say it ran nothing.
        DialogResult = Chosen is not null;
    }

    private void Steering(object sender, KeyEventArgs what)
    {
        switch (what.Key)
        {
            case Key.Escape:
                Chosen = null;
                DialogResult = false;
                what.Handled = true;
                break;

            case Key.Enter:
                Take();
                what.Handled = true;
                break;

            case Key.Down:
                Move(1);
                what.Handled = true;
                break;

            case Key.Up:
                Move(-1);
                what.Handled = true;
                break;

            default:
                break;
        }
    }

    /// <summary>Moves the selection without wrapping, so holding a key settles at an end.</summary>
    private void Move(int by)
    {
        if (_showing.Items.Count == 0)
        {
            return;
        }

        int to = Math.Clamp(_showing.SelectedIndex + by, 0, _showing.Items.Count - 1);

        _showing.SelectedIndex = to;
        _showing.ScrollIntoView(_showing.Items[to]);
    }

    /// <summary>What the typed text matches, with the best of it selected.</summary>
    private void Filter()
    {
        _showing.ItemsSource = Commands.Matching(_all, _typed.Text, _recent);

        // Always something selected, so Enter never has to be preceded by an arrow key.
        _showing.SelectedIndex = _showing.Items.Count > 0 ? 0 : -1;
    }

    /// <summary>
    /// Each row announces the action's name.
    ///
    /// <para><b>Without this a row announces the record.</b> A templated <see cref="ListBoxItem"/>
    /// takes its accessible name from the item's <c>ToString</c>, and for a record that is
    /// <c>Command { Name = …, Chord = …, Runs = … }</c> — which is what a screen reader would read
    /// aloud, and what a case would have to match. The palette is the surface that makes every
    /// action reachable, so a palette a screen reader cannot read is one that reaches nobody using
    /// one.</para>
    /// </summary>
    private static Style Announcing()
    {
        Style row = new(typeof(ListBoxItem));

        row.Setters.Add(new Setter(AutomationProperties.NameProperty,
                                   new System.Windows.Data.Binding(nameof(Command.Name))));

        return row;
    }

    /// <summary>A row: what it is called on the left, what it is bound to on the right.</summary>
    private static DataTemplate Row()
    {
        FrameworkElementFactory line = new(typeof(DockPanel));

        FrameworkElementFactory chord = new(typeof(TextBlock));

        chord.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(Command.Chord)));
        chord.SetValue(DockPanel.DockProperty, Dock.Right);
        chord.SetValue(FrameworkElement.MarginProperty, new Thickness(12, 0, 0, 0));
        chord.SetValue(TextBlock.OpacityProperty, 0.6);
        chord.SetValue(TextBlock.FontFamilyProperty, new FontFamily("Consolas, Cascadia Mono, monospace"));

        FrameworkElementFactory name = new(typeof(TextBlock));

        name.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(Command.Name)));
        name.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);

        line.AppendChild(chord);
        line.AppendChild(name);

        return new DataTemplate { VisualTree = line };
    }
}
