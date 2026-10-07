using System.Buffers;
using System.Text;
using Quickshell.Terminal;
using Vortice.DirectWrite;

namespace Quickshell.Render;

/// <summary>
/// A screen of cells, turned into the instances one draw call takes.
///
/// <para><b>QS116: this is the piece that was living in a test file.</b> The golden suite's
/// `Painter` does exactly this and was written there because there was nowhere else for it to go —
/// which is why the client could open a window and could open a session and could not do both. It
/// is the same work, reading a real <see cref="TerminalBuffer"/> instead of a string.</para>
///
/// <para><b>Colours are resolved here and stored nowhere.</b> A cell holds what the host said —
/// "default", or an index, or a direct value — and what those mean is the palette's business at the
/// moment a frame is built. That is what lets a theme change repaint scrollback written under the
/// old one, and it is why this takes a <see cref="Palette"/> rather than baking one in.</para>
///
/// <para><b>It allocates nothing per frame.</b> The instance array is the caller's and is reused;
/// a frame that allocated would put a collection pause in the middle of somebody's session, which
/// is the whole of what Block C's zero-allocation criterion is about.</para>
/// </summary>
public sealed class GridPainter
{
    /// <summary>
    /// How many clusters' composed forms are remembered before the memory starts again: both screens'
    /// worth of a full cluster table, so an ordinary session never reaches it and a hostile one
    /// cannot grow it without bound.
    /// </summary>
    private const int MaximumComposed = TerminalBuffer.MaximumClusters * 2;

    private readonly GlyphAtlas _atlas;
    private readonly Palette _palette;

    /// <summary>
    /// What each cluster the painter has met draws as, keyed by the buffer's own interned string —
    /// so a cluster already met is a lookup and allocates nothing, and the one allocation
    /// composing costs is paid once per distinct cluster and not once per frame.
    /// </summary>
    private readonly Dictionary<string, int> _composed = new(StringComparer.Ordinal);

    /// <summary>Builds a painter over an atlas and the palette its colours mean something in.</summary>
    public GridPainter(GlyphAtlas atlas, Palette palette)
    {
        ArgumentNullException.ThrowIfNull(atlas);
        ArgumentNullException.ThrowIfNull(palette);

        _atlas = atlas;
        _palette = palette;
    }

    /// <summary>How many cells the last <see cref="Paint"/> filled, which is what to draw.</summary>
    public int Painted { get; private set; }

