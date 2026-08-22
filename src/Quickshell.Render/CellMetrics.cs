namespace Quickshell.Render;

/// <summary>
/// The grid's geometry in whole pixels: how wide a cell is, how tall, where the baseline sits
/// inside it, and where the font puts its own rules.
///
/// <para><b>Whole pixels, and rounded once here rather than in the shader.</b> A cell on a
/// fractional boundary puts every glyph on a fractional texel, and then a coverage bitmap
/// rasterised for one alignment is sampled at another - which is blurred text nobody can point at
/// a cause for. The subpixel positioning the atlas keys on is about where the pen is inside a
/// cell, not about where the cell is.</para>
///
/// <para><b>The rules come from the font, not from a fraction of the cell.</b> DirectWrite reports
/// an underline position and thickness per face, and using them is what stops the line cutting
/// through the descenders of g, p and y. Metrics built by hand - a test, a placeholder - have no
/// face to ask, so they fall back to the conventional placement and say so.</para>
/// </summary>
/// <param name="Width">The advance of one cell in pixels.</param>
/// <param name="Height">The line height in pixels: ascent, descent and the font's line gap.</param>
/// <param name="Baseline">Pixels from the top of the cell down to the baseline.</param>
public readonly record struct CellMetrics(int Width, int Height, int Baseline)
{
    private readonly int _underlineY;
    private readonly int _underlineThickness;
    private readonly int _strikeY;
    private readonly int _strikeThickness;

    /// <summary>Pixels from the top of the cell to the middle of the underline.</summary>
    public int UnderlineY
    {
        get => _underlineY > 0 ? _underlineY : Baseline + Math.Max(1, Height / 12);
        init => _underlineY = value;
    }

    /// <summary>How thick the underline is, at least one pixel.</summary>
    public int UnderlineThickness
    {
        get => _underlineThickness > 0 ? _underlineThickness : Math.Max(1, Height / 16);
        init => _underlineThickness = value;
    }

    /// <summary>Pixels from the top of the cell to the middle of the strikethrough.</summary>
    public int StrikeY
    {
        get => _strikeY > 0 ? _strikeY : Baseline - (Height / 5);
        init => _strikeY = value;
    }

    /// <summary>How thick the strikethrough is, at least one pixel.</summary>
    public int StrikeThickness
    {
        get => _strikeThickness > 0 ? _strikeThickness : UnderlineThickness;
        init => _strikeThickness = value;
    }

    /// <summary>The grid size a viewport of this many pixels holds, floored: a half row is not a row.</summary>
    public (int Columns, int Rows) GridFor(uint width, uint height) =>
        ((int)(width / (uint)Width), (int)(height / (uint)Height));
}
