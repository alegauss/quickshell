// System.IO is not in a WPF project's implicit usings, and turning WPF on is what made this
// file name it.
using System.IO;
using System.Windows;
using Quickshell.App;

namespace Quickshell.App;

/// <summary>
/// The entry point, and the order it does things in is the point of it.
///
/// <para><b>The window is built and shown before anything else happens</b>, and everything it needs
/// to do that is already in memory: no configuration is parsed, no file is read and no connection is
/// opened before the first paint. Cold start is a number this project publishes, and the half a user
/// feels is the wait before anything appears at all.</para>
///
/// <para>What is read afterwards is read because it can be. A remembered window position is a
/// correction to a window that is already up, not a precondition for putting one up — so a slow disk
/// costs a window that moves once, rather than a window that is late.</para>
/// </summary>
public static class Entry
{
    /// <summary>Opens the window, runs until it closes, and remembers where it was.</summary>
    [STAThread]
    public static int Main()
    {
        Application application = new() { ShutdownMode = ShutdownMode.OnMainWindowClose };
        MainWindow window = new();

        window.Show();

        // Only now, with something on screen.
        window.PlaceAt(WindowPlacements.ReadFrom(Placements()).For(Screens()));

        application.Run(window);

        WindowPlacements.ReadFrom(Placements()).Remember(Screens(), window.Where());

        return 0;
    }

    /// <summary>The screens as WPF sees them, in the facts a placement is checked against.</summary>
    private static IEnumerable<Screen> Screens() =>
    [
        new((int)SystemParameters.VirtualScreenLeft, (int)SystemParameters.VirtualScreenTop,
            (int)SystemParameters.VirtualScreenWidth, (int)SystemParameters.VirtualScreenHeight),
    ];

    /// <summary>Where the window's position is remembered, beside the user's other settings.</summary>
    private static string Placements() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                     "quickshell", "windows.json");
}
