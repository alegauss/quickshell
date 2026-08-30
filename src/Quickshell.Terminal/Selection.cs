namespace Quickshell.Terminal;

/// <summary>What a user meant by the gesture that started the selection.</summary>
public enum SelectionMode
{
    /// <summary>A drag, which takes exactly the cells it covered.</summary>
    Character,

    /// <summary>A double click, which grows to whole words.</summary>
    Word,

    /// <summary>A triple click, which takes whole logical lines.</summary>
    Line,

    /// <summary>
    /// A drag on a modifier, which takes a rectangle. The only way to copy one column out of tabular
    /// output, and the reason it is a mode rather than a setting.
    /// </summary>
    Block,
}

/// <summary>
/// One end of a selection, in coordinates that survive the screen scrolling underneath it.
/// </summary>
/// <param name="Line">The line of the buffer's whole life, as <see cref="TerminalBuffer.AbsoluteLine"/>
/// numbers them.</param>
/// <param name="Column">The column.</param>
public readonly record struct SelectionPoint(long Line, int Column);

/// <summary>
/// What is selected, and what it copies as.
///
/// <para><b>Its ends are absolute line numbers and not screen rows</b>, which is what QS22's line
/// identity was for. Output arriving while a selection is up scrolls the screen underneath it; a
/// selection held as rows would slide up the screen with the text it is not attached to, and
/// highlight the wrong words a second later.</para>
///
/// <para><b>Copying is where the wrapped flag earns its place.</b> A logical line broken across three
/// rows copies as one line with no break inserted — a user who copies a long path out of a terminal
/// and finds a newline in the middle of it has been handed something that does not work. That is
/// this class's falsification and its first test.</para>
///
/// <para><b>Trailing whitespace goes from a row that ends a line and stays on one that continues.</b>
/// A terminal pads rows the user did not type, so the padding is not theirs to copy; but a row in the
/// middle of a wrapped line is full by construction, so a space in its last column is a space the
/// host printed — the one between two words the wrap fell between. QS23 found that the hard way in
/// reflow, and it is the same rule here.</para>
/// </summary>
public sealed class Selection
{
    private SelectionPoint _anchor;
    private SelectionPoint _focus;

    /// <summary>Whether anything is selected.</summary>
    public bool IsActive { get; private set; }

    /// <summary>What the gesture meant.</summary>
    public SelectionMode Mode { get; private set; }

    /// <summary>Where the gesture started. Not necessarily the earlier end.</summary>
    public SelectionPoint Anchor => _anchor;

    /// <summary>Where it currently reaches.</summary>
    public SelectionPoint Focus => _focus;

    /// <summary>The earlier of the two ends, which is where a copy begins.</summary>
    public SelectionPoint Start => Before(_anchor, _focus) ? _anchor : _focus;

    /// <summary>The later of the two ends.</summary>
    public SelectionPoint End => Before(_anchor, _focus) ? _focus : _anchor;

    /// <summary>Begins a selection at a point, in the mode the gesture meant.</summary>
    public void Begin(TerminalBuffer buffer, SelectionPoint at, SelectionMode mode)
    {
        ArgumentNullException.ThrowIfNull(buffer);

        IsActive = true;
        Mode = mode;
        _anchor = at;
        _focus = at;

        Snap(buffer);
    }

    /// <summary>
    /// Moves the far end, which is a drag. The anchor stays where the gesture started, so dragging
    /// back past it selects the other way round without starting again.
    /// </summary>
    public void Extend(TerminalBuffer buffer, SelectionPoint to)
    {
        ArgumentNullException.ThrowIfNull(buffer);

        if (!IsActive)
        {
            return;
        }

        _focus = to;

        Snap(buffer);
    }

    /// <summary>Drops the selection.</summary>
    public void Clear()
    {
        IsActive = false;
        _anchor = default;
        _focus = default;
    }

