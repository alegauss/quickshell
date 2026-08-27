namespace Quickshell.Terminal;

/// <summary>
/// What the host has printed: a ring of rows with a moving origin, and a window onto it.
///
/// <para><b>The ring is the data structure, not an optimisation.</b> Scrolling by one line is the
/// most frequent structural operation a terminal performs. As an array of rows it is a move of the
/// entire screen; here it is an increment of an origin and a fill of one row, whatever the
/// scrollback holds. Every index goes through that origin, which is the price and it is one
/// addition.</para>
///
/// <para><b>Scrollback is the same ring extended.</b> The visible screen is the last
/// <see cref="Rows"/> lines of it; a line leaving the top is only a line the window no longer
/// covers. Eviction is by overwrite and never by copy.</para>
///
/// <para><b>The wrapped flag is not cosmetic.</b> It is the only record that a line the host wrote
/// as one logical line occupies two rows — which reflow, selection and copy each need, and none of
/// them can reconstruct once it is gone.</para>
///
/// <para><b>Every mutation is recorded, and the record cannot be bypassed.</b> That is why
/// <see cref="Screen"/> hands out a read-only span: a caller that could write through it would be a
/// change <see cref="Generation"/> never saw, and a renderer comparing generations would then decide
/// there was nothing to draw. Whatever a mutation is, it is a method on this class.</para>
/// </summary>
public sealed class TerminalBuffer
{
    /// <summary>How many distinct multi-codepoint clusters are kept before new ones lose their tail.</summary>
    public const int MaximumClusters = 4096;

    private readonly Dictionary<string, int> _clusterIndex = new(StringComparer.Ordinal);
    private readonly List<string> _clusters = [];
    private readonly Dictionary<string, int> _linkIndex = new(StringComparer.Ordinal);
    private readonly List<string> _links = [];

    private Cell[] _cells;
    private bool[] _wrapped;
    private long[] _stamps;
    private bool[] _dirty;
    private int _origin;
    private long _generation;
    private long _firstLine;
    private int _dirtyRows;

    /// <summary>Opens a buffer of a given screen size, with room for that much scrollback behind it.</summary>
    /// <param name="columns">Cells across.</param>
    /// <param name="rows">Rows of visible screen.</param>
    /// <param name="scrollback">Extra lines kept behind the screen. Zero is an alternate screen.</param>
    public TerminalBuffer(int columns, int rows, int scrollback = 1000)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(columns, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(rows, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(scrollback);

        Columns = columns;
        Rows = rows;
        Capacity = rows + scrollback;

        _cells = new Cell[Capacity * columns];
        _wrapped = new bool[Capacity];
        _stamps = new long[Capacity];
        _dirty = new bool[rows];
        LineCount = rows;

        Array.Fill(_cells, Cell.Blank);
    }

    /// <summary>Cells across one row.</summary>
    public int Columns { get; private set; }

    /// <summary>Rows of visible screen.</summary>
    public int Rows { get; private set; }

    /// <summary>Total lines the ring holds, screen and scrollback together.</summary>
    public int Capacity { get; private set; }

    /// <summary>How many lines are currently retained. Never above <see cref="Capacity"/>.</summary>
    public int LineCount { get; private set; }

    /// <summary>Lines above the visible screen that are still held.</summary>
    public int ScrollbackLines => LineCount - Rows;

    /// <summary>The cursor's row within the visible screen.</summary>
    public int CursorRow { get; set; }

    /// <summary>The cursor's column.</summary>
    public int CursorColumn { get; set; }

    /// <summary>How many lines have been scrolled off the top over this buffer's life.</summary>
    public long Scrolls { get; private set; }

    /// <summary>
    /// Cells written by scrolling, cumulatively.
    ///
    /// <para>This is the number the design's own falsification is read against: scrolling a full
    /// buffer by one line must not copy more than a bounded amount of memory, and a ring that was
    /// quietly rewritten as an array would show this growing with the scrollback rather than with
    /// the width.</para>
    /// </summary>
    public long CellsWrittenByScrolling { get; private set; }

    /// <summary>How many distinct clusters the side table holds.</summary>
    public int ClusterCount => _clusters.Count;

    /// <summary>Whether the side table is full and further new clusters lose everything but their base.</summary>
    public bool ClustersExhausted => _clusters.Count >= MaximumClusters;

    // ---- What changed ----

    /// <summary>
    /// A count that goes up on every mutation and never comes down.
    ///
    /// <para><b>This is the whole cross-thread mechanism, and it is deliberately not the changed
    /// content.</b> The parser mutates and the renderer reads, on different threads; the cheapest
    /// correct thing to pass between them is the fact that something changed. A renderer that
    /// remembers the number it last drew and finds it unchanged has established that there is nothing
    /// to do, and goes back to waiting — which is what an idle window costing nothing actually
    /// is.</para>
    ///
    /// <para>Read through a fence, because the reader is not the writer. The fence is why a renderer
    /// that sees a new number is also guaranteed to see the cells behind it.</para>
    /// </summary>
    public long Generation => Volatile.Read(ref _generation);

    /// <summary>
    /// Which line of this buffer's whole life is at the top of the visible screen.
    ///
    /// <para><b>This is the structural half, and skipping it is the trap.</b> A scroll changes every
    /// row's <em>position</em> without changing any row's content, so a scheme that only asked "did
    /// row three change" would report the whole screen dirty on the one operation a terminal performs
    /// most. Here a pure scroll moves this number by one and marks a single row dirty, and a consumer
    /// that compares it recognises the scroll for what it is.</para>
    /// </summary>
    public long TopLine => _firstLine + ScrollbackLines;

    /// <summary>Which line of this buffer's whole life a visible row is showing.</summary>
    public long AbsoluteLine(int row) => TopLine + row;

    /// <summary>
    /// The generation at which a retained line was last written. Compared against a remembered one,
    /// it says whether that particular line's content is the same content.
    /// </summary>
    public long GenerationOf(int line)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(line);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(line, LineCount);

        return _stamps[RingRow(line)];
    }

