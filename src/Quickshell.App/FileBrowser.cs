using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using Quickshell.Transport;

namespace Quickshell.App;

/// <summary>
/// Local on one side, remote on the other — QS60's browser.
///
/// <para><b>Two panes because that is the shape of the task.</b> Every alternative makes the user
/// hold the direction in their head: a single list with a toggle for which machine it shows is a
/// list somebody copies the wrong way out of.</para>
///
/// <para><b>Its own window and not a pane of the terminal's.</b> The main window's argument is a
/// title bar and a terminal and nothing else, and a browser docked into it would be the sidebar that
/// argument refuses. Here it is opened when asked for and closed when done with, and a key pressed
/// in it is never one the terminal owed a program — which is also why its keys are the ones every
/// two-pane file tool on this platform uses: F5 copies, F2 renames, F7 makes a directory, F8 and
/// Delete delete.</para>
///
/// <para><b>The listing is the panes' business and the operations are
/// <see cref="BrowserActions"/>'s</b>: this window draws what a pane says, tells the actions what
/// was clicked, and asks the questions they ask.</para>
/// </summary>
public sealed class FileBrowser : Window
{
    /// <summary>
    /// What the right-hand side says when the tab has no remote side, which is true of every tab
    /// running a local shell.
    /// </summary>
    public const string NoRemote = "This tab is a local shell, so there is no remote side to list.";

    private readonly Button _copy = Action("Copy", "F5");

    /// <summary>Builds the browser. Nothing is listed until it is shown.</summary>
    /// <param name="local">This machine.</param>
    /// <param name="remote">The host the tab's session is connected to, or null where there is none.</param>
    public FileBrowser(IFileSide local, IFileSide? remote)
    {
        ArgumentNullException.ThrowIfNull(local);

        Title = remote is null ? "Files — quickshell" : $"Files on {remote.Title} — quickshell";
        Width = 1100;
        Height = 640;
        MinWidth = 480;
        MinHeight = 240;

        Local = new DirectoryPane(local, Post);
        Remote = remote is null ? null : new DirectoryPane(remote, Post);

        Actions = new BrowserActions(Local, Remote, Post)
        {
            AskingToCopy = question => Ask("Copy", question.Asking, "Copy"),
            AskingToDelete = question => Ask("Delete", question.Asking, "Delete"),
            AskingForName = question => Named(question),
            AskingForMode = question => Moded(question),
            OnCollision = (collision, _) => new ValueTask<CollisionChoice>(
                Dispatcher.InvokeAsync(() => Colliding(collision)).Task),
        };

        Grid panes = new();

        panes.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        panes.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        panes.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        FrameworkElement left = new PaneView(Local, this);
        FrameworkElement right = Remote is null ? Absent() : new PaneView(Remote, this);

        Grid.SetColumn(left, 0);
        Grid.SetColumn(right, 2);

        panes.Children.Add(left);
        panes.Children.Add(right);

        DockPanel layout = new() { Margin = new Thickness(8) };
        StackPanel bar = Bar();

        DockPanel.SetDock(bar, Dock.Bottom);

        layout.Children.Add(bar);
        layout.Children.Add(panes);

        Content = layout;

        Keys();

        // Listed once there is a window to list into, and not before: a browser built and never
        // shown should cost no disk read and no round trip.
        Loaded += (_, _) =>
        {
            _ = Local.Start();
            _ = Remote?.Start();
        };
    }

    /// <summary>This machine's half.</summary>
    public DirectoryPane Local { get; }

    /// <summary>The host's half, or null where the tab has no remote side.</summary>
    public DirectoryPane? Remote { get; }

    /// <summary>What the buttons and keys do, over whichever pane has the keyboard.</summary>
    public BrowserActions Actions { get; }

    /// <summary>Hands a pane's work to this window's thread, which is the only one that may draw it.</summary>
    private void Post(Action work) => Dispatcher.BeginInvoke(work);

    /// <summary>The pane with the keyboard changed, and the copy button says which way it now goes.</summary>
    private void Focused(DirectoryPane pane)
    {
        Actions.Active = pane;

        _copy.Content = ReferenceEquals(pane, Local) ? "Copy →  (F5)" : "←  Copy  (F5)";
        _copy.IsEnabled = Remote is not null;
    }

