using Quickshell.Render;
using Quickshell.Terminal;

namespace Quickshell.App;

/// <summary>
/// The one device, the one atlas, the one set of shaders, and the one thread that draws.
///
/// <para><b>QS49, and it is a decision about ownership rather than an optimisation.</b> A pane needs
/// a swapchain and an instance buffer of its own. Everything else it needs — the device, the
/// shaders, the constant buffers and above all the glyph atlas — is the same for every pane in the
/// process, and giving each one a private copy would be sixteen copies of the same rasterised font
/// and sixteen passes over the same characters.</para>
///
/// <para><b>One thread and not one per pane, and that follows from the sharing rather than being a
/// second choice.</b> D3D11's immediate context is not free-threaded: two panes drawing on two
/// threads through one context is a data race, and an unsynchronised context is what makes the
/// draw path cheap in the first place. So the loop is here, it waits on one signal that every
/// session sets, and it asks each pane in turn whether its picture has changed — which costs a
/// comparison per pane and draws only the ones that answer yes.</para>
///
/// <para><b>The device opens on the first pane that has a handle.</b> Which adapter to use is
/// decided by the window the output goes to, and no window has a handle until WPF has laid it out —
/// so this exists before the device does, and the client can put a window on screen before either.
/// </para>
/// </summary>
public sealed class TerminalShare : IDisposable
{
    private readonly List<(TerminalView View, Emulator Model)> _drawing = [];
    private readonly Lock _guard = new();
    private readonly CancellationTokenSource _stop = new();

    private GraphicsDevice? _device;
    private GlyphRasteriser? _rasteriser;
    private GlyphAtlas? _atlas;
    private CellRenderer? _renderer;
    private Task _loop = Task.CompletedTask;
    private bool _disposed;

    /// <summary>
    /// What every session sets when its parser has changed something.
    ///
    /// <para>One for all of them, because there is one loop: a signal per pane would be a loop that
    /// had to wait on several things at once, and what it would learn from knowing which one fired
    /// is exactly what the redraw gate answers anyway.</para>
    /// </summary>
    public DamageSignal Damage { get; } = new();

    /// <summary>
    /// Whether the loop runs, which it does everywhere but in a test that counts frames.
    ///
    /// <para>A caller measuring how many panes one pass draws cannot also have a thread drawing
    /// them: the first frame would already be on the glass and the pass would report nothing to do.
    /// So the pacing is the caller's where it says so, and <see cref="DrawOnce"/> is the pass.</para>
    /// </summary>
    public bool Looping { get; init; } = true;

    /// <summary>The device, once a pane has caused it to open. Null before that.</summary>
    public GraphicsDevice? Device => _device;

    /// <summary>The atlas every pane draws from, or null before the device exists.</summary>
    public GlyphAtlas? Atlas => _atlas;

    /// <summary>What went wrong opening the device, or null.</summary>
    public Exception? Failed { get; private set; }

    /// <summary>How many panes this share is drawing.</summary>
    public int Panes
    {
        get
        {
            lock (_guard)
            {
                return _drawing.Count;
            }
        }
    }

    /// <summary>
    /// Opens a view on a pane's handle, opening the device first if this is the first pane.
    /// </summary>
    /// <param name="window">The pane's own handle, which the swapchain presents into.</param>
    /// <param name="width">Its width in pixels.</param>
    /// <param name="height">Its height.</param>
    /// <param name="font">What to rasterise with. Only the first pane's is used, because the atlas
    /// is shared and a second font would be a second atlas — which is what this exists to
    /// prevent.</param>
    /// <param name="palette">The session's own colours, which are not shared.</param>
    /// <returns>The view, or null where the device could not be opened.</returns>
    public TerminalView? View(nint window, uint width, uint height, FontSettings font,
                              Palette palette)
    {
        ArgumentNullException.ThrowIfNull(palette);

        if (!Ready(window, font))
        {
            return null;
        }

        return TerminalView.On(_device!, _atlas!, _renderer!, window, width, height, palette);
    }

    /// <summary>Starts drawing a pane, and starts the loop if this is the first.</summary>
    public void Draw(TerminalView view, Emulator model)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(model);

        lock (_guard)
        {
            _drawing.Add((view, model));
        }

