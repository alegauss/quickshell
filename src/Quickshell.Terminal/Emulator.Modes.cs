namespace Quickshell.Terminal;

public sealed partial class Emulator
{
    private bool[] _tabStops = [];

    /// <summary>
    /// Whether the cursor is sitting on the last column it wrote to, waiting for one more character.
    ///
    /// <para><b>This is the subtle one, and the one most implementations skip.</b> Writing to the
    /// last column does not move the cursor to the next row. It leaves it where it is with this set,
    /// and only the next <em>printable</em> character wraps. Anything else — a movement, an erase, a
    /// carriage return — clears it.</para>
    ///
    /// <para>Get it wrong and a line exactly the width of the terminal is followed by a blank line
    /// the host never sent, which a user sees at once and blames on the remote program.</para>
    /// </summary>
    public bool PendingWrap { get; private set; }

    /// <summary>DECAWM. With it off, the last column simply overwrites itself.</summary>
    public bool AutoWrap { get; private set; } = true;

    /// <summary>DECOM. With it on, row one means the top margin rather than the top of the screen.</summary>
    public bool OriginMode { get; private set; }

    /// <summary>
    /// DECCKM. With it on the arrows send their SS3 form instead of their CSI one.
    ///
    /// <para>This is why a key map cannot be a static table: a shell editing a line and the same
    /// shell running <c>vim</c> disagree about what Up sends, and the terminal is the thing that
    /// knows which — because it is the thing the host told.</para>
    /// </summary>
    public bool ApplicationCursorKeys { get; private set; }

    /// <summary>
    /// DECKPAM and DECKPNM, which the host sets with <c>ESC =</c> and clears with <c>ESC &gt;</c>.
    ///
    /// <para>With it on the numeric pad sends sequences of its own, which is how a program tells the
    /// pad's keys from the digits above the letters.</para>
    /// </summary>
    public bool ApplicationKeypad { get; private set; }

    /// <summary>The top row of the scrolling region, zero-based and inclusive.</summary>
    public int MarginTop { get; private set; }

    /// <summary>The bottom row of the scrolling region, zero-based and inclusive.</summary>
    public int MarginBottom { get; private set; }

    /// <summary>Whether the region is the whole screen, which is what lets scrolling reach scrollback.</summary>
    public bool RegionIsWholeScreen => MarginTop == 0 && MarginBottom == Buffer.Rows - 1;

    /// <summary>Whether a column carries a tab stop.</summary>
    public bool IsTabStop(int column) => column >= 0 && column < _tabStops.Length && _tabStops[column];

    /// <summary>
    /// Puts the tab stops back to one every eight columns.
    ///
    /// <para>A real set, and never a modulo-eight assumption in the code that moves. A program that
    /// sets its own stops and then tabs is testing whether this set exists, and one that computed
    /// the answer instead would pass every test written by someone who also assumed eight.</para>
    /// </summary>
    private void ResetTabStops()
    {
        // Reused where the width has not changed, which is nearly always. This runs on every screen
        // switch, and a shell that starts and leaves full-screen programs switches often - a fresh
        // array each time was the last two hundred bytes of allocation on the printing path, and the
        // one QS24's measurement found after every plausible candidate had been inspected.
        if (_tabStops.Length != Buffer.Columns)
        {
            _tabStops = new bool[Buffer.Columns];
        }
        else
        {
            Array.Clear(_tabStops);
        }

        for (int column = 8; column < _tabStops.Length; column += 8)
        {
            _tabStops[column] = true;
        }
    }

    /// <summary>Where a tab from this column lands: the next stop, or the last column.</summary>
    private int NextTabStop(int from)
    {
        for (int column = from + 1; column < Buffer.Columns; column++)
        {
            if (IsTabStop(column))
            {
                return column;
            }
        }

        return Buffer.Columns - 1;
    }

    private int PreviousTabStop(int from)
    {
        for (int column = from - 1; column > 0; column--)
        {
            if (IsTabStop(column))
            {
                return column;
            }
        }

        return 0;
    }

    /// <summary>
    /// DECSTBM. Absent parameters mean the whole screen, and setting the region homes the cursor —
    /// which programs rely on, so it is part of the instruction rather than a courtesy.
    /// </summary>
    private void SetMargins(in CsiParameters parameters)
    {
        int top = Math.Max(1, parameters.Value(0, 1)) - 1;
        int bottom = Math.Max(1, parameters.Value(1, Buffer.Rows)) - 1;

        // A region that is not at least two rows tall is refused outright rather than clamped: it
        // is what a host sends when its own arithmetic went wrong, and honouring it would scroll a
        // single row against itself forever.
        if (top >= bottom || bottom >= Buffer.Rows)
        {
            MarginTop = 0;
            MarginBottom = Buffer.Rows - 1;
        }
        else
        {
            MarginTop = top;
            MarginBottom = bottom;
        }

        Home();
    }

    /// <summary>The top-left of whichever space the origin mode says the cursor lives in.</summary>
    private void Home()
    {
        Buffer.CursorRow = OriginMode ? MarginTop : 0;
        Buffer.CursorColumn = 0;
        PendingWrap = false;
    }

    /// <summary>
    /// DECSET and DECRESET: the modes a host turns on and off with a private marker.
    ///
    /// <para>Unknown modes are counted rather than guessed at. A mode answered wrongly is worse than
    /// one not answered: the host believes it took effect and draws accordingly.</para>
    /// </summary>
    private void PrivateMode(in CsiParameters parameters, bool set)
    {
        for (int group = 0; group < parameters.Count; group++)
        {
            int mode = parameters.Value(group, -1);

            // The mouse modes are asked first because they are a set rather than a switch, and the
            // one that decides which of them is live has to see all five.
            if (MouseMode(mode, set))
            {
                continue;
            }

            switch (mode)
            {
                case 1:
                    ApplicationCursorKeys = set;
                    break;

                case 6:
                    OriginMode = set;

                    // Changing it homes the cursor, because the coordinate space it lives in has
                    // just changed underneath it.
                    Home();
                    break;

                case 7:
                    AutoWrap = set;
                    PendingWrap = false;
                    break;

                case 25:
                    CursorVisible = set;
                    break;

                case 47:
                case 1047:
                    SwitchScreen(set);
                    break;

                case 1048:
                    if (set)
                    {
                        SaveCursor();
                    }
                    else
                    {
                        RestoreCursor();
                    }

                    break;

                case 1049:
                    // The one every full-screen program actually sends: save the cursor and switch,
                    // then switch back and restore. The two halves are one instruction.
                    if (set)
                    {
                        SaveCursor();
                        SwitchScreen(true);
                    }
                    else
                    {
                        SwitchScreen(false);
                        RestoreCursor();
                    }

                    break;

                default:
                    Unhandled++;
                    break;
            }
        }
    }

    private void SwitchScreen(bool alternate)
    {
        if (alternate)
        {
            Screens.EnterAlternate();
        }
        else
        {
            Screens.LeaveAlternate();
        }

        // Each screen has its own region and its own stops, and carrying the old ones across is how
        // a program that set a region leaves the shell scrolling inside it afterwards.
        MarginTop = 0;
        MarginBottom = Buffer.Rows - 1;
        PendingWrap = false;
        ResetTabStops();
    }
}
