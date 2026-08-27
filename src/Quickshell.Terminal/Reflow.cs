namespace Quickshell.Terminal;

/// <summary>Where the reflowed lines and the cursor ended up.</summary>
/// <param name="LineCount">How many lines the result holds, before the screen's own minimum.</param>
/// <param name="CursorLine">Which of those lines the cursor is on.</param>
/// <param name="CursorColumn">Which column of it.</param>
public readonly record struct ReflowOutcome(int LineCount, int CursorLine, int CursorColumn);

/// <summary>
/// Re-wrapping the buffer at a new width, which is where terminals lose their reputation.
///
/// <para><b>A function and not a method on the buffer.</b> It reads a buffer and writes into arrays
/// the caller owns, and it keeps nothing at all between calls — so every rule below is checkable by
/// building a buffer, calling this, and looking at what came out. There is no window in that
/// sentence, which is the point: this is the behaviour emulators most reliably get wrong, and the one
/// that most tempts an implementer into testing by dragging a window edge and squinting.</para>
///
/// <para><b>The wrapped flag is what makes it definable at all.</b> A logical line is a run of
/// physical rows joined by that flag; recovering those, re-wrapping them at the new width and putting
/// the result back is the whole algorithm. Everything difficult is at its edges.</para>
///
/// <para><b>The cursor ends on the same character, not the same coordinates.</b> A cursor left at row
/// three column forty through a narrowing is one that has silently moved to a different letter, and a
/// shell's line editing is then wrong about where the user is. Where the cursor sits past the end of
/// its line — which is where it usually sits — its offset from the line's start is preserved
/// instead.</para>
///
/// <para><b>Nothing goes that a user could read.</b> Trailing blanks go, because they are the
/// terminal's padding rather than the host's text and trimming them is what makes narrowing and
/// widening again come back to the same rows; a space the host coloured is not blank and stays. A
/// wide character that no longer fits against the right margin moves down whole rather than being
/// split. If the result needs more lines than the ring can hold, the oldest go, which is the
/// direction time already runs in.</para>
///
/// <para>The alternate screen never reaches here and is not reflowed: a full-screen program is about
/// to redraw it at the size it has just been told. <see cref="Screens.Resize"/> is where that
/// happens.</para>
/// </summary>
public static class Reflow
{
    /// <summary>
    /// Re-wraps <paramref name="source"/> at <paramref name="width"/> into the caller's arrays.
    /// </summary>
    /// <param name="source">The buffer to read. Not modified.</param>
    /// <param name="cursorLine">Which retained line of <paramref name="source"/> the cursor is on.</param>
    /// <param name="cursorColumn">Which column of it.</param>
    /// <param name="destination">Cells to fill, <c>wrapped.Length * width</c> of them, already blanked.</param>
    /// <param name="wrapped">One flag per destination line, already false.</param>
    /// <param name="width">The new width, in columns.</param>
    /// <returns>What came out, and where the cursor went.</returns>
    public static ReflowOutcome Run(
        TerminalBuffer source,
        int cursorLine,
        int cursorColumn,
        Span<Cell> destination,
        Span<bool> wrapped,
        int width)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(wrapped.Length, 1);

        int lines = Content(source, cursorLine);

        // Two passes, because where a line lands depends on how many lines there turn out to be. The
        // first counts and writes nothing; the second copies, having been told how many of the oldest
        // the ring cannot hold. Counting costs a second read of the widths and saves building the
        // logical lines as objects first, which on a window drag is the whole cost.
        Walk counting = new(source, lines, cursorLine, cursorColumn, default, default, width, skip: 0);
        counting.Go();

        int skip = Math.Max(0, counting.OutLine - wrapped.Length);

        Walk copying = new(source, lines, cursorLine, cursorColumn, destination, wrapped, width, skip);
        copying.Go();

