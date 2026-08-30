using System.Text;

namespace Quickshell.Terminal;

/// <summary>How far a reader is moving through the document at a time.</summary>
public enum TextStep
{
    /// <summary>One character.</summary>
    Character,

    /// <summary>To the next run of non-space, which is what a reader means by a word.</summary>
    Word,

    /// <summary>To the start of the next line.</summary>
    Line,
}

/// <summary>
/// The buffer as one document, so something other than a camera can read it.
///
/// <para><b>Everything this renderer does well is what makes it invisible.</b> There are no
/// controls, no text elements and no automation tree — there is a texture. So the text a screen
/// reader needs is published deliberately, from here, or it does not exist at all.</para>
///
/// <para><b>Scrollback and screen are one document, not two.</b> A reader that could only see the
/// visible rows would be a reader that cannot review what just scrolled past, which is most of what
/// a person uses a terminal's history for. Offsets run from the oldest line kept to the cursor.</para>
///
/// <para><b>Nothing is materialised until it is asked for.</b> A scrollback of ten thousand lines is
/// a megabyte of text nobody wants a copy of, so this holds an index of where each line starts and
/// builds characters only for the range a reader is actually on. The index is rebuilt when the
/// buffer's generation moves and not before.</para>
/// </summary>
public sealed class TerminalDocument
{
    private readonly TerminalBuffer _buffer;
    private readonly List<int> _starts = [];

    private long _indexed = -1;
    private int _length;

    /// <summary>Reads a buffer as a document. The buffer is not copied and stays the source.</summary>
    public TerminalDocument(TerminalBuffer buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);

