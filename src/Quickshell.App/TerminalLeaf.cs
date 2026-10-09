using System.Windows.Automation;
using Quickshell.Terminal;
using Quickshell.Transport;

namespace Quickshell.App;

/// <summary>
/// One session, and everything that belongs to it for as long as it lasts.
///
/// <para><b>The session lives here and not in the window.</b> That sentence is the whole of this
/// class and the reason it exists at all: a model, a pane, a device, a loop, a keyboard and a shell
/// were built one-of-each in the client's entry point, and a client with two of anything needed
/// them somewhere they could be counted.</para>
///
/// <para><b>A leaf and not a tab, since QS48.</b> A tab holds a tree of these rather than one, so
/// two sessions can be on screen at once — and each of them is a full terminal with its own
/// scrollback, its own size and its own idea of what the host has called it.</para>
///
/// <para><b>It is also what a detached tab will carry.</b> A tab moved to another window keeps its
/// connection rather than reconnecting, which is only possible where the connection was never the
/// window's — QS160 is that move, and it is a move of these objects.</para>
///
/// <para><b>The title has three sources and they are ranked.</b> A name the user set outranks
/// everything, because they said so. Then the title the host is writing through OSC, which is what
/// turns a strip of identical host names into information — a shell reporting its directory or its
/// running command. Then what the tab is connected to, which is always true and never interesting.
/// </para>
/// </summary>
public sealed class TerminalLeaf : IAsyncDisposable
{
    private readonly DamageSignal _damage;
    private readonly TerminalShare _share;

    private IShellSession? _session;
    private long _seen;
    private bool _disposed;
    private bool _receiving;

    // A model a shell was started into before this leaf existed (QS191), and the scrollback depth
    // settings asked for before that shell's pipeline was this leaf's to tell.
    private bool _adopted;
    private int? _owedScrollback;

    private TerminalLeaf(Emulator emulator, TerminalPane pane, TerminalShare share,
                         Settings settings, string host)
    {
        Emulator = emulator;
        Pane = pane;
        Host = host;

        // The one signal every pane in the process sets, because there is one loop reading it.
        _damage = share.Damage;
        _share = share;

        Typist = new Typist(emulator);

        Terminal = TerminalView.Attach(pane, emulator, share, settings.FontFamily,
                                       (float)settings.FontSize, settings.Ligatures);
    }

    /// <summary>The model this tab's session is parsed into.</summary>
    public Emulator Emulator { get; }

    /// <summary>The child window its swapchain presents into, which a detach replaces (QS160).</summary>
    public TerminalPane Pane { get; private set; }

    /// <summary>The device, the loop, the selection and the viewport, replaced with the pane.</summary>
    public PaneAttachment Terminal { get; private set; }

    /// <summary>
    /// A new pane for this leaf, in another window, with the same model and the same session behind
    /// it (QS160).
    ///
    /// <para><b>The pane is replaced, never the connection.</b> A pane is an <c>HwndHost</c>, and
    /// leaving its window destroyed the child window its swapchain presented into, so the old view
    /// is taken off the loop and a new pane and view are built for wherever the tab goes. What the
    /// session was told to write to - the model, the typist - is this leaf's and does not change,
    /// so nothing reconnects. Only the view's own wiring, which belonged to the pane that went, is
    /// joined to the session again.</para>
    /// </summary>
    public void Repane(Settings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        Terminal.Dispose();

        Pane = new TerminalPane { Reading = Emulator.Buffer };
        Terminal = TerminalView.Attach(Pane, Emulator, _share, settings.FontFamily,
                                       (float)settings.FontSize, settings.Ligatures);

        if (_session is { } session)
        {
            Terminal.Sending = bytes => session.TypeAsync(bytes);
            Typist.Typed = Terminal.ToBottom;
            Terminal.Resized = session.Resize;
        }
    }

    /// <summary>Where this tab's keystrokes go.</summary>
    public Typist Typist { get; }

    /// <summary>What it is connected to, which is the title of last resort.</summary>
    public string Host { get; }