    /// <summary>
    /// Fills <paramref name="into"/> with the visible screen of <paramref name="buffer"/>.
    /// </summary>
    /// <param name="buffer">What to read. Its visible rows, not its scrollback.</param>
    /// <param name="into">
    /// Where the instances go. Must hold at least the screen's cells; anything beyond what is
    /// painted is left alone, and <see cref="Painted"/> is what a caller draws.
    /// </param>
    /// <param name="cursorRow">Where the cursor is, or -1 for no cursor.</param>
    /// <param name="cursorColumn">Its column.</param>
    /// <param name="cursor">What shape to draw it as.</param>
    /// <param name="metrics">The cell box, for the advance a wide glyph is fitted to.</param>
    /// <param name="selection">
    /// What is selected, or null for nothing.
    ///
    /// <para><b>The two colours are swapped rather than a highlight colour being chosen</b>, which is
    /// what every terminal does and is the only rule that works against an arbitrary palette: a fixed
    /// blue over a blue scheme selects text into invisibility, and a scheme this client did not write
    /// is the ordinary case.</para>
    ///
    /// <para>Asked per cell by absolute line, because a selection outlives the scrolling underneath
    /// it — that is what QS22's line identities were for.</para>
    /// </param>
    /// <param name="viewport">
    /// Which part of the history is on screen, or null for the live screen.
    ///
    /// <para>Everything else here is in rows and the viewport is in absolute lines, so this is where
    /// the two meet: the top row is whatever line the viewport is anchored to, and the cursor is
    /// drawn only where the line it sits on is one of the lines being shown. A cursor kept at its
    /// row number would be a caret blinking in the middle of somebody's scrollback.</para>
    /// </param>
    /// <param name="composing">
    /// What an input method is composing, or null for nothing (QS153).
    ///
    /// <para>Drawn into the grid at the cursor, in the session's own font and colours and
    /// underlined, the convention every terminal follows — and not in the box an input method draws
    /// over a surface it cannot see, in a font nobody chose. It wraps at the right edge as the text
    /// under it does, because it is the same grid, and the cursor moves to the composition's own
    /// caret.</para>
    /// </param>
    public void Paint(TerminalBuffer buffer, Span<CellInstance> into, int cursorRow,
                      int cursorColumn, CursorShape cursor, CellMetrics metrics,
                      Selection? selection = null, Viewport? viewport = null,
                      Composition? composing = null)
    {
        ArgumentNullException.ThrowIfNull(buffer);

        int columns = buffer.Columns;
        int rows = buffer.Rows;

        ArgumentOutOfRangeException.ThrowIfLessThan(into.Length, columns * rows);

        Painted = 0;

        bool selecting = selection is { IsActive: true };

        // The first line on screen, and the first the ring still holds. A row is drawn from the
        // retained index between them, which for a viewport at the bottom is the screen's own.
        long top = viewport?.Top(buffer) ?? buffer.TopLine;
        long oldest = buffer.TopLine - buffer.ScrollbackLines;

        // The cursor's row within what is on screen, which is its own row only while the viewport is
        // at the bottom. Anywhere else it is off-screen and there is no caret to draw.
        int caret = cursorRow < 0 ? -1 : (int)(buffer.AbsoluteLine(cursorRow) - top);

        for (int row = 0; row < rows; row++)
        {
            long absolute = top + row;
            int retained = (int)(absolute - oldest);

            ReadOnlySpan<Cell> line = retained >= 0 && retained < buffer.LineCount
                ? buffer.Line(retained)
                : default;

            for (int column = 0; column < columns; column++)
            {
                Cell cell = column < line.Length ? line[column] : Cell.Blank;

                // The trailing half of a wide character is a real cell holding nothing, and drawing
                // a glyph for it would put a second copy of the character one column along.
                int span = cell.Width;

                Rgb foreground = _palette.Resolve(cell.Foreground);
                Rgb background = _palette.Resolve(cell.Background, background: true);

                if (selecting && selection!.Contains(absolute, column))
                {
                    (foreground, background) = (background, foreground);
                }

                GlyphPlacement glyph = span == 0
                    ? GlyphPlacement.Empty
                    : GlyphFor(buffer, cell, metrics.Width * span);

                into[Painted++] = CellInstance.For(
                    glyph, foreground, background, cell.Flags, Math.Max(1, span), cell.Underline,
                    row == caret && column == cursorColumn ? cursor : CursorShape.None);
            }
        }

        // Where the view is, while it is anywhere but the bottom (QS158).
        if (viewport is { IsAtBottom: false })
        {
            Mark(buffer, viewport, into, top, oldest, columns, rows, metrics);
        }

        // Over the cells just painted rather than after them: an instance's place in the grid is its
        // index, so the composition replaces the cells it covers. Only where the cursor is on screen,
        // because that is where the text being typed is going.
        if (composing is { IsActive: true } && caret >= 0 && caret < rows && !composing.Text.IsEmpty)
        {
            Overlay(composing, into, caret, Math.Clamp(cursorColumn, 0, columns - 1), columns, rows,
                    cursor, metrics);
        }
    }

    /// <summary>The down arrow a scrolled-back view shows when output arrived below it.</summary>
    private const int Arrived = 0x2193;

