namespace Quickshell.Terminal;

public sealed partial class Emulator
{
    // DECSACE as the host last set it: 0 or 1 is a stream, 2 a rectangle. Held and reported; the
    // attribute changes it shapes (DECCARA, DECRARA) are not carried (QS238).
    private int _attributeExtent;

    /// <summary>DECSACE as DECRQSS reports it.</summary>
    private int AttributeExtentReport => _attributeExtent;

    /// <summary>
    /// The rectangle a VT420 rectangle operation names, from <paramref name="first"/> on, zero-based
    /// and inclusive, or false when it names none. Missing or zero edges are the screen's; under
    /// DECOM the numbers count from the margins and the rectangle is clipped to them, otherwise to
    /// the screen; one whose top is below its bottom or whose left is past its right does nothing,
    /// as xterm's does. Every rectangle operation ignores the margins otherwise.
    /// </summary>
    private bool Rectangle(in CsiParameters parameters, int first,
                           out int top, out int left, out int bottom, out int right)
    {
        (int firstRow, int lastRow, int firstColumn, int lastColumn) = Page();

        top = firstRow + Math.Max(1, parameters.Value(first, 1)) - 1;
        left = firstColumn + Math.Max(1, parameters.Value(first + 1, 1)) - 1;
        bottom = parameters.Value(first + 2, 0) > 0 ? firstRow + parameters.Value(first + 2, 0) - 1 : lastRow;
        right = parameters.Value(first + 3, 0) > 0 ? firstColumn + parameters.Value(first + 3, 0) - 1 : lastColumn;

        bottom = Math.Min(bottom, lastRow);
        right = Math.Min(right, lastColumn);

        return top <= bottom && left <= right;
    }

    /// <summary>What a rectangle's numbers count from and are clipped to: the margins under DECOM, the screen otherwise.</summary>
    private (int FirstRow, int LastRow, int FirstColumn, int LastColumn) Page() => OriginMode
        ? (MarginTop, MarginBottom, MarginLeft, Right)
        : (0, Buffer.Rows - 1, 0, Buffer.Columns - 1);

    /// <summary>
    /// DECFRA, <c>CSI Pch ; Pt ; Pl ; Pb ; Pr $ x</c>: fills the rectangle with one character in the
    /// pen's colours and attributes. Only a printable character fills - GL or GR, as DEC allows -
    /// and anything else is ignored rather than written as a control.
    /// </summary>
    private void FillRectangle(in CsiParameters parameters)
    {
        int character = parameters.Value(0, 0);

        if (character is not ((>= 32 and <= 126) or (>= 160 and <= 255)))
        {
            Unhandled++;
            return;
        }

        if (!Rectangle(parameters, 1, out int top, out int left, out int bottom, out int right))
        {
            return;
        }

        Cell fill = Cell.For(character, _pen.Foreground, _pen.Background, _pen.Flags, _pen.Underline,
                             link: _pen.Link);

        for (int row = top; row <= bottom; row++)
        {
            Buffer.Fill(row, left, right - left + 1, fill);
        }
    }

    /// <summary>
    /// DECERA, <c>CSI Pt ; Pl ; Pb ; Pr $ z</c>: erases the rectangle whatever is protected - the
    /// selective form is DECSERA - leaving the pen's background, as every erase here does.
    /// </summary>
    private void EraseRectangle(in CsiParameters parameters)
    {
        if (!Rectangle(parameters, 0, out int top, out int left, out int bottom, out int right))
        {
            return;
        }

        for (int row = top; row <= bottom; row++)
        {
            Buffer.Clear(row, left, right - left + 1, _pen.Background);
        }
    }

    /// <summary>
    /// DECCRA, <c>CSI Pts ; Pls ; Pbs ; Prs ; Pps ; Ptd ; Pld ; Ppd $ v</c>: copies the source
    /// rectangle so its top left lands on the destination's. The pages are ignored, since there is
    /// one. The source is clipped like any rectangle, and what would land past the edge is dropped.
    /// The cells are read whole before any is written, so a destination overlapping its source
    /// copies what the source held, not what the copy has already overwritten.
    /// </summary>
    private void CopyRectangle(in CsiParameters parameters)
    {
        if (!Rectangle(parameters, 0, out int top, out int left, out int bottom, out int right))
        {
            return;
        }

        (int firstRow, int lastRow, int firstColumn, int lastColumn) = Page();
        int toRow = firstRow + Math.Max(1, parameters.Value(5, 1)) - 1;
        int toColumn = firstColumn + Math.Max(1, parameters.Value(6, 1)) - 1;

        int height = Math.Min(bottom - top + 1, lastRow - toRow + 1);
        int width = Math.Min(right - left + 1, lastColumn - toColumn + 1);

        if (height <= 0 || width <= 0)
        {
            return;
        }

        // Pooled, because the parse path allocates nothing in steady state and a host can send
        // this as often as it likes.
        Cell[] copied = System.Buffers.ArrayPool<Cell>.Shared.Rent(height * width);

        try
        {
            for (int row = 0; row < height; row++)
            {
                Buffer.Screen(top + row).Slice(left, width).CopyTo(copied.AsSpan(row * width, width));
            }

            for (int row = 0; row < height; row++)
            {
                Buffer.WriteRun(toRow + row, toColumn, copied.AsSpan(row * width, width));
            }
        }
        finally
        {
            System.Buffers.ArrayPool<Cell>.Shared.Return(copied);
        }
    }

    /// <summary>
    /// DECALN: every cell an E in the default colours, every margin gone and the cursor home, as
    /// xterm does it (QS244). The margins go first, so home is the screen's corner whatever
    /// origin mode says.
    /// </summary>
    private void AlignmentTest()
    {
        MarginTop = 0;
        MarginBottom = Buffer.Rows - 1;
        ClearColumnMargins();

        Cell fill = Cell.For('E', Colour.Default, Colour.Default);

        for (int row = 0; row < Buffer.Rows; row++)
        {
            Buffer.Fill(row, 0, Buffer.Columns, fill);
        }

        Home();
    }

    /// <summary>DECSACE, <c>CSI Ps * x</c>: held for DECRQSS, a value outside 0 to 2 counted and ignored.</summary>
    private void SelectAttributeExtent(int extent)
    {
        if (extent is >= 0 and <= 2)
        {
            _attributeExtent = extent;
        }
        else
        {
            Unhandled++;
        }
    }
}
