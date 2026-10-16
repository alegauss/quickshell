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

    /// <summary>The DPI of the primary monitor, which is where a window with nowhere remembered opens.</summary>
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();

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

        // `--mcp` is the agent gateway (QS325): a process of its own, on the pipes an agent's host gave
        // it, with no window, no device and nothing else this method sets up (QS321).
        if (arguments.Contains(CommandLine.Mcp, StringComparer.Ordinal))
        {
            return ServeAgent();
        }

        // `--audit [run]`: a gateway run's log as a change summary, or the runs there are (QS333).
        if (arguments.Contains(CommandLine.Audit, StringComparer.Ordinal))
        {
            return PrintAudit(Given(arguments, CommandLine.Audit));
        }

        // `--agent-hook`: one Claude Code PreToolUse call judged and answered, no window (QS332).
        if (arguments.Contains(CommandLine.AgentHook, StringComparer.Ordinal))
        {
            Console.Out.Write(AgentHook.Answer(Console.In.ReadToEnd()));

            return 0;
        }

        // `--startup-report <file>` times this start and writes the milestones there once the shell
        // is on screen — QS75's instrument, and first so that it sees everything after the runtime.
        if (Given(arguments, CommandLine.StartupReport) is { } report)
        {
            StartupTimeline.Arm(report);
            StartupTimeline.Mark("main");
        }

        // The last window, not the first, ends the process (QS160): a tab detached into a window of
        // its own is as much the user's as the one it came from.
        Application application = new() { ShutdownMode = ShutdownMode.OnLastWindowClose };

        StartupTimeline.Mark("application");

        MainWindow? window = null;

        // One device, one atlas, one set of shaders and one render thread for every pane this
        // process opens — QS49. It holds nothing yet: the device is opened by the first pane that
        // has a handle, because which adapter to use is decided by the window the output goes to.
        using TerminalShare share = new();

        // `--frame-report <file>`: the loop's wakes, frames and host bytes, for the idle tool (QS255).
        if (Given(arguments, CommandLine.FrameReport) is { } frames)
        {
            FrameReport.Arm(frames, share);
        }

        // A driver reset the loop came back from, in the client's own log (QS221): a crash report
        // counts recoveries, and this is the line behind each one.
        share.Recovered = (why, adapter, count) => Logged.Value.DeviceRecovered(why, adapter, count);

        // Opened now, on the pool, while WPF spends its first window on this thread (QS191). The
        // settings are read there too, which is no read on the way to the first paint: this thread
        // does not wait for it, and the first pane checks the guess before it uses any of it.
        share.Prepare(() =>
        {
            Settings guessed = SettingsFile.ReadFrom(Locations.Current.Settings);

            return new Quickshell.Render.FontSettings(guessed.FontFamily, (float)guessed.FontSize, GetDpiForSystem())
            {
                Ligatures = guessed.Ligatures,
            };
        });

        // And the first tab's shell, for the same reason (QS191): a pseudo-console and cmd need no
        // pixel, so they start now, into a model of their own at the placeholder size every pane
        // starts at, and the first tab takes both. The pane's real grid is sent once it is laid out,
        // as it always was; the settings' scrollback depth goes through the shell's own pipeline.
        //
        // Not where the window opens on Home (QS272): a window asked for nothing on its command line reads
        // `startWith` here, before any pseudo-console, and starts no shell at all for Home.
        bool askedForTabs = arguments.Any(argument => argument is CommandLine.Tabs or CommandLine.Panes or CommandLine.Session or CommandLine.Broadcast);
        Emulator early = new(80, 25, Settings.Default.Scrollback);
        Task<LocalSession?> earlyShell = Task.Run<LocalSession?>(async () =>
        {
            if (!askedForTabs && SettingsFile.ReadFrom(Locations.Current.Settings).StartWith == StartWith.Home)
            {
                return null;
            }

            LocalSession started = await LocalSession.OpenAsync(early, share.Damage, 80, 25).ConfigureAwait(false);

            // When it started, which is the milestone's meaning; the leaf marking it later changes nothing.
            StartupTimeline.Mark("shell");

            return started;
        });

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

        window.Apply(settings);
        window.PlaceAt(WindowPlacements.ReadFrom(Placements()).For(Screens()));

        // Each step between the window on the desk and its first pane, which is where a start that
        // got slower spent it (QS288).
        StartupTimeline.Mark("applied");

        // And every time the file changes after this, in every window there is. The read happens on
        // a thread pool thread, so what it found is handed to each window's own - every pane it
        // touches is WPF's.
        using SettingsWatch watching = SettingsWatch.On(
            Locations.Current.Settings,
            read =>
            {
                MainWindow[] every;

                lock (Windows)
                {
                    every = [.. Windows];
                }

                foreach (MainWindow each in every)
                {
                    each.Dispatcher.BeginInvoke(() => each.Apply(read));
                }
            });

        Wire(window, share, watching);

        StartupTimeline.Mark("wired");

        // The terminal itself, and it is deliberately the last thing: opening a device, compiling
        // two shaders and rasterising a font are the most expensive things this process does, and
        // none of them is between the user and their first sight of the window. The first tab is the
        // one whose shell started with the process.
        //
        // Or Home, with no tab and no shell (QS272), where the settings say so and the command line
        // asked for nothing else. The early task decided the same from the same file; where the file
        // changed between the two reads, a shell it started is ended and Home wins.
        if (!askedForTabs && settings.StartWith == StartWith.Home)
        {
            _ = Task.Run(async () =>
            {
                if (await earlyShell.ConfigureAwait(false) is { } stray)
                {
                    await stray.DisposeAsync().ConfigureAwait(false);
                }
            });
            window.Dispatcher.BeginInvoke(() => window.ShowHome());
        }
        else
        {
            Opened(window, settings, share, early, earlyShell);
        }

        StartupTimeline.Mark("opened");

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

            window.Dispatcher.BeginInvoke(() => window.ImportOrSay(from));
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

        // `--home` opens Home over the first tab (QS272), once the sidebar's store has been read so
        // its cards are the store's, and with the same standing as `--palette`.
        if (arguments.Contains(CommandLine.Home, StringComparer.Ordinal))
        {
            window.Dispatcher.BeginInvoke(() => window.ShowHome());
        }

        application.Run(window);

        // Every tab, and the loops before the sessions, so nothing is drawing into a handle that is
        // on its way out. Waited for here and only here: the process is leaving, and a pseudo-console
        // still holding a child is a shell that outlives the window that opened it.
        foreach (TerminalTab tab in (_lastClosed ?? window).Held.ToArray())
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

        WindowPlacements.ReadFrom(Placements()).Remember(Screens(), (_lastClosed ?? window).Where());

        return 0;
    }

    /// <summary>Every window this process has open, the first included (QS160).</summary>
    private static readonly List<MainWindow> Windows = [];

    /// <summary>The window that closed last, whose place on the desk is the one remembered.</summary>
    private static MainWindow? _lastClosed;

    /// <summary>
    /// Every surface a window has, pointed at whichever pane has the keyboard, for the first window
    /// and for each one a detached tab opens (QS160) alike.
    ///
    /// <para>Asked afresh each time for exactly that reason: switching tabs and moving between panes
    /// are the two places a client like this goes wrong quietly, and it goes wrong by answering for
    /// the one before. The window's own copy of the settings and not the one read at start-up, so a
    /// tab opened after the settings changed opens at what they are now.</para>
    ///
    /// <para><b>Closing one of several windows ends that window's sessions and no other</b>, as the
    /// owner decided on 2026-10-09; the process leaves with the last window, whose sessions are
    /// waited for there.</para>
    /// </summary>
    private static void Wire(MainWindow window, TerminalShare share, SettingsWatch watching)
    {
        lock (Windows)
        {
            Windows.Add(window);
        }

        window.Guard = CloseGuard.ReadFrom(Locations.Current.CloseSilently);
        window.Reloads = () => watching.Reload();

        window.Opens = () => Opened(window, window.Settings, share);
        window.Connects = (leaf, from) => ConnectSplit(window, leaf, from);
        window.OpensSession = path => OpenedSession(window, window.Settings, share, path, trace: false);

        // An address typed into quick connect and not saved (QS271): the same path a saved one takes.
        window.OpensUnsaved = session => OpenResolved(window, window.Settings, share, session, saved: false);

        // The session dialog's Test connection (QS270): the host-key check and the sign-in questions
        // a tab would put, on a connection that closes again.
        window.TestsConnection = (session, token) =>
            RemoteShell.TestAsync(session,
                                  new Quickshell.Transport.TrustOnFirstUse(Quickshell.Transport.KnownHosts.ReadFrom(), window.AskHostKey),
                                  token, Logged.Value, window.AskSignIn,
                                  Locations.Current.Portable ? null : Quickshell.Transport.SecretStore.Installed());

        // The sidebar's sessions, read now that the window is on the desk and not before (QS268) — and
        // listed once the window is idle, after its first pane, not on the way to it (QS288): a store of
        // forty sessions laid out as rows was a sixth of a start. The sidebar says nothing until then.
        window.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, window.RefreshSessions);

        // An agent's work through the gateway, each run in a tab of its own (QS335): the feeds written in
        // the last ten minutes once the window is up, and any written from then on as it is. Not left for
        // idle, which a busy desk reaches late: it is one directory listing, after the first paint.
        window.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,
                                      () => WatchAgentFeeds(window, share));

        // And what an agent asks the person to approve (QS337): this window says it is here to answer,
        // and answers each request in a dialog of its own.
        window.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () => WatchAgentAsks(window));

        // And a change to the agent policy made outside this window, which holds every call until the
        // person answers it (QS339).
        window.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () => WatchAgentPolicy(window));

        // A session recorded from its first byte, local or saved (QS134).
        window.OpensRecorded = recording => Opened(window, window.Settings, share, recording: recording);
        window.OpensSessionRecorded = (path, recording) =>
            OpenedSession(window, window.Settings, share, path, trace: false, recording);

        // The browser's host side: the files of the session in the pane with the keyboard, over its
        // own connection (QS219). A local pane has none, and the browser says so.
        window.RemoteFiles = tab => tab.Focused.Files;

        // A trace turned on for an open tab, in the same place --trace puts one (QS129).
        window.TraceFor = Traced;

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

        // A tab pulled out goes to a window of its own, wired as this one is, a little below and to
        // the right of it so both are seen (QS160).
        window.Detaches = tab =>
        {
            MainWindow next = new()
            {
                Left = window.Left + 32,
                Top = window.Top + 32,
                Width = window.ActualWidth,
                Height = window.ActualHeight,
                WindowStartupLocation = WindowStartupLocation.Manual,
            };

            Wire(next, share, watching);

            next.Show();
            next.Apply(window.Settings);
            next.Adopt(tab);
            next.Activate();
        };

        window.Closed += (_, _) =>
        {
            int left;

            lock (Windows)
            {
                Windows.Remove(window);
                left = Windows.Count;
            }

            _lastClosed = window;

            // The last window's tabs are ended where the process leaves, and waited for there.
            if (left == 0)
            {
                return;
            }

            foreach (TerminalTab tab in window.Held.ToArray())
            {
                _ = tab.DisposeAsync().AsTask();
            }
        };
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
    /// <param name="window">The window the tab goes into.</param>
    /// <param name="settings">What the tab is opened with.</param>
    /// <param name="share">The one device and loop.</param>
    /// <param name="model">A model a shell was started into ahead of the window, or null.</param>
    /// <param name="started">That shell, or null to start one now.</param>
    /// <param name="recording">Where to keep what the new shell sends, or null (QS134).</param>
    private static void Opened(MainWindow window, Settings settings, TerminalShare share,
                               Emulator? model = null, Task<LocalSession?>? started = null,
                               SessionRecording? recording = null)
    {
        string host = Path.GetFileName(LocalSession.Shell);
        TerminalTab tab = TerminalTab.Open(settings, share, host, model);

        // Everything a pane reads live, applied to the one just opened as well as to the rest.
        window.Apply(settings);

        window.Add(tab);
        window.Sessions.Open(host, another: true);

        if (recording is not null)
        {
            _ = tab.Focused.ConnectAsync(recording: recording);

            return;
        }

        _ = started is null
            ? tab.ConnectAsync()
            : tab.Focused.ConnectAsync(async (emulator, damage, columns, rows, token) =>
                  // The early shell, or a new one where the early task started none (QS272: it read
                  // Home in a file that said Shell by the time this window asked).
                  await started.ConfigureAwait(false)
                  ?? await LocalSession.OpenAsync(emulator, damage, columns, rows, null, token, null).ConfigureAwait(false));
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
                                      bool trace, SessionRecording? recording = null)
    {
        if (!OpenSessionInTab(window, settings, share, path, trace, recording) && recording is not null)
        {
            // Started for a session that did not open into a pane of its own, so there is nothing
            // to keep, and an empty file left behind would read as a recording that captured nothing.
            recording.DisposeAsync().AsTask().GetAwaiter().GetResult();
            File.Delete(recording.Path);
            window.Retitle();
        }
    }

    /// <returns>Whether a single session was opened into a pane of its own, which is what a recording needs.</returns>
    private static bool OpenSessionInTab(MainWindow window, Settings settings, TerminalShare share, string path,
                                         bool trace, SessionRecording? recording)
    {
        ResolvedSession? session;
        SessionTree tree;

        try
        {
            tree = SessionTree.ReadFrom(window.SessionsFile ?? Locations.Current.Sessions);
            session = tree.Session(path);
        }
        catch (SessionStoreException unreadable)
        {
            MessageBox.Show(window, $"{unreadable.Message}\n\n{unreadable.Means}", "Sessions",
                            MessageBoxButton.OK, MessageBoxImage.Warning);

            return false;
        }

        // A recording is one stream, and a folder is several sessions typing at once (QS134).
        if (recording is not null && session is null && tree.Group(path).Count > 0)
        {
            MessageBox.Show(window, $"{path} is a folder of sessions, and a recording keeps one session's output. "
                                    + "Record one of the sessions in it instead.",
                            "Recording", MessageBoxButton.OK, MessageBoxImage.Information);

            return false;
        }

        Quickshell.Transport.TrustOnFirstUse trusting = new(Quickshell.Transport.KnownHosts.ReadFrom(), window.AskHostKey);

        // Remembered passwords live in Credential Manager, where the user can see and revoke them
        // with a tool they already trust (QS218). A portable copy remembers none: its store needs a
        // master password, and nothing here sets one yet.
        Quickshell.Transport.SecretStore? secrets = Locations.Current.Portable ? null : Quickshell.Transport.SecretStore.Installed();

        // A folder is a group: every session in it as a pane of one tab, typing into all of them
        // (QS179).
        if (session is null && tree.Group(path) is { Count: > 0 } members)
        {
            (_, int leftOut) = SessionGroup.Open(window, settings, share, members, member =>
                async (emulator, damage, columns, rows, token) =>
                    await RemoteShell.OpenAsync(member, trusting, emulator, damage, columns, rows, token,
                                                trace ? Traced(member.Host) : Logged.Value, window.AskSignIn, secrets)
                                     .ConfigureAwait(false));

            if (leftOut > 0)
            {
                MessageBox.Show(window,
                                $"{path} holds {members.Count} sessions and a tab holds {MainWindow.MaximumPanes}, "
                                + $"so the first {MainWindow.MaximumPanes} are open and {leftOut} are not.",
                                "Sessions", MessageBoxButton.OK, MessageBoxImage.Information);
            }

            return false;
        }

        if (session is null)
        {
            MessageBox.Show(window, $"There is no saved session or folder of sessions called {path}.", "Sessions",
                            MessageBoxButton.OK, MessageBoxImage.Information);

            return false;
        }

        OpenResolved(window, settings, share, session, saved: true, trace, recording, trusting, secrets);

        return true;
    }

    /// <summary>
    /// Opens one resolved session in a tab of its own and connects it: a saved one, or an address
    /// typed into quick connect and not saved (QS271), which is the same session by the same path.
    /// </summary>
    private static void OpenResolved(MainWindow window, Settings settings, TerminalShare share, ResolvedSession session,
                                     bool saved, bool trace = false, SessionRecording? recording = null,
                                     Quickshell.Transport.TrustOnFirstUse? trusting = null,
                                     Quickshell.Transport.SecretStore? secrets = null)
    {
        trusting ??= new(Quickshell.Transport.KnownHosts.ReadFrom(), window.AskHostKey);
        secrets ??= Locations.Current.Portable ? null : Quickshell.Transport.SecretStore.Installed();

        TerminalTab tab = TerminalTab.Open(settings, share, session.Host);

        // An unsaved address has no row in the sidebar to mark, and nothing to edit.
        tab.Saved = saved ? session.Path : null;

        window.Apply(settings);
        window.Add(tab);
        window.Sessions.Open(session.Host, another: true);

        // What the session says about its own pane, after the window's settings so it wins (QS245).
        tab.Focused.Wear(session, settings, window.SessionsFile ?? Locations.Current.Sessions);

        Quickshell.Transport.SessionLog log = trace ? Traced(session.Host) : Logged.Value;

        // The pane owns the recording before the first byte arrives, so the title says so at once.
        tab.Focused.Recording = recording;
        window.Retitle();

        _ = tab.Focused.ConnectAsync(async (emulator, damage, columns, rows, token) =>
            await RemoteShell.OpenAsync(session, trusting, emulator, damage, columns, rows, token, log,
                                        window.AskSignIn, secrets, recording)
                             .ConfigureAwait(false));
    }

    /// <summary>Held while a window can answer an agent's request, so a gateway knows whether anybody is here (QS337).</summary>
    private static Mutex? _answering;

    /// <summary>The requests this process has shown, so one is never asked twice.</summary>
    private static readonly HashSet<string> AsksShown = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Answers each pending and each new request for the person's approval (QS337).</summary>
    private static void WatchAgentAsks(MainWindow window)
    {
        string folder = Locations.Current.AgentAsks;

        Directory.CreateDirectory(folder);
        _answering ??= new Mutex(initiallyOwned: false, AgentAsks.WindowName);

        foreach (string asked in Directory.GetFiles(folder, "*.ask.json")
                                          .Where(file => File.GetLastWriteTimeUtc(file) > DateTime.UtcNow - AgentAsks.Patience))
        {
            window.Dispatcher.BeginInvoke(() => AnswerAsk(window, folder, asked));
        }

        FileSystemWatcher watching = new(folder, "*.ask.json") { EnableRaisingEvents = true };

        void Seen(object? sender, FileSystemEventArgs seen) =>
            window.Dispatcher.BeginInvoke(() => AnswerAsk(window, folder, seen.FullPath));

        watching.Created += Seen;
        watching.Renamed += (sender, renamed) => Seen(sender, renamed);
        window.Closed += (_, _) => watching.Dispose();
    }

    /// <summary>One request in its dialog, in front of the window; the answer written beside it, and a grant into the policy.</summary>
    private static void AnswerAsk(MainWindow window, string folder, string asked)
    {
        if (!AsksShown.Add(asked) || !File.Exists(asked) || AgentAsks.ReadRequest(File.ReadAllText(asked)) is not { } request)
        {
            return;
        }

        ApprovalDialog dialog = new(request) { Owner = window.IsLoaded ? window : null, ThemeMode = window.ThemeMode };

        dialog.ShowDialog();

        ApprovalAnswer answer = dialog.Answer;

        if (answer.Decision == Decision.Grant)
        {
            try
            {
                ApprovalDialog.WriteGrant(Locations.Current.AgentPolicy, request, answer.GrantMinutes, DateTimeOffset.Now);
            }
            catch (Exception unwritten) when (unwritten is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
            {
                // The batch still runs as approved; the grant is what could not be kept, and the person is told.
                answer = answer with { Decision = Decision.Approve };
                MessageBox.Show(window, $"The batch runs as approved, but the grant could not be written: {unwritten.Message}", "Agent policy");
            }
        }

        AgentAsks.WriteAnswer(folder, request.Id, answer);
    }

    private static bool _policyAsking;

    /// <summary>Asks the person about the agent policy whenever it differs from the version they approved (QS339).</summary>
    private static void WatchAgentPolicy(MainWindow window)
    {
        string policy = Locations.Current.AgentPolicy;
        string folder = Path.GetDirectoryName(policy)!;

        Directory.CreateDirectory(folder);

        FileSystemWatcher watching = new(folder, Path.GetFileName(policy) + "*")
        {
            EnableRaisingEvents = true,
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
        };

        // A moment after the last change, so the window's own grant, which writes the policy and then its
        // approved copy, is read once both have landed and not between them.
        async void Changed(object? sender, FileSystemEventArgs changed)
        {
            await Task.Delay(400).ConfigureAwait(true);
            await window.Dispatcher.InvokeAsync(() => AskAboutPolicy(window, policy));
        }

        watching.Changed += Changed;
        watching.Created += Changed;
        watching.Deleted += Changed;
        watching.Renamed += (sender, renamed) => Changed(sender, renamed);
        window.Closed += (_, _) => watching.Dispose();

        AskAboutPolicy(window, policy);
    }

    private static void AskAboutPolicy(MainWindow window, string policy)
    {
        if (_policyAsking || PolicyApproval.Approved(policy))
        {
            return;
        }

        _policyAsking = true;

        try
        {
            new PolicyChangeDialog(policy) { Owner = window.IsLoaded ? window : null, ThemeMode = window.ThemeMode }.ShowDialog();
        }
        finally
        {
            _policyAsking = false;
        }
    }

    /// <summary>The feeds this process has given a tab, so a feed that grows is not opened twice.</summary>
    private static readonly HashSet<string> FeedsShown = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Opens a tab for each recent agent feed and watches for new ones (QS335).</summary>
    private static void WatchAgentFeeds(MainWindow window, TerminalShare share)
    {
        string folder = Locations.Current.AgentFeeds;

        Directory.CreateDirectory(folder);

        foreach (string feed in Directory.GetFiles(folder, "*.vt")
                                         .Where(file => File.GetLastWriteTimeUtc(file) > DateTime.UtcNow.AddMinutes(-10))
                                         .Order(StringComparer.Ordinal))
        {
            OpenedFeed(window, share, feed);
        }

        FileSystemWatcher watching = new(folder, "*.vt") { EnableRaisingEvents = true };

        void Seen(object? sender, FileSystemEventArgs seen) =>
            window.Dispatcher.BeginInvoke(() => OpenedFeed(window, share, seen.FullPath));

        watching.Created += Seen;
        watching.Changed += Seen;
        window.Closed += (_, _) => watching.Dispose();
    }

    /// <summary>
    /// One agent feed in a tab badged as the agent's, put behind the tab the person is in so it never
    /// takes the screen from them (QS335).
    /// </summary>
    private static void OpenedFeed(MainWindow window, TerminalShare share, string feed)
    {
        if (!FeedsShown.Add(feed) || !File.Exists(feed))
        {
            return;
        }

        TerminalTab tab = TerminalTab.Open(window.Settings, share, "agent");

        tab.Agent = true;

        int was = window.Active;
        bool had = window.Held.Count > 0;

        window.Add(tab);

        if (had)
        {
            window.Active = was;
        }

        _ = tab.Focused.ConnectAsync((emulator, damage, columns, rows, _) =>
            Task.FromResult<IShellSession>(FeedSession.Open(feed, emulator, damage, columns, rows)));
    }

    /// <summary>
    /// What a split pane runs: another shell on the server of the pane it was split from (QS309), or
    /// a local shell where that pane was local or remembers no session.
    /// </summary>
    private static void ConnectSplit(MainWindow window, TerminalLeaf leaf, TerminalLeaf? from)
    {
        if (from?.Worn is { } session)
        {
            ConnectRemote(window, window.Settings, leaf, session);

            return;
        }

        _ = leaf.ConnectAsync();
    }

    /// <summary>
    /// Opens a session into a pane that is already there: a split of a server's pane, which is a
    /// second shell on that server, over a connection of its own (QS309). Nothing is recorded, since
    /// a recording belongs to the pane it was started in.
    /// </summary>
    private static void ConnectRemote(MainWindow window, Settings settings, TerminalLeaf leaf, ResolvedSession session)
    {
        Quickshell.Transport.TrustOnFirstUse trusting = new(Quickshell.Transport.KnownHosts.ReadFrom(), window.AskHostKey);
        Quickshell.Transport.SecretStore? secrets = Locations.Current.Portable ? null : Quickshell.Transport.SecretStore.Installed();

        window.Sessions.Open(session.Host, another: true);
        leaf.Wear(session, settings, window.SessionsFile ?? Locations.Current.Sessions);

        _ = leaf.ConnectAsync(async (emulator, damage, columns, rows, token) =>
            await RemoteShell.OpenAsync(session, trusting, emulator, damage, columns, rows, token, Logged.Value,
                                        window.AskSignIn, secrets)
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

    /// <summary>
    /// Serves the agent gateway until its input ends (QS325), or refuses where the setting is off.
    /// Standard output carries the protocol and nothing else, so anything said to a person goes to
    /// standard error.
    /// </summary>
    /// <returns>Zero once the input ended; 2 where the gateway is not turned on.</returns>
    private static int ServeAgent()
    {
        if (!SettingsFile.ReadFrom(Locations.Current.Settings).AgentGateway)
        {
            Console.Error.WriteLine("quickshell --mcp: the agent gateway is off. Turn on agentGateway in quickshell's "
                                    + "settings to let an agent run commands on your saved sessions.");

            return 2;
        }

        UTF8Encoding plain = new(encoderShouldEmitUTF8Identifier: false);
        using StreamReader input = new(Console.OpenStandardInput(), plain);
        using StreamWriter output = new(Console.OpenStandardOutput(), plain) { AutoFlush = false, NewLine = "\n" };

        string store = Locations.Current.Sessions;

        AuditLog audit = AuditLog.Start(Locations.Current.Audit, DateTimeOffset.Now);

        AgentGateway gateway = new(() => SessionTree.ReadFrom(store), AgentGateway.Connect, CrashContext.Build())
        {
            Audit = audit,
            StopFile = Locations.Current.AgentStop,
            PolicyFile = Locations.Current.AgentPolicy,
            AsksFolder = Locations.Current.AgentAsks,
            TaintFile = Path.Combine(Locations.Current.AgentTaints, audit.Run + ".taint"),
            Feed = AgentFeed.Start(Locations.Current.AgentFeeds, audit.Run),
        };

        // The kill switch (QS334): the window writes the file and this sweeps at once, without waiting
        // for the agent's next call, so a job stops while the agent is still thinking.
        Directory.CreateDirectory(Path.GetDirectoryName(Locations.Current.AgentStop)!);

        using FileSystemWatcher stop = new(Path.GetDirectoryName(Locations.Current.AgentStop)!, Path.GetFileName(Locations.Current.AgentStop))
        {
            EnableRaisingEvents = true,
        };

        stop.Created += (_, _) => _ = gateway.StopAsync();

        try
        {
            gateway.ServeAsync(input, output).GetAwaiter().GetResult();
        }
        finally
        {
            gateway.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        return 0;
    }

    /// <summary>
    /// Prints a gateway run's audit as a change summary, or lists the runs where none is named (QS333).
    /// </summary>
    /// <returns>Zero for a whole log or a list; 1 where the run's chain is broken; 2 where there is no such run.</returns>
    private static int PrintAudit(string? run)
    {
        string folder = Locations.Current.Audit;

        if (run is null)
        {
            IReadOnlyList<string> runs = AuditLog.Runs(folder);

            Console.Out.WriteLine(runs.Count == 0 ? $"No agent gateway run is recorded in {folder}." : string.Join(Environment.NewLine, runs));

            return 0;
        }

        if (AuditLog.Read(folder, run) is not { } summary)
        {
            Console.Error.WriteLine($"quickshell --audit: no run {run} is recorded in {folder}. quickshell --audit alone lists them.");

            return 2;
        }

        Console.Out.Write(summary.ToString());

        return summary.Breaks.Count == 0 ? 0 : 1;
    }

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
