namespace Quickshell.Render;

/// <summary>
/// The font the grid is drawn in, at the display it is drawn on.
///
/// <para>These three fields are together the one thing a change to which invalidates every cached
/// glyph at once, which is why they are a value passed to the atlas rather than fields the atlas
/// keeps: a rebuild is then a new value, not a sweep somebody has to get right.</para>
///
/// <para>Points and DPI rather than pixels, because that is what a settings surface will ask a user
/// for. <see cref="SizeInPixels"/> is the derived number, and it is the one the cache key carries.</para>
/// </summary>
/// <param name="Family">The font family name, as the system font collection spells it.</param>
/// <param name="SizeInPoints">The em size in typographic points.</param>
/// <param name="Dpi">The display's dots per inch. 96 is the unscaled desktop.</param>
public readonly record struct FontSettings(string Family, float SizeInPoints, float Dpi)
{
    /// <summary>Consolas at eleven points on an unscaled display: a monospaced face on every Windows.</summary>
    public static FontSettings Default => new("Consolas", 11f, 96f);

    /// <summary>The em size in DIPs, which is the unit DirectWrite states a glyph run in.</summary>
    public float SizeInDips => SizeInPoints * 96f / 72f;

    /// <summary>How many physical pixels one DIP is worth on this display.</summary>
    public float PixelsPerDip => Dpi / 96f;

    /// <summary>
    /// The em size in physical pixels. This is what the cache key carries: a change of points and a
    /// change of DPI that land on the same pixel size really do produce the same coverage bitmap.
    /// </summary>
    public float SizeInPixels => SizeInDips * PixelsPerDip;
}
