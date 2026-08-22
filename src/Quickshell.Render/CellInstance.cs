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
/// <param name="Foreground">The foreground in the low 24 bits, <see cref="CellFlags"/> in the top 8.</param>
/// <param name="Background">The background in the low 24 bits, the atlas page in the top 8.</param>
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
    /// Packs a cell. A placement that marks no pixels is not a special case here: its zero size is
    /// what the shader reads as nothing to sample, so a space costs the same instance as a letter.
    /// </summary>
    public static CellInstance For(GlyphPlacement glyph, Rgb foreground, Rgb background,
                                   CellFlags flags = CellFlags.None)
    {
        return new CellInstance(
            foreground.Packed | ((uint)flags << 24),
            background.Packed | ((uint)glyph.Page << 24),
            Pair(glyph.X, glyph.Y),
            Pair(glyph.Width, glyph.Height),
            Pair(glyph.Left, glyph.Top));
    }

    /// <summary>Two signed sixteen-bit halves in one word, the second in the high bits.</summary>
    private static uint Pair(int low, int high) => (ushort)(short)low | ((uint)(ushort)(short)high << 16);
}
