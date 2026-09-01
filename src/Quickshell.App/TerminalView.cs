using Quickshell.Render;
using Quickshell.Terminal;

namespace Quickshell.App;

/// <summary>
/// A swapchain on a pane's handle and the loop that decides when to draw into it.
///
/// <para><b>QS116: this is where the three halves meet.</b> The renderer draws a grid, the pipeline
/// parses bytes into a model and raises a signal, and the pane owns a handle — each built and tested
/// on its own, and until this class none of them held the others. What was missing was never a
/// feature; it was an object with a device, a surface and a loop in the same scope.</para>
///
/// <para><b>The loop waits on the damage signal, not on a clock.</b> Block C's criterion is that an
/// idle window issues no draw calls, and a frame drawn on a timer is a frame drawn for nothing: it
/// costs a wake-up, a present and a GPU queue slot to put the same picture back. So the wait has no
/// interval — it ends when the parser says something changed, or when the cursor's blink phase is
/// due, and <see cref="CellRenderer.NextCursorWake"/> answers null when even that is not coming. A
/// window whose host is silent and whose cursor is hidden sleeps until a byte arrives.</para>
///
/// <para><b>It draws on its own thread and never on WPF's.</b> The D3D11 context here is touched
/// from the loop and from nowhere else, which is what makes an unsynchronised context correct — and
/// it is why a resize cannot be applied where it arrives. The UI thread's job is to hand this the
/// handle and then leave it alone.</para>
///
/// <para>What is still unjoined: the resize in the three parts QS32 settled, and keystrokes. Both
/// have to cross the same thread boundary this one does, and neither is here yet.</para>
/// </summary>
public sealed class TerminalView : IDisposable
{
    private readonly GraphicsDevice _device;
    private readonly GlyphRasteriser _rasteriser;
    private readonly GlyphAtlas _atlas;
    private readonly PresentSurface _surface;
    private readonly CellRenderer _renderer;
    private readonly GridPainter _painter;
    private readonly RedrawGate _gate = new();

    private CellInstance[] _cells;
    private long _wanted;

    private TerminalView(GraphicsDevice device, GlyphRasteriser rasteriser, GlyphAtlas atlas,
                         PresentSurface surface, CellRenderer renderer, Palette palette)
    {
        _device = device;
        _rasteriser = rasteriser;
        _atlas = atlas;
        _surface = surface;
        _renderer = renderer;
        _painter = new GridPainter(atlas, palette);

        (Columns, Rows) = renderer.Metrics.GridFor(surface.Width, surface.Height);

        _cells = new CellInstance[Math.Max(1, Columns * Rows)];
    }

    /// <summary>How many columns the surface holds at this font and size.</summary>
    public int Columns { get; private set; }

    /// <summary>How many rows.</summary>
    public int Rows { get; private set; }

    /// <summary>
    /// The grid this view now holds, raised after a resize has reached the swapchain.
    ///
    /// <para><b>Raised on the render thread, and after rather than before.</b> The order is QS32's:
    /// what a model reflows to and what the far end is eventually told is the size the window
    /// actually has, not the size it was on its way to. A handler that reflowed first would be
    /// reflowing to a grid the swapchain might still fail to take.</para>
    /// </summary>
    public event Action<int, int>? GridChanged;

    /// <summary>Frames this view has authorised, which is the number the idle criterion reads.</summary>
    public long Frames => _gate.Frames;

    /// <summary>Wake-ups that found nothing to draw.</summary>
    public long Skipped => _gate.Skipped;

    /// <summary>Draw calls the renderer has issued into this surface.</summary>
    public long Draws => _renderer.Draws;

    /// <summary>
    /// What the cursor is drawn as.
    ///
    /// <para>A block, because nothing parses DECSCUSR yet and inventing a shape the host did not ask
    /// for would be worse than the one every terminal starts with. It is settable so the shape has
    /// somewhere to arrive when it becomes the model's.</para>
    /// </summary>
    public CursorShape Cursor { get; set; } = CursorShape.Block;

    /// <summary>The renderer, for the blink and the colours a settings surface changes.</summary>
    public CellRenderer Renderer => _renderer;

    /// <summary>
    /// The swapchain on the pane's handle, for what a diagnostic bundle asks it: how deep the
    /// present queue is, how many frames reached the glass and how many were occluded.
    /// </summary>
    public PresentSurface Surface => _surface;

    /// <summary>
    /// The device this view drew with, which is the one the crash report has been saying it does
    /// not hold. Nothing outside this class draws with it.
    /// </summary>
    public GraphicsDevice Device => _device;