        return new ReflowOutcome(
            Math.Min(copying.OutLine, wrapped.Length),
            copying.CursorLine,
            copying.CursorColumn);
    }

    /// <summary>
    /// How many of the buffer's lines are the host's, rather than the screen's own padding.
    ///
    /// <para><b>Without this the round trip does not close.</b> A buffer always holds at least a
    /// screenful of lines, so the rows below the last thing the host printed are blank ones the
    /// terminal invented. Reflowing them as though they were content turns each into a logical line,
    /// the screen minimum invents fresh ones underneath, and a window dragged narrow and wide again
    /// comes back with more blank lines than it started with — every drag, for ever.</para>
    ///
    /// <para>Never below the cursor's own line, which may legitimately be a blank one: that is where
    /// a prompt sits after a bare newline.</para>
    /// </summary>
    private static int Content(TerminalBuffer source, int cursorLine)
    {
        int last = source.LineCount;

        while (last > 0 && Walk.Extent(source.Line(last - 1)) == 0)
        {
            last--;
        }

        return Math.Max(last, Math.Min(cursorLine + 1, source.LineCount));
    }

    /// <summary>
    /// One pass over the buffer, in the two flavours the algorithm needs: counting, where the
    /// destination is empty, and copying, where it is not.
    ///
    /// <para>One body for both, so the count cannot disagree with the copy. A <c>ref struct</c>
    /// because it carries the destination spans, and because the state is the walk's and nothing
    /// else's — which is what keeps <see cref="Reflow"/> a function rather than an object with a
    /// memory.</para>
    /// </summary>
    private ref struct Walk
    {
        private readonly TerminalBuffer _source;
        private readonly int _lines;
        private readonly int _cursorSourceLine;
        private readonly int _cursorSourceColumn;
        private readonly Span<Cell> _destination;
        private readonly Span<bool> _wrapped;
        private readonly int _width;
        private readonly int _skip;
        private readonly bool _copying;

        private int _outColumn;
        private bool _placed;

        public Walk(
            TerminalBuffer source,
            int lines,
            int cursorLine,
            int cursorColumn,
            Span<Cell> destination,
            Span<bool> wrapped,
            int width,
            int skip)
        {
            _source = source;
            _lines = lines;
            _cursorSourceLine = cursorLine;
            _cursorSourceColumn = cursorColumn;
            _destination = destination;
            _wrapped = wrapped;
            _width = width;
            _skip = skip;
            _copying = !destination.IsEmpty;
        }

        /// <summary>How many lines the walk produced, dropped ones included.</summary>
        public int OutLine { get; private set; }

        /// <summary>Which produced line the cursor landed on.</summary>
        public int CursorLine { get; private set; }

        /// <summary>Which column of it.</summary>
        public int CursorColumn { get; private set; }

        /// <summary>Walks every logical line the buffer holds, oldest first.</summary>
        public void Go()
        {
            int line = 0;

            while (line < _lines)
            {
                // The logical line: this row, and every row after it that the wrapped flag joins on.
                int last = line;

                while (last < _lines - 1 && _source.IsWrapped(last))
                {
                    last++;
                }

                Logical(line, last);
                line = last + 1;
            }

            if (!_placed)
            {
                // A cursor on a line the buffer does not have. A caller can ask for one, and it is
                // answered rather than thrown at: the last line that does exist.
                CursorLine = Math.Max(0, OutLine - 1 - _skip);
                CursorColumn = 0;
            }

            CursorLine = Math.Max(0, CursorLine);
        }

        private void Logical(int first, int last)
        {
            int startLine = OutLine;
            int offset = 0;
            int cursorOffset = -1;

            for (int physical = first; physical <= last; physical++)
            {
                ReadOnlySpan<Cell> row = _source.Line(physical);
                int extent = physical == last
                    ? Extent(row)
                    : Continued(row, _source.Line(physical + 1));

                if (physical == _cursorSourceLine)
                {
                    cursorOffset = offset + _cursorSourceColumn;
                }

                for (int column = 0; column < extent;)
                {
                    column += Emit(row, column, physical, ref offset);
                }
            }

            // Where the content ran out, which is where a cursor beyond it belongs.
            int endLine = OutLine;
            int endColumn = _outColumn;

            // The logical line has ended, so the row it ended on does not continue into the next.
            Mark(OutLine - _skip, false);
            OutLine++;
            _outColumn = 0;

            if (cursorOffset >= 0 && !_placed)
            {
                Beyond(cursorOffset, startLine, endLine, endColumn);
            }
        }

        /// <summary>
        /// Places a cursor that was past the end of its own line, which is where a cursor usually is.
        ///
        /// <para>Its offset from the line's start is what survives — a prompt's cursor sitting just
        /// after the text being edited lands just after that text at any width, which is the whole
        /// point. Where the new width has no room for that offset at all, it goes to the end of the
        /// content rather than to some column arithmetic happened to produce: after the last
        /// character is somewhere a user can recognise, and column thirty-nine of a five-character
        /// line is not.</para>
        /// </summary>
        private void Beyond(int cursorOffset, int startLine, int endLine, int endColumn)
        {
            int wanted = startLine + (cursorOffset / _width);

            if (wanted <= endLine)
            {
                CursorLine = Math.Max(0, wanted - _skip);
                CursorColumn = Math.Clamp(cursorOffset % _width, 0, _width - 1);
            }
            else
            {
                CursorLine = Math.Max(0, endLine - _skip);
                CursorColumn = Math.Clamp(endColumn, 0, _width - 1);
            }

            _placed = true;
        }

        /// <summary>Places one cell, wrapping first if it no longer fits. Answers how many source
        /// columns it consumed.</summary>
        private int Emit(ReadOnlySpan<Cell> row, int column, int physical, ref int offset)
        {
            Cell cell = row[column];

            if (cell.Width == 0)
            {
                // The trailing half of a pair arriving on its own: its partner was erased or shifted
                // away. It holds no text, so passing over it loses nothing, and emitting it would put
                // a hole in the new row.
                return 1;
            }

            // A wide character in a terminal narrower than it is cannot be a pair, and it is not going
            // to be thrown away either, so it takes the column it has.
            int units = cell.Width == 2 && _width > 1 ? 2 : 1;

            if (_outColumn + units > _width)
            {
                // It does not fit against the new margin, so it moves down whole. The row it leaves is
                // a column short and continues into the next.
                Mark(OutLine - _skip, true);
                OutLine++;
                _outColumn = 0;
            }

            if (physical == _cursorSourceLine && column == _cursorSourceColumn)
            {
                CursorLine = OutLine - _skip;
                CursorColumn = _outColumn;
                _placed = true;
            }

            if (_copying)
            {
                Put(OutLine - _skip, _outColumn, cell);

                if (units == 2)
                {
                    // The trailing half is rebuilt rather than copied: the pair may have landed at a
                    // different column and the half has to sit beside its own partner, or the column
                    // count stops being honest.
                    Put(
                        OutLine - _skip,
                        _outColumn + 1,
                        Cell.For(
                            ' ',
                            cell.Foreground,
                            cell.Background,
                            cell.Flags,
                            cell.Underline,
                            width: 0,
                            cell.Link));
                }
            }

            _outColumn += units;
            offset += units;

            return cell.Width == 2 ? 2 : 1;
        }

        /// <summary>
        /// Writes one cell, or drops it where it belongs to a line the ring could not keep.
        ///
        /// <para>Those lines are still walked, because the ones after them only land in the right
        /// place if the wrapping before them was computed.</para>
        /// </summary>
        private void Put(int line, int column, Cell cell)
        {
            if (line < 0)
            {
                return;
            }

            int index = (line * _width) + column;

            if (index >= 0 && index < _destination.Length)
            {
                _destination[index] = cell;
            }
        }

        private void Mark(int line, bool value)
        {
            if (_copying && line >= 0 && line < _wrapped.Length)
            {
                _wrapped[line] = value;
            }
        }

        /// <summary>
        /// How much of a row is content, where the row is <em>not</em> the last of its logical line.
        ///
        /// <para><b>Trailing blanks are content here, and trimming them loses text.</b> A row in the
        /// middle of a wrapped line is full by construction, so a space sitting in its last column is
        /// a space the host printed — the one between two words that the wrap happened to fall
        /// between. Trimming it deletes a word boundary, and the property test that narrows and widens
        /// a paragraph is what says so.</para>
        ///
        /// <para>The single exception is the column a wide character could not fit into. That blank is
        /// the terminal's own, and the proof is that the next row begins with the pair that was
        /// pushed off this one.</para>
        /// </summary>
        private static int Continued(ReadOnlySpan<Cell> row, ReadOnlySpan<Cell> next)
        {
            bool pushedOff = row.Length > 0
                && row[^1].IsBlank
                && next.Length > 0
                && next[0].Width == 2;

            return pushedOff ? row.Length - 1 : row.Length;
        }

        /// <summary>
        /// How much of a row is content, where the row ends its logical line: everything up to the
        /// last cell that was written to.
        ///
        /// <para>This is the one place a re-wrap is allowed to differ from what it read, and it is
        /// what makes the round trip come back. A blank is a space in the default colours with no
        /// attribute on it — a space the host coloured or underlined fails that test and is kept.</para>
        /// </summary>
        internal static int Extent(ReadOnlySpan<Cell> row)
        {
            int extent = row.Length;

            while (extent > 0 && row[extent - 1].IsBlank)
            {
                extent--;
            }

            return extent;
        }
    }
}
