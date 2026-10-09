using System.Text;

namespace Quickshell.Terminal;

/// <summary>
/// What the parser's events mean: where the cursor goes, what gets erased, and what colour the next
/// character is.
///
/// <para><b>Two rules decide most of the correctness here, and both are about parameters.</b> An
/// absent parameter and a zero parameter both mean one, for every movement — <c>CSI A</c>,
/// <c>CSI 0A</c> and <c>CSI 1A</c> are the same instruction, and a client that treats the first two
/// differently drifts a row every time a program is terse. And a movement <em>clamps</em> at the
/// margin rather than wrapping, because a program that asks to go up ten rows from row two means
/// row one and not row twenty-two.</para>
///
/// <para><b>Colours are stored as the host expressed them.</b> Default is not the theme's current
/// value and a palette index is not the colour it currently maps to; both are resolved when a frame
/// is built. That is what lets a theme change repaint scrollback that was written under the old one.</para>
/// </summary>
public sealed partial class Emulator : IAnsiHandler
{
    private readonly AnsiParser _parser = new();
    private readonly StreamDecoder _decoder = new();
    private readonly GraphemeSegmenter _segmenter = new();

    private Pen _pen = Pen.Default;
    private int _lastPrinted = ' ';
    private readonly CharacterSet[] _designated = [CharacterSet.Ascii, CharacterSet.Ascii];
    private int _activeSet;

    // DECSC's saved state, one per screen as xterm keeps it (QS239): a full-screen program saving
    // on the alternate screen does not overwrite what the shell saved on the main one.
    private readonly SavedCursor[] _saved = [SavedCursor.Home, SavedCursor.Home];

    /// <summary>Opens a terminal of a given size, with scrollback behind the primary screen.</summary>
    public Emulator(int columns, int rows, int scrollback = 1000)
    {
        Screens = new Screens(columns, rows, scrollback);
        MarginBottom = rows - 1;
        ResetTabStops();
    }

    /// <summary>The primary and alternate screens, and which is live.</summary>
    public Screens Screens { get; }

    /// <summary>The buffer currently being written to.</summary>
    public TerminalBuffer Buffer => Screens.Active;

    /// <summary>What the next printed cell inherits.</summary>
    public Pen Pen => _pen;

    /// <summary>What the indices and defaults look like. Consulted when a frame is built, not before.</summary>
    public Palette Palette { get; } = new();

    /// <summary>Sequences the parser dispatched that nothing here answers for.</summary>
    public int Unhandled { get; private set; }

    /// <summary>Whether the host has asked for the cursor to be shown. DECTCEM.</summary>
    public bool CursorVisible { get; private set; } = true;

    /// <summary>Which of the two designated sets shift-in and shift-out have selected.</summary>
    public CharacterSet ActiveCharacterSet => _designated[_activeSet];

    /// <summary>
    /// What a consumer compares to know whether the screen it drew is still the screen.
    ///
    /// <para>Read in one go, and compared against the last one. See <see cref="Terminal.Damage"/>
    /// for why each field is in it.</para>
    /// </summary>
    public Damage Damage => new(
        Buffer.Generation,
        Buffer.TopLine,
        Buffer.Columns,
        Buffer.Rows,
        Buffer.CursorRow,
        Buffer.CursorColumn,
        CursorVisible,
        Screens.IsAlternate,
        CursorStyle,
        Palette.Reversed);

    /// <summary>How this session sends alt. Escape-prefix, which is what a shell expects.</summary>
    public AltSends AltSends { get; set; } = AltSends.Escape;

    /// <summary>
    /// What a key sends, given what the host has asked for.
    ///
    /// <para>Here rather than on <see cref="Keys"/> alone because the answer depends on two modes the
    /// host changes, and this is the object that was told about them. A caller with a key and a
    /// buffer needs to know nothing else.</para>
    /// </summary>
    /// <param name="key">The key.</param>
    /// <param name="modifiers">What was held with it.</param>
    /// <param name="destination">At least <see cref="Keys.MaximumLength"/> bytes.</param>
    /// <returns>How many bytes were written.</returns>
    public int Encode(Key key, KeyModifiers modifiers, Span<byte> destination) =>
        Keys.Encode(key, modifiers, ApplicationCursorKeys, ApplicationKeypad, destination,
                    BackarrowSendsBackspace);

    /// <summary>The same for a character key, which only the alt setting changes.</summary>
    /// <param name="text">The character the window resolved, already through the keyboard layout.</param>
    /// <param name="modifiers">What was held with it.</param>
    /// <param name="destination">At least <see cref="Keys.MaximumLength"/> bytes.</param>
    /// <returns>How many bytes were written.</returns>
    public int Encode(ReadOnlySpan<char> text, KeyModifiers modifiers, Span<byte> destination) =>
        Keys.EncodeText(text, modifiers, AltSends, destination);

    /// <summary>
    /// Feeds bytes from the host, through the parser and into the buffer.
    ///
    /// <para><b>The segmenter is flushed at the end of every read</b>, which is a deliberate
    /// departure from what it does on its own. It holds the last cluster back because a combining
    /// mark in the next read would have belonged to it — correct for a stream, and wrong for a
    /// terminal, where holding it means the last character a user typed does not appear until they
    /// type another. So it is flushed, and a mark that arrives afterwards attaches to the cell that
    /// was already written rather than being lost.</para>
    /// </summary>
    public void Feed(ReadOnlySpan<byte> bytes)
    {
        Emulator self = this;
        _parser.Parse(bytes, ref self);
        FlushText();
    }

