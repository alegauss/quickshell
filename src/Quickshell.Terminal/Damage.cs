namespace Quickshell.Terminal;

/// <summary>
/// Everything a consumer has to compare to know whether the screen it drew is still the screen.
///
/// <para><b>A value and not a callback.</b> The parser mutates on one thread and the renderer reads
/// on another; what crosses between them is this, copied out and compared to the last one. A consumer
/// that held a reference to the emulator instead would be comparing fields that move between the
/// comparisons.</para>
///
/// <para><b>Reading it is not atomic, and it does not need to be.</b> Eight fields are read while
/// the parser may be writing, so a value can be assembled from two moments — but the only way that
/// could hurt is by looking <em>equal</em> to a screen already drawn, and it cannot:
/// <see cref="Generation"/> only ever goes up, so a torn read is at worst one wasted frame and never
/// a stale window. The expensive guarantee is unnecessary, which is the reason there is no lock
/// here.</para>
///
/// <para><b>Each field is here because something changes it without changing anything else.</b>
/// <see cref="Generation"/> is the cells. <see cref="TopLine"/> is a scroll, which moves every row's
/// position and no row's content. The cursor's three fields are the damage source with no mutation
/// behind it at all — a program that only moves the cursor has still changed what is on screen.
/// <see cref="Alternate"/> is the screen switch, whose two buffers each count their own generation
/// and would otherwise compare equal across the swap.</para>
/// </summary>
/// <param name="Generation">The active buffer's mutation count.</param>
/// <param name="TopLine">Which line of the buffer's life is at the top of the screen.</param>
/// <param name="Columns">Cells across, which a resize changes.</param>
/// <param name="Rows">Rows down.</param>
/// <param name="CursorRow">The cursor's row within the screen.</param>
/// <param name="CursorColumn">The cursor's column.</param>
/// <param name="CursorVisible">Whether the host has asked for the cursor to be shown at all.</param>
/// <param name="Alternate">Whether a full-screen program has taken the screen.</param>
public readonly record struct Damage(
    long Generation,
    long TopLine,
    int Columns,
    int Rows,
    int CursorRow,
    int CursorColumn,
    bool CursorVisible,
    bool Alternate);
