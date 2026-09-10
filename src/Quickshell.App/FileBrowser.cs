using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;

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
/// in it is never one the terminal owed a program.</para>
///
/// <para><b>The listing is the panes' business</b>, and <see cref="DirectoryPane"/> is where the
/// design's falsification is kept: this window only draws what a pane says and tells it what was
/// clicked.</para>
/// </summary>
public sealed class FileBrowser : Window
{
    /// <summary>
    /// What the right-hand side says when the tab has no remote side, which is true of every tab
    /// running a local shell.
    /// </summary>
    public const string NoRemote = "This tab is a local shell, so there is no remote side to list.";

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

        Grid layout = new() { Margin = new Thickness(8) };

        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        FrameworkElement left = new PaneView(Local);
        FrameworkElement right = Remote is null ? Absent() : new PaneView(Remote);

        Grid.SetColumn(left, 0);
        Grid.SetColumn(right, 2);

        layout.Children.Add(left);
        layout.Children.Add(right);

        Content = layout;

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

    /// <summary>Hands a pane's work to this window's thread, which is the only one that may draw it.</summary>
    private void Post(Action work) => Dispatcher.BeginInvoke(work);

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
    /// there is no second copy of it to fall out of step.</para>
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

        private readonly ListView _list = new();
        private readonly TextBlock _status = new() { Margin = new Thickness(2, 6, 2, 0) };
        private readonly Dictionary<SortBy, GridViewColumnHeader> _headers = [];

        public PaneView(DirectoryPane pane)
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

            _path.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter && _path.Text.Trim() is { Length: > 0 } typed)
                {
                    _ = _pane.Go(typed);
                    e.Handled = true;
                }
            };

            _list.MouseDoubleClick += (_, _) => OpenSelected();

            // Enter opens and Backspace goes up, which is what a keyboard does in every file list
            // on this platform; Alt with an arrow walks the history, as it does in a browser.
            _list.PreviewKeyDown += (_, e) =>
            {
                Key key = e.Key == Key.System ? e.SystemKey : e.Key;
                bool alt = (Keyboard.Modifiers & ModifierKeys.Alt) != 0;

                e.Handled = (key, alt) switch
                {
                    (Key.Enter, false) => Run(OpenSelected),
                    (Key.Back, false) or (Key.Up, true) => Run(() => _ = _pane.Up()),
                    (Key.Left, true) => Run(() => _ = _pane.Back()),
                    (Key.Right, true) => Run(() => _ = _pane.Forward()),
                    (Key.F5, false) => Run(() => _ = _pane.Refresh()),
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
