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
    private Task<Prepared?>? _preparing;
    private bool _disposed;

    /// <summary>What <see cref="Prepare"/> opened ahead of any window.</summary>
    private sealed record Prepared(GraphicsDevice Device, GlyphRasteriser Rasteriser, GlyphAtlas Atlas,
                                   CellRenderer Renderer);

    // A font asked for and not yet applied. Written on WPF's thread and taken on the loop's, under
    // the guard, because the atlas and the renderer it changes are the loop's alone.
    private (string Family, float SizeInPoints, bool Ligatures)? _refont;

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

    /// <summary>
    /// The device as a crash report states it: the adapter chain's own account, and how many losses
    /// it had already come back from (QS132).
    ///
    /// <para>The share and not a pane, because the share is what holds the device for every pane in
    /// every tab — a pane can close and take its reference with it, and the device goes on. Where
    /// there is none, the reason is said instead, and a device that failed to open says how, which
    /// is the report most likely to be about a driver.</para>
    /// </summary>
    public (string Adapter, int Recoveries) Describe()
    {
        if (_device is { } device)
        {
            return (device.Adapter.ToString(), device.Recoveries);
        }

        return Failed is { } failed
            ? ($"no device: opening one failed ({failed.GetType().Name}: {failed.Message})", 0)
            : ("no device yet: no pane had been laid out", 0);
    }

    /// <summary>
    /// Opens the device, the atlas and the shaders on the thread pool now, so they are ready while
    /// WPF builds the window instead of after its first layout (QS191).
    ///
    /// <para><b>Ahead of the window, and only kept if the window agrees.</b> The adapter is the
    /// default one, since there is no window yet to ask; the first pane checks it against the
    /// adapter its own monitor is on and opens afresh where they differ — a second GPU driving the
    /// monitor the window landed on — so the guess never decides where the output is drawn. A font
    /// that differs from the one guessed — the settings changed it, or the monitor's DPI is not the
    /// system's — is applied to what was opened, as a font change is.</para>
    ///
    /// <para><b>A failure here is nobody's.</b> It is kept off the window's path: the first pane
    /// opens the device the way it always did and reports what happens then.</para>
    /// </summary>
    /// <param name="font">The font the first pane is expected to ask for, worked out on the pool too.</param>
    public void Prepare(Func<FontSettings> font)
    {
        ArgumentNullException.ThrowIfNull(font);

        if (_preparing is not null || _renderer is not null)
        {
            return;
        }

        _preparing = Task.Run(() =>
        {
            GraphicsDevice? device = null;
            GlyphRasteriser? rasteriser = null;

            try
            {
                FontSettings guessed = font();

                device = GraphicsDevice.Open();
                rasteriser = new GlyphRasteriser();

                GlyphAtlas atlas = GlyphAtlas.For(device, guessed, rasteriser: rasteriser);
                Prepared ready = new(device, rasteriser, atlas,
                                     CellRenderer.For(device, atlas, rasteriser.Measure(guessed)));

                // Where QS75's timeline can see it: before "device" is what this was for.
                StartupTimeline.Mark("prepared");

                return ready;
            }
            catch (Exception)
            {
                rasteriser?.Dispose();
                device?.Dispose();

                return null;
            }
        });
    }

    /// <summary>Whether what <see cref="Prepare"/> opened was taken by the first pane.</summary>
    public bool UsedPrepared { get; private set; }

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

    /// <summary>
    /// Changes the typeface or its size for every pane, without a restart (QS135).
    ///
    /// <para><b>Recorded here and applied by the loop before its next pass</b>, because the atlas
    /// and the renderer are the loop's: rebuilding the cache under a frame that is reading it would
    /// be two threads in one atlas. One font for every pane, as there has been one atlas since QS49
    /// — so this is the window's font and not a pane's, and the last one asked for wins.</para>
    /// </summary>
    public void UseFont(string family, float sizeInPoints, bool ligatures)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(family);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sizeInPoints);

        lock (_guard)
        {
            _refont = (family, sizeInPoints, ligatures);
        }

        // The loop sleeps on this, and a font change is nothing any parser will ever report.
        Damage.Set();
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

        Refont(panes);

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

        // Prepared and never taken: no pane was ever laid out.
        if (Interlocked.Exchange(ref _preparing, null) is { } preparing
            && preparing.GetAwaiter().GetResult() is { } prepared)
        {
            prepared.Renderer.Dispose();
            prepared.Atlas.Dispose();
            prepared.Rasteriser.Dispose();
            prepared.Device.Dispose();
        }

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

        if (Adopt(window, font))
        {
            return true;
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
    /// Takes what <see cref="Prepare"/> opened, waiting for it if it is still opening, where it is on
    /// the adapter this window's monitor uses; otherwise lets it go.
    /// </summary>
    private bool Adopt(nint window, FontSettings font)
    {
        if (Interlocked.Exchange(ref _preparing, null) is not { } preparing
            || preparing.GetAwaiter().GetResult() is not { } prepared)
        {
            return false;
        }

        AdapterInfo? wanted = new DxgiAdapterProbe().ForOutputWindow(window);

        // No adapter owns the window: the chain falls to the default one, which is what was opened.
        if (wanted is not null && wanted != prepared.Device.Adapter.Adapter)
        {
            prepared.Renderer.Dispose();
            prepared.Atlas.Dispose();
            prepared.Rasteriser.Dispose();
            prepared.Device.Dispose();

            return false;
        }

        _device = prepared.Device;
        _rasteriser = prepared.Rasteriser;
        _atlas = prepared.Atlas;
        _renderer = prepared.Renderer;

        if (_atlas.Font != font)
        {
            _atlas.UseFont(font);
            _renderer.UseMetrics(_rasteriser.Measure(font));
        }

        UsedPrepared = true;

        return true;
    }

    /// <summary>
    /// Applies a font asked for since the last pass: the atlas is pointed at it, the cell measured
    /// again, and every pane asked what grid it now holds.
    ///
    /// <para>A font asked for before there is a device stays asked for, and the first pass after
    /// the device opens applies it. One that cannot be measured — a family this machine does not
    /// have — leaves the font that was drawing, because a terminal with no glyphs is worse than a
    /// setting that did not take.</para>
    /// </summary>
    private void Refont((TerminalView View, Emulator Model)[] panes)
    {
        (string Family, float SizeInPoints, bool Ligatures)? wanted;

        lock (_guard)
        {
            if (_refont is null || _atlas is null || _renderer is null || _rasteriser is null)
            {
                return;
            }

            wanted = _refont;
            _refont = null;
        }

        (string family, float size, bool ligatures) = wanted.Value;
        FontSettings was = _atlas.Font;
        FontSettings font = new(family, size, was.Dpi) { Ligatures = ligatures };

        if (font == was)
        {
            return;
        }

        CellMetrics metrics;

        try
        {
            metrics = _rasteriser.Measure(font);
        }
        catch (Exception)
        {
            return;
        }

        _atlas.UseFont(font);
        _renderer.UseMetrics(metrics);

        foreach ((TerminalView view, _) in panes)
        {
            view.Refit();
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