    /// <summary>
    /// Opens a device, an atlas and a swapchain on one window.
    /// </summary>
    /// <param name="window">
    /// The handle to present into — <see cref="TerminalPane.PaneHandle"/> and not the host's own.
    /// A swapchain on WPF's window would draw over the whole client area including the tab strip.
    /// </param>
    /// <param name="width">The pane's width in pixels.</param>
    /// <param name="height">Its height.</param>
    /// <param name="font">What to rasterise with. Its measured cell decides the grid.</param>
    /// <param name="palette">The session's palette, resolved afresh every frame.</param>
    public static TerminalView Open(nint window, uint width, uint height, FontSettings font,
                                    Palette palette)
    {
        ArgumentNullException.ThrowIfNull(palette);
        ArgumentOutOfRangeException.ThrowIfZero(window);

        GraphicsDevice device = GraphicsDevice.Open(outputWindow: window);
        GlyphRasteriser rasteriser = new();

        try
        {
            GlyphAtlas atlas = GlyphAtlas.For(device, font, rasteriser: rasteriser);
            PresentSurface surface = PresentSurface.For(device, window, Math.Max(1u, width),
                                                        Math.Max(1u, height));
            CellRenderer renderer = CellRenderer.For(device, atlas, rasteriser.Measure(font));

            return new TerminalView(device, rasteriser, atlas, surface, renderer, palette);
        }
        catch
        {
            rasteriser.Dispose();
            device.Dispose();

            throw;
        }
    }

    /// <summary>
    /// The window is a different size in pixels.
    ///
    /// <para><b>Recorded here and applied on the render thread</b>, which is the whole reason this
    /// method does nothing else. A resize reallocates the swapchain's buffers and draws into them,
    /// and the D3D11 context that would do it is being used by the loop; applying it where it
    /// arrives would be two threads in one context, on the one path guaranteed to be busy — a drag
    /// fires continuously.</para>
    ///
    /// <para>The last size wins, and the ones in between are never drawn. A drag across a screen
    /// produces hundreds of these, and every one of them that reached the swapchain would be a
    /// buffer reallocation for a size the window has already left.</para>
    /// </summary>
    /// <param name="width">The pane's new width in pixels.</param>
    /// <param name="height">Its new height.</param>
    public void Resize(uint width, uint height)
    {
        // One field, so a wake-up cannot read a width from one size and a height from another.
        Interlocked.Exchange(ref _wanted, ((long)Math.Max(1u, width) << 32) | Math.Max(1u, height));
    }

    /// <summary>
    /// Takes whatever size arrived while the loop was elsewhere.
    ///
    /// <para>Called on the render thread and nowhere else. The gate is invalidated because a
    /// resized window is a changed picture the terminal knows nothing about — the model's damage
    /// is identical across a resize that only moved pixels.</para>
    /// </summary>
    private void ApplyResize()
    {
        long wanted = Interlocked.Exchange(ref _wanted, 0);

        if (wanted == 0)
        {
            return;
        }

        uint width = (uint)(wanted >> 32);
        uint height = (uint)wanted;

        if (width == _surface.Width && height == _surface.Height)
        {
            return;
        }

        _surface.Resize(width, height);
        _gate.Invalidate();

        (int columns, int rows) = _renderer.Metrics.GridFor(_surface.Width, _surface.Height);

        // A window dragged narrower than one cell is not a grid. Clamped rather than refused,
        // because some programs divide by it and none of them expects a zero.
        columns = Math.Max(1, columns);
        rows = Math.Max(1, rows);

        if (columns == Columns && rows == Rows)
        {
            // Pixels moved and the grid did not, which is most of a slow drag. The frame is owed;
            // the model and the far end are not.
            return;
        }

        Columns = columns;
        Rows = rows;

        GridChanged?.Invoke(columns, rows);
    }

    /// <summary>
    /// Draws one frame if the screen is not the one already on the glass.
    ///
    /// <para>The gate is asked once and the answer is acted on, which is the contract it documents:
    /// asking twice without drawing would have the second answer be no about a window that never got
    /// the first frame.</para>
    /// </summary>
    /// <param name="emulator">The model to draw. Read on this thread while the parser writes on
    /// its own — see <see cref="Damage"/> for why that is safe and why it needs no lock.</param>
    /// <returns>True where a frame was drawn and presented.</returns>
    public bool DrawIfNeeded(Emulator emulator)
    {
        ArgumentNullException.ThrowIfNull(emulator);

        // First, because a resize decides both what the frame is drawn into and what the model is
        // about to be reflowed to. Asking the model for its damage first would read a grid that is
        // one size behind the window.
        ApplyResize();

        Damage damage = emulator.Damage;

        if (!_gate.Claim(damage, _renderer.CursorShowing))
        {
            return false;
        }

        TerminalBuffer buffer = emulator.Buffer;
        int needed = buffer.Columns * buffer.Rows;

        // Only ever on the way up, and a resize is the only thing that moves it. A frame in the
        // steady state allocates nothing, which is what GridPainter is built for.
        if (_cells.Length < needed)
        {
            _cells = new CellInstance[needed];
        }

        // A cursor the host has hidden, or one the blink has dark this instant, is painted as no
        // cursor at all rather than drawn and then hidden: the instance is the frame.
        bool caret = damage.CursorVisible && _renderer.CursorShowing;

        _painter.Paint(buffer, _cells, caret ? damage.CursorRow : -1, damage.CursorColumn,
                       caret ? Cursor : CursorShape.None, _renderer.Metrics);

        // Waited for here and not at the top of the loop: the wait is for a queue slot, and a
        // wake-up with nothing to draw should not be parked on the swapchain.
        _surface.WaitForNextFrame();

        _renderer.Draw(_surface, _cells.AsSpan(0, _painter.Painted), buffer.Columns);
        _surface.Present();

        return true;
    }