    /// <summary>
    /// Whether a cell is inside the selection, which is what the renderer asks once per cell.
    /// </summary>
    public bool Contains(long line, int column)
    {
        if (!IsActive)
        {
            return false;
        }

        SelectionPoint start = Start;
        SelectionPoint end = End;

        if (Mode == SelectionMode.Block)
        {
            // A rectangle, so the columns bound every row rather than only the first and last.
            int left = Math.Min(_anchor.Column, _focus.Column);
            int right = Math.Max(_anchor.Column, _focus.Column);

            return line >= start.Line && line <= end.Line && column >= left && column < right;
        }

        if (line < start.Line || line > end.Line)
        {
            return false;
        }

        int from = line == start.Line ? start.Column : 0;
        int to = line == end.Line ? end.Column : int.MaxValue;

        return column >= from && column < to;
    }

    /// <summary>How many characters <see cref="CopyTo"/> would write.</summary>
    public int MeasureCopy(TerminalBuffer buffer) => Write(buffer, default, measuring: true);

    /// <summary>
    /// Writes the selected text.
    /// </summary>
    /// <returns>How many characters were written, or -1 where the destination is too small.</returns>
    public int CopyTo(TerminalBuffer buffer, Span<char> destination) =>
        Write(buffer, destination, measuring: false);

    /// <summary>
    /// Grows the ends to whatever the mode says they cover.
    ///
    /// <para>Done on every change rather than once, because a drag in word mode grows word by word
    /// and a drag in line mode grows line by line — a gesture that snapped only at its start would
    /// select half a word as soon as it moved.</para>
    /// </summary>
    private void Snap(TerminalBuffer buffer)
    {
        switch (Mode)
        {
            case SelectionMode.Word:
                SnapWords(buffer);
                break;

            case SelectionMode.Line:
                SnapLines(buffer);
                break;

            default:
                break;
        }
    }

    private void SnapWords(TerminalBuffer buffer)
    {
        bool forward = Before(_anchor, _focus) || _anchor == _focus;
        SelectionPoint start = forward ? _anchor : _focus;
        SelectionPoint end = forward ? _focus : _anchor;

        start = start with { Column = WordStart(buffer, start) };
        end = end with { Column = WordEnd(buffer, end) };

        _anchor = forward ? start : end;
        _focus = forward ? end : start;
    }

    private void SnapLines(TerminalBuffer buffer)
    {
        bool forward = Before(_anchor, _focus) || _anchor == _focus;
        SelectionPoint start = forward ? _anchor : _focus;
        SelectionPoint end = forward ? _focus : _anchor;

        // A logical line and not a row: a triple click on the middle of a wrapped line takes all of
        // it, because that is the line the user can see.
        start = new SelectionPoint(LogicalStart(buffer, start.Line), 0);
        end = new SelectionPoint(LogicalEnd(buffer, end.Line), buffer.Columns);

        _anchor = forward ? start : end;
        _focus = forward ? end : start;
    }

    private static int WordStart(TerminalBuffer buffer, SelectionPoint at)
    {
        ReadOnlySpan<Cell> row = Row(buffer, at.Line);
        int column = Math.Clamp(at.Column, 0, Math.Max(0, row.Length - 1));

        if (row.IsEmpty || !IsWord(buffer, row[column]))
        {
            return column;
        }

        while (column > 0 && IsWord(buffer, row[column - 1]))
        {
            column--;
        }

        return column;
    }

    private static int WordEnd(TerminalBuffer buffer, SelectionPoint at)
    {
        ReadOnlySpan<Cell> row = Row(buffer, at.Line);
        int column = Math.Clamp(at.Column, 0, Math.Max(0, row.Length - 1));

        if (row.IsEmpty || !IsWord(buffer, row[column]))
        {
            return column + 1;
        }

        while (column + 1 < row.Length && IsWord(buffer, row[column + 1]))
        {
            column++;
        }

        return column + 1;
    }

    /// <summary>
    /// What counts as one word. Letters, digits and the punctuation a path or a URL is made of,
    /// because a double click on a path that stopped at the first slash would be a gesture nobody
    /// wanted.
    /// </summary>
    private static bool IsWord(TerminalBuffer buffer, Cell cell)
    {
        if (cell.IsCluster)
        {
            return true;
        }

        int codepoint = cell.Codepoint;

        return codepoint > 0x7F
            || char.IsLetterOrDigit((char)codepoint)
            || codepoint is '_' or '-' or '.' or '/' or '\\' or ':' or '~' or '@' or '+' or '=' or '%' or '#';
    }

