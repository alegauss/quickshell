using Vortice.DirectWrite;

namespace Quickshell.Render;

/// <summary>
/// Everything about a glyph that changes its pixels, and nothing that does not.
///
/// <para>Face, glyph index, weight, slant and size are the obvious five.
/// <see cref="SubpixelOffset"/> is the one that is easy to leave out and expensive to leave out:
/// advances are not integers, so the same character lands on a different fraction of a pixel at
/// every column of a line. Cached without that field, the first fraction to arrive is the one every
/// later column gets, and a whole line looks faintly wrong without any single character being
/// identifiably wrong.</para>
/// </summary>
/// <param name="Family">The font family, as the system font collection spells it.</param>
/// <param name="Weight">The weight the face was matched at.</param>
/// <param name="Slant">Upright, italic or oblique.</param>
/// <param name="SizeInPixels">The em size in physical pixels, from <see cref="FontSettings.SizeInPixels"/>.</param>
/// <param name="Glyph">The glyph index in that face — not a character: shaping has already happened.</param>
/// <param name="SubpixelOffset">The horizontal pen fraction, quantised to <see cref="SubpixelPositions"/>.</param>
public readonly record struct GlyphKey(string Family, FontWeight Weight, FontStyle Slant,
                                       float SizeInPixels, ushort Glyph, int SubpixelOffset)
{
    /// <summary>
    /// How many horizontal positions one pixel is divided into. Four is the usual bargain: the
    /// quarter-pixel error left over is below what an eye resolves on a glyph edge, and the atlas
    /// holds four copies of a character rather than as many as there are columns.
    /// </summary>
    public const int SubpixelPositions = 4;

    /// <summary>
    /// Whether this glyph is rasterised with one coverage per colour stripe rather than one per
    /// pixel.
    ///
    /// <para>Part of the key because it is three different bitmaps and one of them: the same
    /// character at the same size in the same face is a different set of pixels either way, and a
    /// cache that could not tell them apart would hand a grayscale tile to a ClearType frame the
    /// first time a font setting changed.</para>
    ///
    /// <para>Not to be confused with <see cref="SubpixelOffset"/>, which is a horizontal position
    /// and not a colour stripe. The words collide; the concepts do not touch.</para>
    /// </summary>
    public bool ClearType { get; init; }

    /// <summary>The offset this key was quantised to, back in pixels.</summary>
    public float SubpixelOffsetInPixels => (float)SubpixelOffset / SubpixelPositions;

    /// <summary>
    /// Quantises a horizontal pen position to one of <see cref="SubpixelPositions"/>. The integer
    /// part is dropped: where the glyph lands in the atlas does not depend on which column it is in,
    /// only on the fraction of a pixel it starts at.
    /// </summary>
    public static int Quantise(float penX)
    {
        float fraction = penX - MathF.Floor(penX);

        // A fraction rounding up to a whole pixel is the next pixel's zero, not a fifth position.
        return (int)MathF.Round(fraction * SubpixelPositions) % SubpixelPositions;
    }
}
