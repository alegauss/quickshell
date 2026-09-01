using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Quickshell.Render;
using Quickshell.Terminal;

namespace Quickshell.App;

/// <summary>
/// A pane, a model and the loop between them, for as long as this is alive.
///
/// <para><b>This is the object the client holds.</b> A <see cref="TerminalView"/> needs a handle and
/// a size, and a window has neither until WPF has laid it out — so something has to wait, and a
/// client that waited on the way up would be a client that shows nothing until a device opens. This
/// waits instead, off the first-paint path, and the window is on screen throughout.</para>
///
/// <para>Disposing it stops the loop before releasing the device, in that order and not the other:
/// a device released under a thread that is mid-frame is a crash on the way out, which is the worst
/// kind because the user has already decided to leave.</para>
/// </summary>
public sealed class PaneAttachment : IDisposable
{
    private readonly TerminalPane _pane;
    private readonly Emulator _emulator;
    private readonly DamageSignal _damage;
    private readonly string _family;
    private readonly float _sizeInPoints;
    private readonly CancellationTokenSource _stop = new();
    private readonly Pointer _pointer = new();

    private Task _loop = Task.CompletedTask;
    private bool _dragging;
    private bool _disposed;

    internal PaneAttachment(TerminalPane pane, Emulator emulator, DamageSignal damage,
                            string family, float sizeInPoints)
    {
        ArgumentNullException.ThrowIfNull(pane);
        ArgumentNullException.ThrowIfNull(emulator);
        ArgumentNullException.ThrowIfNull(damage);
        ArgumentException.ThrowIfNullOrWhiteSpace(family);

        _pane = pane;
        _emulator = emulator;
        _damage = damage;
        _family = family;
        _sizeInPoints = sizeInPoints;

        // The model's own method, until a session replaces it. See Resized.
        Resized = emulator.Resize;

        _pane.SizeChanged += Sized;
        _pane.Mouse += Pointed;

        // In case the pane already has both, which is the case when a caller attaches to a window
        // that is already up.
        Begin();
    }

    /// <summary>The view, once there was something to open it on. Null until then.</summary>
    public TerminalView? View { get; private set; }

    /// <summary>
    /// Who is told the grid changed size, and it is the second and third of QS32's three parties.
    ///
    /// <para><b>A session's <c>Resize</c> when there is a session, and this is why it is settable
    /// rather than wired.</b> The pipeline takes a size in order with the bytes around it and tells
    /// the far end once the drag settles; calling the model directly, as the default below does,
    /// has neither property and is only safe because nothing is parsing into it yet. QS126 is where
    /// a session arrives and this stops being the model's own method.</para>
    /// </summary>
    public Action<int, int>? Resized { get; set; }

    /// <summary>
    /// What went wrong opening the device, or null.
    ///
    /// <para>Kept rather than thrown: this runs on a layout callback, and a machine with no usable
    /// adapter should get a client with an unpainted pane and a line in a diagnostic bundle, not a
    /// window that vanishes during its own first layout.</para>
    /// </summary>
    public Exception? Failed { get; private set; }

    /// <summary>Stops the loop and releases the device.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _pane.SizeChanged -= Sized;
        _pane.Mouse -= Pointed;

        _stop.Cancel();

        // Bounded, because a loop that will not stop must not hold a window open. The device is
        // released either way: the process is leaving.
        _loop.Wait(TimeSpan.FromSeconds(2));

        View?.Dispose();
        View = null;

