// System.IO is not in a WPF project's implicit usings, and turning WPF on is what made this
// file name it.
using System.Diagnostics;
using System.IO;
using System.Windows;
using Quickshell.App;
using Quickshell.Terminal;

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
    /// <summary>When this process started, for the one line of a crash report that says how long.</summary>
    private static readonly long Started = Stopwatch.GetTimestamp();

    /// <summary>
    /// Opens the window, runs until it closes, and remembers where it was.
    /// </summary>
    /// <param name="arguments">
    /// What the command line asked for. This client has no menu on purpose, so the command line is
    /// a real surface here rather than a convenience — the same standing as a keybinding, and the
    /// only one a script or another program can reach.
    /// </param>
    [STAThread]
    public static int Main(string[] arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        Application application = new() { ShutdownMode = ShutdownMode.OnMainWindowClose };
        MainWindow? window = null;
        PaneAttachment? terminal = null;

        // Armed before the window, because a failure while building one is a failure the user would
        // otherwise see as nothing happening at all. It reads no file and opens nothing, so it does
        // not spend the cold start this order exists to protect.
        using CrashGuard guard = CrashGuard.Arm(application, () => Doing(window, terminal));

        window = new MainWindow();

        window.Show();

        // Only now, with something on screen. Both of these are corrections to a window that is
        // already up: the settings file decides the theme, and neither read is on the way to the
        // first paint.
        Settings settings = SettingsFile.ReadFrom(Locations.Current.Settings);

        window.Apply(settings);
        window.PlaceAt(WindowPlacements.ReadFrom(Placements()).For(Screens()));

        // The terminal itself, and it is deliberately the last thing: opening a device, compiling
        // two shaders and rasterising a font are the most expensive things this process does, and
        // none of them is between the user and their first sight of the window.
        //
        // The size is a placeholder for one layout pass. The pane decides the real grid, and the
        // model is resized to it before a frame is drawn.
        Emulator emulator = new(80, 25, settings.Scrollback);
        TerminalPane pane = new() { Reading = emulator.Buffer };

        terminal = TerminalView.Attach(pane, emulator, new DamageSignal(),
                                       settings.FontFamily, (float)settings.FontSize);

        // Before the pane is shown, and the order is the whole of it: WPF builds an element's
        // automation peer once and keeps it, so a pane shown without a buffer publishes a terminal
        // with no text in it for the life of the window.
        window.Show(pane);

        // `--import` opens what Ctrl+Shift+I opens, and after the window is up rather than before:
        // the preview is a dialog over a window, and a modal with nothing behind it is a client that
        // looks like it failed to start. It still writes nothing until the answer is yes.
        if (arguments.Contains("--import", StringComparer.Ordinal))
        {
            window.Dispatcher.BeginInvoke(() => window.ImportSessions());
        }

        application.Run(window);

        // The loop is stopped before the window's position is written, so nothing is drawing into a
        // handle that is on its way out.
        terminal.Dispose();

        WindowPlacements.ReadFrom(Placements()).Remember(Screens(), window.Where());

        return 0;
    }

    /// <summary>
    /// What the client was doing, for a report written after it stopped doing it.
    ///
    /// <para>Every field here is one this composing layer can actually answer today. The adapter is
    /// one of them now: QS116 gave the pane a device, so a report from a machine that quietly fell
    /// back to WARP says so — which was the whole reason <c>AdapterChoice</c> carries what it
    /// skipped. Before the pane is laid out there is still no device, and the report says that
    /// instead of naming one.</para>
    /// </summary>
    private static CrashContext Doing(MainWindow? window, PaneAttachment? terminal) =>
        new(CrashContext.Build(),
            Environment.OSVersion.VersionString,
            terminal?.View?.Device.Adapter.ToString() ?? "no device is held at this level",
            0,
            // Before the window exists there is nothing open, which is itself worth knowing: it says
            // the client stopped on the way up.
            window?.Tabs ?? 0,
            Stopwatch.GetElapsedTime(Started),
            SessionLogs());

    /// <summary>
    /// The session log's files, newest last, so the report can carry the end of one.
    ///
    /// <para>Read off the folder rather than from a live <c>SessionLog</c>, because nothing at this
    /// level owns one yet — QS129 is where a session gets a log at all. Until then this finds
    /// nothing and the report says so, which is the truth about this build.</para>
    /// </summary>
    private static IReadOnlyList<string> SessionLogs()
    {
        try
        {
            string folder = Locations.Current.Logs;

            return Directory.Exists(folder)
                ? [.. Directory.EnumerateFiles(folder, "*.log")
                               .OrderBy(file => file, StringComparer.Ordinal)]
                : [];
        }
        catch (Exception)
        {
            return [];
        }
    }

    /// <summary>The screens as WPF sees them, in the facts a placement is checked against.</summary>
    private static IEnumerable<Screen> Screens() =>
    [
        new((int)SystemParameters.VirtualScreenLeft, (int)SystemParameters.VirtualScreenTop,
            (int)SystemParameters.VirtualScreenWidth, (int)SystemParameters.VirtualScreenHeight),
    ];

    /// <summary>Where the window's position is remembered, beside the user's other settings.</summary>
    private static string Placements() => Locations.Current.Windows;
}