    /// <summary>
    /// Prints whatever the segmenter is still holding.
    ///
    /// <para>Called before every control and at the end of every read, and both are the same rule:
    /// a cluster is only held back in case something extends it, and a control byte proves nothing
    /// will. Held past that, the text would be written after the sequence that was meant to follow
    /// it — a carriage return would land before the character it was meant to come after.</para>
    /// </summary>
    private void FlushText()
    {
        while (_segmenter.TryFlush(out ReadOnlySpan<char> cluster))
        {
            PrintCluster(cluster);
        }
    }

    /// <summary>
    /// Holds this many lines of history from now on. The primary screen's alone: the alternate
    /// screen keeps none, whatever the setting says, because a full-screen program's frames are not
    /// history.
    /// </summary>
    public void KeepScrollback(int scrollback) => Screens.Primary.KeepScrollback(scrollback);

    /// <summary>Resizes both screens and puts the cursor back inside the new one.</summary>
    public void Resize(int columns, int rows)
    {
        Screens.Resize(columns, rows);

        // The region and the stops are both stated in the old geometry, and neither survives a
        // resize meaningfully. A host that had set either is told the new size and sets them again.
        MarginTop = 0;
        MarginBottom = rows - 1;
        ClearColumnMargins();
        PendingWrap = false;
        ResetTabStops();
    }

    // ---- Text ----

    void IAnsiHandler.Print(ReadOnlySpan<byte> text)
    {
        _segmenter.Append(_decoder.Decode(text));

        while (_segmenter.TryNext(out ReadOnlySpan<char> cluster))
        {
            PrintCluster(cluster);
        }
    }

    private void PrintCluster(ReadOnlySpan<char> incoming)
    {
        // Scoped, so the remapped character below may live on the stack: without it the compiler has
        // to assume this span outlives the method and refuses a stackalloc into it.
        scoped ReadOnlySpan<char> cluster = incoming;

        int codepoint = Codepoint(cluster);

        // The designated set is a remapping of what arrived, and it happens here because it changes
        // which character this is - a box corner rather than the letter l.
        //
        // The remapped character is built on the stack: this runs for every printed byte while a set
        // is designated, and a string here would be one allocation per character.
        Span<char> remapped = stackalloc char[2];

        if (cluster.Length == 1 && _designated[_activeSet] != CharacterSet.Ascii)
        {
            int mapped = CharacterSets.Map(_designated[_activeSet], codepoint);

            if (mapped != codepoint && Rune.TryCreate(mapped, out Rune rune))
            {
                int written = rune.EncodeToUtf16(remapped);
                codepoint = mapped;
                cluster = remapped[..written];
            }
        }

        int width = CharacterWidth.OfCluster(cluster);

        if (width == 0)
        {
            AttachToPrevious(cluster);
            return;
        }

        TerminalBuffer buffer = Buffer;

        // Where this line ends: the right margin while the cursor is inside the region, the screen's
        // edge otherwise (QS233). Without margins the two are the same column.
        int end = buffer.CursorColumn <= Right ? Right + 1 : buffer.Columns;

        // The wrap that was owed from the last character, taken now that a printable one has
        // actually arrived. Everything between then and now had its chance to cancel it.
        if (PendingWrap)
        {
            PendingWrap = false;

            if (AutoWrap)
            {
                // The row the host wrote as one logical line continues into the next, and saying so
                // is the only record reflow, selection and copy will have.
                buffer.SetScreenWrapped(buffer.CursorRow, true);
                NextLine();

                // A wrap continues at the start of the next row - the left margin's, inside the
                // region - which NextLine does not imply.
                buffer.CursorColumn = end == Right + 1 ? MarginLeft : 0;
                end = buffer.CursorColumn <= Right ? Right + 1 : buffer.Columns;
            }
        }

        if (buffer.CursorColumn + width > end)
        {
            // A wide character with one column left. It cannot be split, so either the line wraps
            // or - with wrapping off - it lands at the end and overwrites what is there.
            if (AutoWrap)
            {
                buffer.SetScreenWrapped(buffer.CursorRow, true);
                NextLine();
                buffer.CursorColumn = end == Right + 1 ? MarginLeft : 0;
                end = buffer.CursorColumn <= Right ? Right + 1 : buffer.Columns;
            }
            else
            {
                buffer.CursorColumn = end - width;
            }
        }

        // IRM: room for this character first, pushing the rest of the row right and losing what
        // goes past the edge (QS207) - the right margin's, inside the region (QS233). The buffer's
        // own insert, so the damage is recorded.
        if (InsertMode)
        {
            buffer.InsertCells(buffer.CursorRow, buffer.CursorColumn, width, end);
        }

        Cell cell = cluster.Length == 1 || (cluster.Length == 2 && char.IsSurrogatePair(cluster[0], cluster[1]))
            ? Cell.For(codepoint, _pen.Foreground, _pen.Background, _pen.Flags, _pen.Underline, width, _pen.Link)
            : ClusterCell(buffer, cluster, codepoint, width);

        buffer.Write(buffer.CursorRow, buffer.CursorColumn, cell);

        if (width == 2 && buffer.CursorColumn + 1 < buffer.Columns)
        {
            // The trailing half is a real cell holding nothing, which is what keeps the column
            // count honest for everything that reads the row afterwards.
            buffer.Write(buffer.CursorRow, buffer.CursorColumn + 1,
                         Cell.For(' ', _pen.Foreground, _pen.Background, _pen.Flags, _pen.Underline, 0, _pen.Link));
        }

        _lastPrinted = codepoint;

        if (buffer.CursorColumn + width >= end)
        {
            // Stay on the cell just written and owe a wrap. Moving now is what puts a blank line
            // after every line that happens to be exactly the width of the terminal.
            buffer.CursorColumn = end - width;
            PendingWrap = true;
        }
        else
        {
            buffer.CursorColumn += width;
        }
    }

