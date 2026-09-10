using System.Windows.Automation;
using Quickshell.Terminal;

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

    private LocalSession? _session;
    private long _seen;
    private bool _disposed;
    private bool _receiving;

    private TerminalLeaf(Emulator emulator, TerminalPane pane, TerminalShare share,
                         Settings settings, string host)
    {
        Emulator = emulator;
        Pane = pane;
        Host = host;

        // The one signal every pane in the process sets, because there is one loop reading it.
        _damage = share.Damage;

        Typist = new Typist(emulator);

        Terminal = TerminalView.Attach(pane, emulator, share, settings.FontFamily,
                                       (float)settings.FontSize, settings.Ligatures);
    }

    /// <summary>The model this tab's session is parsed into.</summary>
    public Emulator Emulator { get; }

    /// <summary>The child window its swapchain presents into.</summary>
    public TerminalPane Pane { get; }

    /// <summary>The device, the loop, the selection and the viewport.</summary>
    public PaneAttachment Terminal { get; }

    /// <summary>Where this tab's keystrokes go.</summary>
    public Typist Typist { get; }

    /// <summary>What it is connected to, which is the title of last resort.</summary>
    public string Host { get; }

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
    public static TerminalLeaf Open(Settings settings, string host, TerminalShare share)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentNullException.ThrowIfNull(share);

        // The size is a placeholder for one layout pass. The pane decides the real grid, and the
        // model is resized to it before a frame is drawn.
        Emulator emulator = new(80, 25, settings.Scrollback);

        // Before anything is drawn, so a pane opened after the scheme was chosen is not the one
        // pane wearing the defaults.
        settings.Colours.ApplyTo(emulator.Palette);

        return new TerminalLeaf(emulator, new TerminalPane { Reading = emulator.Buffer },
                                share, settings, host);
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
    public async Task ConnectAsync(string? commandLine = null,
                                   CancellationToken cancellationToken = default)
    {
        try
        {
            LocalSession session = await LocalSession
                .OpenAsync(Emulator, _damage, Emulator.Buffer.Columns, Emulator.Buffer.Rows,
                           commandLine, cancellationToken)
                .ConfigureAwait(false);

            _session = session;

            // The shell is running, which is when a timed start stops waiting on this client.
            StartupTimeline.Mark("shell");

            Typist.Sending = bytes => session.Pipeline.TypeAsync(bytes);
            Typist.Typed = Terminal.ToBottom;
            Terminal.Resized = session.Pipeline.Resize;

            // The grid the pane settled on while this was starting, which arrived when there was no
            // session to hear it. Sent once rather than assumed: a program wrong about its own width
            // draws a screen for a terminal nobody has.
            session.Pipeline.Resize(Emulator.Buffer.Columns, Emulator.Buffer.Rows);
        }
        catch (Exception failed)
        {
            Ended = failed.Message;

            // Onto the terminal itself, because that is where the user is already looking. Safe to
            // write from here for the one reason that matters: no pipeline started, so this is the
            // only writer the render loop has.
            Emulator.Feed(System.Text.Encoding.UTF8.GetBytes(
                $"quickshell could not start {Host}\r\n{failed.Message}\r\n"));

            _damage.Set();
        }
    }

    /// <summary>
    /// Takes settings that have changed, on a pane that is already open and drawing.
    ///
    /// <para><b>The cursor and the blink reach the glass at once</b>, because both are read by the
    /// loop every frame and neither is built into anything. The font is the one that cannot be: the
    /// atlas rasterised at a size and the grid was measured from it, so changing it is a new atlas
    /// and a new grid for every pane at once — QS168, and it is the share's to do rather than a
    /// pane's.</para>
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
        // written after this.
        settings.Colours.ApplyTo(Emulator.Palette);

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
    }
}
