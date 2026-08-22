namespace Quickshell.Render;

/// <summary>
/// The grid's geometry in whole pixels: how wide a cell is, how tall, and where the baseline sits
/// inside it.
///
/// <para><b>Whole pixels, and rounded once here rather than in the shader.</b> A cell on a
/// fractional boundary puts every glyph on a fractional texel, and then a coverage bitmap
/// rasterised for one alignment is sampled at another - which is blurred text nobody can point at
/// a cause for. The subpixel positioning the atlas keys on is about where the pen is inside a
/// cell, not about where the cell is.</para>
/// </summary>
/// <param name="Width">The advance of one cell in pixels.</param>
/// <param name="Height">The line height in pixels: ascent, descent and the font's line gap.</param>
/// <param name="Baseline">Pixels from the top of the cell down to the baseline.</param>
public readonly record struct CellMetrics(int Width, int Height, int Baseline)
{
    /// <summary>The grid size a viewport of this many pixels holds, floored: a half row is not a row.</summary>
    public (int Columns, int Rows) GridFor(uint width, uint height) =>
        ((int)(width / (uint)Width), (int)(height / (uint)Height));
}