    /// <summary>The first codepoint of a cluster, which is the character the cluster is about.</summary>
    private static int Codepoint(ReadOnlySpan<char> cluster) =>
        cluster.Length >= 2 && char.IsSurrogatePair(cluster[0], cluster[1])
            ? char.ConvertToUtf32(cluster[0], cluster[1])
            : cluster[0];

    /// <summary>
    /// A mark that arrived after its base had already been written, because the read ended between
    /// them. It belongs to the cell before the cursor, so it is added to that cell's text rather
    /// than dropped — dropping it is how an accent typed as two codepoints disappears.
    ///
    /// <para><b>The joined text is built on the stack and bounded there.</b> A host can send a base
    /// and then marks for ever; the string version grew the cluster by one mark at a time and
    /// interned each intermediate, which is quadratic in what the host chose to send. Past the cap
    /// the mark is counted and dropped, and the cell keeps the text it has.</para>
    /// </summary>
    private void AttachToPrevious(ReadOnlySpan<char> mark)
    {
        TerminalBuffer buffer = Buffer;
        int row = buffer.CursorRow;
        int column = buffer.CursorColumn - 1;

        // Step back over the trailing half of a wide pair, which holds no text of its own.
        while (column >= 0 && buffer.Screen(row)[column].Width == 0)
        {
            column--;
        }

        if (column < 0)
        {
            // A mark with nothing before it. The host sent it into an empty row, and there is no
            // cell for it to modify.
            return;
        }

        Cell before = buffer.Screen(row)[column];
        Span<char> joined = stackalloc char[GraphemeSegmenter.MaximumCluster];
        int written = buffer.TextOf(before, joined);

        if (written < 0 || written + mark.Length > joined.Length)
        {
            Unhandled++;
            return;
        }

        mark.CopyTo(joined[written..]);

        int index = buffer.InternCluster(joined[..(written + mark.Length)]);

        if (index < 0)
        {
            return;
        }

        buffer.Write(row, column, Cell.ForCluster(
            index, before.Foreground, before.Background, before.Flags, before.Underline, before.Width));
    }

    private Cell ClusterCell(TerminalBuffer buffer, ReadOnlySpan<char> cluster, int codepoint, int width)
    {
        int index = buffer.InternCluster(cluster);

        // A table that has stopped growing gives -1, and the base codepoint is what is left. The
        // accent is lost rather than the session, which is the trade the ceiling exists to make.
        return index < 0
            ? Cell.For(codepoint, _pen.Foreground, _pen.Background, _pen.Flags, _pen.Underline, width, _pen.Link)
            : Cell.ForCluster(index, _pen.Foreground, _pen.Background, _pen.Flags, _pen.Underline, width, _pen.Link);
    }

    // ---- Controls ----

    void IAnsiHandler.Execute(byte control)
    {
        FlushText();

        TerminalBuffer buffer = Buffer;

        // Every control cancels an owed wrap. Only a printable character takes it, which is the
        // whole of what makes a full-width line not grow a blank one after it. A backspace needs to
        // know it was owed, under reverse wraparound (QS242).
        bool wasPending = PendingWrap;
        PendingWrap = false;

        switch (control)
        {
            case 0x08:
                CursorBack(buffer, 1, wasPending);
                break;

            case 0x09:
                // An owed wrap survives a tab, as xterm's does, unless mode 41 asks for the tab to
                // take it (QS244).
                if (wasPending && MoreFix && AutoWrap)
                {
                    buffer.SetScreenWrapped(buffer.CursorRow, true);
                    NextLine();
                    buffer.CursorColumn = LeftRightMarginMode ? MarginLeft : 0;
                }
                else if (wasPending)
                {
                    PendingWrap = true;
                    break;
                }

                buffer.CursorColumn = NextTabStop(buffer.CursorColumn);
                break;

            case 0x0A:
            case 0x0B:
            case 0x0C:
                NextLine();

                // A line feed moves down and leaves the column, unless LNM asks for the carriage
                // too (QS232). A shell never showed the difference: its pty sends CR LF.
                if (LineFeedMode)
                {
                    CarriageReturn();
                }

                break;

            case 0x0D:
                CarriageReturn();
                break;

            case 0x0E:
                _activeSet = 1;
                break;

            case 0x0F:
                _activeSet = 0;
                break;

            default:
                break;
        }
    }