        _buffer = buffer;
    }

    /// <summary>How many characters the whole document is, newlines included.</summary>
    public int Length
    {
        get
        {
            Index();

            return _length;
        }
    }

    /// <summary>How many lines there are: everything kept, not only what is on screen.</summary>
    public int Lines
    {
        get
        {
            Index();

            return _starts.Count;
        }
    }

    /// <summary>
    /// Where the cursor is, as an offset — which is what a screen reader exposes as the caret and
    /// what lets it follow a shell prompt as somebody types.
    /// </summary>
    public int Caret
    {
        get
        {
            Index();

            int row = _buffer.ScrollbackLines + Math.Clamp(_buffer.CursorRow, 0, _buffer.Rows - 1);

            if (row >= _starts.Count)
            {
                return _length;
            }

            int line = _starts[row];
            int end = row + 1 < _starts.Count ? _starts[row + 1] - 1 : _length;

            return Math.Min(line + Math.Max(0, _buffer.CursorColumn), end);
        }
    }

    /// <summary>The characters between two offsets, clamped to what exists.</summary>
    public string Text(int start, int length)
    {
        Index();

        int from = Math.Clamp(start, 0, _length);
        int to = Math.Clamp(from + Math.Max(0, length), from, _length);

        StringBuilder text = new(to - from);
        Span<char> cell = stackalloc char[16];

        for (int row = Row(from); row < _starts.Count && _starts[row] < to; row++)
        {
            int lineStart = _starts[row];
            string line = LineText(row, cell);

            for (int at = 0; at < line.Length + 1; at++)
            {
                int offset = lineStart + at;

                if (offset < from)
                {
                    continue;
                }

                if (offset >= to)
                {
                    break;
                }

                // The character past a line's own text is the newline that separates it from the
                // next, and the last line has none — a document does not end in a break.
                text.Append(at < line.Length ? line[at] : '\n');
            }
        }

        return text.ToString();
    }

    /// <summary>The whole of one line, without its newline.</summary>
    public string LineAt(int offset)
    {
        Index();

        Span<char> cell = stackalloc char[16];

        return LineText(Row(Math.Clamp(offset, 0, _length)), cell);
    }

    /// <summary>Which line an offset falls on, counting from the oldest kept.</summary>
    public int LineOf(int offset)
    {
        Index();

        return Row(Math.Clamp(offset, 0, _length));
    }

    /// <summary>
    /// Moves an offset by whole units, forwards or backwards, and stops at the ends.
    ///
    /// <para>A reader asks for this constantly and expects it to be cheap; it is a walk over the
    /// index and, for a word, over one line's characters.</para>
    /// </summary>
    public int Move(int offset, TextStep step, int count)
    {
        Index();

        int at = Math.Clamp(offset, 0, _length);

        for (int moved = 0; moved < Math.Abs(count); moved++)
        {
            int next = step switch
            {
                TextStep.Character => at + Math.Sign(count),
                TextStep.Line => ByLine(at, Math.Sign(count)),
                _ => ByWord(at, Math.Sign(count)),
            };

            if (next == at)
            {
                break;
            }

            at = Math.Clamp(next, 0, _length);
        }

        return at;
    }

    /// <summary>The offset a line begins at.</summary>
    public int StartOfLine(int line)
    {
        Index();

        return _starts[Math.Clamp(line, 0, _starts.Count - 1)];
    }

    private int ByLine(int offset, int direction)
    {
        int row = Row(offset) + direction;

        return row < 0 ? 0 : row >= _starts.Count ? _length : _starts[row];
    }

    /// <summary>
    /// To the start of the next run of non-space, which is what a reader means by a word.
    ///
    /// <para>Not the parser's idea of a word and not the shell's: a screen reader reading a command
    /// line aloud wants the units a person hears, and those are separated by spaces.</para>
    /// </summary>
    private int ByWord(int offset, int direction)
    {
        string all = Text(0, _length);
        int at = Math.Clamp(offset, 0, all.Length);

        if (direction > 0)
        {
            while (at < all.Length && !char.IsWhiteSpace(all[at]))
            {
                at++;
            }

            while (at < all.Length && char.IsWhiteSpace(all[at]))
            {
                at++;
            }

            return at;
        }

        at = Math.Max(0, at - 1);

        while (at > 0 && char.IsWhiteSpace(all[at]))
        {
            at--;
        }

        while (at > 0 && !char.IsWhiteSpace(all[at - 1]))
        {
            at--;
        }

        return at;
    }

    /// <summary>Which row an offset is on, by binary search over the line starts.</summary>
    private int Row(int offset)
    {
        int found = _starts.BinarySearch(offset);

        return found >= 0 ? found : Math.Max(0, ~found - 1);
    }

    /// <summary>
    /// One line's text, with the trailing blanks a terminal pads every row with removed.
    ///
    /// <para><b>Except on the row the cursor is on.</b> A prompt that ends in a space — which is
    /// almost every prompt — has the cursor one past its last visible character, and a line trimmed
    /// to its ink has nowhere for the caret to be. So the cursor's own row keeps whatever it needs
    /// to reach, and every other row is trimmed. It is the same distinction QS23 drew between an
    /// extent and a continuation: trailing space is padding except where it is not.</para>
    /// </summary>
    private string LineText(int row, Span<char> cell)
    {
        ReadOnlySpan<Cell> cells = _buffer.Line((int)(_buffer.TopLine - _buffer.ScrollbackLines) + row);
        int extent = cells.Length;

        while (extent > 0 && cells[extent - 1].IsBlank)
        {
            extent--;
        }

        if (row == _buffer.ScrollbackLines + _buffer.CursorRow)
        {
            extent = Math.Max(extent, Math.Min(cells.Length, _buffer.CursorColumn));
        }

        StringBuilder line = new(extent);

        for (int column = 0; column < extent; column++)
        {
            int written = _buffer.TextOf(cells[column], cell);

            line.Append(cell[..written]);
        }

        return line.ToString();
    }

    /// <summary>
    /// Rebuilds the line index where the buffer has moved on.
    ///
    /// <para>Keyed on the buffer's generation, so a document read a thousand times between two
    /// keystrokes walks the buffer once. A reader asking for ranges is the commonest caller and it
    /// asks constantly.</para>
    /// </summary>
    private void Index()
    {
        if (_indexed == _buffer.Generation)
        {
            return;
        }

        _starts.Clear();

        int at = 0;
        Span<char> cell = stackalloc char[16];
        int lines = _buffer.ScrollbackLines + _buffer.Rows;

        for (int row = 0; row < lines; row++)
        {
            _starts.Add(at);

            at += LineText(row, cell).Length;

            if (row + 1 < lines)
            {
                at++;
            }
        }

        _length = at;
        _indexed = _buffer.Generation;
    }
}
