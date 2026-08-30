namespace Quickshell.Terminal;

/// <summary>Where something was found.</summary>
/// <param name="Line">The absolute line the match starts on.</param>
/// <param name="Column">The column it starts at.</param>
/// <param name="Cells">How many cells it covers, which may run past the end of that row.</param>
public readonly record struct Match(long Line, int Column, int Cells);

/// <summary>
/// Finding something in the history.
///
/// <para><b>It runs over logical lines and not over rows, which is the whole of it.</b> A terminal
/// breaks a long line across three rows; a search that worked row by row would not find a word the
/// wrap fell inside, and the user searching for it can see it on their screen. That is this file's
/// falsification, and the reason the comparison walks across a wrap as though it were not there.</para>
///
/// <para><b>Case-insensitive by default.</b> Somebody looking for an error message in ten thousand
/// lines is not thinking about capitals, and the option to be exact is there for when they are.</para>
///
/// <para><b>It allocates nothing and holds no lock.</b> Nothing is copied out of the ring: the needle
/// is compared against the cells where they are. What that costs is a rule about where it may run —
/// on the stage that owns the model, which is the parser's, exactly as reading
/// <see cref="Emulator.Reply"/> is. A search issued from a window thread wants a snapshot first, and
/// that snapshot is not built here because nothing yet asks for one.</para>
///
/// <para>Regular expressions are deliberately absent. The design offers them as optional, and an
/// option costs a surface, a syntax to document and a way to be slow on a large scrollback — so it
/// waits for somebody to want it rather than arriving because it was mentioned.</para>
/// </summary>
public static class Search
{
    /// <summary>The longest single cell's text this compares without giving up.</summary>
    private const int LongestCell = 32;

    /// <summary>
    /// Finds the next match at or after a position, or the previous one before it.
    /// </summary>
    /// <param name="buffer">Where to look.</param>
    /// <param name="needle">What to look for. Empty finds nothing.</param>
    /// <param name="from">The line to start at.</param>
    /// <param name="column">The column to start at, on that line.</param>
    /// <param name="forward">Whether to look forwards.</param>
    /// <param name="caseSensitive">Whether capitals matter.</param>
    /// <param name="match">Where it was found.</param>
    /// <returns>Whether anything was found.</returns>
    public static bool TryFind(
        TerminalBuffer buffer,
        ReadOnlySpan<char> needle,
        long from,
        int column,
        bool forward,
        bool caseSensitive,
        out Match match)
    {
        ArgumentNullException.ThrowIfNull(buffer);

        match = default;

        if (needle.IsEmpty || buffer.LineCount == 0)
        {
            return false;
        }

        long first = buffer.TopLine - buffer.ScrollbackLines;
        long last = first + buffer.LineCount - 1;

        long line = Math.Clamp(from, first, last);
        int start = column;

        while (line >= first && line <= last)
        {
            int width = buffer.Line(Retained(buffer, line)).Length;
            int at = forward
                ? Forward(buffer, needle, line, Math.Max(0, start), width, caseSensitive)
                : Backward(buffer, needle, line, Math.Min(start, width - 1), caseSensitive);

            if (at >= 0)
            {
                match = new Match(line, at, Length(buffer, needle, line, at, caseSensitive));

                return true;
            }

            line += forward ? 1 : -1;
            start = forward ? 0 : int.MaxValue;
        }

        return false;
    }

    /// <summary>How many matches the whole history holds, which is the number a search box shows.</summary>
    public static int Count(TerminalBuffer buffer, ReadOnlySpan<char> needle, bool caseSensitive)
    {
        ArgumentNullException.ThrowIfNull(buffer);

        if (needle.IsEmpty)
        {
            return 0;
        }

        long first = buffer.TopLine - buffer.ScrollbackLines;
        int found = 0;

        for (long line = first; line < first + buffer.LineCount; line++)
        {
            int width = buffer.Line(Retained(buffer, line)).Length;

            for (int column = 0; column < width; column++)
            {
                if (Matches(buffer, needle, line, column, caseSensitive))
                {
                    found++;
                }
            }
        }

        return found;
    }

    private static int Forward(
        TerminalBuffer buffer,
        ReadOnlySpan<char> needle,
        long line,
        int from,
        int width,
        bool caseSensitive)
    {
        for (int column = from; column < width; column++)
        {
            if (Matches(buffer, needle, line, column, caseSensitive))
            {
                return column;
            }
        }

        return -1;
    }

    private static int Backward(
        TerminalBuffer buffer,
        ReadOnlySpan<char> needle,
        long line,
        int from,
        bool caseSensitive)
    {
        for (int column = from; column >= 0; column--)
        {
            if (Matches(buffer, needle, line, column, caseSensitive))
            {
                return column;
            }
        }

        return -1;
    }

    /// <summary>
    /// Whether the needle sits at this cell, walking across a wrap as though it were not there.
    ///
    /// <para>The comparison is against the cells themselves rather than against a copy of the line,
    /// which is what lets a match cross rows without anything being assembled first.</para>
    /// </summary>
    private static bool Matches(
        TerminalBuffer buffer,
        ReadOnlySpan<char> needle,
        long line,
        int column,
        bool caseSensitive) =>
        Walk(buffer, needle, line, column, caseSensitive) >= 0;

    /// <summary>How many cells a match at this position covers, or -1 where there is none.</summary>
    private static int Length(
        TerminalBuffer buffer,
        ReadOnlySpan<char> needle,
        long line,
        int column,
        bool caseSensitive) =>
        Walk(buffer, needle, line, column, caseSensitive);

    private static int Walk(
        TerminalBuffer buffer,
        ReadOnlySpan<char> needle,
        long line,
        int column,
        bool caseSensitive)
    {
        Span<char> cell = stackalloc char[LongestCell];

        long first = buffer.TopLine - buffer.ScrollbackLines;
        long last = first + buffer.LineCount - 1;
        int matched = 0;
        int cells = 0;

        while (matched < needle.Length)
        {
            ReadOnlySpan<Cell> row = buffer.Line(Retained(buffer, line));

            if (column >= row.Length)
            {
                // Off the end of the row. Only a row that continues carries the match onward: a
                // logical line that ended is a line the match cannot be spanning.
                if (line >= last || !buffer.IsWrapped(Retained(buffer, line)))
                {
                    return -1;
                }

                line++;
                column = 0;

                continue;
            }

            if (row[column].Width == 0)
            {
                // The trailing half of a wide pair holds no text of its own.
                column++;
                cells++;

                continue;
            }

            int written = buffer.TextOf(row[column], cell);

            if (written <= 0 || matched + written > needle.Length)
            {
                return -1;
            }

            if (!cell[..written].Equals(
                    needle.Slice(matched, written),
                    caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase))
            {
                return -1;
            }

            matched += written;
            column++;
            cells++;
        }

        // The last character matched may be a wide one, whose trailing half is a cell the match
        // covers and holds no text to have matched against. Without this a search for two CJK
        // characters reports three cells and a highlight stops halfway through the second.
        while (true)
        {
            ReadOnlySpan<Cell> row = buffer.Line(Retained(buffer, line));

            if (column >= row.Length || row[column].Width != 0)
            {
                break;
            }

            column++;
            cells++;
        }

        return cells;
    }

    private static int Retained(TerminalBuffer buffer, long line) =>
        (int)(line - (buffer.TopLine - buffer.ScrollbackLines));
}