    /// <summary>The row of operations, each labelled with its key so the key is learnt by looking.</summary>
    private StackPanel Bar()
    {
        StackPanel bar = new()
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 8, 0, 0),
        };

        Button rename = Action("Rename", "F2");
        Button make = Action("New directory", "F7");
        Button delete = Action("Delete", "F8");
        Button mode = Action("Permissions", "Alt+Enter");
        Button refresh = Action("Refresh", "Ctrl+R");

        _copy.Click += (_, _) => _ = Actions.CopyAsync();
        rename.Click += (_, _) => _ = Actions.RenameAsync();
        make.Click += (_, _) => _ = Actions.CreateDirectoryAsync();
        delete.Click += (_, _) => _ = Actions.DeleteAsync();
        mode.Click += (_, _) => _ = Actions.ChangeModeAsync();
        refresh.Click += (_, _) => _ = Actions.Active.Refresh();

        foreach (Button button in (Button[])[_copy, rename, make, delete, mode, refresh])
        {
            bar.Children.Add(button);
        }

        Focused(Local);

        return bar;
    }

    /// <summary>A button named for its operation, with the key that also does it beside the name.</summary>
    private static Button Action(string what, string key)
    {
        Button button = new()
        {
            Content = $"{what}  ({key})",
            Margin = new Thickness(0, 0, 6, 0),
            Padding = new Thickness(10, 3, 10, 3),
        };

        AutomationProperties.SetName(button, what);

        return button;
    }

    /// <summary>
    /// The window's keys. On the window and not on a list, so they work wherever in the browser the
    /// keyboard is — except in a text box, where a key belongs to what is being typed.
    /// </summary>
    private void Keys()
    {
        PreviewKeyDown += (sender, e) =>
        {
            if (Keyboard.FocusedElement is TextBox)
            {
                return;
            }

            Key key = e.Key == Key.System ? e.SystemKey : e.Key;
            ModifierKeys held = Keyboard.Modifiers;

            Func<Task>? doing = (key, held) switch
            {
                (Key.F5, ModifierKeys.None) => () => Actions.CopyAsync(),
                (Key.F2, ModifierKeys.None) => () => Actions.RenameAsync(),
                (Key.F7, ModifierKeys.None) => () => Actions.CreateDirectoryAsync(),
                (Key.F8, ModifierKeys.None) or (Key.Delete, ModifierKeys.None) => () => Actions.DeleteAsync(),
                (Key.Enter, ModifierKeys.Alt) => () => Actions.ChangeModeAsync(),
                (Key.R, ModifierKeys.Control) => () => Actions.Active.Refresh(),
                _ => null,
            };

            if (doing is not null)
            {
                _ = doing();
                e.Handled = true;
            }
        };
    }

    /// <summary>
    /// A yes-or-no about something irreversible, with the safe answer as the default: Enter and
    /// Escape both leave everything as it was, the rule the closing question keeps too.
    /// </summary>
    private bool Ask(string title, string question, string doing)
    {
        StackPanel body = new() { Margin = new Thickness(20) };

        body.Children.Add(new TextBlock { Text = question, TextWrapping = TextWrapping.Wrap });

        (Button stay, Button go) = Buttons(body, "Cancel", doing);

        stay.IsDefault = true;

        return Dialog(title, body, go) is true;
    }

    /// <summary>A name, asked for in a box that starts with what the entry is called now.</summary>
    private string? Named(NameQuestion question)
    {
        StackPanel body = new() { Margin = new Thickness(20) };
        TextBox name = new() { Text = question.Initial, MinWidth = 320 };

        body.Children.Add(new TextBlock { Text = question.Asking, Margin = new Thickness(0, 0, 0, 8) });
        body.Children.Add(name);

        (_, Button go) = Buttons(body, "Cancel", "OK");

        go.IsDefault = true;

        name.Loaded += (_, _) =>
        {
            name.Focus();
            name.SelectAll();
        };

        return Dialog("Name", body, go) is true ? name.Text : null;
    }

    /// <summary>
    /// A mode, asked for as the three octal digits a server writes, with what it is now in the box.
    /// On this computer's side the one bit Windows keeps is said out loud, rather than a mode
    /// silently losing eight of its nine bits.
    /// </summary>
    private int? Moded(ModeQuestion question)
    {
        StackPanel body = new() { Margin = new Thickness(20) };
        TextBox mode = new() { Text = question.Current, MinWidth = 120 };

        string asking = question.OnlyWritable
            ? $"Mode for {question.Entry.Name}. Windows keeps only whether the owner may write: "
              + "a mode without that bit makes it read-only."
            : $"Mode for {question.Entry.Name}, in octal — 644 for a file anybody may read.";

        body.Children.Add(new TextBlock { Text = asking, TextWrapping = TextWrapping.Wrap, MaxWidth = 380,
                                          Margin = new Thickness(0, 0, 0, 8) });
        body.Children.Add(mode);

        (_, Button go) = Buttons(body, "Cancel", "Set");

        go.IsDefault = true;

        mode.Loaded += (_, _) =>
        {
            mode.Focus();
            mode.SelectAll();
        };

        if (Dialog("Permissions", body, go) is not true)
        {
            return null;
        }

        try
        {
            int parsed = Convert.ToInt32(mode.Text.Trim(), 8);

            return parsed is >= 0 and <= 0b_111_111_111 ? parsed : null;
        }
        catch (Exception)
        {
            // Not an octal number, which is not a mode. Nothing is changed rather than something
            // being changed to a guess.
            return null;
        }
    }

    /// <summary>
    /// A name already taken at the destination: both sides of it, the four answers, and whether the
    /// answer stands for the rest of this copy.
    /// </summary>
    private CollisionChoice Colliding(Collision collision)
    {
        StackPanel body = new() { Margin = new Thickness(20) };

        string sizes = string.Create(CultureInfo.InvariantCulture,
            $"Copying {FileItem.Human(collision.Length)}, modified {collision.Modified.ToLocalTime():yyyy-MM-dd HH:mm}; "
            + $"there already: {FileItem.Human(collision.ExistingLength)}, modified "
            + $"{collision.ExistingModified.ToLocalTime():yyyy-MM-dd HH:mm}.");

        body.Children.Add(new TextBlock
        {
            Text = $"{collision.Path} already exists.",
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 460,
        });
        body.Children.Add(new TextBlock { Text = sizes, TextWrapping = TextWrapping.Wrap, MaxWidth = 460,
                                          Margin = new Thickness(0, 6, 0, 0) });

        CheckBox rest = new() { Content = "Do the same for the rest of this copy", Margin = new Thickness(0, 12, 0, 0) };

        body.Children.Add(rest);

        StackPanel buttons = new()
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0),
        };

        CollisionAnswer answer = CollisionAnswer.Skip;
        Window dialog = Shell("Already there", body);

        foreach ((string label, CollisionAnswer means) in (ValueTuple<string, CollisionAnswer>[])
                 [
                     ("Skip", CollisionAnswer.Skip), ("Keep both", CollisionAnswer.Rename),
                     ("Replace if newer", CollisionAnswer.TakeNewer), ("Replace", CollisionAnswer.Overwrite),
                 ])
        {
            Button button = new() { Content = label, MinWidth = 88, Margin = new Thickness(8, 0, 0, 0) };

            // Skip is the default and the one Escape gives: leaving a file alone is the answer that
            // cannot destroy anything, and a question answered by reflex should land there.
            button.IsDefault = button.IsCancel = means == CollisionAnswer.Skip;
            button.Click += (_, _) =>
            {
                answer = means;
                dialog.DialogResult = true;
            };

            buttons.Children.Add(button);
        }

        body.Children.Add(buttons);

        dialog.ShowDialog();

        return new CollisionChoice(answer, rest.IsChecked == true);
    }

    /// <summary>Two buttons at the foot of a dialog body, the refusal first.</summary>
    private static (Button Stay, Button Go) Buttons(StackPanel body, string stay, string go)
    {
        StackPanel buttons = new()
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 20, 0, 0),
        };

        Button cancel = new() { Content = stay, MinWidth = 88, IsCancel = true };
        Button act = new() { Content = go, MinWidth = 88, Margin = new Thickness(8, 0, 0, 0), Tag = "go" };

        buttons.Children.Add(cancel);
        buttons.Children.Add(act);
        body.Children.Add(buttons);

        return (cancel, act);
    }

    /// <summary>Shows a dialog whose one affirmative button is <paramref name="go"/>.</summary>
    private bool? Dialog(string title, StackPanel body, Button go)
    {
        Window dialog = Shell(title, body);

        go.Click += (_, _) => dialog.DialogResult = true;

        return dialog.ShowDialog();
    }

    /// <summary>A small window over this one, sized to what it says.</summary>
    private Window Shell(string title, StackPanel body) => new()
    {
        Title = title,
        Content = body,
        Owner = this,
        ThemeMode = ThemeMode,
        SizeToContent = SizeToContent.WidthAndHeight,
        ResizeMode = ResizeMode.NoResize,
        ShowInTaskbar = false,
        WindowStartupLocation = WindowStartupLocation.CenterOwner,
    };

    /// <summary>The right-hand side when there is nothing to put there, saying why rather than being blank.</summary>
    private static TextBlock Absent() => new()
    {
        Text = NoRemote,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(12),
        VerticalAlignment = VerticalAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Center,
    };

    /// <summary>
    /// One pane on screen: where it is, how to move, what is there, and one line about it.
    ///
    /// <para><b>Drawn from the pane and never the other way.</b> Every control here is set from a
    /// change the pane announced, so what is on screen is always a picture of the pane's state and
    /// there is no second copy of it to fall out of step. The one thing that flows the other way is
    /// the selection, which is the user's and which the operations act on.</para>
    /// </summary>
    private sealed class PaneView : DockPanel
    {
        private readonly DirectoryPane _pane;
        private readonly TextBox _path = new() { VerticalContentAlignment = VerticalAlignment.Center };
        private readonly Button _back = Tool("←", "Back");
        private readonly Button _forward = Tool("→", "Forward");
        private readonly Button _up = Tool("↑", "Up one directory");
        private readonly CheckBox _hidden = new()
        {
            Content = "Hidden",
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };

        private readonly ListView _list = new() { SelectionMode = SelectionMode.Extended };
        private readonly TextBlock _status = new() { Margin = new Thickness(2, 6, 2, 0), TextWrapping = TextWrapping.Wrap };
        private readonly Dictionary<SortBy, GridViewColumnHeader> _headers = [];

        public PaneView(DirectoryPane pane, FileBrowser browser)
        {
            _pane = pane;

            TextBlock title = new()
            {
                Text = pane.Side.Title,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(2, 0, 2, 6),
            };

            DockPanel tools = new() { Margin = new Thickness(0, 0, 0, 6) };

            DockPanel.SetDock(_back, Dock.Left);
            DockPanel.SetDock(_forward, Dock.Left);
            DockPanel.SetDock(_up, Dock.Left);
            DockPanel.SetDock(_hidden, Dock.Right);

            tools.Children.Add(_back);
            tools.Children.Add(_forward);
            tools.Children.Add(_up);
            tools.Children.Add(_hidden);
            tools.Children.Add(_path);

            AutomationProperties.SetName(_path, "Path on " + pane.Side.Title);
            AutomationProperties.SetName(_list, "Files on " + pane.Side.Title);

            GridView columns = new();

            columns.Columns.Add(Column("Name", nameof(FileItem.Name), SortBy.Name, 280));
            columns.Columns.Add(Column("Size", nameof(FileItem.Size), SortBy.Size, 90));
            columns.Columns.Add(Column("Modified", nameof(FileItem.When), SortBy.Modified, 130));
            columns.Columns.Add(Column("Permissions", nameof(FileItem.Permissions), SortBy.Permissions, 110));

            _list.View = columns;

            // Virtualised, which is the other half of fifty thousand entries: the list realises the
            // rows on screen and nothing else, so its cost is the window's height and not the
            // directory's size.
            VirtualizingPanel.SetIsVirtualizing(_list, true);
            VirtualizingPanel.SetVirtualizationMode(_list, VirtualizationMode.Recycling);

            DockPanel.SetDock(title, Dock.Top);
            DockPanel.SetDock(tools, Dock.Top);
            DockPanel.SetDock(_status, Dock.Bottom);

            Children.Add(title);
            Children.Add(tools);
            Children.Add(_status);
            Children.Add(_list);

            _back.Click += (_, _) => _ = _pane.Back();
            _forward.Click += (_, _) => _ = _pane.Forward();
            _up.Click += (_, _) => _ = _pane.Up();
            _hidden.Click += (_, _) => _pane.ShowHidden = _hidden.IsChecked == true;

            _path.KeyDown += (sender, e) =>
            {
                if (e.Key == Key.Enter && _path.Text.Trim() is { Length: > 0 } typed)
                {
                    _ = _pane.Go(typed);
                    e.Handled = true;
                }
            };

            // Anywhere in the pane taking the keyboard makes it the one the operations act on.
            IsKeyboardFocusWithinChanged += (_, _) =>
            {
                if (IsKeyboardFocusWithin)
                {
                    browser.Focused(_pane);
                }
            };

            _list.SelectionChanged += (_, _) => _pane.Selected = [.. _list.SelectedItems.OfType<FileItem>()];

            // Files dragged in from Explorer land in the directory this pane shows — QS64.
            AllowDrop = true;

            DragOver += (_, e) =>
            {
                e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
                e.Handled = true;
            };

            Drop += (sender, e) =>
            {
                if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files)
                {
                    browser.Focused(_pane);

                    _ = browser.Actions.DropAsync(_pane, files);
                }

                e.Handled = true;
            };
            _list.MouseDoubleClick += (_, _) => OpenSelected();

            // Enter opens and Backspace goes up, which is what a keyboard does in every file list
            // on this platform; Alt with an arrow walks the history, as it does in a browser.
            _list.PreviewKeyDown += (sender, e) =>
            {
                Key key = e.Key == Key.System ? e.SystemKey : e.Key;
                bool alt = (Keyboard.Modifiers & ModifierKeys.Alt) != 0;

                e.Handled = (key, alt) switch
                {
                    (Key.Enter, false) => Run(OpenSelected),
                    (Key.Back, false) or (Key.Up, true) => Run(() => _ = _pane.Up()),
                    (Key.Left, true) => Run(() => _ = _pane.Back()),
                    (Key.Right, true) => Run(() => _ = _pane.Forward()),
                    _ => false,
                };
            };

            _pane.PropertyChanged += Changed;

            Draw();
        }

        /// <summary>A toolbar button, named for a screen reader by what it does rather than by its arrow.</summary>
        private static Button Tool(string glyph, string what)
        {
            Button button = new()
            {
                Content = glyph,
                MinWidth = 30,
                Margin = new Thickness(0, 0, 4, 0),
                ToolTip = what,
            };

            AutomationProperties.SetName(button, what);

            return button;
        }

        /// <summary>A column whose header orders the pane by it.</summary>
        private GridViewColumn Column(string title, string property, SortBy by, double width)
        {
            GridViewColumnHeader header = new() { Content = title, Tag = title };

            header.Click += (_, _) => _pane.SortOn(by);

            _headers[by] = header;

            return new GridViewColumn
            {
                Header = header,
                Width = width,
                DisplayMemberBinding = new System.Windows.Data.Binding(property),
            };
        }

        /// <summary>Runs something from a key handler and says it was handled.</summary>
        private static bool Run(Action action)
        {
            action();

            return true;
        }

        private void OpenSelected()
        {
            if (_list.SelectedItem is FileItem item)
            {
                _ = _pane.Open(item);
            }
        }

        private void Changed(object? sender, PropertyChangedEventArgs e)
        {
            // Every change arrives as a burst naming each property, and the list is the one worth
            // not redoing twelve times: it is swapped only when the listing itself changed.
            if (e.PropertyName == nameof(DirectoryPane.Items))
            {
                _list.ItemsSource = _pane.Items;

                return;
            }

            if (e.PropertyName == nameof(DirectoryPane.Status))
            {
                Draw();
            }
        }

        /// <summary>Everything but the list, from the pane as it is now.</summary>
        private void Draw()
        {
            _status.Text = _pane.Status;

            // The path box is left alone while somebody is typing in it: overwriting a half-typed
            // path because a listing finished is the box taking the keyboard back from them.
            if (!_path.IsKeyboardFocusWithin)
            {
                _path.Text = _pane.Path;
            }

            _back.IsEnabled = _pane.CanGoBack;
            _forward.IsEnabled = _pane.CanGoForward;
            _up.IsEnabled = _pane.CanGoUp;
            _hidden.IsChecked = _pane.ShowHidden;

            foreach ((SortBy by, GridViewColumnHeader header) in _headers)
            {
                string title = (string)header.Tag;

                header.Content = by == _pane.Sort ? title + (_pane.Descending ? " ▼" : " ▲") : title;
            }
        }
    }
}
