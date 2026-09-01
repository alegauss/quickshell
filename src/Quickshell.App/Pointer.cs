using System.Windows.Input;
using Quickshell.Render;
using Quickshell.Terminal;

namespace Quickshell.App;

/// <summary>
/// The mouse, in the terminal's coordinates rather than the window's.
///
/// <para><b>Every gesture this client owns is decided here, and none of it touches Windows.</b> A
/// pixel becomes a cell, a run of clicks becomes a mode, and a drag past the edge becomes a number
/// of lines to scroll. That is the whole of what a selection needs, and it is arithmetic — so it is
/// checked as arithmetic rather than by driving a window.</para>
///
/// <para><b>The click run is counted here rather than taken from Windows</b>, for two reasons. The
/// pane's window class does not ask for double-click messages, and asking for them would still leave
/// the triple click — which has no message at all and is the gesture that selects a whole line.</para>
/// </summary>
public sealed class Pointer
{
    /// <summary>
    /// How far a click may move and still be part of the same run, in pixels.
    ///
    /// <para>A person double-clicking does not hold the mouse perfectly still, and a run broken by
    /// two pixels of hand movement is a double click that selects one character.</para>
    /// </summary>
    public const int Slop = 4;

    private int _clicks;
    private int _x = int.MinValue;
    private int _y = int.MinValue;
    private long _at;

    /// <summary>How long a run may pause for, from the user's own double-click setting.</summary>
    public int Within { get; init; } = 500;

    /// <summary>
    /// Counts one press, and answers how many clicks it makes in a row.
    /// </summary>
    /// <param name="x">Where it landed, in the pane's pixels.</param>
    /// <param name="y">Its row of pixels.</param>
    /// <param name="milliseconds">When, on any clock that counts forwards.</param>
    /// <returns>1, 2 or 3. A fourth click starts again at one, which is what every editor does.</returns>
    public int Clicked(int x, int y, long milliseconds)
    {
        bool same = milliseconds - _at <= Within
                    && Math.Abs(x - _x) <= Slop
                    && Math.Abs(y - _y) <= Slop;

        _clicks = same ? (_clicks % 3) + 1 : 1;
        _x = x;
        _y = y;
        _at = milliseconds;

        return _clicks;
    }

    /// <summary>Forgets the run, so the next press is a first click.</summary>
    public void Rest()
    {
        _clicks = 0;
        _x = int.MinValue;
        _y = int.MinValue;
        _at = 0;
    }

    /// <summary>
    /// What a run of clicks with these modifiers means.
    ///
    /// <para>Alt takes a rectangle whatever the count is, because block selection is the only way to
    /// copy one column out of tabular output and somebody reaching for it has already decided.</para>
    /// </summary>
    public static SelectionMode ModeFor(int clicks, ModifierKeys modifiers) =>
        (modifiers & ModifierKeys.Alt) != 0
            ? SelectionMode.Block
            : clicks switch
            {
                >= 3 => SelectionMode.Line,
                2 => SelectionMode.Word,
                _ => SelectionMode.Character,
            };

    /// <summary>
    /// The cell a pixel is in, as a point that survives the screen scrolling underneath it.
    ///
    /// <para><b>Clamped to the screen rather than refused.</b> A drag leaves the pane constantly —
    /// that is how a person selects past the bottom — and every pixel outside it still names the row
    /// the drag is reaching towards.</para>
    ///
    /// <para>The column is allowed one past the last, because a selection that ends after the final
    /// character of a row has to be expressible; <see cref="Selection.Contains"/> reads the far end
    /// as exclusive.</para>
    /// </summary>
    /// <param name="x">Pixels from the pane's left edge; negative is off the left of it.</param>
    /// <param name="y">Pixels from its top.</param>
    /// <param name="box">How big one cell is.</param>
    /// <param name="buffer">The screen, for its size and for what line the top row is.</param>
    public static SelectionPoint CellAt(int x, int y, CellMetrics box, TerminalBuffer buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);

        int row = Math.Clamp(Divided(y, box.Height), 0, Math.Max(0, buffer.Rows - 1));
        int column = Math.Clamp(Divided(x, box.Width), 0, buffer.Columns);

        return new SelectionPoint(buffer.AbsoluteLine(row), column);
    }

    /// <summary>
    /// How many lines a drag at this height is asking to scroll by, and zero inside the pane.
    ///
    /// <para>One line per cell past the edge, so a drag just outside creeps and one flung to the top
    /// of the screen moves quickly — which is the behaviour a person expects without knowing they
    /// expect it.</para>
    /// </summary>
    /// <param name="y">Where the drag is, in the pane's pixels.</param>
    /// <param name="height">The pane's height in pixels.</param>
    /// <param name="box">How big one cell is.</param>
    public static int ScrollFor(int y, int height, CellMetrics box)
    {
        if (y < 0)
        {
            return Divided(y, box.Height);
        }

        return y >= height ? Divided(y - height, box.Height) + 1 : 0;
    }

    /// <summary>
    /// Divides towards negative infinity, which is what a coordinate above the pane needs.
    ///
    /// <para>C# truncates towards zero, so a pixel one row above the top divides to 0 and lands on
    /// the first row — a drag upwards that never leaves it.</para>
    /// </summary>
    private static int Divided(int value, int by)
    {
        int size = Math.Max(1, by);

        return value >= 0 ? value / size : ((value + 1) / size) - 1;
    }
}
