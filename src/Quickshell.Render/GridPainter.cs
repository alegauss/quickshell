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
    private readonly GlyphAtlas _atlas;
    private readonly Palette _palette;

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
    public void Paint(TerminalBuffer buffer, Span<CellInstance> into, int cursorRow,
                      int cursorColumn, CursorShape cursor, CellMetrics metrics)
    {
        ArgumentNullException.ThrowIfNull(buffer);

        int columns = buffer.Columns;
        int rows = buffer.Rows;

        ArgumentOutOfRangeException.ThrowIfLessThan(into.Length, columns * rows);

        Painted = 0;

        for (int row = 0; row < rows; row++)
        {
            ReadOnlySpan<Cell> line = buffer.Screen(row);

            for (int column = 0; column < columns; column++)
            {
                Cell cell = column < line.Length ? line[column] : Cell.Blank;

                // The trailing half of a wide character is a real cell holding nothing, and drawing
                // a glyph for it would put a second copy of the character one column along.
                int span = cell.Width;

                Rgb foreground = _palette.Resolve(cell.Foreground);
                Rgb background = _palette.Resolve(cell.Background, background: true);

                GlyphPlacement glyph = span == 0 || cell.Codepoint == ' '
                    ? GlyphPlacement.Empty
                    : _atlas.Cache(cell.Codepoint,
                                   (cell.Flags & CellFlags.Bold) != 0 ? FontWeight.Bold : FontWeight.Normal,
                                   (cell.Flags & CellFlags.Slant) != 0 ? FontStyle.Italic : FontStyle.Normal,
                                   maximumAdvance: metrics.Width * Math.Max(1, span));

                into[Painted++] = CellInstance.For(
                    glyph, foreground, background, cell.Flags, Math.Max(1, span), cell.Underline,
                    row == cursorRow && column == cursorColumn ? cursor : CursorShape.None);
            }
        }
    }
}