    /// <summary>
    /// Down one row, scrolling the screen when there is no row below - and in the same column, which
    /// is what LF, VT, FF and IND all mean (QS232). NEL and LNM add the carriage return themselves.
    /// </summary>
    private void NextLine()
    {
        TerminalBuffer buffer = Buffer;
        PendingWrap = false;

        if (buffer.CursorRow != MarginBottom)
        {
            buffer.CursorRow = Math.Min(buffer.Rows - 1, buffer.CursorRow + 1);
            return;
        }

        // At the bottom margin but outside the left and right ones: the cursor stays, and nothing
        // scrolls, because the region the line would scroll is not the one the cursor is in (QS233).
        if (!InColumns(buffer.CursorColumn))
        {
            return;
        }

        // At the bottom margin. Only a region that is the whole screen scrolls into the scrollback:
        // a line leaving a region inside the screen has not left the screen, and putting it in the
        // history would interleave a program's own scrolling with the shell's output behind it.
        if (RegionIsWholeScreen)
        {
            buffer.ScrollUp();
            return;
        }

        ScrollRegion(MarginTop, up: true, 1);
    }

    /// <summary>
    /// The region from <paramref name="top"/> to the bottom margin scrolled by <paramref name="count"/>
    /// lines, between the left and right margins where they narrow it (QS233) and whole rows where
    /// they do not, which is the path every region scroll took before margins existed.
    /// </summary>
    private void ScrollRegion(int top, bool up, int count)
    {
        TerminalBuffer buffer = Buffer;

        if (ColumnsAreWholeWidth)
        {
            if (up)
            {
                buffer.ScrollRegionUp(top, MarginBottom, count);
            }
            else
            {
                buffer.ScrollRegionDown(top, MarginBottom, count);
            }

            return;
        }

        if (up)
        {
            buffer.ScrollRectUp(top, MarginBottom, MarginLeft, Right, count);
        }
        else
        {
            buffer.ScrollRectDown(top, MarginBottom, MarginLeft, Right, count);
        }
    }

    /// <summary>
    /// CR: to the left margin from at or right of it, to the screen's edge from left of it, and to
    /// the margin always under DECOM, as xterm does (QS233). Column one, without margins.
    /// </summary>
    private void CarriageReturn()
    {
        TerminalBuffer buffer = Buffer;

        buffer.CursorColumn = OriginMode || buffer.CursorColumn >= MarginLeft ? MarginLeft : 0;
    }

    // ---- Escape sequences ----

    void IAnsiHandler.EscapeDispatch(ReadOnlySpan<byte> intermediates, byte final)
    {
        FlushText();

        if (!intermediates.IsEmpty)
        {
            // `ESC ( x` and `ESC ) x` designate the two slots. Everything else with an intermediate
            // is counted rather than guessed at: a sequence answered wrongly is worse than one not
            // answered at all.
            // DECALN, ESC # 8: the screen alignment test, a screenful of E (QS244).
            if (intermediates.Length == 1 && intermediates[0] == (byte)'#' && final == (byte)'8')
            {
                AlignmentTest();
                return;
            }

            int slot = intermediates[0] switch { (byte)'(' => 0, (byte)')' => 1, _ => -1 };

            if (slot >= 0 && CharacterSets.Designated(final) is CharacterSet set)
            {
                _designated[slot] = set;
            }
            else
            {
                Unhandled++;
            }

            return;
        }

        switch (final)
        {
            case (byte)'7':
                SaveCursor();
                break;

            case (byte)'8':
                RestoreCursor();
                break;

            case (byte)'D':
                NextLine();
                break;

            case (byte)'E':
                NextLine();
                CarriageReturn();
                break;

            case (byte)'6':
                BackIndex();
                break;

            case (byte)'9':
                ForwardIndex();
                break;

            case (byte)'H':
                if (Buffer.CursorColumn < Buffer.Columns)
                {
                    SetTabStop(Buffer.CursorColumn);
                }

                break;

            case (byte)'M':
                ReverseIndex();
                break;

            case (byte)'=':
                ApplicationKeypad = true;
                break;

            case (byte)'>':
                ApplicationKeypad = false;
                break;

            case (byte)'\\':
                // ST. The string it terminated already ended when the escape left that state.
                break;

            case (byte)'c':
                Reset();
                break;

            case (byte)'Z':
                // DECID, the VT52-era spelling of DA1, answered the same (QS243).
                Send(Answer.DeviceAttributes);
                break;

            case (byte)'V':
                ProtectedArea(start: true);
                break;

            case (byte)'W':
                ProtectedArea(start: false);
                break;

            default:
                Unhandled++;
                break;
        }
    }

    private void ReverseIndex()
    {
        TerminalBuffer buffer = Buffer;
        PendingWrap = false;

        if (buffer.CursorRow != MarginTop)
        {
            buffer.CursorRow = Math.Max(0, buffer.CursorRow - 1);
            return;
        }

        // At the top margin and outside the left and right ones: nothing moves (QS233).
        if (InColumns(buffer.CursorColumn))
        {
            ScrollRegion(MarginTop, up: false, 1);
        }
    }

    /// <summary>
    /// DECBI, <c>ESC 6</c>: one column left, or at the left margin inside the region, the region's
    /// columns pushed right with a blank one opening at the margin (QS233). Outside the region it
    /// moves and never scrolls, as DEC STD 070 has it.
    /// </summary>
    private void BackIndex()
    {
        TerminalBuffer buffer = Buffer;
        PendingWrap = false;

        if (buffer.CursorColumn == MarginLeft && buffer.CursorRow >= MarginTop && buffer.CursorRow <= MarginBottom)
        {
            buffer.InsertColumns(MarginTop, MarginBottom, MarginLeft, Right, 1);
        }
        else if (buffer.CursorColumn > 0)
        {
            buffer.CursorColumn--;
        }
    }

