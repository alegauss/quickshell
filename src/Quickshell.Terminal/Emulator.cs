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
    private Pen _savedPen = Pen.Default;
    private int _savedRow;
    private int _savedColumn;
    private int _lastPrinted = ' ';
    private readonly CharacterSet[] _designated = [CharacterSet.Ascii, CharacterSet.Ascii];
    private int _activeSet;

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
        Screens.IsAlternate);

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
        Keys.Encode(key, modifiers, ApplicationCursorKeys, ApplicationKeypad, destination);

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

    /// <summary>Resizes both screens and puts the cursor back inside the new one.</summary>
    public void Resize(int columns, int rows)
    {
        Screens.Resize(columns, rows);
        _savedRow = Math.Clamp(_savedRow, 0, rows - 1);
        _savedColumn = Math.Clamp(_savedColumn, 0, columns - 1);

        // The region and the stops are both stated in the old geometry, and neither survives a
        // resize meaningfully. A host that had set either is told the new size and sets them again.
        MarginTop = 0;
        MarginBottom = rows - 1;
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
            }
        }

        if (buffer.CursorColumn + width > buffer.Columns)
        {
            // A wide character with one column left. It cannot be split, so either the line wraps
            // or - with wrapping off - it lands at the end and overwrites what is there.
            if (AutoWrap)
            {
                buffer.SetScreenWrapped(buffer.CursorRow, true);
                NextLine();
            }
            else
            {
                buffer.CursorColumn = buffer.Columns - width;
            }
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

        if (buffer.CursorColumn + width >= buffer.Columns)
        {
            // Stay on the cell just written and owe a wrap. Moving now is what puts a blank line
            // after every line that happens to be exactly the width of the terminal.
            buffer.CursorColumn = buffer.Columns - width;
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
        // whole of what makes a full-width line not grow a blank one after it.
        PendingWrap = false;

        switch (control)
        {
            case 0x08:
                buffer.CursorColumn = Math.Max(0, buffer.CursorColumn - 1);
                break;

            case 0x09:
                buffer.CursorColumn = NextTabStop(buffer.CursorColumn);
                break;

            case 0x0A:
            case 0x0B:
            case 0x0C:
                NextLine();
                break;

            case 0x0D:
                buffer.CursorColumn = 0;
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

    /// <summary>Down one row, scrolling the screen when there is no row below.</summary>
    private void NextLine()
    {
        TerminalBuffer buffer = Buffer;
        buffer.CursorColumn = 0;
        PendingWrap = false;

        if (buffer.CursorRow != MarginBottom)
        {
            buffer.CursorRow = Math.Min(buffer.Rows - 1, buffer.CursorRow + 1);
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

        buffer.ScrollRegionUp(MarginTop, MarginBottom);
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

        TerminalBuffer buffer = Buffer;

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
                buffer.CursorColumn = 0;
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

        buffer.ScrollRegionDown(MarginTop, MarginBottom);
    }

    private void SaveCursor()
    {
        // The whole state and not the position: a program that restores expects its colours back
        // too, and one that gets only the position paints the rest of its screen in whatever the
        // last thing to run happened to leave set.
        _savedRow = Buffer.CursorRow;
        _savedColumn = Buffer.CursorColumn;
        _savedPen = _pen;
    }

    private void RestoreCursor()
    {
        Buffer.CursorRow = Math.Clamp(_savedRow, 0, Buffer.Rows - 1);
        Buffer.CursorColumn = Math.Clamp(_savedColumn, 0, Buffer.Columns - 1);
        _pen = _savedPen;
    }

    private void Reset()
    {
        _pen = Pen.Default;
        AutoWrap = true;
        OriginMode = false;
        ApplicationCursorKeys = false;
        ApplicationKeypad = false;
        PendingWrap = false;
        CursorVisible = true;
        MarginTop = 0;
        MarginBottom = Buffer.Rows - 1;
        ResetTabStops();
        ResetMouse();
        _designated[0] = CharacterSet.Ascii;
        _designated[1] = CharacterSet.Ascii;
        _activeSet = 0;
        Title = string.Empty;
        _savedPen = Pen.Default;
        _savedRow = 0;
        _savedColumn = 0;

        if (Screens.IsAlternate)
        {
            Screens.LeaveAlternate();
        }

        Buffer.ClearScreen();
        Buffer.CursorRow = 0;
        Buffer.CursorColumn = 0;
    }

    // ---- Control sequences ----

    void IAnsiHandler.CsiDispatch(in CsiParameters parameters, ReadOnlySpan<byte> intermediates, byte final)
    {
        FlushText();

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
                    DeviceStatus(parameters.Value(0, 0), priv: true);
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

        TerminalBuffer buffer = Buffer;
        PendingWrap = false;

        // A parameter that is absent and a parameter that is zero are the same instruction. This is
        // the one line that makes that true for every movement below, and the falsification this
        // design names is exactly its absence.
        int count = Math.Max(1, parameters.Value(0, 1));

        switch (final)
        {
            case (byte)'A':
                buffer.CursorRow = Math.Max(0, buffer.CursorRow - count);
                break;

            case (byte)'B':
                buffer.CursorRow = Math.Min(buffer.Rows - 1, buffer.CursorRow + count);
                break;

            case (byte)'C':
                buffer.CursorColumn = Math.Min(buffer.Columns - 1, buffer.CursorColumn + count);
                break;

            case (byte)'D':
                buffer.CursorColumn = Math.Max(0, buffer.CursorColumn - count);
                break;

            case (byte)'E':
                buffer.CursorRow = Math.Min(buffer.Rows - 1, buffer.CursorRow + count);
                buffer.CursorColumn = 0;
                break;

            case (byte)'F':
                buffer.CursorRow = Math.Max(0, buffer.CursorRow - count);
                buffer.CursorColumn = 0;
                break;

            case (byte)'G':
            case (byte)'`':
                buffer.CursorColumn = Math.Clamp(count - 1, 0, buffer.Columns - 1);
                break;

            case (byte)'d':
                buffer.CursorRow = RowFor(count);
                break;

            case (byte)'H':
            case (byte)'f':
                buffer.CursorRow = RowFor(Math.Max(1, parameters.Value(0, 1)));
                buffer.CursorColumn = Math.Clamp(Math.Max(1, parameters.Value(1, 1)) - 1, 0, buffer.Columns - 1);
                break;

            case (byte)'r':
                SetMargins(parameters);
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
                // margin is asking for nothing to happen, not for the margin to be ignored.
                if (buffer.CursorRow >= MarginTop && buffer.CursorRow <= MarginBottom)
                {
                    buffer.ScrollRegionDown(buffer.CursorRow, MarginBottom, count);
                }

                break;

            case (byte)'M':
                if (buffer.CursorRow >= MarginTop && buffer.CursorRow <= MarginBottom)
                {
                    buffer.ScrollRegionUp(buffer.CursorRow, MarginBottom, count);
                }

                break;

            case (byte)'@':
                InsertCharacters(count);
                break;

            case (byte)'P':
                DeleteCharacters(count);
                break;

            case (byte)'X':
                buffer.Clear(buffer.CursorRow, buffer.CursorColumn, count);
                break;

            case (byte)'S':
                buffer.ScrollRegionUp(MarginTop, MarginBottom, count);
                break;

            case (byte)'T':
                buffer.ScrollRegionDown(MarginTop, MarginBottom, count);
                break;

            case (byte)'b':
                Repeat(count);
                break;

            case (byte)'m':
                ApplySgr(parameters);
                break;

            case (byte)'s':
                SaveCursor();
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
                DeviceStatus(parameters.Value(0, 0), priv: false);
                break;

            case (byte)'t':
                WindowOperation(parameters.Value(0, 0));
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

    private void EraseDisplay(int mode)
    {
        TerminalBuffer buffer = Buffer;

        switch (mode)
        {
            case 0:
                buffer.Clear(buffer.CursorRow, buffer.CursorColumn, buffer.Columns);

                for (int row = buffer.CursorRow + 1; row < buffer.Rows; row++)
                {
                    buffer.Clear(row, 0, buffer.Columns);
                }

                break;

            case 1:
                for (int row = 0; row < buffer.CursorRow; row++)
                {
                    buffer.Clear(row, 0, buffer.Columns);
                }

                buffer.Clear(buffer.CursorRow, 0, buffer.CursorColumn + 1);
                break;

            case 2:
                buffer.ClearScreen();
                break;

            case 3:
                buffer.ClearScreen();
                buffer.DropScrollback();
                break;

            default:
                Unhandled++;
                break;
        }
    }

    private void EraseLine(int mode)
    {
        TerminalBuffer buffer = Buffer;

        switch (mode)
        {
            case 0:
                buffer.Clear(buffer.CursorRow, buffer.CursorColumn, buffer.Columns);
                break;

            case 1:
                buffer.Clear(buffer.CursorRow, 0, buffer.CursorColumn + 1);
                break;

            case 2:
                buffer.Clear(buffer.CursorRow, 0, buffer.Columns);
                break;

            default:
                Unhandled++;
                break;
        }
    }

    // Both are the buffer's own operations, because a mutation performed out here through a span
    // would be one its damage record never saw - QS22.
    private void InsertCharacters(int count) =>
        Buffer.InsertCells(Buffer.CursorRow, Buffer.CursorColumn, count);

    private void DeleteCharacters(int count) =>
        Buffer.DeleteCells(Buffer.CursorRow, Buffer.CursorColumn, count);

    // Device control strings are answered in Emulator.Dcs.cs, which is where DECRQSS lives.
}
