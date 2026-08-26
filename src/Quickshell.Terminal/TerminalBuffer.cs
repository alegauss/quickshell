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
    private int _origin;

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

    /// <summary>One row of the retained lines, oldest first. Row <see cref="ScrollbackLines"/> is the screen's top.</summary>
    public Span<Cell> Line(int line)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(line);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(line, LineCount);

        return _cells.AsSpan(RingRow(line) * Columns, Columns);
    }

    /// <summary>One row of the visible screen.</summary>
    public Span<Cell> Screen(int row)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(row);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(row, Rows);

        return Line(ScrollbackLines + row);
    }

    /// <summary>Whether a retained line continues into the one after it rather than ending.</summary>
    public bool IsWrapped(int line) => _wrapped[RingRow(line)];

    /// <summary>Records that a line continues into the next, which is what a soft wrap is.</summary>
    public void SetWrapped(int line, bool wrapped) => _wrapped[RingRow(line)] = wrapped;

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
        }

        Span<Cell> bottom = Line(LineCount - 1);
        bottom.Fill(Cell.Blank);
        _wrapped[RingRow(LineCount - 1)] = false;

        CellsWrittenByScrolling += bottom.Length;
        Scrolls++;
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
            Screen(row + shift).CopyTo(Screen(row));
            SetScreenWrapped(row, IsScreenWrapped(row + shift));
            CellsWrittenByScrolling += Columns;
        }

        for (int row = bottom - shift + 1; row <= bottom; row++)
        {
            Screen(row).Fill(Cell.Blank);
            SetScreenWrapped(row, false);
            CellsWrittenByScrolling += Columns;
        }
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
            Screen(row - shift).CopyTo(Screen(row));
            SetScreenWrapped(row, IsScreenWrapped(row - shift));
            CellsWrittenByScrolling += Columns;
        }

        for (int row = top; row < top + shift; row++)
        {
            Screen(row).Fill(Cell.Blank);
            SetScreenWrapped(row, false);
            CellsWrittenByScrolling += Columns;
        }
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

        _origin = RingRow(ScrollbackLines);
        LineCount = Rows;
    }

    /// <summary>Writes one cell of the visible screen.</summary>
    public void Write(int row, int column, Cell cell)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(column);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(column, Columns);

        Screen(row)[column] = cell;
    }

    /// <summary>Clears a run of a visible row, which is what every erase sequence reduces to.</summary>
    public void Clear(int row, int from, int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(from);
        ArgumentOutOfRangeException.ThrowIfNegative(count);

        Span<Cell> line = Screen(row);
        Screen(row).Slice(from, Math.Min(count, line.Length - from)).Fill(Cell.Blank);
    }

    /// <summary>Clears the whole visible screen without touching what is behind it.</summary>
    public void ClearScreen()
    {
        for (int row = 0; row < Rows; row++)
        {
            Screen(row).Fill(Cell.Blank);
            SetScreenWrapped(row, false);
        }
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
        }

        _cells = cells;
        _wrapped = wrapped;
        _origin = 0;
        Columns = columns;
        Rows = rows;
        Capacity = capacity;
        LineCount = Math.Max(rows, kept);
        CursorRow = Math.Clamp(CursorRow, 0, rows - 1);
        CursorColumn = Math.Clamp(CursorColumn, 0, columns - 1);
    }

    private int RingRow(int line) => (_origin + line) % Capacity;

    private int Next(int ring) => ring + 1 == Capacity ? 0 : ring + 1;
}