    /// <summary>
    /// The SSH connection behind this pane, for whatever else is opened over it — a file channel for
    /// a drop (QS189) — or null where the pane runs a local shell or nothing yet.
    /// </summary>
    public ISshTransport? Transport => (_session as RemoteShell)?.Transport;

    /// <summary>
    /// The host's files over that connection (QS219), or null where the pane runs a local shell, its
    /// file channel has not opened yet, or the server offers none.
    /// </summary>
    public RemoteFiles? Files => (_session as RemoteShell)?.Files;

    /// <summary>
    /// The forwards of the session in this pane, on the connection there is now (QS70), or null
    /// where the pane runs a local shell or is between connections.
    /// </summary>
    public SessionForwards? Forwards => (_session as RemoteShell)?.ForwardsNow;

    /// <summary>The saved session in this pane, or null where it runs a local shell or nothing yet (QS129).</summary>
    public RemoteShell? Remote => _session as RemoteShell;

    /// <summary>
    /// How the shell in this pane reads a quoted word, which is what a dropped path is typed as.
    /// Read off what the pane runs: a local tab's is Windows' command processor, and anything this
    /// client connects to over SSH is a POSIX shell.
    /// </summary>
    public ShellKind Shell => ShellQuoting.Of(Host);

    /// <summary>
    /// Files dropped onto this pane, typed at the prompt as their paths — QS64.
    ///
    /// <para><b>Typed and not transferred</b>, because what somebody dropping a file onto a shell
    /// wants, nine times in ten, is its path as an argument. Each path is quoted for this pane's
    /// shell, so a name with a space, a quote or a dollar in it is one argument and nothing else.
    /// </para>
    ///
    /// <para><b>Into this pane and no other</b>, even while its tab is broadcasting: a drop is
    /// aimed at one pane by where it is let go, which is a different gesture from typing.</para>
    /// </summary>
    public void Drop(IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        if (paths.Count > 0)
        {
            Typist.Type(ShellQuoting.Line(paths, Shell), System.Windows.Input.ModifierKeys.None);
        }
    }

    /// <summary>
    /// The name the user gave this tab, which outranks everything the host has to say.
    /// </summary>
    public string? Named { get; set; }

    /// <summary>
    /// Why this tab's session ended, or null while it is still running.
    ///
    /// <para><b>A tab whose session died stays open showing this.</b> A tab that vanished would take
    /// the message with it, and the message is the one thing the user needed.</para>
    /// </summary>
    public string? Ended { get; private set; }

    /// <summary>Whether the session is still running, which is what closing asks about.</summary>
    public bool IsLive => Ended is null && _session is not null;

    /// <summary>
    /// What this pane says to a screen reader while it is receiving broadcast typing.
    ///
    /// <para>Beside the name and not in it: the name is read every time a reader arrives, and this
    /// is a state that comes and goes. It is UI Automation's help text, which is where a state that
    /// has no pattern of its own is put.</para>
    /// </summary>
    public const string ReceivingSays = "Receiving everything typed in this tab";

    /// <summary>
    /// Whether what is typed in this tab reaches this pane whichever pane has the keyboard — QS53.
    ///
    /// <para><b>Setting it is what marks the pane, and nothing else may.</b> The design is falsified
    /// by a pane that receives input without being visibly marked, so the mark and the membership
    /// are one property rather than two that have to be kept in step: the edge goes on the glass,
    /// and the same sentence goes where a screen reader will find it.</para>
    /// </summary>
    public bool Receiving
    {
        get => _receiving;

        internal set
        {
            if (_receiving == value)
            {
                return;
            }

            _receiving = value;

            Terminal.Outlined = value;

            string was = AutomationProperties.GetHelpText(Pane);
            string now = value ? ReceivingSays : string.Empty;

            AutomationProperties.SetHelpText(Pane, now);

            // Only where a reader already asked for the peer. Building one here to announce a change
            // nobody is listening for would be the client creating accessibility objects on its own.
            Pane.Automation?.RaisePropertyChangedEvent(AutomationElementIdentifiers.HelpTextProperty,
                                                       was, now);
        }
    }