    /// <summary>
    /// Draws until cancelled, waking only for something that changes the picture.
    /// </summary>
    /// <param name="emulator">The model to draw.</param>
    /// <param name="damage">What the pipeline sets when the parser has drained a batch.</param>
    /// <param name="token">Stops the loop. Cancelling it is how a session ends.</param>
    public async Task RunAsync(Emulator emulator, DamageSignal damage,
                               CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(emulator);
        ArgumentNullException.ThrowIfNull(damage);

        // The first frame is owed unconditionally: the pane's handle is a rectangle the colour of
        // whatever was behind it until something presents into it, and nothing has changed yet.
        DrawIfNeeded(emulator);

        // Held across iterations on purpose. A waiter abandoned when the blink deadline wins would
        // still be in the queue, and DamageSignal takes the change when it wakes — so a discarded
        // wait is a wake-up consumed by nobody, and the frame behind it is never drawn.
        Task? waiting = null;

        try
        {
            while (!token.IsCancellationRequested)
            {
                waiting ??= damage.WaitAsync(token);

                // Null is the answer that matters: no blink, no clock, nothing that changes on its
                // own. The loop then sleeps on the host alone, and an idle window costs nothing.
                TimeSpan? wake = _renderer.NextCursorWake();

                Task woken = wake is null
                    ? waiting
                    : await Task.WhenAny(waiting, Task.Delay(wake.Value, token))
                                .ConfigureAwait(false);

                if (woken == waiting)
                {
                    // Awaited rather than assumed complete, so a cancellation leaves by the one
                    // exit below and the change it was carrying is not counted as drawn.
                    await waiting.ConfigureAwait(false);

                    waiting = null;
                }

                if (token.IsCancellationRequested)
                {
                    return;
                }

                DrawIfNeeded(emulator);
            }
        }
        catch (OperationCanceledException)
        {
            // The end of a session, and not a fault.
        }
    }

    /// <summary>
    /// Opens a view on a pane the moment it has both a handle and a size, and runs its loop.
    ///
    /// <para><b>Not at construction, because neither exists then.</b> A pane's handle is built during
    /// layout and its size is decided by it, so the earliest honest moment is the first size it is
    /// given — which is what this waits for. That is also why the client can show a window before any
    /// of this happens: the first paint does not wait for a device.</para>
    ///
    /// <para>The DPI is the pane's own and not a constant. A glyph rasterised for 96 and shown on a
    /// 150% display is a blurred glyph, and a swapchain sized in DIPs is a terminal that is two
    /// thirds of the window.</para>
    /// </summary>
    /// <param name="pane">The pane to draw into.</param>
    /// <param name="emulator">The model. Resized to whatever grid the pane turns out to hold.</param>
    /// <param name="damage">What a session sets when the parser has drained a batch.</param>
    /// <param name="family">The font family, from the user's settings.</param>
    /// <param name="sizeInPoints">Its size.</param>
    /// <returns>The attachment, which stops the loop and releases the device when disposed.</returns>
    public static PaneAttachment Attach(TerminalPane pane, Emulator emulator, DamageSignal damage,
                                        string family, float sizeInPoints) =>
        new(pane, emulator, damage, family, sizeInPoints);

    /// <summary>
    /// Forgets the last frame, so the next wake-up draws.
    ///
    /// <para>For everything the terminal cannot see: a theme, a font, a device recreated. Each
    /// leaves the model's damage identical and the picture wrong.</para>
    /// </summary>
    public void Invalidate() => _gate.Invalidate();

    /// <inheritdoc/>
    public void Dispose()
    {
        _renderer.Dispose();
        _surface.Dispose();
        _atlas.Dispose();
        _rasteriser.Dispose();
        _device.Dispose();
    }
}