    /// <summary>The same, for a visible row.</summary>
    public long ScreenGenerationOf(int row)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(row);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(row, Rows);

        return GenerationOf(ScrollbackLines + row);
    }

    /// <summary>
    /// Whether new content has landed at this screen position since <see cref="ClearDamage"/>.
    ///
    /// <para><b>This is the optimisation, and it is labelled as one.</b> Correctness needs only
    /// <see cref="Generation"/>: a consumer that rebuilt every row whenever that changed would be
    /// slower and never wrong. The bitset is what lets a screen where one row changed rebuild one
    /// row's worth of instances instead of all of them.</para>
    ///
    /// <para>It is about a <em>position</em>, not about a line. A scroll leaves the rows it moved
    /// unmarked, because their content did not change — <see cref="TopLine"/> is what says they are
    /// somewhere else now, and a consumer that reads this without reading that will draw a stale
    /// screen.</para>
    /// </summary>
    public bool IsScreenDirty(int row)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(row);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(row, Rows);

        return _dirty[row];
    }

    /// <summary>How many screen rows carry new content, which is what makes "nothing" cheap to ask.</summary>
    public int DirtyRows => _dirtyRows;

    /// <summary>
    /// Forgets which rows were dirty, called by whoever has just drawn them.
    ///
    /// <para><see cref="Generation"/> is deliberately untouched: it is a count of this buffer's life
    /// and not a flag, so two consumers can each remember their own last-drawn number without one
    /// clearing the other's evidence.</para>
    /// </summary>
    public void ClearDamage()
    {
        if (_dirtyRows == 0)
        {
            return;
        }

        Array.Clear(_dirty);
        _dirtyRows = 0;
    }

    // ---- Reading ----

    /// <summary>
    /// One row of the retained lines, oldest first. Row <see cref="ScrollbackLines"/> is the screen's
    /// top.
    ///
    /// <para>Read-only, and that is the damage record's whole enforcement: a caller writing through a
    /// span would be a change <see cref="Generation"/> never saw.</para>
    /// </summary>
    public ReadOnlySpan<Cell> Line(int line)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(line);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(line, LineCount);

        return _cells.AsSpan(RingRow(line) * Columns, Columns);
    }

    /// <summary>One row of the visible screen.</summary>
    public ReadOnlySpan<Cell> Screen(int row)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(row);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(row, Rows);

        return Line(ScrollbackLines + row);
    }

    /// <summary>Whether a retained line continues into the one after it rather than ending.</summary>
    public bool IsWrapped(int line) => _wrapped[RingRow(line)];

    /// <summary>Records that a line continues into the next, which is what a soft wrap is.</summary>
    public void SetWrapped(int line, bool wrapped)
    {
        if (_wrapped[RingRow(line)] == wrapped)
        {
            return;
        }

        _wrapped[RingRow(line)] = wrapped;
        Touch(line);
    }

    /// <summary>Whether a visible row continues into the one below it.</summary>
    public bool IsScreenWrapped(int row) => IsWrapped(ScrollbackLines + row);

    /// <summary>Records that a visible row continues into the one below it.</summary>
    public void SetScreenWrapped(int row, bool wrapped) => SetWrapped(ScrollbackLines + row, wrapped);

    /// <summary>
    /// Scrolls the screen up by one line: the top line joins the scrollback and a blank row appears
    /// at the bottom.
    ///
    /// <para>The whole operation is an origin increment and one row's fill. Nothing else moves, and
    /// <see cref="CellsWrittenByScrolling"/> is what says so.</para>
    /// </summary>
    public void ScrollUp()
    {
        if (LineCount < Capacity)
        {
            LineCount++;
        }
        else
        {
            // The ring is full, so the oldest line is the one the new bottom row overwrites. This is
            // the eviction, and it costs an addition.
            _origin = Next(_origin);
            _firstLine++;
        }

        Span<Cell> bottom = Mutable(LineCount - 1);
        bottom.Fill(Cell.Blank);
        _wrapped[RingRow(LineCount - 1)] = false;

        CellsWrittenByScrolling += bottom.Length;
        Scrolls++;

        // One row dirty and not the screen: the rows above moved without changing, and TopLine is
        // what says so. Marking them all here is the pessimism this whole task exists to avoid.
        //
        // The bits do move, though. A bit means "new content at this position", and every position
        // has just come down by one — so a row written before this scroll and not yet drawn must
        // still be findable, one row higher. Fifty bytes of memmove, against a redraw of the screen.
        Bump();
        _stamps[RingRow(LineCount - 1)] = _generation;
        ShiftDirtyUp();
        Soil(Rows - 1);
    }

    /// <summary>
    /// Scrolls a region of the screen up by one line, which is what a scrolling region and an insert
    /// or delete line need. Unlike <see cref="ScrollUp"/> this really does move rows, because a
    /// region inside the screen is not what the origin describes.
    /// </summary>
    public void ScrollRegionUp(int top, int bottom, int count = 1)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(top);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(bottom, Rows);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(top, bottom);
        ArgumentOutOfRangeException.ThrowIfNegative(count);

        int height = bottom - top + 1;
        int shift = Math.Min(count, height);

        for (int row = top; row + shift <= bottom; row++)
        {
            MutableScreen(row + shift).CopyTo(MutableScreen(row));
            _wrapped[RingRow(ScrollbackLines + row)] = IsScreenWrapped(row + shift);
            CellsWrittenByScrolling += Columns;
        }

        for (int row = bottom - shift + 1; row <= bottom; row++)
        {
            MutableScreen(row).Fill(Cell.Blank);
            _wrapped[RingRow(ScrollbackLines + row)] = false;
            CellsWrittenByScrolling += Columns;
        }

        // Every position in the region really did get different content, unlike a whole-screen
        // scroll, which is why this one is honestly the whole region.
        Bump();
        Region(top, bottom);
    }

    /// <summary>
    /// Scrolls a region down, which is what a reverse index at the top margin and an insert-line
    /// both are. The rows really move, for the same reason they do going up: a region inside the
    /// screen is not what the ring's origin describes.
    /// </summary>
    public void ScrollRegionDown(int top, int bottom, int count = 1)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(top);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(bottom, Rows);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(top, bottom);
        ArgumentOutOfRangeException.ThrowIfNegative(count);

        int height = bottom - top + 1;
        int shift = Math.Min(count, height);

        for (int row = bottom; row - shift >= top; row--)
        {
            MutableScreen(row - shift).CopyTo(MutableScreen(row));
            _wrapped[RingRow(ScrollbackLines + row)] = IsScreenWrapped(row - shift);
            CellsWrittenByScrolling += Columns;
        }

        for (int row = top; row < top + shift; row++)
        {
            MutableScreen(row).Fill(Cell.Blank);
            _wrapped[RingRow(ScrollbackLines + row)] = false;
            CellsWrittenByScrolling += Columns;
        }

        Bump();
        Region(top, bottom);
    }

    /// <summary>
    /// Throws away everything above the visible screen, which is what <c>CSI 3 J</c> asks for. The
    /// screen itself is untouched: a host clearing its scrollback has not asked to lose what is in
    /// front of the user.
    /// </summary>
    public void DropScrollback()
    {
        if (ScrollbackLines == 0)
        {
            return;
        }

        // The screen itself does not move, so no position's content changed and nothing is dirtied:
        // the anchor moves by exactly what the line count loses, which leaves TopLine where it was.
        _firstLine += ScrollbackLines;
        _origin = RingRow(ScrollbackLines);
        LineCount = Rows;
        Bump();
    }

    /// <summary>Writes one cell of the visible screen.</summary>
    public void Write(int row, int column, Cell cell)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(column);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(column, Columns);

        MutableScreen(row)[column] = cell;
        TouchScreen(row);
    }

    /// <summary>Clears a run of a visible row, which is what every erase sequence reduces to.</summary>
    public void Clear(int row, int from, int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(from);
        ArgumentOutOfRangeException.ThrowIfNegative(count);

        Span<Cell> line = MutableScreen(row);
        line.Slice(from, Math.Min(count, line.Length - from)).Fill(Cell.Blank);
        TouchScreen(row);
    }

    /// <summary>Clears the whole visible screen without touching what is behind it.</summary>
    public void ClearScreen()
    {
        for (int row = 0; row < Rows; row++)
        {
            MutableScreen(row).Fill(Cell.Blank);
            _wrapped[RingRow(ScrollbackLines + row)] = false;
        }

        Bump();
        Region(0, Rows - 1);
    }

    /// <summary>
    /// Shifts a row's cells right and blanks what the shift opened, which is what <c>CSI @</c> is.
    ///
    /// <para>Here rather than in the emulator because a mutation the buffer did not perform is a
    /// mutation <see cref="Generation"/> did not see.</para>
    /// </summary>
    public void InsertCells(int row, int from, int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(from);
        ArgumentOutOfRangeException.ThrowIfNegative(count);

        Span<Cell> line = MutableScreen(row);

        if (from >= line.Length || count == 0)
        {
            return;
        }

        int shift = Math.Min(count, line.Length - from);

        line[from..(line.Length - shift)].CopyTo(line[(from + shift)..]);
        line.Slice(from, shift).Fill(Cell.Blank);
        TouchScreen(row);
    }

    /// <summary>Shifts a row's cells left and blanks the tail, which is what <c>CSI P</c> is.</summary>
    public void DeleteCells(int row, int from, int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(from);
        ArgumentOutOfRangeException.ThrowIfNegative(count);

        Span<Cell> line = MutableScreen(row);

        if (from >= line.Length || count == 0)
        {
            return;
        }

        int shift = Math.Min(count, line.Length - from);

        line[(from + shift)..].CopyTo(line[from..]);
        line[(line.Length - shift)..].Fill(Cell.Blank);
        TouchScreen(row);
    }

    /// <summary>
    /// Interns a grapheme cluster and answers the index a cell can hold.
    ///
    /// <para>Identical clusters share an entry, which is what keeps a screen of accented text to a
    /// handful. Beyond <see cref="MaximumClusters"/> distinct ones the table stops growing and
    /// answers -1: a stream that reaches that is generating clusters rather than writing text, and
    /// the cell falls back to its base codepoint rather than letting a host exhaust memory.</para>
    /// </summary>
    public int InternCluster(string cluster)
    {
        ArgumentNullException.ThrowIfNull(cluster);

        if (_clusterIndex.TryGetValue(cluster, out int existing))
        {
            return existing;
        }

        if (_clusters.Count >= MaximumClusters)
        {
            return -1;
        }

        _clusters.Add(cluster);
        _clusterIndex[cluster] = _clusters.Count - 1;

        return _clusters.Count - 1;
    }

    /// <summary>
    /// Interns a hyperlink and answers the identifier a cell can hold. Identical URIs share one, so
    /// a run of a hundred linked cells costs one entry.
    ///
    /// <para>Index zero is reserved for "no link", which is why the first real one is one.</para>
    /// </summary>
    public int InternLink(string uri)
    {
        ArgumentNullException.ThrowIfNull(uri);

        if (_linkIndex.TryGetValue(uri, out int existing))
        {
            return existing;
        }

        if (_links.Count >= Cell.MaximumLinks)
        {
            return 0;
        }

        _links.Add(uri);
        _linkIndex[uri] = _links.Count;

        return _links.Count;
    }

    /// <summary>The URI a cell points at, or empty where it points at none.</summary>
    public string LinkOf(Cell cell) =>
        cell.Link > 0 && cell.Link <= _links.Count ? _links[cell.Link - 1] : string.Empty;

    /// <summary>How many distinct hyperlinks this buffer has been told about.</summary>
    public int LinkCount => _links.Count;

    /// <summary>The text a cell holds: its cluster, or the single codepoint it carries inline.</summary>
    public string TextOf(Cell cell) =>
        cell.IsCluster && cell.ClusterIndex < _clusters.Count
            ? _clusters[cell.ClusterIndex]
            : char.ConvertFromUtf32(cell.Codepoint);

    /// <summary>
    /// Resizes the screen. The contents are kept where they still fit and the scrollback is
    /// preserved; <b>reflowing wrapped lines to the new width is deliberately not done here</b> and
    /// is its own line, because it is the behaviour terminals most reliably get wrong and it wants
    /// a pure function nobody has to open a window to test.
    /// </summary>
    public void Resize(int columns, int rows)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(columns, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(rows, 1);

        if (columns == Columns && rows == Rows)
        {
            return;
        }

        int scrollback = Capacity - Rows;
        int capacity = rows + scrollback;
        Cell[] cells = new Cell[capacity * columns];
        bool[] wrapped = new bool[capacity];
        long[] stamps = new long[capacity];
        Array.Fill(cells, Cell.Blank);

        // Keep the newest lines: what a user is looking at survives a resize and the oldest
        // scrollback is what falls off, which is the same direction time runs in.
        int kept = Math.Min(LineCount, capacity);
        int width = Math.Min(columns, Columns);

        for (int line = 0; line < kept; line++)
        {
            int source = LineCount - kept + line;
            Line(source)[..width].CopyTo(cells.AsSpan(line * columns, columns));
            wrapped[line] = IsWrapped(source);
            stamps[line] = GenerationOf(source);
        }

        _cells = cells;
        _wrapped = wrapped;
        _stamps = stamps;
        _dirty = new bool[rows];
        _dirtyRows = 0;

        // The anchor follows the lines that fell off the front, so a line's absolute number is the
        // same number after a resize as before it — which is what makes it an identity at all.
        _firstLine += LineCount - kept;
        _origin = 0;
        Columns = columns;
        Rows = rows;
        Capacity = capacity;
        LineCount = Math.Max(rows, kept);
        CursorRow = Math.Clamp(CursorRow, 0, rows - 1);
        CursorColumn = Math.Clamp(CursorColumn, 0, columns - 1);

        // Every row is new at its position: the geometry changed underneath all of them.
        Bump();
        Region(0, Rows - 1);
    }

    // ---- The record ----

    /// <summary>
    /// One more mutation, published through a fence.
    ///
    /// <para>Read-modify-write and not an interlocked increment, because there is exactly one writer:
    /// the thread that owns the parser. Readers are many and they only ever read. An interlocked
    /// increment here would buy nothing and cost a locked instruction on the hot path.</para>
    /// </summary>
    private void Bump() => Volatile.Write(ref _generation, _generation + 1);

    /// <summary>Stamps one retained line and dirties its screen position, if it has one.</summary>
    private void Touch(int line)
    {
        Bump();
        _stamps[RingRow(line)] = _generation;

        int row = line - ScrollbackLines;

        if (row >= 0 && row < Rows)
        {
            Soil(row);
        }
    }

    private void TouchScreen(int row)
    {
        Bump();
        _stamps[RingRow(ScrollbackLines + row)] = _generation;
        Soil(row);
    }

    /// <summary>Stamps a run of screen rows against the generation already bumped by the caller.</summary>
    private void Region(int top, int bottom)
    {
        for (int row = top; row <= bottom; row++)
        {
            _stamps[RingRow(ScrollbackLines + row)] = _generation;
            Soil(row);
        }
    }

    private void Soil(int row)
    {
        if (!_dirty[row])
        {
            _dirty[row] = true;
            _dirtyRows++;
        }
    }

    /// <summary>Moves every dirty bit up one row, because every position just came down one.</summary>
    private void ShiftDirtyUp()
    {
        if (_dirtyRows == 0)
        {
            return;
        }

        if (_dirty[0])
        {
            _dirtyRows--;
        }

        Array.Copy(_dirty, 1, _dirty, 0, Rows - 1);
        _dirty[Rows - 1] = false;
    }

    private Span<Cell> Mutable(int line) => _cells.AsSpan(RingRow(line) * Columns, Columns);

    private Span<Cell> MutableScreen(int row)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(row);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(row, Rows);

        return Mutable(ScrollbackLines + row);
    }

    private int RingRow(int line) => (_origin + line) % Capacity;

    private int Next(int ring) => ring + 1 == Capacity ? 0 : ring + 1;
}
