// System.IO is not in a WPF project's implicit usings, and turning WPF on is what made this
// file name it.
using System.Diagnostics;
using System.IO;
using System.Text;
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

        // Only now, with something on screen. All of these are corrections to a window that is
        // already up: the settings file decides the theme, and no read here is on the way to the
        // first paint. The guard is one of them — it is a file whose presence says this user asked
        // not to be warned, and it is needed when the window closes rather than when it opens.
        Settings settings = SettingsFile.ReadFrom(Locations.Current.Settings);

        window.Guard = CloseGuard.ReadFrom(Locations.Current.CloseSilently);

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

        // One signal for the loop and for the session behind it. The loop is opened here and the
        // session several statements later, so the thing they share is created before either.
        DamageSignal damage = new();

        terminal = TerminalView.Attach(pane, emulator, damage,
                                       settings.FontFamily, (float)settings.FontSize);

        // Keys go to the model, which knows what its modes make them mean. Where they go after that
        // is the session's, and Opening below is where it is told.
        Typist typist = new(emulator);

        window.Typing = typist;

        // Before the pane is shown, and the order is the whole of it: WPF builds an element's
        // automation peer once and keeps it, so a pane shown without a buffer publishes a terminal
        // with no text in it for the life of the window.
        window.Show(pane);

        // The shell, and it is the last thing for the same reason the pane was: creating a
        // pseudo-console and starting a process are not on the way to the user's first sight of the
        // window. Not awaited here — this is the thread the window is drawn on.
        Task<LocalSession?> opening = Opening(window, emulator, damage, terminal, typist);

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

        Close(opening);

        WindowPlacements.ReadFrom(Placements()).Remember(Screens(), window.Where());

        return 0;
    }

    /// <summary>
    /// Opens the shell and joins it to the window, or says on the terminal why it could not.
    ///
    /// <para><b>Nothing here is on the way to the first frame</b>, which is why it is a task the
    /// caller does not await. The pane already has a device, a loop and a keyboard by the time this
    /// runs; what it gains is somebody to talk to.</para>
    /// </summary>
    private static async Task<LocalSession?> Opening(MainWindow window, Emulator emulator,
                                                     DamageSignal damage, PaneAttachment terminal,
                                                     Typist typist)
    {
        try
        {
            LocalSession session = await LocalSession
                .OpenAsync(emulator, damage, emulator.Buffer.Columns, emulator.Buffer.Rows)
                .ConfigureAwait(false);

            typist.Sending = bytes => session.Pipeline.TypeAsync(bytes);
            terminal.Resized = session.Pipeline.Resize;

            // The program's name and not its path, because this string is read back to the user in
            // the closing question and nowhere else. Registered once the shell is actually running:
            // a window asking about a session that failed to start would be asking about nothing.
            window.Dispatcher.Invoke(
                () => window.Sessions.Open(Path.GetFileName(LocalSession.Shell)));

            // The grid the pane settled on while this was starting, which arrived when there was no
            // session to hear it. Sent once rather than assumed: a program wrong about its own width
            // draws a screen for a terminal nobody has.
            session.Pipeline.Resize(emulator.Buffer.Columns, emulator.Buffer.Rows);

            return session;
        }
        catch (Exception failed)
        {
            Refused(emulator, damage, failed);

            return null;
        }
    }

    /// <summary>
    /// Says on the terminal that the shell did not start, and what Windows said about it.
    ///
    /// <para>Written into the model, because that is where the user is already looking and there is
    /// no session to have carried it anywhere else. It is safe to write from here for the one reason
    /// that matters: the pipeline never started, so this is the only writer the render loop has.</para>
    /// </summary>
    private static void Refused(Emulator emulator, DamageSignal damage, Exception failed)
    {
        emulator.Feed(Encoding.UTF8.GetBytes(
            $"quickshell could not start {LocalSession.Shell}\r\n{failed.Message}\r\n"));

        damage.Set();
    }

    /// <summary>
    /// Ends the session, and waits for it.
    ///
    /// <para>Blocking, on the way out rather than on the way in: a pseudo-console still holding a
    /// child is a shell that outlives the window that opened it.</para>
    /// </summary>
    private static void Close(Task<LocalSession?> opening)
    {
        try
        {
            opening.GetAwaiter().GetResult()?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        catch (Exception)
        {
            // A session that would not close is not a reason to fail on the way out, and the next
            // thing to happen is this process ending — which Windows closes the handles for.
        }
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