        if (Looping && _loop.IsCompleted && !_disposed)
        {
            _loop = Task.Run(() => RunAsync(_stop.Token));
        }

        // The first frame is owed unconditionally: a pane's handle is a rectangle the colour of
        // whatever was behind it until something presents into it, and nothing has changed yet.
        Damage.Set();
    }

    /// <summary>Stops drawing a pane, which is what closing one does.</summary>
    public void Forget(TerminalView view)
    {
        lock (_guard)
        {
            _drawing.RemoveAll(one => ReferenceEquals(one.View, view));
        }
    }

    /// <summary>
    /// Draws every pane whose picture has changed, once.
    ///
    /// <para>Public because a test wants one pass rather than a loop, and because the loop below is
    /// nothing but this in a wait.</para>
    /// </summary>
    /// <returns>How many panes were drawn.</returns>
    public int DrawOnce()
    {
        (TerminalView View, Emulator Model)[] panes;

        lock (_guard)
        {
            panes = [.. _drawing];
        }

        int drawn = 0;

        foreach ((TerminalView view, Emulator model) in panes)
        {
            if (view.DrawIfNeeded(model))
            {
                drawn++;
            }
        }

        return drawn;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _stop.Cancel();

        // Bounded, because a loop that will not stop must not hold a window open. The device is
        // released either way: the process is leaving.
        _loop.Wait(TimeSpan.FromSeconds(2));

        lock (_guard)
        {
            foreach ((TerminalView view, _) in _drawing)
            {
                view.Dispose();
            }

            _drawing.Clear();
        }

        _renderer?.Dispose();
        _atlas?.Dispose();
        _rasteriser?.Dispose();
        _device?.Dispose();

        _stop.Dispose();
    }

    /// <summary>
    /// Opens the device, the atlas and the shaders, once.
    ///
    /// <para>Kept rather than thrown, for the reason <see cref="PaneAttachment.Failed"/> is: this
    /// runs on a layout callback, and a machine with no usable adapter should get a client with an
    /// unpainted pane and a line in a diagnostic bundle, not a window that vanishes during its own
    /// first layout.</para>
    /// </summary>
    private bool Ready(nint window, FontSettings font)
    {
        if (_renderer is not null)
        {
            return true;
        }

        if (Failed is not null)
        {
            return false;
        }

        try
        {
            _device = GraphicsDevice.Open(outputWindow: window);
            _rasteriser = new GlyphRasteriser();
            _atlas = GlyphAtlas.For(_device, font, rasteriser: _rasteriser);
            _renderer = CellRenderer.For(_device, _atlas, _rasteriser.Measure(font));

            return true;
        }
        catch (Exception error)
        {
            Failed = error;

            _rasteriser?.Dispose();
            _device?.Dispose();

            _rasteriser = null;
            _atlas = null;
            _device = null;

            return false;
        }
    }

    /// <summary>
    /// Draws until cancelled, waking only for something that changes a picture.
    ///
    /// <para>The wait has no interval: it ends when a parser says something changed, or when the
    /// cursor's blink phase is due, and <see cref="CellRenderer.NextCursorWake"/> answers null when
    /// even that is not coming. A window whose hosts are silent and whose cursors are hidden sleeps
    /// until a byte arrives, however many panes it has.</para>
    /// </summary>
    private async Task RunAsync(CancellationToken token)
    {
        DrawOnce();

        // Held across iterations on purpose. A waiter abandoned when the blink deadline won would
        // still be in the queue, and DamageSignal takes the change when it wakes — so a discarded
        // wait is a wake-up consumed by nobody, and the frame behind it is never drawn.
        Task? waiting = null;

        try
        {
            while (!token.IsCancellationRequested)
            {
                waiting ??= Damage.WaitAsync(token);

                TimeSpan? wake = _renderer?.NextCursorWake();

                Task woken = wake is null
                    ? waiting
                    : await Task.WhenAny(waiting, Task.Delay(wake.Value, token))
                                .ConfigureAwait(false);

                if (woken == waiting)
                {
                    await waiting.ConfigureAwait(false);

                    waiting = null;
                }

                if (token.IsCancellationRequested)
                {
                    return;
                }

                DrawOnce();
            }
        }
        catch (OperationCanceledException)
        {
            // The end of a window, and not a fault.
        }
    }
}
