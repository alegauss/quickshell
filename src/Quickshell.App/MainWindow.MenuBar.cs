using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;

namespace Quickshell.App;

/// <summary>
/// The menu bar (QS354): every action the palette has, under headings, with its keys, for a person who
/// has not learned the keys or the palette. A default window shows it under the title bar (QS366); the
/// <c>menuBar</c> setting can leave it to an Alt tap, the Windows way, or keep it away.
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>
    /// The bar, made the first time it is shown and not before. A start's settings are applied after the
    /// window is shown but before its first pane draws, so a start places it at idle (QS373) and not then.
    /// </summary>
    private Menu? _menuBar;

    /// <summary>Whether a start has asked for the bar at idle, which any placement asked for before then waits for.</summary>
    private bool _menuBarAtIdle;

    private DockPanel? _menuDock;

    /// <summary>Whether Alt went down with nothing else pressed since, which is what makes its release a tap.</summary>
    private bool _altAlone;

    /// <summary>Whether the menu bar is on screen.</summary>
    public bool MenuBarShowing => _menuBar is { Visibility: Visibility.Visible };

    /// <summary>The bar itself, for a test that reads what it lists; made if it was not yet.</summary>
    public Menu MenuBarMenu => Bar();

    /// <summary>The bar, made and docked above the layout the first time it is asked for.</summary>
    private Menu Bar()
    {
        if (_menuBar is { } made)
        {
            return made;
        }

        Menu bar = new() { IsMainMenu = false, Visibility = Visibility.Collapsed };

        AutomationProperties.SetAutomationId(bar, "MenuBar");
        AutomationProperties.SetName(bar, "Menu bar");
        DockPanel.SetDock(bar, Dock.Top);

        // Shown by Alt, it goes once the keyboard leaves it: an item chosen, a click elsewhere.
        bar.IsKeyboardFocusWithinChanged += (_, _) =>
        {
            if (!bar.IsKeyboardFocusWithin && Settings.MenuBar == MenuBarMode.Alt
                && !bar.Items.OfType<MenuItem>().Any(heading => heading.IsSubmenuOpen))
            {
                HideMenuBar();
            }
        };

        _menuDock!.Children.Insert(0, bar);
        _menuBar = bar;

        return bar;
    }

    /// <summary>The window's content with the bar docked above it, and the keys that show and hide it.</summary>
    private DockPanel WithMenuBar(UIElement layout)
    {
        DockPanel around = new() { LastChildFill = true };

        around.Children.Add(layout);
        _menuDock = around;

        PreviewKeyDown += (_, pressed) =>
        {
            bool alt = pressed.Key == Key.System && pressed.SystemKey is Key.LeftAlt or Key.RightAlt;

            _altAlone = alt && !pressed.IsRepeat || (alt && _altAlone);

            // Esc at the bar's top level, with no heading open, puts it away and the keyboard back.
            if (pressed.Key == Key.Escape && _menuBar is { IsKeyboardFocusWithin: true } bar
                && !bar.Items.OfType<MenuItem>().Any(heading => heading.IsSubmenuOpen))
            {
                HideMenuBar();
                pressed.Handled = true;
            }
        };

        PreviewKeyUp += (_, released) =>
        {
            if (released.Key == Key.System && released.SystemKey is Key.LeftAlt or Key.RightAlt && _altAlone)
            {
                _altAlone = false;

                if (Settings.MenuBar != MenuBarMode.Hidden && ToggleMenuBar())
                {
                    released.Handled = true;
                }
            }
        };

        return around;
    }

    /// <summary>
    /// Shows the bar with the keyboard on its first heading, or puts it away where it was showing by Alt.
    /// </summary>
    /// <returns>Whether anything changed.</returns>
    public bool ToggleMenuBar()
    {
        if (MenuBarShowing)
        {
            if (_menuBar!.IsKeyboardFocusWithin && Settings.MenuBar == MenuBarMode.Always)
            {
                Focus();

                return true;
            }

            if (Settings.MenuBar == MenuBarMode.Always)
            {
                FocusMenuBar();

                return true;
            }

            HideMenuBar();

            return true;
        }

        ShowMenuBar();
        FocusMenuBar();

        return true;
    }

    /// <summary>Builds the bar afresh from the bindings, so names, keys and what can run are as they are now, and shows it.</summary>
    public void ShowMenuBar()
    {
        Menu bar = Bar();

        bar.Items.Clear();

        foreach ((string heading, IReadOnlyList<Command> actions) in MenuBar.Grouped(Commands.From(InputBindings.OfType<InputBinding>(), runnable: false)))
        {
            string plain = heading.Replace("_", string.Empty, StringComparison.Ordinal);
            MenuItem top = new() { Header = heading };

            AutomationProperties.SetAutomationId(top, "Menu" + plain.Replace(" ", string.Empty, StringComparison.Ordinal));
            AutomationProperties.SetName(top, plain);

            foreach (Command action in actions)
            {
                // An underscore in a name is a character, not an access key.
                MenuItem item = new()
                {
                    Header = action.Name.Replace("_", "__", StringComparison.Ordinal),
                    InputGestureText = action.Chord ?? string.Empty,
                    Command = action.Runs,
                };

                AutomationProperties.SetName(item, action.Name);
                top.Items.Add(item);
            }

            bar.Items.Add(top);
        }

        bar.Visibility = Visibility.Visible;
    }

    /// <summary>Puts the bar away, unless the setting keeps it up, and gives the keyboard back to the window.</summary>
    public void HideMenuBar()
    {
        if (_menuBar is not { } bar)
        {
            return;
        }

        bool had = bar.IsKeyboardFocusWithin;

        if (Settings.MenuBar != MenuBarMode.Always)
        {
            bar.Visibility = Visibility.Collapsed;
        }

        if (had)
        {
            Focus();
        }
    }

    /// <summary>
    /// The bar placed once the window is idle, after its first pane, not on the way to it (QS373): a shell
    /// start applies the settings twice before its first frame, and each rebuilt the bar from every
    /// binding, which was 35 ms of a 546 ms start in the guest.
    /// </summary>
    private void PlaceMenuBarAtIdle()
    {
        if (_menuBarAtIdle)
        {
            return;
        }

        _menuBarAtIdle = true;

        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, () =>
        {
            _menuBarAtIdle = false;
            PlaceMenuBar();
        });
    }

    /// <summary>The bar as the setting says it should be: up for Always, away otherwise.</summary>
    private void PlaceMenuBar()
    {
        if (_menuBarAtIdle)
        {
            return;
        }

        if (Settings.MenuBar == MenuBarMode.Always)
        {
            ShowMenuBar();
        }
        else if (_menuBar is { IsKeyboardFocusWithin: false } bar)
        {
            bar.Visibility = Visibility.Collapsed;
        }
    }

    private void FocusMenuBar() =>
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, () =>
        {
            if (_menuBar is { Items.Count: > 0 } bar && bar.Items[0] is MenuItem first)
            {
                first.Focus();
            }
        });

    /// <summary>The palette: keep the menu bar on screen, or let Alt show it again (QS354).</summary>
    private sealed class KeepingMenuBar(MainWindow window) : Doing(window)
    {
        /// <inheritdoc/>
        public override string Name => Window.Settings.MenuBar == MenuBarMode.Always ? "Let Alt show the menu bar" : "Keep the menu bar on screen";

        /// <inheritdoc/>
        public override void Execute(object? parameter) => Window.KeepMenuBar(Window.Settings.MenuBar != MenuBarMode.Always);
    }

    /// <summary>Keeps the bar on screen for good, or lets Alt show it again, and writes that to the settings file.</summary>
    public void KeepMenuBar(bool always)
    {
        Settings = Settings with { MenuBar = always ? MenuBarMode.Always : MenuBarMode.Alt };

        string path = SettingsPath ?? Locations.Current.Settings;

        if (SettingsFile.Readable(path))
        {
            SettingsFile.WriteTo(path, SettingsFile.ReadFrom(path) with { MenuBar = Settings.MenuBar });
        }

        PlaceMenuBar();
    }
}