    /// <summary>
    /// Whether something happened here while this tab was not the one on screen.
    ///
    /// <para>A dot and never a count: a terminal has no unit worth counting, and a badge saying
    /// "1,482" is a number about bytes pretending to be a number about attention.</para>
    ///
    /// <para><b>Asked rather than raised, and the buffer's own generation is the answer.</b> Output
    /// arrives on the parser stage, which has no thread this window may be touched from and no event
    /// it could raise onto one. The generation is a number that changes when the screen does and is
    /// safe to read from anywhere — so the question is answered by comparing it against what it was
    /// when this tab was last looked at.</para>
    /// </summary>
    public bool HasActivity => Emulator.Buffer.Generation != _seen;

    /// <summary>What the strip shows, from the three sources in the order they outrank each other.</summary>
    public string Title
    {
        get
        {
            if (Named is { Length: > 0 } named)
            {
                return named;
            }

            return Emulator.Title is { Length: > 0 } written ? written : Host;
        }
    }

    /// <summary>
    /// Builds a tab with a device and a loop but no session yet.
    /// </summary>
    /// <param name="settings">The font, its size and how much scrollback to keep.</param>
    /// <param name="host">What it will be connected to, for the title of last resort.</param>
    /// <param name="share">The one device, atlas and render loop every pane in the process uses.</param>
    /// <param name="model">
    /// A model a shell was already started into, ahead of the window (QS191), or null for a new one.
    /// Its scrollback depth is told to the shell's pipeline once the leaf has it, never to the model
    /// under the parser.
    /// </param>
    public static TerminalLeaf Open(Settings settings, string host, TerminalShare share, Emulator? model = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentNullException.ThrowIfNull(share);

        // The size is a placeholder for one layout pass. The pane decides the real grid, and the
        // model is resized to it before a frame is drawn.
        Emulator emulator = model ?? new(80, 25, settings.Scrollback);

        // Before anything is drawn, so a pane opened after the scheme was chosen is not the one
        // pane wearing the defaults.
        settings.Colours.ApplyTo(emulator.Palette);

        return new TerminalLeaf(emulator, new TerminalPane { Reading = emulator.Buffer },
                                share, settings, host) { _adopted = model is not null };
    }

    /// <summary>
    /// Starts a shell behind this tab and joins the two ends of it.
    ///
    /// <para>Everything a session needs to be told is told here rather than by the caller, because
    /// every one of these is a fact about this tab: where its keystrokes go, who hears its resizes,
    /// and that typing in it means its reading is finished.</para>
    /// </summary>
    /// <param name="commandLine">What to run, or null for this user's own shell.</param>
    /// <param name="cancellationToken">Gives up on the pseudo-console's pipes connecting.</param>
    /// <param name="recording">Where to keep what the shell sends, which this pane then owns, or null (QS134).</param>
    public Task ConnectAsync(string? commandLine = null,
                             CancellationToken cancellationToken = default,
                             SessionRecording? recording = null)
    {
        Recording = recording ?? Recording;

        return ConnectAsync(async (emulator, damage, columns, rows, token) =>
                                await LocalSession.OpenAsync(emulator, damage, columns, rows, commandLine, token,
                                                             recording)
                                                  .ConfigureAwait(false),
                            cancellationToken);
    }

    /// <summary>
    /// What this pane's session is being recorded into, or null. Given as the session opens and
    /// never afterwards (QS134): a recording that could start mid-session is one a user could be
    /// unaware had started. The pane owns it, so it is closed when the pane is.
    /// </summary>
    public SessionRecording? Recording { get; set; }

    /// <summary>
    /// Closes the recording, which is what makes the file readable, and says where it is.
    /// </summary>
    /// <returns>The file it wrote, or null where nothing was being recorded.</returns>
    public async ValueTask<string?> StopRecordingAsync()
    {
        if (Recording is not { Running: true } recording)
        {
            return null;
        }

        await recording.DisposeAsync().ConfigureAwait(false);

        return recording.Path;
    }