        _stop.Dispose();
    }

    /// <summary>
    /// The pane is a different size: open the view if it was waiting for one, otherwise pass the
    /// new pixels to the loop.
    /// </summary>
    private void Sized(object sender, SizeChangedEventArgs e)
    {
        if (View is null)
        {
            Begin();

            return;
        }

        DpiScale dpi = VisualTreeHelper.GetDpi(_pane);

        View.Resize((uint)Math.Max(1d, _pane.ActualWidth * dpi.DpiScaleX),
                    (uint)Math.Max(1d, _pane.ActualHeight * dpi.DpiScaleY));

        // The loop sleeps on this signal, and a resize is the one change no parser ever reports.
        // Without it a window resized while the host is silent keeps the frame it had.
        _damage.Set();
    }

    /// <summary>
    /// What is selected, as text, with a wrapped line joined back into one.
    ///
    /// <para>Empty where nothing is selected, which is what a copy with no selection should put on
    /// the clipboard: a client that emptied somebody's clipboard because they pressed a chord with
    /// nothing highlighted would have destroyed something they still wanted.</para>
    /// </summary>
    public string Selected()
    {
        if (View is not { Selection.IsActive: true } view)
        {
            return string.Empty;
        }

        TerminalBuffer buffer = _emulator.Buffer;
        int length = view.Selection.MeasureCopy(buffer);

        if (length <= 0)
        {
            return string.Empty;
        }

        char[] text = new char[length];

        return view.Selection.CopyTo(buffer, text) is var written && written > 0
            ? new string(text, 0, written)
            : string.Empty;
    }

    /// <summary>
    /// How many lines one wheel notch moves.
    ///
    /// <para>Three, which is what Windows itself reports as the system default and what every other
    /// terminal on this platform does. Read once rather than per notch: a user changing it mid-drag
    /// is not a case, and a system call per wheel event is a call in the path of the one gesture
    /// people do fastest.</para>
    /// </summary>
    public const int LinesPerNotch = 3;

    /// <summary>
    /// Moves the view back through the history, or forward towards the bottom.
    /// </summary>
    /// <returns>Whether anything moved, which is false at either end.</returns>
    public bool ScrollBy(int lines)
    {
        if (View is not { } view || !view.Viewport.ScrollBy(_emulator.Buffer, lines))
        {
            return false;
        }

        Redraw(view);

        return true;
    }

    /// <summary>Returns to the newest output and follows it again.</summary>
    public void ToBottom()
    {
        if (View is not { } view || view.Viewport.IsAtBottom)
        {
            return;
        }

        view.Viewport.ToBottom();
        Redraw(view);
    }

    /// <summary>
    /// How far back the view is, and whether output arrived while it was there.
    /// </summary>
    public (long Depth, long Held, bool Unseen) Where() =>
        View is { } view
            ? (view.Viewport.Depth(_emulator.Buffer), _emulator.Buffer.LineCount,
               view.Viewport.HasUnseenOutput)
            : (0, 0, false);

    /// <summary>
    /// Finds text in the history and brings it on screen, selected.
    ///
    /// <para><b>The match becomes the selection</b>, which is not a shortcut: a found line has to be
    /// highlighted, and this client already has exactly one way of highlighting cells. It also means
    /// what was found can be copied without being selected again.</para>
    ///
    /// <para>Searching runs over logical lines, so a match the wrap fell inside is found — which is
    /// this line's falsification and is <see cref="Search"/>'s to keep.</para>
    /// </summary>
    /// <param name="needle">What to look for. Empty clears the selection and finds nothing.</param>
    /// <param name="forward">Which way to look from where the last match was.</param>
    /// <param name="caseSensitive">Whether capitals matter.</param>
    /// <returns>Where it was found, or null.</returns>
    public Match? Find(string needle, bool forward = true, bool caseSensitive = false)
    {
        if (View is not { } view)
        {
            return null;
        }

        if (string.IsNullOrEmpty(needle))
        {
            view.Selection.Clear();
            Redraw(view);

            return null;
        }

        TerminalBuffer buffer = _emulator.Buffer;

        // From just past the last match, so pressing again walks rather than finding the same line
        // for ever. With nothing found yet, from the bottom backwards and the top forwards.
        (long line, int column) = Resume(view, buffer, forward);

        if (!Search.TryFind(buffer, needle, line, column, forward, caseSensitive, out Match match))
        {
            return null;
        }

        view.Selection.Begin(buffer, new SelectionPoint(match.Line, match.Column),
                             SelectionMode.Character);

        view.Selection.Extend(buffer, After(buffer, match));

        Show(view, buffer, match.Line);
        Redraw(view);

        return match;
    }

    /// <summary>
    /// The cell just past a match, following it onto the next row where the wrap fell inside it.
    ///
    /// <para><b>A match found across a wrap has to be highlighted across it too.</b> Search runs
    /// over logical lines and a row is one line of the ring, so a match that begins near the right
    /// edge ends on the row below — and an end point left on the first row highlights the half the
    /// user can already see and not the half that proves it was found.</para>
    /// </summary>
    private static SelectionPoint After(TerminalBuffer buffer, Match match)
    {
        long line = match.Line;
        int column = match.Column + match.Cells;

        while (column > buffer.Columns)
        {
            column -= buffer.Columns;
            line++;
        }

        return new SelectionPoint(line, column);
    }

    /// <summary>
    /// Where the next search starts from, which is one column past the match before it.
    ///
    /// <para>Without this, searching forward finds the same match every time and the user concludes
    /// there is only one.</para>
    /// </summary>
    private static (long Line, int Column) Resume(TerminalView view, TerminalBuffer buffer,
                                                  bool forward)
    {
        if (!view.Selection.IsActive)
        {
            return forward
                ? (buffer.TopLine - buffer.ScrollbackLines, 0)
                : (buffer.TopLine + buffer.Rows, buffer.Columns);
        }

        SelectionPoint from = view.Selection.Start;

        return forward ? (from.Line, from.Column + 1) : (from.Line, from.Column - 1);
    }

    /// <summary>
    /// Brings a line on screen, and leaves the view alone where it already is.
    ///
    /// <para>A match already visible must not scroll: somebody stepping through matches on one
    /// screenful should see the highlight move, not the text.</para>
    /// </summary>
    private static void Show(TerminalView view, TerminalBuffer buffer, long line)
    {
        long top = view.Viewport.Top(buffer);

        if (line >= top && line < top + buffer.Rows)
        {
            return;
        }

        // A third of a screen above it, so what was found has context above rather than sitting on
        // the top edge with the reason for it off-screen.
        view.Viewport.ScrollBy(buffer, (int)(line - top) - (buffer.Rows / 3));
    }

    /// <summary>
    /// The picture changed and the terminal does not know.
    ///
    /// <para>Both calls, always: one forgets the frame on the glass, the other wakes a loop that
    /// would otherwise be asleep on a silent host. Either alone is a window that updates only when
    /// the host next prints something.</para>
    /// </summary>
    private void Redraw(TerminalView view)
    {
        view.Moved();
        _damage.Set();
    }

    /// <summary>
    /// The mouse on the pane, in the client's own gestures rather than the host's.
    ///
    /// <para><b>The host gets the mouse the moment it asks for it, and shift takes it back.</b> That
    /// is the convention every terminal follows and the only one that works: a program tracking the
    /// mouse owns the pointer, and a user who still wants to copy something out of it holds shift.
    /// Forwarding to the host is QS21's encoder and is not reached from here yet — until it is, a
    /// click inside a program that asked for the mouse does nothing, which is honest and is not a
    /// selection made behind that program's back.</para>
    /// </summary>
    private void Pointed(PaneMouse mouse)
    {
        if (View is not { } view)
        {
            return;
        }

        ModifierKeys held = Keyboard.Modifiers;

        if (mouse.Kind == PaneMouseKind.Wheeled)
        {
            Wheeled(mouse.Notches);

            return;
        }

        if (_emulator.MouseReporting != MouseTracking.Off && (held & ModifierKeys.Shift) == 0)
        {
            return;
        }

        switch (mouse.Kind)
        {
            case PaneMouseKind.Pressed:
                Press(view, mouse, held);
                break;

            case PaneMouseKind.Moved when _dragging:
                view.Selection.Extend(_emulator.Buffer,
                                      Pointer.CellAt(mouse.X, mouse.Y, view.Renderer.Metrics,
                                                     _emulator.Buffer));
                break;

            case PaneMouseKind.Released:
                _dragging = false;

                return;

            default:
                return;
        }

        // The terminal never sees a selection — nothing was printed — so the gate would answer that
        // the frame on the glass is still current. Both calls: one to forget that frame, one to wake
        // the loop that would otherwise be asleep on a silent host.
        view.Moved();
        _damage.Set();
    }

    /// <summary>
    /// A wheel notch, which does not always mean the scrollback.
    ///
    /// <para><b><see cref="Viewport.Wheel"/> decides and this only carries out one of its three
    /// answers.</b> Under a full-screen program there is no scrollback to move into, so the notch
    /// belongs to the program — as a mouse event where it asked for the mouse and as arrow keys
    /// where it did not, which is what makes a wheel work inside a pager that never heard of one.
    /// Neither of those is reachable from this client yet and both are QS155's; until then a notch
    /// under the alternate screen does nothing, which is honest and is not the terminal scrolling
    /// out from under a program that owns the screen.</para>
    /// </summary>
    private void Wheeled(int notches)
    {
        if (notches == 0 || Viewport.Wheel(_emulator) != WheelGoes.ToScrollback)
        {
            return;
        }

        // Away from the user is back through the history, which is the opposite sign.
        ScrollBy(-notches * LinesPerNotch);
    }

    /// <summary>
    /// A press: extend what is there where shift is held, and otherwise start again.
    ///
    /// <para>Shift-click extending rather than restarting is what lets somebody select a screenful
    /// by clicking at the top and shift-clicking at the bottom, which is how a person selects
    /// something too long to drag across.</para>
    /// </summary>
    private void Press(TerminalView view, PaneMouse mouse, ModifierKeys held)
    {
        TerminalBuffer buffer = _emulator.Buffer;
        SelectionPoint at = Pointer.CellAt(mouse.X, mouse.Y, view.Renderer.Metrics, buffer);

        _dragging = true;

        if ((held & ModifierKeys.Shift) != 0 && view.Selection.IsActive)
        {
            view.Selection.Extend(buffer, at);

            return;
        }

        int clicks = _pointer.Clicked(mouse.X, mouse.Y, Environment.TickCount64);

        view.Selection.Begin(buffer, at, Pointer.ModeFor(clicks, held));
    }

    private void Begin()
    {
        if (View is not null || Failed is not null || _disposed)
        {
            return;
        }

        if (_pane.PaneHandle == nint.Zero || _pane.ActualWidth < 1d || _pane.ActualHeight < 1d)
        {
            return;
        }

        // Pixels, not DIPs. The manifest declares this process per-monitor aware, which means
        // nothing is scaled for it afterwards and the numbers here are the ones that count.
        DpiScale dpi = VisualTreeHelper.GetDpi(_pane);

        try
        {
            View = TerminalView.Open(
                _pane.PaneHandle,
                (uint)Math.Max(1d, _pane.ActualWidth * dpi.DpiScaleX),
                (uint)Math.Max(1d, _pane.ActualHeight * dpi.DpiScaleY),
                new FontSettings(_family, _sizeInPoints, (float)dpi.PixelsPerInchX),
                _emulator.Palette);
        }
        catch (Exception error)
        {
            Failed = error;

            return;
        }

        // The grid the window turned out to hold is the size the model takes, and later the size the
        // far end is told. Here it is the first size rather than a resize, which is why nothing is
        // debounced and nobody is told: there is no previous size to have been wrong.
        _emulator.Resize(View.Columns, View.Rows);

        // Every size after this one, from the render thread once the swapchain has taken it.
        View.GridChanged += (columns, rows) => Resized?.Invoke(columns, rows);

        // A screen reader reads this buffer, and the texture is unreadable to assistive technology
        // by construction — this is the only path. Set here only for a caller that did not: WPF
        // builds an element's peer once, so a pane already asked about keeps whatever it answered.
        _pane.Reading ??= _emulator.Buffer;

        // Off the UI thread from here. Nothing else touches the device, which is what makes an
        // unsynchronised D3D11 context correct.
        _loop = Task.Run(() => View.RunAsync(_emulator, _damage, _stop.Token));
    }
}
