using System.IO;
using System.Windows;

using Xunit;

namespace Quickshell.App.Tests;

/// <summary>
/// QS257: full screen is the window borderless at its monitor's exact bounds in pixels, with the client's
/// own chrome collapsed, and leaving it puts back the window that entered it.
/// </summary>
public sealed class FullScreenTests : IDisposable
{
    private static readonly TerminalShare Shared = new();

    private readonly string _here = Path.Combine(Path.GetTempPath(), "qs-full-" + Guid.NewGuid().ToString("N"));

    public FullScreenTests() => Directory.CreateDirectory(_here);

    public void Dispose() => Directory.Delete(_here, recursive: true);

    /// <summary>
    /// The falsification's precondition: a chain can only cover the monitor if the window does, to the
    /// pixel, with no frame and nothing of the client's beside the pane.
    /// </summary>
    [Fact]
    public void FullScreenCoversTheMonitorToThePixelWithNoFrameOrChrome()
    {
        (Int32Rect window, Int32Rect monitor, WindowStyle style, Visibility sidebar, bool full) = Sta.Run(() =>
        {
            MainWindow shown = Shown();

            shown.Add(TerminalTab.Open(Settings.Default, Shared, "first"));
            shown.Add(TerminalTab.Open(Settings.Default, Shared, "second"));
            shown.ToggleFullScreen();

            (Int32Rect, Int32Rect, WindowStyle, Visibility, bool) read =
                (shown.PixelBounds(), shown.MonitorBounds(), shown.WindowStyle, shown.Sidebar.Visibility, shown.FullScreen);

            shown.Close();

            return read;
        });

        Assert.True(full);
        Assert.Equal(monitor, window);
        Assert.Equal(WindowStyle.None, style);
        Assert.Equal(Visibility.Collapsed, sidebar);
    }

    /// <summary>Leaving puts back the frame and the rectangle, and a window closed full screen is remembered as it was before.</summary>
    [Fact]
    public void LeavingPutsTheWindowBackAndClosingFullScreenRemembersTheWindowBefore()
    {
        // Where Windows put it, which is not always where it was asked: a window placed far off the desk
        // is clamped to the coordinates the system allows.
        (Rect before, Placement during, WindowStyle style, Rect after, bool full) = Sta.Run(() =>
        {
            MainWindow shown = Shown();

            shown.Add(TerminalTab.Open(Settings.Default, Shared, "local"));

            Rect was = new(shown.Left, shown.Top, shown.Width, shown.Height);

            shown.ToggleFullScreen();

            Placement remembered = shown.Where();

            shown.ToggleFullScreen();

            (Rect, Placement, WindowStyle, Rect, bool) read =
                (was, remembered, shown.WindowStyle, new Rect(shown.Left, shown.Top, shown.Width, shown.Height), shown.FullScreen);

            shown.Close();

            return read;
        });

        Assert.False(full);
        Assert.Equal(new Placement((int)before.Left, (int)before.Top, (int)before.Width, (int)before.Height, false), during);
        Assert.Equal(WindowStyle.SingleBorderWindow, style);
        Assert.Equal(before, after);
    }

    /// <summary>The palette names the mode for what choosing it will do now.</summary>
    [Fact]
    public void ThePaletteOffersFullScreenAndThenLeavingIt()
    {
        (bool before, bool during) = Sta.Run(() =>
        {
            MainWindow shown = Shown();

            shown.Add(TerminalTab.Open(Settings.Default, Shared, "local"));

            bool offered = shown.Actions.Any(action => action.Name == "Full screen");

            shown.ToggleFullScreen();

            bool leaving = shown.Actions.Any(action => action.Name == "Leave full screen");

            shown.Close();

            return (offered, leaving);
        });

        Assert.True(before);
        Assert.True(during);
    }

    /// <summary>A window shown off the desk and without the focus, as the other window tests show one.</summary>
    private MainWindow Shown()
    {
        MainWindow window = new()
        {
            SessionsFile = Path.Combine(_here, "sessions.json"),
            SshConfigFile = Path.Combine(_here, "none"),
            SettingsPath = Path.Combine(_here, "settings.json"),
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -20000,
            Top = -20000,
            Width = 1000,
            Height = 700,
            ShowActivated = false,
        };

        window.Show();

        return window;
    }
}