    /// <summary>
    /// Starts whatever <paramref name="open"/> opens behind this pane — a saved session's remote
    /// shell, or anything else that arrives as a pipeline — and joins the two ends of it (QS126).
    /// </summary>
    /// <param name="open">Opens the session once the grid is known.</param>
    /// <param name="cancellationToken">Gives up on opening it.</param>
    public async Task ConnectAsync(ShellOpener open, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(open);

        // What is typed while the session opens is kept for it, not dropped (QS249).
        Typist.Hold();

        try
        {
            IShellSession session = await open(Emulator, _damage, Emulator.Buffer.Columns, Emulator.Buffer.Rows,
                                               cancellationToken)
                .ConfigureAwait(false);

            _session = session;

            if (_owedScrollback is { } depth)
            {
                session.KeepScrollback(depth);
                _owedScrollback = null;
            }

            // The shell is running, which is when a timed start stops waiting on this client.
            StartupTimeline.Mark("shell");

            Typist.Sending = bytes => session.TypeAsync(bytes);

            // And the mouse a program asks for, by the same path: a click is something typed (QS155).
            Terminal.Sending = bytes => session.TypeAsync(bytes);
            Typist.Typed = Terminal.ToBottom;
            Terminal.Resized = session.Resize;

            // The grid the pane settled on while this was starting, which arrived when there was no
            // session to hear it. Sent once rather than assumed: a program wrong about its own width
            // draws a screen for a terminal nobody has.
            session.Resize(Emulator.Buffer.Columns, Emulator.Buffer.Rows);

            // And the one path that carries an ending rather than bytes, which nothing waited on
            // until QS152: a shell that exits sends nothing, and nothing is what the pane would go on
            // drawing.
            _ = SayWhenItEnds(session);
        }
        catch (Exception failed)
        {
            Ended = failed.Message;
            Typist.Release();

            // Onto the terminal itself, because that is where the user is already looking. Safe to
            // write from here for the one reason that matters: no pipeline started, so this is the
            // only writer the render loop has.
            Emulator.Feed(System.Text.Encoding.UTF8.GetBytes(
                $"quickshell could not start {Host}\r\n{failed.Message}\r\n"));

            _damage.Set();
        }
    }

    /// <summary>
    /// Says, in the terminal, that the session has ended and what it ended with (QS152).
    ///
    /// <para><b>In the pane and not in a dialog</b>, because the pane is where the person is
    /// looking, and a window holding its last frame with the cursor still blinking reads as hung —
    /// typing <c>exit</c> is the ordinary way a session ends, and it must not look like a fault. A
    /// program that exited and a link that went are told apart, as a remote session words them.
    /// The cursor is hidden as well: a blinking caret is an invitation to type somewhere nothing is
    /// listening.</para>
    ///
    /// <para>Written once the pipeline has completed, which is when its parser has stopped: from then
    /// on this is the only writer the model has. A tab being closed is not a session ending, and
    /// says nothing.</para>
    /// </summary>
    private async Task SayWhenItEnds(IShellSession session)
    {
        PtyExit exit;

        try
        {
            exit = await session.Ended.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // However the session finished, it has finished, and that is what is being said.
            exit = PtyExit.Failed(string.Empty);
        }

        if (_disposed)
        {
            return;
        }

        string said = Ending(exit);

        Ended = said;

        // A fresh line, the pen reset so the sentence is not in whatever colour the shell left
        // behind, and the cursor hidden.
        string line = (Emulator.Buffer.CursorColumn > 0 ? "\r\n" : string.Empty)
                      + "\u001b[0m\r\n[" + Host + ": " + said + ". Nothing typed here goes anywhere now.]\r\n"
                      + "\u001b[?25l";

        Emulator.Feed(System.Text.Encoding.UTF8.GetBytes(line));

        _damage.Set();
    }