    /// <summary>
    /// A scrollbar drawn in the grid, because there is nowhere else to draw one (QS158).
    ///
    /// <para><b>In the grid and not beside it</b>: a WPF scrollbar is chrome a default installation
    /// does not show, and nothing can be drawn over the pane, which is a child window a swapchain
    /// presents into. So the right-hand column's cells carry it — the rows the view spans of the whole
    /// history have their two colours swapped, as a selection's are, so the text under it still
    /// reads.</para>
    ///
    /// <para><b>Output that arrived is a mark and never a jump.</b> Somebody reading is not moved: the
    /// bottom-right cell shows an arrow, which is all it takes to know the screen below has changed.
    /// </para>
    ///
    /// <para>Only while the view is scrolled back. At the bottom there is nothing to say, and the
    /// live screen is never drawn over.</para>
    /// </summary>
    private void Mark(TerminalBuffer buffer, Viewport viewport, Span<CellInstance> into, long top,
                      long oldest, int columns, int rows, CellMetrics metrics)
    {
        long total = Math.Max(rows, buffer.LineCount);
        int edge = columns - 1;

        // The thumb: as many rows as the screen is of the history, at least one, placed where the top
        // of the view is in it.
        int size = (int)Math.Clamp(rows * (long)rows / total, 1, rows);
        int from = (int)Math.Clamp((top - oldest) * rows / total, 0, rows - size);

        for (int row = from; row < from + size; row++)
        {
            CellInstance cell = into[(row * columns) + edge];

            // The two colours' low 24 bits change places; everything above them — flags, span,
            // page, underline, cursor — stays where it is.
            into[(row * columns) + edge] = cell with
            {
                Foreground = (cell.Foreground & 0xFF000000u) | (cell.Background & 0x00FFFFFFu),
                Background = (cell.Background & 0xFF000000u) | (cell.Foreground & 0x00FFFFFFu),
            };
        }

        if (viewport.HasUnseenOutput)
        {
            Rgb foreground = _palette.Resolve(Colour.Default);
            Rgb background = _palette.Resolve(Colour.Default, background: true);

            // Inverted, so it reads whatever is behind it.
            into[((rows - 1) * columns) + edge] = CellInstance.For(
                _atlas.Cache(Arrived, FontWeight.Normal, FontStyle.Normal, maximumAdvance: metrics.Width),
                background, foreground);
        }
    }

    /// <summary>
    /// The composition, cell by cell from the cursor: underlined, in the default colours, wide
    /// characters across two cells, wrapping to the next row at the edge, and the cursor on the
    /// composition's caret — which may be the cell just past it.
    /// </summary>
    private void Overlay(Composition composing, Span<CellInstance> into, int row, int column,
                         int columns, int rows, CursorShape cursor, CellMetrics metrics)
    {
        Rgb foreground = _palette.Resolve(Colour.Default);
        Rgb background = _palette.Resolve(Colour.Default, background: true);

        ReadOnlySpan<char> text = composing.Text;
        int before = composing.CellsBeforeCaret;
        int drawn = 0;
        int caretAt = -1;

        for (int at = 0; at < text.Length;)
        {
            if (Rune.DecodeFromUtf16(text[at..], out Rune rune, out int used) != OperationStatus.Done)
            {
                break;
            }

            at += used;

            int width = Math.Min(CharacterWidth.Of(rune.Value), CellInstance.MaximumSpan);

            if (width == 0)
            {
                continue;
            }

            // A wide character never straddles the edge: it starts the next row, as the host's would.
            if (column + width > columns)
            {
                column = 0;
                row++;
            }

            if (row >= rows)
            {
                break;
            }

            int index = (row * columns) + column;

            if (drawn == before)
            {
                caretAt = index;
            }

            GlyphPlacement glyph = rune.Value == ' '
                ? GlyphPlacement.Empty
                : _atlas.Cache(rune.Value, FontWeight.Normal, FontStyle.Normal, maximumAdvance: metrics.Width * width);

            into[index] = CellInstance.For(glyph, foreground, background, CellFlags.None, width,
                                           UnderlineStyle.Single);

            if (width == 2)
            {
                into[index + 1] = CellInstance.For(GlyphPlacement.Empty, foreground, background,
                                                   CellFlags.None, 0, UnderlineStyle.Single);
            }

            column += width;
            drawn += width;

            if (column >= columns)
            {
                column = 0;
                row++;
            }
        }

        // A caret at the end of the composition sits on the cell after it, which is the buffer's.
        if (caretAt < 0 && drawn <= before && row < rows)
        {
            caretAt = (row * columns) + column;
        }

        if (caretAt >= 0 && cursor != CursorShape.None)
        {
            CellInstance under = into[caretAt];

            // The cursor's two bits, at the top of the background word, replaced and nothing else.
            into[caretAt] = under with { Background = (under.Background & ~(3u << 30)) | ((uint)cursor << 30) };
        }
    }

