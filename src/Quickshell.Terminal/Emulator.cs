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

    /// <summary>Opens a terminal of a given size, with scrollback behind the primary screen.</summary>
    public Emulator(int columns, int rows, int scrollback = 1000)
    {
        Screens = new Screens(columns, rows, scrollback);
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
        if (_segmenter.Pending == 0)
        {
            return;
        }

        foreach (string cluster in _segmenter.Flush())
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
    }

    // ---- Text ----

    void IAnsiHandler.Print(ReadOnlySpan<byte> text)
    {
        foreach (string cluster in _segmenter.Feed(_decoder.Decode(text)))
        {
            PrintCluster(cluster);
        }
    }

    private void PrintCluster(string cluster)
    {
        int codepoint = char.ConvertToUtf32(cluster, 0);
        int width = CharacterWidth.OfCluster(cluster);

        if (width == 0)
        {
            AttachToPrevious(cluster);
            return;
        }

        TerminalBuffer buffer = Buffer;

        if (buffer.CursorColumn + width > buffer.Columns)
        {
            // The row the host wrote as one logical line continues into the next, and saying so is
            // the only record reflow, selection and copy will have.
            buffer.SetScreenWrapped(buffer.CursorRow, true);
            NextLine();
        }

        Cell cell = cluster.Length == 1 || char.IsSurrogatePair(cluster, 0)
            ? Cell.For(codepoint, _pen.Foreground, _pen.Background, _pen.Flags, _pen.Underline, width)
            : ClusterCell(buffer, cluster, codepoint, width);

        buffer.Write(buffer.CursorRow, buffer.CursorColumn, cell);

        if (width == 2 && buffer.CursorColumn + 1 < buffer.Columns)
        {
            // The trailing half is a real cell holding nothing, which is what keeps the column
            // count honest for everything that reads the row afterwards.
            buffer.Write(buffer.CursorRow, buffer.CursorColumn + 1,
                         Cell.For(' ', _pen.Foreground, _pen.Background, _pen.Flags, _pen.Underline, 0));
        }

        _lastPrinted = codepoint;
        buffer.CursorColumn += width;
    }

    /// <summary>
    /// A mark that arrived after its base had already been written, because the read ended between
    /// them. It belongs to the cell before the cursor, so it is added to that cell's text rather
    /// than dropped — dropping it is how an accent typed as two codepoints disappears.
    /// </summary>
    private void AttachToPrevious(string mark)
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
        int index = buffer.InternCluster(buffer.TextOf(before) + mark);

        if (index < 0)
        {
            return;
        }

        buffer.Write(row, column, Cell.ForCluster(
            index, before.Foreground, before.Background, before.Flags, before.Underline, before.Width));
    }

    private Cell ClusterCell(TerminalBuffer buffer, string cluster, int codepoint, int width)
    {
        int index = buffer.InternCluster(cluster);

        // A table that has stopped growing gives -1, and the base codepoint is what is left. The
        // accent is lost rather than the session, which is the trade the ceiling exists to make.
        return index < 0
            ? Cell.For(codepoint, _pen.Foreground, _pen.Background, _pen.Flags, _pen.Underline, width)
            : Cell.ForCluster(index, _pen.Foreground, _pen.Background, _pen.Flags, _pen.Underline, width);
    }

    // ---- Controls ----

    void IAnsiHandler.Execute(byte control)
    {
        FlushText();

        TerminalBuffer buffer = Buffer;

        switch (control)
        {
            case 0x08:
                buffer.CursorColumn = Math.Max(0, buffer.CursorColumn - 1);
                break;

            case 0x09:
                buffer.CursorColumn = Math.Min(buffer.Columns - 1, (buffer.CursorColumn + 8) & ~7);
                break;

            case 0x0A:
            case 0x0B:
            case 0x0C:
                NextLine();
                break;

            case 0x0D:
                buffer.CursorColumn = 0;
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

        if (buffer.CursorRow + 1 < buffer.Rows)
        {
            buffer.CursorRow++;
            return;
        }

        buffer.ScrollUp();
    }

    // ---- Escape sequences ----

    void IAnsiHandler.EscapeDispatch(ReadOnlySpan<byte> intermediates, byte final)
    {
        FlushText();

        if (!intermediates.IsEmpty)
        {
            // Charset designation and the rest. Counted rather than guessed at: a sequence answered
            // wrongly is worse than one answered not at all.
            Unhandled++;
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

            case (byte)'M':
                ReverseIndex();
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

        if (buffer.CursorRow > 0)
        {
            buffer.CursorRow--;
            return;
        }

        buffer.ScrollRegionDown(0, buffer.Rows - 1);
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

        TerminalBuffer buffer = Buffer;

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
                buffer.CursorRow = Math.Clamp(count - 1, 0, buffer.Rows - 1);
                break;

            case (byte)'H':
            case (byte)'f':
                buffer.CursorRow = Math.Clamp(Math.Max(1, parameters.Value(0, 1)) - 1, 0, buffer.Rows - 1);
                buffer.CursorColumn = Math.Clamp(Math.Max(1, parameters.Value(1, 1)) - 1, 0, buffer.Columns - 1);
                break;

            case (byte)'J':
                EraseDisplay(parameters.Value(0, 0));
                break;

            case (byte)'K':
                EraseLine(parameters.Value(0, 0));
                break;

            case (byte)'L':
                buffer.ScrollRegionDown(buffer.CursorRow, buffer.Rows - 1, count);
                break;

            case (byte)'M':
                buffer.ScrollRegionUp(buffer.CursorRow, buffer.Rows - 1, count);
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
                buffer.ScrollRegionUp(0, buffer.Rows - 1, count);
                break;

            case (byte)'T':
                buffer.ScrollRegionDown(0, buffer.Rows - 1, count);
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

    private void InsertCharacters(int count)
    {
        TerminalBuffer buffer = Buffer;
        Span<Cell> row = buffer.Screen(buffer.CursorRow);
        int from = buffer.CursorColumn;
        int shift = Math.Min(count, row.Length - from);

        row[from..(row.Length - shift)].CopyTo(row[(from + shift)..]);
        row.Slice(from, shift).Fill(Cell.Blank);
    }

    private void DeleteCharacters(int count)
    {
        TerminalBuffer buffer = Buffer;
        Span<Cell> row = buffer.Screen(buffer.CursorRow);
        int from = buffer.CursorColumn;
        int shift = Math.Min(count, row.Length - from);

        row[(from + shift)..].CopyTo(row[from..]);
        row[(row.Length - shift)..].Fill(Cell.Blank);
    }

    // ---- Strings, which this line does not answer for ----

    void IAnsiHandler.OscStart() => FlushText();

    void IAnsiHandler.OscPut(ReadOnlySpan<byte> bytes)
    {
    }

    void IAnsiHandler.OscEnd()
    {
    }

    void IAnsiHandler.DcsHook(in CsiParameters parameters, ReadOnlySpan<byte> intermediates, byte final) =>
        FlushText();

    void IAnsiHandler.DcsPut(ReadOnlySpan<byte> bytes)
    {
    }

    void IAnsiHandler.DcsUnhook()
    {
    }
}
