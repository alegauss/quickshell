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

    /// <summary>
    /// DECSET 45, reverse wrap: with it on, and autowrap on, a backspace at the left edge goes to the
    /// last column of the row above instead of stopping (QS105). Off by default, as in xterm.
    /// </summary>
    public bool ReverseWrap { get; private set; }

    /// <summary>
    /// DECSCUSR's style, <c>CSI Ps SP q</c>: 0 for none asked, which leaves the user's own shape;
    /// 1 and 2 a block, 3 and 4 an underline, 5 and 6 a bar, odd blinking and even steady (QS243).
    /// </summary>
    public int CursorStyle { get; private set; }

    /// <summary>
    /// The shape a DECSCUSR style asks for, or <see cref="CursorShape.None"/> for 0, which asks for
    /// nothing and leaves the shape the user chose. Blinking and steady draw alike: whether the
    /// cursor blinks is the user's setting, not the host's.
    /// </summary>
    public static CursorShape ShapeFor(int style) => style switch
    {
        1 or 2 => CursorShape.Block,
        3 or 4 => CursorShape.Underline,
        5 or 6 => CursorShape.Bar,
        _ => CursorShape.None,
    };

    private void SetCursorStyle(int style)
    {
        if (style is >= 0 and <= 6)
        {
            CursorStyle = style;
        }
        else
        {
            Unhandled++;
        }
    }

    /// <summary>
    /// xterm's extended reverse wraparound, mode 1045: a backspace at the left margin wraps to the
    /// row above whether or not that row wrapped, and from the top margin to the bottom one. Mode 45
    /// crosses only a row that really wrapped into the cursor's (QS242).
    /// </summary>
    public bool ReverseWrapExtended { get; private set; }

    /// <summary>
    /// A backspace. One column left, and at the left edge, under reverse wrap, to the end of the row
    /// above — never above the top margin, which is where the region the cursor is in begins.
    ///
    /// <para><b>xterm's rule and not a stricter one.</b> It does not ask whether the row above really
    /// wrapped into this one: the reference does not, the suites written against it expect it not
    /// to, and a shell that turns the mode on is the one telling the terminal where its lines
    /// continue.</para>
    /// </summary>
    /// <summary>
    /// BS and CUB: <paramref name="count"/> columns left, which is xterm's <c>CursorBack</c> turned
    /// into C# line for line (QS242), because every rule in it is one esctest checks.
    ///
    /// <para><b>Without reverse wraparound</b> the cursor stops at the left margin, or at the
    /// screen's edge when it starts left of the margin. <b>With mode 45 and autowrap</b> it carries
    /// on to the right margin of the row above, but only where that row wrapped into this one, so
    /// a backspace walks back through one long wrapped line and no further. <b>With 1045</b> it
    /// crosses any row, and from the top margin to the bottom one.</para>
    ///
    /// <para>A cursor waiting to wrap at the right edge spends the first step on cancelling the
    /// wait rather than moving: the character it is past is the one a backspace means.</para>
    /// </summary>
    private void CursorBack(TerminalBuffer buffer, int count, bool wasPending)
    {
        bool reverse = ReverseWrap && AutoWrap;
        bool extended = ReverseWrapExtended && AutoWrap;
        int left = LeftRightMarginMode ? MarginLeft : 0;
        int right = Right;
        int top = MarginTop;
        int bottom = MarginBottom;
        int row = buffer.CursorRow;
        int column = buffer.CursorColumn;
        bool fetched = false;

        // Already left of the left margin: the margin no longer holds it.
        if (column < left)
        {
            left = 0;
        }

        if ((reverse || extended) && wasPending)
        {
            count--;
        }
        else
        {
            column--;
        }

        while (true)
        {
            if (column < left)
            {
                if (extended)
                {
                    column = right;

                    if (row == top)
                    {
                        row = bottom + 1;
                    }
                }
                else if (!reverse)
                {
                    column = left;
                    break;
                }

                fetched = false;
                row--;
            }

            if (!fetched)
            {
                fetched = true;

                if (row != buffer.CursorRow)
                {
                    // The row above the screen is history, which a cursor cannot go into: a wrap
                    // that would reach it fails like one into a row that did not wrap.
                    if (row < 0 || (!extended && !buffer.IsScreenWrapped(row)))
                    {
                        if (row < bottom)
                        {
                            row++;
                        }

                        column = left;
                        break;
                    }

                    column = right;
                }
            }

            if (--count <= 0)
            {
                break;
            }

            column--;
        }

        buffer.CursorRow = row;
        buffer.CursorColumn = column;
        PendingWrap = false;
    }

    /// <summary>DECOM. With it on, row one means the top margin rather than the top of the screen.</summary>
    public bool OriginMode { get; private set; }

    /// <summary>
    /// IRM, <c>CSI 4 h</c>: with it on, a printed character pushes the rest of the row right
    /// instead of overwriting it, and what is pushed past the right edge is lost (QS207). Editors and
    /// readline-style prompts insert in place this way. Off by default.
    /// </summary>
    public bool InsertMode { get; private set; }

    /// <summary>
    /// LNM, <c>CSI 20 h</c>: with it on, a line feed, VT or FF also returns the carriage, as NEL
    /// does (QS232). Off by default, which is the whole of QS232: a line feed moves down and nothing
    /// else, and only a pty's ONLCR or a CR beside it ever made it look otherwise.
    /// </summary>
    public bool LineFeedMode { get; private set; }

    /// <summary>
    /// SM and RM: the modes a host turns on and off without the private marker. IRM and LNM are the
    /// ones this client honours; the rest are counted, as <see cref="PrivateMode"/> counts the
    /// private ones.
    /// </summary>
    private void AnsiMode(in CsiParameters parameters, bool set)
    {
        for (int group = 0; group < parameters.Count; group++)
        {
            switch (parameters.Value(group, -1))
            {
                case 4:
                    InsertMode = set;
                    break;

                case 20:
                    LineFeedMode = set;
                    break;

                default:
                    Unhandled++;
                    break;
            }
        }
    }

    /// <summary>
    /// DECCKM. With it on the arrows send their SS3 form instead of their CSI one.
    ///
    /// <para>This is why a key map cannot be a static table: a shell editing a line and the same
    /// shell running <c>vim</c> disagree about what Up sends, and the terminal is the thing that
    /// knows which — because it is the thing the host told.</para>
    /// </summary>
    public bool ApplicationCursorKeys { get; private set; }

    /// <summary>
    /// DECSET 2004. Whether the program wants to be told that a paste is a paste.
    ///
    /// <para>The one mode that is a security property rather than a display one: a program that has
    /// turned this on can decline to run what was pasted, and one that has not leaves this client
    /// asking the user instead.</para>
    /// </summary>
    public bool BracketedPaste { get; private set; }

    /// <summary>
    /// DECKPAM and DECKPNM, which the host sets with <c>ESC =</c> and clears with <c>ESC &gt;</c>.
    ///
    /// <para>With it on the numeric pad sends sequences of its own, which is how a program tells the
    /// pad's keys from the digits above the letters.</para>
    /// </summary>
    public bool ApplicationKeypad { get; private set; }

    /// <summary>
    /// DECBKM, mode 67: Backspace sends BS (0x08) and control-Backspace DEL, the other way round
    /// from the default (QS237). Off unless a host asks, because DEL is what <c>$TERM</c> promises.
    /// </summary>
    public bool BackarrowSendsBackspace { get; private set; }

    /// <summary>The top row of the scrolling region, zero-based and inclusive.</summary>
    public int MarginTop { get; private set; }

    /// <summary>The bottom row of the scrolling region, zero-based and inclusive.</summary>
    public int MarginBottom { get; private set; }

    /// <summary>
    /// Whether the region is the whole screen, which is what lets scrolling reach scrollback. A
    /// region narrower than the screen is not, however tall: a line leaving it has not left the screen.
    /// </summary>
    public bool RegionIsWholeScreen =>
        MarginTop == 0 && MarginBottom == Buffer.Rows - 1 && ColumnsAreWholeWidth;

    /// <summary>
    /// DECLRMM, <c>CSI ? 69 h</c>: whether left and right margins may be set, which is also whether
    /// <c>CSI s</c> means DECSLRM rather than saving the cursor (QS233).
    /// </summary>
    public bool LeftRightMarginMode { get; private set; }

    /// <summary>The leftmost column of the region, zero-based and inclusive; the screen's edge unless DECSLRM set it.</summary>
    public int MarginLeft { get; private set; }

    /// <summary>The rightmost column of the region; the screen's edge unless DECSLRM set it.</summary>
    public int MarginRight { get; private set; } = -1;

    /// <summary>The right margin in force, which is the screen's last column until one is set.</summary>
    private int Right => MarginRight < 0 ? Buffer.Columns - 1 : Math.Min(MarginRight, Buffer.Columns - 1);

    /// <summary>Whether no left or right margin narrows the region, which keeps every old path as it was.</summary>
    private bool ColumnsAreWholeWidth => MarginLeft == 0 && Right == Buffer.Columns - 1;

    /// <summary>Whether a column is between the left and right margins.</summary>
    private bool InColumns(int column) => column >= MarginLeft && column <= Right;

    /// <summary>The margins back to the screen's edges, which a resize, a screen switch and a reset all do.</summary>
    private void ClearColumnMargins()
    {
        MarginLeft = 0;
        MarginRight = -1;
    }

    /// <summary>
    /// DECSLRM, <c>CSI Pl ; Pr s</c> while DECLRMM is set (QS233). As DECSTBM: absent parameters mean
    /// the screen's edges, a region narrower than two columns is refused as the whole width, and
    /// setting it homes the cursor.
    /// </summary>
    private void SetColumnMargins(in CsiParameters parameters)
    {
        int left = Math.Max(1, parameters.Value(0, 1)) - 1;
        int right = Math.Max(1, parameters.Value(1, Buffer.Columns)) - 1;

        if (left >= right || right >= Buffer.Columns)
        {
            ClearColumnMargins();
        }
        else
        {
            MarginLeft = left;
            MarginRight = right;
        }

        Home();
    }

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
        // A tab inside the region stops at the right margin and never wraps past it (QS233).
        int last = from <= Right ? Right : Buffer.Columns - 1;

        for (int column = from + 1; column <= last; column++)
        {
            if (IsTabStop(column))
            {
                return column;
            }
        }

        return last;
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
        Buffer.CursorColumn = OriginMode ? MarginLeft : 0;
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
            SetPrivateMode(parameters.Value(group, -1), set);
        }
    }

    /// <summary>One private mode, which XTRESTORE also comes through (QS239).</summary>
    private void SetPrivateMode(int mode, bool set)
    {
        // The mouse modes are asked first because they are a set rather than a switch, and the
        // one that decides which of them is live has to see all five.
        if (MouseMode(mode, set))
        {
            return;
        }

        switch (mode)
        {
            case 1:
                ApplicationCursorKeys = set;
                break;

            case 3:
                // DECCOLM. The width is refused, as xterm refuses it without allowC132: a pane
                // in a split tab has no single size a host could ask it to become. What every
                // DECCOLM also does is kept, because a program asking for it assumes a clean
                // screen with no region, and drew its next frame on that assumption (QS208).
                EraseDisplay(2);
                MarginTop = 0;
                MarginBottom = Buffer.Rows - 1;
                ClearColumnMargins();
                Home();
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

            case 45:
                ReverseWrap = set;
                break;

            case 1045:
                ReverseWrapExtended = set;
                break;

            case 66:
                // DECNKM: the keypad mode ESC = and ESC > set, under its mode number (QS237).
                ApplicationKeypad = set;
                break;

            case 67:
                BackarrowSendsBackspace = set;
                break;

            case 69:
                // Turning it off takes the margins with it, as xterm does: a region nobody can
                // see being set any more is not one a host should go on being clamped by.
                LeftRightMarginMode = set;

                if (!set)
                {
                    ClearColumnMargins();
                }

                break;

            case 2004:
                BracketedPaste = set;
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

    /// <summary>DECRQM's five answers, as the request's reply spells them.</summary>
    private enum ModeState
    {
        /// <summary>A mode this terminal has never heard of.</summary>
        Unrecognised = 0,

        Set = 1,

        Reset = 2,

        /// <summary>Always on, and the host cannot turn it off.</summary>
        PermanentlySet = 3,

        /// <summary>Never on: a mode this client refuses on purpose.</summary>
        PermanentlyReset = 4,
    }

    /// <summary>
    /// DECRQM: <c>CSI Ps $ p</c>, or <c>CSI ? Ps $ p</c> for a private mode — whether a mode is set,
    /// answered as <c>CSI [?] Ps ; Pm $ y</c> (QS104).
    ///
    /// <para><b>Five answers and not two.</b> A program told a mode is merely off will try to turn
    /// it on; told it is permanently off, it falls back. So a mode this client refuses on purpose
    /// answers four, a mode it honours answers one or two by its state, and anything it never heard
    /// of answers zero, because "off" would invite the program to set it. DECCOLM answers four: its
    /// side effects are honoured, its width never is (QS208).</para>
    /// </summary>
    private void ModeReport(in CsiParameters parameters, bool dec)
    {
        int mode = parameters.Value(0, 0);
        ModeState state = dec ? DecModeState(mode) : AnsiModeState(mode);

        Send(dec ? Answer.DecModeReport : Answer.AnsiModeReport, mode, (int)state);
    }

    private ModeState DecModeState(int mode) => mode switch
    {
        1 => On(ApplicationCursorKeys),
        6 => On(OriginMode),
        7 => On(AutoWrap),
        25 => On(CursorVisible),
        45 => On(ReverseWrap),
        1045 => On(ReverseWrapExtended),
        69 => On(LeftRightMarginMode),
        2004 => On(BracketedPaste),
        47 or 1047 or 1049 => On(Screens.IsAlternate),
        9 => On(_tracking == MouseTracking.PressOnly),
        1000 => On(_tracking == MouseTracking.PressRelease),
        1002 => On(_tracking == MouseTracking.ButtonMotion),
        1003 => On(_tracking == MouseTracking.AnyMotion),
        1006 => On(_encoding == MouseEncoding.Sgr),
        66 => On(ApplicationKeypad),
        67 => On(BackarrowSendsBackspace),

        // Refused on purpose, so permanently off: the UTF-8 mouse encoding, which SGR replaces
        // unambiguously, sixel display mode, which is a non-goal, and 132 columns, which is too.
        1005 or 80 or 3 => ModeState.PermanentlyReset,

        // Modes xterm knows that this client deliberately does not have (QS237): smooth scroll (4)
        // and reverse video (5) are drawing it does not do, autorepeat (8) belongs to the local
        // keyboard, the printer's form feed and extent (18, 19) have no printer, the Hebrew and
        // national replacement sets (35, 42) are not carried, and horizontal cursor coupling (60)
        // has no horizontal scroll to couple to. Answered so a host learns it rather than guessing.
        4 or 5 or 8 or 18 or 19 or 35 or 42 or 60 => ModeState.PermanentlyReset,

        _ => ModeState.Unrecognised,
    };

    private ModeState AnsiModeState(int mode) => mode switch
    {
        4 => On(InsertMode),
        20 => On(LineFeedMode),

        // KAM: a host does not get to lock the local keyboard, so it is never on (QS237).
        2 => ModeState.PermanentlyReset,

        // SRM: send/receive, whose reset is local echo. An SSH session's echo is the far end's,
        // so this client never echoes locally and the mode is permanently set.
        12 => ModeState.PermanentlySet,

        // The ISO 6429 modes xterm knows and ignores, answered as xterm answers them: GATM, SRTM,
        // VEM, HEM, PUM, FEAM, FETM, MATM, TTM, SATM, TSM and EBM.
        1 or 5 or 7 or 10 or 11 or 13 or 14 or 15 or 16 or 17 or 18 or 19 => ModeState.PermanentlyReset,

        _ => ModeState.Unrecognised,
    };

    private static ModeState On(bool set) => set ? ModeState.Set : ModeState.Reset;

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
        ClearColumnMargins();
        PendingWrap = false;
        ResetTabStops();
    }
}
