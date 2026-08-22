using System.Runtime.InteropServices;
using Quickshell.Terminal;

namespace Quickshell.Render;

/// <summary>
/// One cell, as the GPU reads it: twenty bytes, five unsigned words, and nothing that does not
/// change pixels.
///
/// <para><b>The cell's own position is not in here.</b> It is derived on the GPU from
/// <c>SV_InstanceID</c> and the column count, which is what keeps the grid one buffer of cells
/// rather than one buffer of coordinates that happen to be a grid.</para>
///
/// <para><b>The background is in here rather than in a second pass.</b> Every cell has one, so a
/// separate pass would double the fill rate for no picture.</para>
///
/// <para>Twenty bytes puts a 200x50 grid under 200 KB, which is why the upload is never the cost
/// worth optimising and the layout is a decision made once rather than tuned later.</para>
/// </summary>
/// <param name="Foreground">The foreground in the low 24 bits, <see cref="CellFlags"/> and the span in the top 8.</param>
/// <param name="Background">The background in the low 24 bits, the atlas page and its kind in the top 8.</param>
/// <param name="GlyphOrigin">The glyph's left in the low 16 bits and its top in the high 16.</param>
/// <param name="GlyphSize">The glyph's width in the low 16 bits and its height in the high 16.</param>
/// <param name="GlyphBearing">The signed left bearing in the low 16 bits and the top in the high 16.</param>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct CellInstance(uint Foreground, uint Background,
                                           uint GlyphOrigin, uint GlyphSize, uint GlyphBearing)
{
    /// <summary>The size the vertex buffer's stride is set to, asserted against the struct itself.</summary>
    public const int Stride = 20;

    /// <summary>
    /// The bit of the background's top byte that says the glyph is on a colour page. Three bits are
    /// left for the page index, which is more than the shader can reach anyway.
    /// </summary>
    public const uint ColourPage = 0x80;

    /// <summary>
    /// Packs a cell.
    ///
    /// <para>A placement that marks no pixels is not a special case: its zero size is what the
    /// shader reads as nothing to sample, so a space costs the same instance as a letter.</para>
    ///
    /// <para><paramref name="span"/> is how many cells this one occupies, and it is the model's
    /// answer rather than the renderer's. <b>One</b> is ordinary. <b>Two</b> widens the quad so a
    /// wide character is drawn across both its cells instead of being clipped at the first.
    /// <b>Zero</b> is the trailing cell of a wide pair: it draws nothing at all, because the cell
    /// before it has already painted that ground and a second quad over the top would erase the
    /// right half of the character.</para>
    /// </summary>
    public static CellInstance For(GlyphPlacement glyph, Rgb foreground, Rgb background,
                                   CellFlags flags = CellFlags.None, int span = 1)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(span);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(span, 2);

        return new CellInstance(
            foreground.Packed | ((uint)flags << 24) | ((uint)span << 30),
            background.Packed | ((uint)glyph.Page << 24) | (glyph.IsColour ? ColourPage << 24 : 0u),
            Pair(glyph.X, glyph.Y),
            Pair(glyph.Width, glyph.Height),
            Pair(glyph.Left, glyph.Top));
    }

    /// <summary>Two signed sixteen-bit halves in one word, the second in the high bits.</summary>
    private static uint Pair(int low, int high) => (ushort)(short)low | ((uint)(ushort)(short)high << 16);
}