    /// <summary>How an ending reads: an exit code is the program's, anything else is the link's.</summary>
    public static string Ending(PtyExit exit) =>
        exit.IsExit
            ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"the shell exited with code {exit.Code}")
            : exit.Reason.Length > 0
                ? $"the connection ended: {exit.Reason}"
                : "the connection ended";

    /// <summary>
    /// The colour scheme this pane's saved session asks for, which <see cref="Apply"/> prefers to the
    /// window's, or null to wear the window's (QS245).
    /// </summary>
    public ColourScheme? OwnColours { get; private set; }

    /// <summary>The scrollback depth this pane's saved session asks for, or null for the window's (QS245).</summary>
    public int? OwnScrollback { get; private set; }

    /// <summary>
    /// Takes on what a saved session says about its own pane - its colour scheme and its scrollback -
    /// and applies them at once (QS245). A scheme is a file named relative to the sessions file, as a
    /// settings file's is to the settings; one that does not read leaves the window's scheme.
    /// </summary>
    public void Wear(ResolvedSession session, Settings settings, string sessionsFile)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(settings);

        OwnColours = session.Scheme?.Value is { Length: > 0 } named
            ? SchemeFile.ReadFrom(System.IO.Path.IsPathRooted(named)
                                      ? named
                                      : System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(sessionsFile)) ?? ".", named))
            : null;
        OwnScrollback = session.Scrollback?.Value is >= 0 and var depth ? depth : null;

        Apply(settings);
    }

    /// <summary>
    /// Takes settings that have changed, on a pane that is already open and drawing.
    ///
    /// <para><b>The cursor and the blink reach the glass at once</b>, because both are read by the
    /// loop every frame and neither is built into anything. The font is built into the atlas and the
    /// grid, so it is handed to the share, which re-points the atlas and refits every pane's grid on
    /// the loop's own thread (QS135, QS168).</para>
    ///
    /// <para><b>The colour scheme repaints the scrollback with it</b>, which is QS51's falsification
    /// and works only because a cell stores the colour role the host asked for. It is applied before
    /// the early return below: a pane whose swapchain has not opened yet still has a model, and a
    /// scheme that reached the model would otherwise be lost on the pane least likely to be
    /// noticed.</para>
    /// </summary>
    public void Apply(Settings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        // Onto the model's own palette, which the painter resolves every cell against as it builds
        // a frame — so every line already on screen takes the new colours rather than only the ones
        // written after this. A session's own scheme wins over the window's (QS245).
        (OwnColours ?? settings.Colours).ApplyTo(Emulator.Palette);

        // The typeface and its size, which are the share's and every pane's: asked of it by each
        // pane, and the same answer each time is applied once, by the loop (QS135). A hand-typed
        // size of nothing or a blank family is passed over: the file keeps what was typed, and the
        // font that was drawing goes on drawing.
        if (settings.FontSize > 0 && !string.IsNullOrWhiteSpace(settings.FontFamily))
        {
            _share.UseFont(settings.FontFamily, (float)settings.FontSize, settings.Ligatures);
        }

        // And the depth of history, through the session where there is one, because the ring is
        // its parser's; without one nothing is writing the model and it is told directly — the
        // same arrangement a resize has.
        int depth = OwnScrollback ?? settings.Scrollback;

        if (depth >= 0)
        {
            if (_session is { } session)
            {
                session.KeepScrollback(depth);
            }
            else if (_adopted)
            {
                // A shell started ahead of the window is already writing this model (QS191), so the
                // depth waits for its pipeline rather than reaching into the ring under the parser.
                _owedScrollback = depth;
            }
            else
            {
                Emulator.KeepScrollback(depth);
            }
        }

        if (Terminal.View is not { } view)
        {
            _damage.Set();

            return;
        }

        view.Cursor = settings.Cursor;
        view.Renderer.Blink.Enabled = settings.CursorBlink;

        // The picture is wrong and the terminal does not know: nothing was printed, so the gate
        // would answer that the frame on the glass is still current.
        view.Moved();

        _damage.Set();
    }

    /// <summary>
    /// This tab is the one on screen now, or is no longer.
    ///
    /// <para>Coming forward takes a reading of the screen, which is what clears the activity mark:
    /// the user is looking at it, and being looked at is the only thing the mark was ever about.
    /// </para>
    /// </summary>
    public void Showing(bool showing)
    {
        if (showing)
        {
            _seen = Emulator.Buffer.Generation;
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // The loop first and the session after it, which is the order the client's own shutdown
        // used: nothing may be drawing into a handle that is on its way out.
        Terminal.Dispose();

        if (_session is { } session)
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }

        // After the session, so its last bytes are in the file, and closed so the file can be read.
        if (Recording is { } recording)
        {
            await recording.DisposeAsync().ConfigureAwait(false);
        }
    }
}