    /// <summary>DECFI, <c>ESC 9</c>: the same rightwards, deleting the left margin's column at the right one.</summary>
    private void ForwardIndex()
    {
        TerminalBuffer buffer = Buffer;
        PendingWrap = false;

        if (buffer.CursorColumn == Right && buffer.CursorRow >= MarginTop && buffer.CursorRow <= MarginBottom)
        {
            buffer.DeleteColumns(MarginTop, MarginBottom, MarginLeft, Right, 1);
        }
        else if (buffer.CursorColumn < buffer.Columns - 1)
        {
            buffer.CursorColumn++;
        }
    }

    /// <summary>The saved state of the screen in use.</summary>
    private ref SavedCursor Saved => ref _saved[Screens.IsAlternate ? 1 : 0];

    private void SaveCursor()
    {
        // The whole state and not the position: a program that restores expects its colours back
        // too, and one that gets only the position paints the rest of its screen in whatever the
        // last thing to run happened to leave set. And the character sets, as DEC's DECSC does and
        // xterm restores (QS206): a program that drew a box in line drawing, saved, and switched
        // back to ASCII expects ESC 8 to hand it the line-drawing set again. And origin mode, which
        // xterm saves with them (QS239).
        Saved = new SavedCursor(Buffer.CursorRow, Buffer.CursorColumn, _pen,
                                _designated[0], _designated[1], _activeSet, OriginMode);
    }

    /// <summary>
    /// DECRC. With nothing saved, what is restored is <see cref="SavedCursor.Home"/>: the top left,
    /// the default pen and origin mode off, as xterm restores it (QS239).
    /// </summary>
    private void RestoreCursor()
    {
        SavedCursor saved = Saved;

        OriginMode = saved.OriginMode;
        Buffer.CursorRow = Math.Clamp(saved.Row, 0, Buffer.Rows - 1);
        Buffer.CursorColumn = Math.Clamp(saved.Column, 0, Buffer.Columns - 1);
        _pen = saved.Pen;
        _designated[0] = saved.G0;
        _designated[1] = saved.G1;
        _activeSet = saved.ActiveSet;
        PendingWrap = false;
    }

    /// <summary>
    /// DECSTR, <c>CSI ! p</c>: the soft reset, xterm's list of it. The screen, the cursor's place,
    /// autowrap (which xterm leaves on) and the mouse are left alone; everything a program sets for
    /// itself goes back to its default, so the next program does not inherit it (QS239).
    /// </summary>
    private void SoftReset()
    {
        CursorVisible = true;
        InsertMode = false;
        OriginMode = false;
        ReverseWrap = false;
        ReverseWrapExtended = false;
        MoreFix = false;
        ApplicationCursorKeys = false;
        ApplicationKeypad = false;
        LeftRightMarginMode = false;
        ClearColumnMargins();
        MarginTop = 0;
        MarginBottom = Buffer.Rows - 1;
        PendingWrap = false;
        _pen = Pen.Default;
        _designated[0] = CharacterSet.Ascii;
        _designated[1] = CharacterSet.Ascii;
        _activeSet = 0;
        ResetProtection();
        _saved[0] = SavedCursor.Home;
        _saved[1] = SavedCursor.Home;
    }

    private void Reset()
    {
        _pen = Pen.Default;
        _protection = Protection.Off;
        _attributeExtent = 0;
        AutoWrap = true;
        ReverseWrap = false;
        ReverseWrapExtended = false;
        MoreFix = false;
        OriginMode = false;
        InsertMode = false;
        LineFeedMode = false;
        LeftRightMarginMode = false;
        ClearColumnMargins();
        ApplicationCursorKeys = false;
        ApplicationKeypad = false;
        BackarrowSendsBackspace = false;
        BracketedPaste = false;
        PendingWrap = false;
        CursorVisible = true;
        MarginTop = 0;
        MarginBottom = Buffer.Rows - 1;
        ResetTabStops();
        ResetMouse();
        _designated[0] = CharacterSet.Ascii;
        _designated[1] = CharacterSet.Ascii;
        _activeSet = 0;
        ResetTitles();
        CursorStyle = 0;
        Palette.Reversed = false;
        _saved[0] = SavedCursor.Home;
        _saved[1] = SavedCursor.Home;
        _savedModes?.Clear();

        if (Screens.IsAlternate)
        {
            Screens.LeaveAlternate();
        }

        // The alternate screen too: since 47 keeps what it held (QS244), a reset that cleared only
        // the main one would hand the next program the last one's screen.
        Screens.ClearAlternate();
        Buffer.ClearScreen();
        Buffer.CursorRow = 0;
        Buffer.CursorColumn = 0;
    }

    // ---- Control sequences ----

