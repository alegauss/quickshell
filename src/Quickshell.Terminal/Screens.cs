namespace Quickshell.Terminal;

/// <summary>
/// The two buffers a terminal has, and which one is live.
///
/// <para><b>The alternate screen is a second ring with no scrollback.</b> That is the whole of it:
/// a full-screen program gets a screen it cannot scroll off, and the shell's output behind it is
/// untouched because it is in a different ring rather than in the same one further back.</para>
///
/// <para><b>Entering also saves the cursor and leaving restores it.</b> DECSET 1049 is defined that
/// way and programs rely on it: <c>vim</c> exiting and leaving the prompt exactly where it was is
/// this, and a client that saved only the buffer puts the prompt in the wrong place every time.</para>
/// </summary>
public sealed class Screens
{
    private readonly TerminalBuffer _primary;
    private TerminalBuffer _alternate;

    private int _savedRow;
    private int _savedColumn;

    /// <summary>Opens both screens at one size, with scrollback on the primary alone.</summary>
    public Screens(int columns, int rows, int scrollback = 1000)
    {
        _primary = new TerminalBuffer(columns, rows, scrollback);
        _alternate = new TerminalBuffer(columns, rows, scrollback: 0);
    }

    /// <summary>The buffer everything is currently written to and read from.</summary>
    public TerminalBuffer Active => IsAlternate ? _alternate : _primary;

    /// <summary>The scrolling one, which keeps its scrollback while a full-screen program runs.</summary>
    public TerminalBuffer Primary => _primary;

    /// <summary>Whether a full-screen program has taken the screen.</summary>
    public bool IsAlternate { get; private set; }

    /// <summary>How many times a program has entered the alternate screen.</summary>
    public int Entries { get; private set; }

    /// <summary>
    /// Enters the alternate screen, saving the cursor and clearing what a program is about to draw
    /// over. Entering twice is not an error and does not save the cursor again — a program that
    /// sets the mode it is already in has not moved the cursor it saved.
    ///
    /// <para>That is 1049's entry, and the default. DECSET 47 and 1047 are older and plainer
    /// (QS244): <paramref name="shareCursor"/> keeps the cursor where it is, one cursor for both
    /// screens, and <paramref name="clear"/> false leaves whatever the alternate screen held.</para>
    /// </summary>
    public void EnterAlternate(bool clear = true, bool shareCursor = false)
    {
        if (IsAlternate)
        {
            return;
        }

        _savedRow = _primary.CursorRow;
        _savedColumn = _primary.CursorColumn;

        if (clear)
        {
            _alternate.ClearScreen();
        }

        _alternate.CursorRow = shareCursor ? _primary.CursorRow : 0;
        _alternate.CursorColumn = shareCursor ? _primary.CursorColumn : 0;

        IsAlternate = true;
        Entries++;
    }

    /// <summary>Blanks the alternate screen, live or not, as a full reset does (QS244).</summary>
    public void ClearAlternate()
    {
        _alternate.ClearScreen();
        _alternate.CursorRow = 0;
        _alternate.CursorColumn = 0;
    }

    /// <summary>
    /// Leaves the alternate screen and puts the cursor back where the program found it - or, with
    /// <paramref name="shareCursor"/>, carries the cursor back as 47 and 1047 do, after clearing
    /// the alternate screen where <paramref name="clear"/> says, which is 1047's (QS244).
    /// </summary>
    public void LeaveAlternate(bool clear = false, bool shareCursor = false)
    {
        if (!IsAlternate)
        {
            return;
        }

        if (clear)
        {
            _alternate.ClearScreen();
        }

        IsAlternate = false;
        _primary.CursorRow = shareCursor ? _alternate.CursorRow : _savedRow;
        _primary.CursorColumn = shareCursor ? _alternate.CursorColumn : _savedColumn;
    }

    /// <summary>Resizes both screens, so the one that is not live is not wrong when it becomes live.</summary>
    public void Resize(int columns, int rows)
    {
        _primary.Resize(columns, rows);

        // The alternate screen has no scrollback and nothing worth carrying across a resize: a
        // full-screen program is about to redraw it in full, having been told the new size.
        _alternate = new TerminalBuffer(columns, rows, scrollback: 0)
        {
            CursorRow = Math.Clamp(_alternate.CursorRow, 0, rows - 1),
            CursorColumn = Math.Clamp(_alternate.CursorColumn, 0, columns - 1),
        };

        _savedRow = Math.Clamp(_savedRow, 0, rows - 1);
        _savedColumn = Math.Clamp(_savedColumn, 0, columns - 1);
    }
}