    private static long LogicalStart(TerminalBuffer buffer, long line)
    {
        long first = buffer.TopLine - buffer.ScrollbackLines;

        while (line > first && buffer.IsWrapped(Retained(buffer, line - 1)))
        {
            line--;
        }

        return line;
    }

    private static long LogicalEnd(TerminalBuffer buffer, long line)
    {
        long last = buffer.TopLine - buffer.ScrollbackLines + buffer.LineCount - 1;

        while (line < last && buffer.IsWrapped(Retained(buffer, line)))
        {
            line++;
        }

        return line;
    }

    /// <summary>
    /// The one walk, measuring or writing. One body so the length cannot disagree with the text.
    /// </summary>
    private int Write(TerminalBuffer buffer, Span<char> destination, bool measuring)
    {
        ArgumentNullException.ThrowIfNull(buffer);

        if (!IsActive)
        {
            return 0;
        }

        SelectionPoint start = Start;
        SelectionPoint end = End;
        int written = 0;

        // Line breaks are owed rather than written, and a break nothing follows is never paid. That
        // keeps a selection dragged to the bottom of a half-empty screen from copying as the text and
        // then eight blank lines: those rows are the terminal's padding too.
        int owed = 0;

        for (long line = start.Line; line <= end.Line; line++)
        {
            if (!Holds(buffer, line))
            {
                continue;
            }

            ReadOnlySpan<Cell> row = Row(buffer, line);
            (int from, int to) = Bounds(buffer, line, start, end, row.Length);

            // A row that continues into the next is full by construction, so its last column may hold
            // a space the host printed. Only a row that ends a line has padding to strip — and only
            // where the selection reached that end, because a space inside what a user dragged over
            // is a space they chose.
            bool continues = Mode != SelectionMode.Block && buffer.IsWrapped(Retained(buffer, line));
            int extent = !continues && to >= row.Length ? Trimmed(row, from, to) : to;

            for (int column = from; column < extent; column++)
            {
                if (row[column].Width == 0)
                {
                    // The trailing half of a wide pair holds no text of its own.
                    continue;
                }

                while (owed > 0)
                {
                    if (!Append("\n", destination, measuring, ref written))
                    {
                        return -1;
                    }

                    owed--;
                }

                if (!Append(buffer.TextOf(row[column]), destination, measuring, ref written))
                {
                    return -1;
                }
            }

            // No break inside a wrapped line. This is the falsification.
            if (line < end.Line && !continues)
            {
                owed++;
            }
        }

        return written;
    }

    private (int From, int To) Bounds(
        TerminalBuffer buffer,
        long line,
        SelectionPoint start,
        SelectionPoint end,
        int width)
    {
        if (Mode == SelectionMode.Block)
        {
            return (
                Math.Clamp(Math.Min(_anchor.Column, _focus.Column), 0, width),
                Math.Clamp(Math.Max(_anchor.Column, _focus.Column), 0, width));
        }

        return (
            Math.Clamp(line == start.Line ? start.Column : 0, 0, width),
            Math.Clamp(line == end.Line ? end.Column : width, 0, width));
    }

    /// <summary>Where the content ends, so a terminal's own padding is not copied as spaces.</summary>
    private static int Trimmed(ReadOnlySpan<Cell> row, int from, int to)
    {
        while (to > from && row[to - 1].IsBlank)
        {
            to--;
        }

        return to;
    }

    private static bool Append(string text, Span<char> destination, bool measuring, ref int written)
    {
        if (!measuring)
        {
            if (written + text.Length > destination.Length)
            {
                return false;
            }

            text.CopyTo(destination[written..]);
        }

        written += text.Length;

        return true;
    }

    /// <summary>Whether the buffer still holds a line, which scrollback eviction decides.</summary>
    private static bool Holds(TerminalBuffer buffer, long line)
    {
        long first = buffer.TopLine - buffer.ScrollbackLines;

        return line >= first && line < first + buffer.LineCount;
    }

    private static int Retained(TerminalBuffer buffer, long line) =>
        (int)(line - (buffer.TopLine - buffer.ScrollbackLines));

    private static ReadOnlySpan<Cell> Row(TerminalBuffer buffer, long line) =>
        buffer.Line(Retained(buffer, line));

    private static bool Before(SelectionPoint left, SelectionPoint right) =>
        left.Line < right.Line || (left.Line == right.Line && left.Column <= right.Column);
}