    void IAnsiHandler.CsiDispatch(in CsiParameters parameters, ReadOnlySpan<byte> intermediates, byte final)
    {
        FlushText();

        // DECRQM, asked about an ANSI mode or, with the private marker, a DEC one - QS104. Before
        // the private branch below, which would otherwise take the question for a setting.
        if (final == (byte)'p' && intermediates.Length > 0 && intermediates[^1] == (byte)'$')
        {
            ModeReport(parameters, dec: intermediates[0] == (byte)'?');
            return;
        }

        // The private marker arrives as an intermediate, and what follows it is a different
        // instruction set entirely - `CSI ?7h` is not `CSI 7h`.
        if (intermediates.Length > 0 && intermediates[0] == (byte)'?')
        {
            switch (final)
            {
                case (byte)'h':
                case (byte)'l':
                    PrivateMode(parameters, final == (byte)'h');
                    break;

                case (byte)'n':
                    DeviceStatus(parameters, priv: true);
                    break;

                // XTSAVE and XTRESTORE, private modes saved and restored by number (QS239).
                case (byte)'s':
                    SaveModes(parameters);
                    break;

                case (byte)'r':
                    RestoreModes(parameters);
                    break;

                // DECSED and DECSEL, the selective erases: ED and EL that leave protected cells
                // alone (QS235).
                case (byte)'J':
                    EraseDisplay(parameters.Value(0, 0), selective: true);
                    break;

                case (byte)'K':
                    EraseLine(parameters.Value(0, 0), selective: true);
                    break;

                default:
                    Unhandled++;
                    break;
            }

            return;
        }

        // DA2 arrives under its own intermediate, and it is a different question from DA1 rather
        // than a variant of it.
        if (intermediates.Length > 0 && intermediates[0] == (byte)'>')
        {
            if (final == (byte)'c')
            {
                Send(Answer.SecondaryDeviceAttributes);
            }
            else
            {
                Unhandled++;
            }

            return;
        }

        // DECRQCRA, the rectangle's checksum, which is how a suite that does not own the screen reads
        // it back - QS103.
        if (intermediates.Length == 1 && intermediates[0] == (byte)'*' && final == (byte)'y')
        {
            RectangleChecksum(parameters);
            return;
        }

        // DECSTR, the soft reset (QS235, QS239).
        if (intermediates.Length == 1 && intermediates[0] == (byte)'!' && final == (byte)'p')
        {
            SoftReset();
            return;
        }

        // DECSCUSR, the cursor's style (QS243).
        if (intermediates.Length == 1 && intermediates[0] == (byte)' ' && final == (byte)'q')
        {
            SetCursorStyle(parameters.Value(0, 0));
            return;
        }

        // DECSCA, which protects what is printed next from the selective erases (QS235).
        if (intermediates.Length == 1 && intermediates[0] == (byte)'"' && final == (byte)'q')
        {
            SelectCharacterProtection(parameters.Value(0, 0));
            return;
        }

        // DECSERA, the selective erase of a rectangle (QS235).
        if (intermediates.Length == 1 && intermediates[0] == (byte)'$' && final == (byte)'{')
        {
            SelectiveEraseRectangle(parameters);
            return;
        }

        // The rectangle operations, DECCRA, DECFRA and DECERA, and DECSACE beside them (QS238).
        if (intermediates.Length == 1 && intermediates[0] == (byte)'$' && final is (byte)'v' or (byte)'x' or (byte)'z')
        {
            switch (final)
            {
                case (byte)'v':
                    CopyRectangle(parameters);
                    break;

                case (byte)'x':
                    FillRectangle(parameters);
                    break;

                default:
                    EraseRectangle(parameters);
                    break;
            }

            return;
        }

        if (intermediates.Length == 1 && intermediates[0] == (byte)'*' && final == (byte)'x')
        {
            SelectAttributeExtent(parameters.Value(0, 0));
            return;
        }

        // DECIC and DECDC, columns inserted and deleted at the cursor across the region (QS233).
        if (intermediates.Length == 1 && intermediates[0] == (byte)'\'' && final is (byte)'}' or (byte)'~')
        {
            Columns(Math.Max(1, parameters.Value(0, 1)), insert: final == (byte)'}');
            return;
        }

        TerminalBuffer buffer = Buffer;

        // CUB needs to know the cursor was waiting to wrap, which every other movement forgets. A
        // report - DA, DSR, a window report - moves nothing, so it leaves an owed wrap owed, as
        // xterm's do: a host asking where the cursor is must not change what it prints next (QS242).
        bool wasPending = PendingWrap;

        if (final is not ((byte)'c' or (byte)'n' or (byte)'t'))
        {
            PendingWrap = false;
        }

        // A parameter that is absent and a parameter that is zero are the same instruction. This is
        // the one line that makes that true for every movement below, and the falsification this
        // design names is exactly its absence.
        int count = Math.Max(1, parameters.Value(0, 1));

        switch (final)
        {
            case (byte)'A':
                buffer.CursorRow = Up(buffer, count);
                break;

            case (byte)'B':
                buffer.CursorRow = Down(buffer, count);
                break;

            case (byte)'C':
                // Stopping at the right margin from inside the region, at the edge from beyond it
                // (QS233) - Up and Down's rule turned sideways.
                buffer.CursorColumn = Math.Min(buffer.CursorColumn <= Right ? Right : buffer.Columns - 1,
                                               buffer.CursorColumn + count);
                break;

            case (byte)'D':
                CursorBack(buffer, count, wasPending);
                break;

            // HPR and VPR: CUF and CUD that know nothing of margins and stop only at the screen's
            // edge (QS241).
            case (byte)'a':
                buffer.CursorColumn = Math.Min(buffer.Columns - 1, buffer.CursorColumn + count);
                break;

            case (byte)'e':
                buffer.CursorRow = Math.Min(buffer.Rows - 1, buffer.CursorRow + count);
                break;

            case (byte)'E':
                buffer.CursorRow = Down(buffer, count);
                CarriageReturn();
                break;

            case (byte)'F':
                buffer.CursorRow = Up(buffer, count);
                CarriageReturn();
                break;

            case (byte)'G':
            case (byte)'`':
                buffer.CursorColumn = ColumnFor(count);
                break;

            case (byte)'d':
                buffer.CursorRow = RowFor(count);
                break;

            case (byte)'H':
            case (byte)'f':
                buffer.CursorRow = RowFor(Math.Max(1, parameters.Value(0, 1)));
                buffer.CursorColumn = ColumnFor(Math.Max(1, parameters.Value(1, 1)));
                break;

            case (byte)'r':
                SetMargins(parameters);
                break;

            case (byte)'h':
            case (byte)'l':
                AnsiMode(parameters, final == (byte)'h');
                break;

            case (byte)'g':
                ClearTabStop(parameters.Value(0, 0));
                break;

            case (byte)'I':
                for (int step = 0; step < count; step++)
                {
                    buffer.CursorColumn = NextTabStop(buffer.CursorColumn);
                }

                break;

            case (byte)'Z':
                for (int step = 0; step < count; step++)
                {
                    buffer.CursorColumn = PreviousTabStop(buffer.CursorColumn);
                }

                break;

            case (byte)'J':
                EraseDisplay(parameters.Value(0, 0));
                break;

            case (byte)'K':
                EraseLine(parameters.Value(0, 0));
                break;

            case (byte)'L':
                // Inside the region and nowhere else: a host that inserts a line below the bottom
                // margin is asking for nothing to happen, not for the margin to be ignored - and the
                // same beside the left and right ones (QS233).
                if (buffer.CursorRow >= MarginTop && buffer.CursorRow <= MarginBottom
                    && InColumns(buffer.CursorColumn))
                {
                    ScrollRegion(buffer.CursorRow, up: false, count);
                }

                break;

            case (byte)'M':
                if (buffer.CursorRow >= MarginTop && buffer.CursorRow <= MarginBottom
                    && InColumns(buffer.CursorColumn))
                {
                    ScrollRegion(buffer.CursorRow, up: true, count);
                }

                break;

            case (byte)'@':
                InsertCharacters(count);
                break;

            case (byte)'P':
                DeleteCharacters(count);
                break;

            case (byte)'X':
                buffer.Clear(buffer.CursorRow, buffer.CursorColumn, count, _pen.Background, KeepsProtected(false));
                break;

            case (byte)'S':
                // The region, wherever the cursor is: SU and SD name the region and not the cursor.
                ScrollRegion(MarginTop, up: true, count);
                break;

            case (byte)'T':
                ScrollRegion(MarginTop, up: false, count);
                break;

            case (byte)'b':
                Repeat(count);
                break;

            case (byte)'m':
                ApplySgr(parameters);
                break;

            case (byte)'s':
                // DECSLRM while left and right margins are allowed, SCOSC otherwise - the one byte
                // both use, told apart by the mode, as xterm tells them apart (QS233).
                if (LeftRightMarginMode)
                {
                    SetColumnMargins(parameters);
                }
                else
                {
                    SaveCursor();
                }

                break;

            case (byte)'u':
                RestoreCursor();
                break;

            case (byte)'c':
                // A parameter here is only ever zero, and a host that sent one meant the same
                // question.
                if (parameters.Value(0, 0) == 0)
                {
                    Send(Answer.DeviceAttributes);
                }
                else
                {
                    Unhandled++;
                }

                break;

            case (byte)'n':
                DeviceStatus(parameters, priv: false);
                break;

            case (byte)'t':
                WindowOperation(parameters);
                break;

            default:
                Unhandled++;
                break;
        }
    }

