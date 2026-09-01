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
/// <para><b>The resize and the keystrokes are joined here too, and each crosses this boundary its
/// own way.</b> A resize is recorded by <see cref="Resize"/> and applied by the loop, because the
/// context belongs to the loop; a keystroke never touches this class at all, because
/// <see cref="Typist"/> writes to the channel and the frame it eventually causes arrives as damage
/// like any other.</para>
/// </summary>
public sealed class TerminalView : IDisposable
{
    private readonly GraphicsDevice _device;
    private readonly PresentSurface _surface;
    private readonly CellRenderer _renderer;
    private readonly GridPainter _painter;
    private readonly RedrawGate _gate = new();

    private CellInstance[] _cells;
    private long _wanted;
    private long _draws;
    private bool _showing = true;
    private bool _covered;

    private TerminalView(GraphicsDevice device, GlyphAtlas atlas, PresentSurface surface,
                         CellRenderer renderer, Palette palette)
    {
        _device = device;
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

    /// <summary>
    /// Draw calls issued into this surface, and no other's.
    ///
    /// <para><b>Counted here rather than read off the renderer, and QS165 is why.</b> Since QS49 the
    /// renderer is every pane's, so its own counter is the whole client's — a window with four panes
    /// reported each of them as having drawn what all four did. That was right in every case that
    /// read it, because each opened a share of its own, and it would have been wrong the first time
    /// anybody wrote the case worth writing: two panes, one idle, asserting the idle one drew
    /// nothing.</para>
    ///
    /// <para>Kept beside <see cref="Frames"/> rather than folded into it, because they are two
    /// different claims: the gate counts frames this pane was owed, and this counts draw calls it
    /// actually issued. A gap between them would be a frame claimed and never drawn.</para>
    /// </summary>
    public long Draws => _draws;

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
    /// What is selected, drawn with its two colours swapped.
    ///
    /// <para><b>Read on the render thread and written on WPF's</b>, which is safe for exactly the
    /// reason the model is: the frame draws whatever the selection says at the instant it is asked,
    /// and a drag that lands between two frames is a highlight one frame late. What must not be
    /// missed is the frame itself, which is why <see cref="Moved"/> exists rather than the drag
    /// simply mutating this and hoping.</para>
    /// </summary>
    public Selection Selection { get; } = new();

    /// <summary>
    /// Which part of the history is on screen.
    ///
    /// <para>Anchored to an absolute line, so output arriving while somebody is reading moves
    /// nothing: the paragraph they are halfway through stays where it is. That is
    /// <see cref="Viewport"/>'s whole design and this is where it reaches the glass.</para>
    /// </summary>
    public Viewport Viewport { get; } = new();

    /// <summary>
    /// The selection changed, so the picture is wrong even though the model is not.
    ///
    /// <para>A selection is invisible to the terminal: nothing was printed and no damage was raised,
    /// so the gate would answer that the screen on the glass is still current. This is the same
    /// admission <see cref="Invalidate"/> makes for a theme or a font, named for the caller that has
    /// it hundreds of times a second.</para>
    /// </summary>
    public void Moved() => _gate.Invalidate();

    /// <summary>
    /// Whether anybody can see this pane, which is false for one behind another tab.
    ///
    /// <para><b>QS166: a pane nobody can see draws nothing at all.</b> A client with eight tabs of
    /// busy hosts would otherwise draw eight panes and show one, and seven of those presents are a
    /// queue slot and a copy for a window that is not on screen.</para>
    ///
    /// <para>Coming back forgets the frame on the glass, because the picture in a swapchain nobody
    /// has been drawing into is however stale the host left it.</para>
    /// </summary>
    public bool Showing
    {
        get => _showing;

        set
        {
            if (_showing == value)
            {
                return;
            }

            _showing = value;

            if (value)
            {
                _gate.Invalidate();
            }
        }
    }

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
    /// <param name="palette">The session's palette, resolved afresh every frame.</param>
    /// <param name="device">The one device, which this view does not own.</param>
    /// <param name="atlas">The one atlas, likewise.</param>
    /// <param name="renderer">The one set of shaders, likewise.</param>
    /// <remarks>
    /// <para><b>Only the swapchain is this view's, and QS49 is why.</b> Everything else here is
    /// process-wide and arrives already built: a view that opened a device would be sixteen devices
    /// in a window with sixteen panes, and sixteen copies of the same rasterised font.</para>
    /// </remarks>
    public static TerminalView On(GraphicsDevice device, GlyphAtlas atlas, CellRenderer renderer,
                                  nint window, uint width, uint height, Palette palette)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(atlas);
        ArgumentNullException.ThrowIfNull(renderer);
        ArgumentNullException.ThrowIfNull(palette);
        ArgumentOutOfRangeException.ThrowIfZero(window);

        PresentSurface surface = PresentSurface.For(device, window, Math.Max(1u, width),
                                                    Math.Max(1u, height));

        return new TerminalView(device, atlas, surface, renderer, palette);
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
    /// Whether something is over this window, asked only of a pane that has already been covered.
    ///
    /// <para><b>The test costs no frame, and asking it every time would still cost a call.</b> So it
    /// is asked only once a present has come back occluded — a window nobody has covered never pays
    /// for the question at all, and a covered one pays a test rather than a frame.</para>
    ///
    /// <para>Coming out from under forgets the frame on the glass: DXGI kept whatever was last
    /// presented, and what the terminal has been doing since is not in it.</para>
    /// </summary>
    private bool Hidden()
    {
        if (!_covered)
        {
            return false;
        }

        if (_surface.Covered())
        {
            return true;
        }

        _covered = false;

        _gate.Invalidate();

        return false;
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

        // Before the gate, and deliberately: the gate is about whether the picture changed, and this
        // is about whether anybody could see it. A pane behind another tab is not asked either
        // question again until it comes forward — QS166.
        if (!_showing || Hidden())
        {
            return false;
        }

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

        // Told before the frame is built, so a scrollbar can say output arrived. It moves nothing:
        // the viewport is anchored to a line and this only records that the bottom has grown.
        Viewport.Produced();

        _painter.Paint(buffer, _cells, caret ? damage.CursorRow : -1, damage.CursorColumn,
                       caret ? Cursor : CursorShape.None, _renderer.Metrics, Selection, Viewport);

        // Waited for here and not at the top of the loop: the wait is for a queue slot, and a
        // wake-up with nothing to draw should not be parked on the swapchain.
        _surface.WaitForNextFrame();

        long occluded = _surface.Occlusions;

        _renderer.Draw(_surface, _cells.AsSpan(0, _painter.Painted), buffer.Columns);
        _surface.Present();

        _draws++;

        // The frame went nowhere: something is over this window. Remembered so the next wake-up asks
        // DXGI whether it still is, rather than drawing another frame to find out.
        _covered = _surface.Occlusions != occluded;

        return true;
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
    /// <param name="share">The one device, atlas and render loop every pane in the process uses.</param>
    /// <param name="family">The font family, from the user's settings.</param>
    /// <param name="sizeInPoints">Its size.</param>
    /// <returns>The attachment, which stops the loop and releases the device when disposed.</returns>
    /// <param name="ligatures">Whether the font's ligatures are formed, which users are divided on.</param>
    public static PaneAttachment Attach(TerminalPane pane, Emulator emulator, TerminalShare share,
                                        string family, float sizeInPoints, bool ligatures = true) =>
        new(pane, emulator, share, family, sizeInPoints, ligatures);

    /// <summary>
    /// Forgets the last frame, so the next wake-up draws.
    ///
    /// <para>For everything the terminal cannot see: a theme, a font, a device recreated. Each
    /// leaves the model's damage identical and the picture wrong.</para>
    /// </summary>
    public void Invalidate() => _gate.Invalidate();

    /// <summary>
    /// Releases the swapchain, and nothing else.
    ///
    /// <para>The device, the atlas and the shaders belong to <see cref="TerminalShare"/> and outlive
    /// every pane that drew with them — a view that released them would take the other panes' render
    /// path with it the first time one was closed.</para>
    /// </summary>
    public void Dispose() => _surface.Dispose();
}
