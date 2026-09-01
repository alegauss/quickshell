using Quickshell.Terminal;

namespace Quickshell.App;

/// <summary>
/// One session, and everything that belongs to it for as long as it lasts.
///
/// <para><b>The session lives in the tab and not in the window.</b> That sentence is the whole of
/// this class and the reason it exists at all: a model, a pane, a device, a loop, a keyboard and a
/// shell were built one-of-each in the client's entry point, and a client with two of anything
/// needed them somewhere they could be counted.</para>
///
/// <para><b>It is also what a detached tab will carry.</b> A tab moved to another window keeps its
/// connection rather than reconnecting, which is only possible where the connection was never the
/// window's — QS160 is that move, and it is a move of this object.</para>
///
/// <para><b>The title has three sources and they are ranked.</b> A name the user set outranks
/// everything, because they said so. Then the title the host is writing through OSC, which is what
/// turns a strip of identical host names into information — a shell reporting its directory or its
/// running command. Then what the tab is connected to, which is always true and never interesting.
/// </para>
/// </summary>
public sealed class TerminalTab : IAsyncDisposable
{
    private readonly DamageSignal _damage;

    private LocalSession? _session;
    private long _seen;
    private bool _disposed;

    private TerminalTab(Emulator emulator, TerminalPane pane, DamageSignal damage,
                        Settings settings, string host)
    {
        Emulator = emulator;
        Pane = pane;
        Host = host;

        _damage = damage;

        Typist = new Typist(emulator);

        Terminal = TerminalView.Attach(pane, emulator, damage,
                                       settings.FontFamily, (float)settings.FontSize);
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
    public static TerminalTab Open(Settings settings, string host)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(host);

        // The size is a placeholder for one layout pass. The pane decides the real grid, and the
        // model is resized to it before a frame is drawn.
        Emulator emulator = new(80, 25, settings.Scrollback);

        return new TerminalTab(emulator, new TerminalPane { Reading = emulator.Buffer },
                               new DamageSignal(), settings, host);
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
