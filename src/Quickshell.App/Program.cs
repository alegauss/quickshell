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

        // `--install` and `--uninstall` are this copy putting itself somewhere or taking itself away,
        // and neither opens a window: the list of installed apps runs the second, and a deployment
        // runs the first with nobody watching.
        if (Setup.Asked(arguments))
        {
            return Setup.Run(arguments);
        }

        // `--startup-report <file>` times this start and writes the milestones there once the shell
        // is on screen — QS75's instrument, and first so that it sees everything after the runtime.
        if (Given(arguments, CommandLine.StartupReport) is { } report)
        {
            StartupTimeline.Arm(report);
            StartupTimeline.Mark("main");
        }

        Application application = new() { ShutdownMode = ShutdownMode.OnMainWindowClose };

        StartupTimeline.Mark("application");

        MainWindow? window = null;

        // One device, one atlas, one set of shaders and one render thread for every pane this
        // process opens — QS49. It holds nothing yet: the device is opened by the first pane that
        // has a handle, because which adapter to use is decided by the window the output goes to.
        using TerminalShare share = new();

        // Armed before the window, because a failure while building one is a failure the user would
        // otherwise see as nothing happening at all. It reads no file and opens nothing, so it does
        // not spend the cold start this order exists to protect.
        using CrashGuard guard = CrashGuard.Arm(application, () => Doing(window, share));

        window = new MainWindow();

        StartupTimeline.Mark("constructed");

        window.Show();

        // The window exists and is on the desk, which is the half of a start a user sees first.
        StartupTimeline.Mark("shown");

        // Only now, with something on screen. All of these are corrections to a window that is
        // already up: the settings file decides the theme, and no read here is on the way to the
        // first paint. The guard is one of them — it is a file whose presence says this user asked
        // not to be warned, and it is needed when the window closes rather than when it opens.
        Settings settings = SettingsFile.ReadFrom(Locations.Current.Settings);

        window.Guard = CloseGuard.ReadFrom(Locations.Current.CloseSilently);

        window.Apply(settings);
        window.PlaceAt(WindowPlacements.ReadFrom(Placements()).For(Screens()));

        // And every time the file changes after this. The read happens on a thread pool thread, so
        // what it found is handed to the window's own — every pane it touches is WPF's.
        using SettingsWatch watching = SettingsWatch.On(
            Locations.Current.Settings,
            read => window.Dispatcher.BeginInvoke(() => window.Apply(read)));

        window.Reloads = () => watching.Reload();

        // Every surface this window has, pointed at whichever pane has the keyboard rather than at
        // one that was current when the window was built. Asked afresh each time for exactly that
        // reason: switching tabs and moving between panes are the two places a client like this goes
        // wrong quietly, and it goes wrong by answering for the one before.
        // The window's own copy and not the one read at start-up, so a tab opened after the
        // settings changed opens at what they are now rather than at what they were.
        window.Opens = () => Opened(window, window.Settings, share);
        window.Connects = leaf => _ = leaf.ConnectAsync();
        window.OpensSession = path => OpenedSession(window, window.Settings, share, path, trace: false);

        // Only a copy that is not the installed one offers to install itself: the installed copy
        // installing itself would be a copy of a folder onto the same folder.
        if (Installation.Of(AppContext.BaseDirectory) is null)
        {
            window.Installs = () => _ = Setup.InstallForUserAsync(window);
        }

        // Not awaited: what is being ended is already out of the window and nothing references it,
        // and a shell given its two seconds to leave is two seconds this thread would spend not
        // repainting.
        window.Ends = tab => _ = tab.DisposeAsync().AsTask();
        window.EndsPane = leaf => _ = leaf.DisposeAsync().AsTask();

        window.Input.Placing = composing => Placed(window, composing);

        // The composition is drawn by the pane with the keyboard, into its own grid (QS153): handed
        // to that pane's view while it is active and taken back when it ends, and the frame forgotten
        // either way, since nothing the host sent has changed.
        window.Input.Changed += composing =>
        {
            if (Pane(window)?.Terminal.View is { } view)
            {
                view.Composing = composing.IsActive ? composing : null;
                view.Moved();
                share.Damage.Set();
            }
        };
        window.Selected = () => Pane(window)?.Terminal.Selected() ?? string.Empty;
        window.Scrolling = lines => Pane(window)?.Terminal.ScrollBy(lines);
        window.Finding = (needle, forward, exactly) =>
            Pane(window)?.Terminal.Find(needle, forward, exactly)?.Cells;

        // The terminal itself, and it is deliberately the last thing: opening a device, compiling
        // two shaders and rasterising a font are the most expensive things this process does, and
        // none of them is between the user and their first sight of the window.
        Opened(window, settings, share);

        // `--tabs <n>` opens that many, which is what Ctrl+Shift+T opens n times. A real surface and
        // not a test hook, for the same reason `--import` is one: this client has no menu, so the
        // command line is the only way another program can ask it for anything — and it is what
        // lets a UI case read a strip that a chord cannot yet be spelled to open.
        if (Asked(arguments, CommandLine.Tabs) is { } more)
        {
            for (int tab = 1; tab < Math.Clamp(more, 1, 16); tab++)
            {
                Opened(window, settings, share);
            }
        }

        // `--panes <n>` splits the tab that many ways, which is what Ctrl+Shift+\ does n-1 times.
        // Same standing as `--tabs`, and the same reason: it is how another program asks, and how a
        // UI case reads an arrangement no chord can yet be spelled to make.
        if (Asked(arguments, CommandLine.Panes) is { } across)
        {
            for (int pane = 1; pane < Math.Clamp(across, 1, 16); pane++)
            {
                window.SplitPane(Divide.Beside);
            }
        }

        // `--broadcast` types into every pane of that tab, which is what Ctrl+Shift+B turns on. After
        // the split and not before it, because a split ends broadcasting — and it is asked for on
        // this command line every time, so it is still a mode somebody chose rather than one this
        // client remembered.
        if (arguments.Contains(CommandLine.Broadcast, StringComparer.Ordinal))
        {
            window.Broadcast();
        }

        // `--browse` opens the file browser the palette opens, after the window for the reason
        // `--import` gives below.
        if (arguments.Contains(CommandLine.Browse, StringComparer.Ordinal))
        {
            window.Dispatcher.BeginInvoke(() => window.BrowseFiles());
        }

        // `--import` opens what Ctrl+Shift+I opens, and after the window is up rather than before:
        // the preview is a dialog over a window, and a modal with nothing behind it is a client that
        // looks like it failed to start. It still writes nothing until the answer is yes.
        //
        // `--import <file>` previews that file instead of looking in MobaXterm's usual places: a
        // session file somebody copied off another machine, and a UI case that needs the same
        // preview on every desk rather than whatever this one has installed (QS181).
        int import = Array.IndexOf(arguments, CommandLine.Import);

        if (import >= 0)
        {
            string? from = import + 1 < arguments.Length && !arguments[import + 1].StartsWith("--", StringComparison.Ordinal)
                ? Path.GetFullPath(arguments[import + 1])
                : null;

            window.Dispatcher.BeginInvoke(() => window.ImportSessions(from));
        }

        // `--session <path>` opens a saved session in a tab of its own, by its path in the store
        // (QS126). The same standing as `--tabs`: this client has no menu yet, so the command line is
        // how a shortcut, a script or a UI case asks for one host — and it is what a restart is.
        //
        // `--trace` beside it records that session's negotiation and channels at trace level, in a
        // file of its own under the log folder (QS129). For this run only: a trace somebody turned
        // on once to diagnose one host is not one this client should go on writing.
        if (Given(arguments, CommandLine.Session) is { } saved)
        {
            bool trace = arguments.Contains(CommandLine.Trace, StringComparer.Ordinal);

            window.Dispatcher.BeginInvoke(() => OpenedSession(window, window.Settings, share, saved, trace));
        }

        // `--palette` opens what Ctrl+Shift+P opens, and for the same reason as `--import`: after
        // the window rather than before it, because a modal with nothing behind it looks like a
        // client that failed to start.
        //
        // The chord would be the better route for a case to take and the engine cannot spell it
        // yet — `press` in the pinned winwright takes Tab and the arrows and no modifier chord. It
        // can in the engine's own source, where WW317 shipped; the package this repository restores
        // is older than that, which is QS176.
        if (arguments.Contains(CommandLine.Palette, StringComparer.Ordinal))
        {
            window.Dispatcher.BeginInvoke(() => window.ShowPalette());
        }

        application.Run(window);

        // Every tab, and the loops before the sessions, so nothing is drawing into a handle that is
        // on its way out. Waited for here and only here: the process is leaving, and a pseudo-console
        // still holding a child is a shell that outlives the window that opened it.
        foreach (TerminalTab tab in window.Held.ToArray())
        {
            Close(tab);
        }

        // The logs after the sessions that wrote to them, so their last lines are on disk.
        foreach (Quickshell.Transport.SessionLog trace in Traces)
        {
            trace.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        if (Logged.IsValueCreated)
        {
            Logged.Value.DisposeAsync().AsTask().GetAwaiter().GetResult();
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
        if (Pane(window) is not { } leaf || leaf.Terminal.View is not { } view)
        {
            return null;
        }

        TerminalPane pane = leaf.Pane;
        Damage where = leaf.Emulator.Damage;

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
    private static void Opened(MainWindow window, Settings settings, TerminalShare share)
    {
        string host = Path.GetFileName(LocalSession.Shell);
        TerminalTab tab = TerminalTab.Open(settings, share, host);

        // Everything a pane reads live, applied to the one just opened as well as to the rest.
        window.Apply(settings);

        window.Add(tab);
        window.Sessions.Open(host, another: true);

        _ = tab.ConnectAsync();
    }

    /// <summary>
    /// Opens a saved session in a tab of its own and connects it (QS126).
    ///
    /// <para>The store is read at this moment, so a session made a minute ago — through the dialog or
    /// by hand — is there. Every hop's host key goes through the user's own <c>known_hosts</c>, and a
    /// key nobody has seen is asked about at this window. A path the store does not have is said in
    /// the tab rather than opening nothing.</para>
    /// </summary>
    private static void OpenedSession(MainWindow window, Settings settings, TerminalShare share, string path,
                                      bool trace)
    {
        ResolvedSession? session;

        try
        {
            session = SessionTree.ReadFrom(window.SessionsFile ?? Locations.Current.Sessions).Session(path);
        }
        catch (SessionStoreException unreadable)
        {
            MessageBox.Show(window, $"{unreadable.Message}\n\n{unreadable.Means}", "Sessions",
                            MessageBoxButton.OK, MessageBoxImage.Warning);

            return;
        }

        if (session is null)
        {
            MessageBox.Show(window, $"There is no saved session called {path}.", "Sessions",
                            MessageBoxButton.OK, MessageBoxImage.Information);

            return;
        }

        TerminalTab tab = TerminalTab.Open(settings, share, session.Host);

        window.Apply(settings);
        window.Add(tab);
        window.Sessions.Open(session.Host, another: true);

        Quickshell.Transport.TrustOnFirstUse trust = new(Quickshell.Transport.KnownHosts.ReadFrom(), window.AskHostKey);

        Quickshell.Transport.SessionLog log = trace ? Traced(session.Host) : Logged.Value;

        _ = tab.Focused.ConnectAsync(async (emulator, damage, columns, rows, token) =>
            await RemoteShell.OpenAsync(session, trust, emulator, damage, columns, rows, token, log)
                             .ConfigureAwait(false));
    }

    /// <summary>
    /// The client's own session log, at the ordinary level, in <see cref="Locations.Logs"/> (QS129).
    /// Opened by the first connection and not at start-up, so nothing on the way to the first paint
    /// touches a disk.
    /// </summary>
    private static readonly Lazy<Quickshell.Transport.SessionLog> Logged =
        new(() => Quickshell.Transport.SessionLog.InFolder(Locations.Current.Logs));

    /// <summary>Every trace opened this run, closed when the process leaves.</summary>
    private static readonly List<Quickshell.Transport.SessionLog> Traces = [];

    /// <summary>A trace for one session, in a folder of its own so it is the only thing in it.</summary>
    private static Quickshell.Transport.SessionLog Traced(string host)
    {
        string safe = string.Concat(host.Select(each => Path.GetInvalidFileNameChars().Contains(each) ? '_' : each));

        Quickshell.Transport.SessionLog trace = Quickshell.Transport.SessionLog.InFolder(
            Path.Combine(Locations.Current.Logs, "trace", safe), Quickshell.Transport.LogDetail.Trace);

        Traces.Add(trace);

        return trace;
    }

    /// <summary>
    /// The pane with the keyboard, or null while there is none.
    ///
    /// <para>Two hops rather than one, and both of them matter: the tab on screen, and the pane
    /// inside it that the user is typing into. A surface that stopped at the first would answer for
    /// a session sitting beside the one they are looking at.</para>
    /// </summary>
    private static TerminalLeaf? Pane(MainWindow window) => window.Current?.Focused;

    /// <summary>The path a flag was given, or null where it was absent.</summary>
    private static string? Given(string[] arguments, string flag)
    {
        int at = Array.IndexOf(arguments, flag);

        return at >= 0 && at + 1 < arguments.Length && arguments[at + 1].Length > 0
            ? arguments[at + 1]
            : null;
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
    /// <para>Every field here is one this composing layer can actually answer today. The adapter and
    /// its recoveries come from the share that holds the one device every pane draws with (QS132),
    /// so a report from a machine that quietly fell back to WARP says so — which was the whole
    /// reason <c>AdapterChoice</c> carries what it skipped — and a device-loss report says how many
    /// losses came before it. Before any pane is laid out there is no device, and the report says
    /// that instead of naming one.</para>
    /// </summary>
    private static CrashContext Doing(MainWindow? window, TerminalShare share)
    {
        (string adapter, int recoveries) = share.Describe();

        return new(CrashContext.Build(),
                   Environment.OSVersion.VersionString,
                   adapter,
                   recoveries,
                   // Before the window exists there is nothing open, which is itself worth knowing:
                   // it says the client stopped on the way up.
                   window?.Tabs ?? 0,
                   Stopwatch.GetElapsedTime(Started),
                   SessionLogs());
    }

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