    /// <summary>
    /// Which screen row a one-based row number means. Under DECOM it is relative to the top margin
    /// and clamped inside the region, which is the whole reason the mode exists rather than being a
    /// flag a movement could ignore.
    /// </summary>
    private int RowFor(int oneBased) => OriginMode
        ? Math.Clamp(MarginTop + oneBased - 1, MarginTop, MarginBottom)
        : Math.Clamp(oneBased - 1, 0, Buffer.Rows - 1);

    /// <summary>The same for a column: relative to the left margin under DECOM, and clamped inside the region (QS233).</summary>
    private int ColumnFor(int oneBased) => OriginMode
        ? Math.Clamp(MarginLeft + oneBased - 1, MarginLeft, Right)
        : Math.Clamp(oneBased - 1, 0, Buffer.Columns - 1);

    /// <summary>
    /// DECIC and DECDC: columns inserted or deleted at the cursor, in every row of the region, between
    /// the cursor and the right margin. Outside the region they do nothing (QS233).
    /// </summary>
    private void Columns(int count, bool insert)
    {
        TerminalBuffer buffer = Buffer;

        if (buffer.CursorRow < MarginTop || buffer.CursorRow > MarginBottom || !InColumns(buffer.CursorColumn))
        {
            return;
        }

        if (insert)
        {
            buffer.InsertColumns(MarginTop, MarginBottom, buffer.CursorColumn, Right, count);
        }
        else
        {
            buffer.DeleteColumns(MarginTop, MarginBottom, buffer.CursorColumn, Right, count);
        }
    }