    /// <summary>The glyph a cell that occupies room is drawn with, from whichever cache holds it.</summary>
    private GlyphPlacement GlyphFor(TerminalBuffer buffer, Cell cell, float room)
    {
        FontWeight weight = (cell.Flags & CellFlags.Bold) != 0 ? FontWeight.Bold : FontWeight.Normal;
        FontStyle slant = (cell.Flags & CellFlags.Slant) != 0 ? FontStyle.Italic : FontStyle.Normal;

        if (!cell.IsCluster)
        {
            return cell.Codepoint == ' '
                ? GlyphPlacement.Empty
                : _atlas.Cache(cell.Codepoint, weight, slant, maximumAdvance: room);
        }

        string text = buffer.TextOf(cell);
        int composed = Composed(text);

        return composed >= 0
            ? _atlas.Cache(composed, weight, slant, maximumAdvance: room)
            : _atlas.CacheCluster(text, Base(text), weight, slant, room);
    }

    /// <summary>
    /// The one character a cluster composes to, or -1 where it composes to more than one.
    ///
    /// <para><b>QS91.</b> A cell holding a cluster has no codepoint of its own — it answers U+FFFD —
    /// and the painter used to draw exactly that, so <c>e</c> followed by U+0301 came out as a
    /// replacement character. The model keeps what the host sent; what is drawn is its canonical
    /// composition where that is one character, which is the precomposed <c>é</c> the face already
    /// has and is exactly what the same text sent precomposed draws as.</para>
    ///
    /// <para><b>Where nothing composes it to one character, the atlas shapes it whole</b> — a mark
    /// with no precomposed form stacked on its base, or an emoji sequence the face joins — through
    /// <see cref="GlyphAtlas.CacheCluster"/>. The cell is still one instance and one glyph; the
    /// glyph is just a picture of several.</para>
    /// </summary>
    private int Composed(string text)
    {
        if (_composed.TryGetValue(text, out int known))
        {
            return known;
        }

        if (_composed.Count >= MaximumComposed)
        {
            _composed.Clear();
        }

        int drawn = Compose(text);
        _composed[text] = drawn;

        return drawn;
    }

    /// <summary>The cluster's NFC form where that is one character, and -1 where not.</summary>
    private static int Compose(string text)
    {
        string composed;

        try
        {
            composed = text.Normalize(NormalizationForm.FormC);
        }
        catch (ArgumentException)
        {
            // Not well-formed enough to normalise, so not one character either.
            return -1;
        }

        return composed.Length > 0
               && Rune.DecodeFromUtf16(composed, out Rune only, out int consumed) == OperationStatus.Done
               && consumed == composed.Length
            ? only.Value
            : -1;
    }

    /// <summary>A cluster's first character, which decides the face it is drawn in.</summary>
    private static int Base(string text) =>
        text.Length > 0 && Rune.DecodeFromUtf16(text, out Rune first, out _) == OperationStatus.Done
            ? first.Value
            : 0xFFFD;
}
