// System.IO is not in a WPF project's implicit usings, and turning WPF on is what made this
// file name it.
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
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

        // Every surface this window has, pointed at whichever tab is on screen rather than at one
        // that was current when the window was built. Asked afresh each time for exactly that
        // reason: switching tabs is the only place a client with tabs goes wrong quietly, and it
        // goes wrong by answering for the tab before.
        window.Opens = () => Opened(window, settings);

        // Not awaited: the tab is already out of the window and nothing references it, and a shell
        // given its two seconds to leave is two seconds this thread would spend not repainting.
        window.Ends = tab => _ = tab.DisposeAsync().AsTask();

        window.Input.Placing = composing => Placed(window, composing);
        window.Selected = () => window.Current?.Terminal.Selected() ?? string.Empty;
        window.Bracketed = () => window.Current?.Emulator.BracketedPaste ?? false;
        window.Scrolling = lines => window.Current?.Terminal.ScrollBy(lines);
        window.Finding = (needle, forward, exactly) =>
            window.Current?.Terminal.Find(needle, forward, exactly)?.Cells;

        // The terminal itself, and it is deliberately the last thing: opening a device, compiling
        // two shaders and rasterising a font are the most expensive things this process does, and
        // none of them is between the user and their first sight of the window.
        Opened(window, settings);

        terminal = window.Current?.Terminal;

        // `--tabs <n>` opens that many, which is what Ctrl+Shift+T opens n times. A real surface and
        // not a test hook, for the same reason `--import` is one: this client has no menu, so the
        // command line is the only way another program can ask it for anything — and it is what
        // lets a UI case read a strip that a chord cannot yet be spelled to open.
        if (Asked(arguments, "--tabs") is { } more)
        {
            for (int tab = 1; tab < Math.Clamp(more, 1, 16); tab++)
            {
                Opened(window, settings);
            }
        }

        // `--import` opens what Ctrl+Shift+I opens, and after the window is up rather than before:
        // the preview is a dialog over a window, and a modal with nothing behind it is a client that
        // looks like it failed to start. It still writes nothing until the answer is yes.
        if (arguments.Contains("--import", StringComparer.Ordinal))
        {
            window.Dispatcher.BeginInvoke(() => window.ImportSessions());
        }

        application.Run(window);

        // Every tab, and the loops before the sessions, so nothing is drawing into a handle that is
        // on its way out. Waited for here and only here: the process is leaving, and a pseudo-console
        // still holding a child is a shell that outlives the window that opened it.
        foreach (TerminalTab tab in window.Held.ToArray())
        {
            Close(tab);
        }

        WindowPlacements.ReadFrom(Placements()).Remember(Screens(), window.Where());

        return 0;
    }

    /// <summary>
    /// Where the candidate list goes for the composition being typed, in the window's own pixels.
    ///
    /// <para>Three things are added up and none of them is a constant. The cursor's cell, from the
    /// model. The composition's own width in cells, which <see cref="Composition.Candidate"/> counts
    /// rather than guessing from a character count. And the pane's origin inside the window, because
    /// the tab strip sits above it — a position measured from the window's corner would put the
    /// candidate list a strip's height too high the moment a second tab opens.</para>
    ///
    /// <para>Null before the pane has a device: there is no cell size then, and a position invented
    /// without one is a number this client made up.</para>
    /// </summary>
    private static CandidateSpot? Placed(MainWindow window, Composition composing)
    {
        if (window.Current is not { } tab || tab.Terminal.View is not { } view)
        {
            return null;
        }

        TerminalPane pane = tab.Pane;
        Damage where = tab.Emulator.Damage;

        CandidatePlacement at = composing.Candidate(where.CursorColumn, where.CursorRow,
                                                    Math.Max(1, view.Columns));

        // The pane's corner in the window's pixels. WPF measures in device-independent units and an
        // input method is told device pixels, so the scale is the pane's own rather than a constant:
        // this client runs on displays that are not all 96 dots per inch, often at the same time.
        Point corner = pane.TranslatePoint(new Point(0, 0), window);
        DpiScale dpi = VisualTreeHelper.GetDpi(pane);

        return InputMethod.SpotFor(at, view.Renderer.Metrics,
                                   (int)(corner.X * dpi.DpiScaleX),
                                   (int)(corner.Y * dpi.DpiScaleY));
    }

    /// <summary>
    /// Opens a tab, puts it on screen, and starts a shell behind it.
    ///
    /// <para><b>The shell is started afterwards and not awaited</b>, which is why the tab appears at
    /// once. Creating a pseudo-console and a process is not on the way to a user seeing the terminal
    /// they asked for, and this is the thread the window is drawn on.</para>
    ///
    /// <para>The tab is registered as an open session in the same breath, because the window's
    /// closing question names what is open and a tab is what "open" now means.</para>
    /// </summary>
    private static void Opened(MainWindow window, Settings settings)
    {
        string host = Path.GetFileName(LocalSession.Shell);
        TerminalTab tab = TerminalTab.Open(settings, host);

        window.Add(tab);
        window.Sessions.Open(host, another: true);

        // A paste goes down the keystroke path and not the parser's, which is what makes it arrive
        // in order with what the user is typing around it — and it goes to whichever tab is on
        // screen when the paste happens, never to the one that was current when it was wired.
        window.Pasting = text => window.Current is { Typist: { } typist }
            ? Sent(typist, text)
            : ValueTask.CompletedTask;

        _ = tab.ConnectAsync();
    }

    /// <summary>
    /// The number a flag was given, or null where it was absent or not a number.
    ///
    /// <para>Not an error either way. A command line is a surface a person types, and a client that
    /// refused to start over a mistyped count would be one they never reach at all.</para>
    /// </summary>
    private static int? Asked(string[] arguments, string flag)
    {
        int at = Array.IndexOf(arguments, flag);

        return at >= 0 && at + 1 < arguments.Length
               && int.TryParse(arguments[at + 1], System.Globalization.NumberStyles.None,
                               System.Globalization.CultureInfo.InvariantCulture, out int how)
            ? how
            : null;
    }

    /// <summary>A paste, down the same route a keystroke takes.</summary>
    private static ValueTask Sent(Typist typist, string text)
    {
        typist.Type(text, System.Windows.Input.ModifierKeys.None);

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Ends one tab, and waits for it.
    ///
    /// <para>Blocking, on the way out rather than on the way in: a pseudo-console still holding a
    /// child is a shell that outlives the window that opened it.</para>
    /// </summary>
    private static void Close(TerminalTab tab)
    {
        try
        {
            tab.DisposeAsync().AsTask().GetAwaiter().GetResult();
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