    /// <summary>
    /// Where CUU and CPL leave the cursor: <paramref name="count"/> rows up, stopping at the top
    /// margin when the cursor starts at or below it, and at the screen's top otherwise (QS205).
    ///
    /// <para><b>DEC's rule and xterm's, whatever DECOM says.</b> A movement inside the region is a
    /// program working inside the region, and one that ran past its edge would write into rows the
    /// program set aside, which is what vttest's soft-scroll test caught over row 1. A cursor
    /// already above the region has no margin between it and the top, so it goes to the top.</para>
    /// </summary>
    private int Up(TerminalBuffer buffer, int count)
    {
        int stop = buffer.CursorRow >= MarginTop ? MarginTop : 0;

        return Math.Max(stop, buffer.CursorRow - count);
    }

    /// <summary>
    /// Where CUD and CNL leave the cursor: <paramref name="count"/> rows down, stopping at the
    /// bottom margin when the cursor starts at or above it, and at the screen's bottom otherwise.
    /// <see cref="Up"/> says why.
    /// </summary>
    private int Down(TerminalBuffer buffer, int count)
    {
        int stop = buffer.CursorRow <= MarginBottom ? MarginBottom : buffer.Rows - 1;

        return Math.Min(stop, buffer.CursorRow + count);
    }

    private void SetTabStop(int column)
    {
        if (column >= 0 && column < _tabStops.Length)
        {
            _tabStops[column] = true;
        }
    }

    /// <summary>TBC. Zero clears the stop under the cursor; three clears every one there is.</summary>
    private void ClearTabStop(int mode)
    {
        switch (mode)
        {
            case 0:
                if (Buffer.CursorColumn < _tabStops.Length)
                {
                    _tabStops[Buffer.CursorColumn] = false;
                }

                break;

            case 3:
                Array.Clear(_tabStops);
                break;

            default:
                Unhandled++;
                break;
        }
    }

    private void Repeat(int count)
    {
        string last = char.ConvertFromUtf32(_lastPrinted);

        for (int index = 0; index < count; index++)
        {
            PrintCluster(last);
        }
    }

    /// <summary>
    /// ED. Every erase here, and EL and ECH beside it, leaves the pen's background behind and
    /// nothing else of it — QS204, xterm's background colour erase.
    /// <paramref name="selective"/> is DECSED, which leaves protected cells alone (QS235).
    /// </summary>
    private void EraseDisplay(int mode, bool selective = false)
    {
        TerminalBuffer buffer = Buffer;
        Colour ground = _pen.Background;
        bool keep = KeepsProtected(selective);

        switch (mode)
        {
            case 0:
                buffer.Clear(buffer.CursorRow, buffer.CursorColumn, buffer.Columns, ground, keep);

                for (int row = buffer.CursorRow + 1; row < buffer.Rows; row++)
                {
                    buffer.Clear(row, 0, buffer.Columns, ground, keep);
                }

                break;

            case 1:
                for (int row = 0; row < buffer.CursorRow; row++)
                {
                    buffer.Clear(row, 0, buffer.Columns, ground, keep);
                }

                buffer.Clear(buffer.CursorRow, 0, buffer.CursorColumn + 1, ground, keep);
                break;

            case 2 when keep:
                for (int row = 0; row < buffer.Rows; row++)
                {
                    buffer.Clear(row, 0, buffer.Columns, ground, keep);
                }

                break;

            case 2:
                buffer.ClearScreen(ground);
                break;

            case 3:
                // The scrollback and nothing else, as xterm's ED 3 and DECSED 3 both are (QS244):
                // a host clearing history has not asked for the screen to go with it.
                buffer.DropScrollback();
                break;

            default:
                Unhandled++;
                break;
        }
    }

    /// <summary>EL, and with <paramref name="selective"/> DECSEL, as <see cref="EraseDisplay"/> has it.</summary>
    private void EraseLine(int mode, bool selective = false)
    {
        TerminalBuffer buffer = Buffer;
        Colour ground = _pen.Background;
        bool keep = KeepsProtected(selective);

        switch (mode)
        {
            case 0:
                buffer.Clear(buffer.CursorRow, buffer.CursorColumn, buffer.Columns, ground, keep);
                break;

            case 1:
                buffer.Clear(buffer.CursorRow, 0, buffer.CursorColumn + 1, ground, keep);
                break;

            case 2:
                buffer.Clear(buffer.CursorRow, 0, buffer.Columns, ground, keep);
                break;

            default:
                Unhandled++;
                break;
        }
    }

    // Both are the buffer's own operations, because a mutation performed out here through a span
    // would be one its damage record never saw - QS22.
    // Inside the left and right margins they shift only up to the right one, and outside them they
    // do nothing at all, as xterm does (QS233).
    private void InsertCharacters(int count)
    {
        if (InColumns(Buffer.CursorColumn))
        {
            Buffer.InsertCells(Buffer.CursorRow, Buffer.CursorColumn, count, Right + 1);
        }
    }

    private void DeleteCharacters(int count)
    {
        if (InColumns(Buffer.CursorColumn))
        {
            Buffer.DeleteCells(Buffer.CursorRow, Buffer.CursorColumn, count, Right + 1);
        }
    }

    // Device control strings are answered in Emulator.Dcs.cs, which is where DECRQSS lives.
}
