namespace Quickshell.Terminal;

/// <summary>What kind of thing a cell's colour is, which is not always a colour.</summary>
public enum ColourKind : byte
{
    /// <summary>The theme's own foreground or background, whichever slot this is.</summary>
    Default = 0,

    /// <summary>An index into the palette: the base sixteen, the cube, or the greyscale ramp.</summary>
    Indexed = 1,

    /// <summary>A colour the host stated outright, in twenty-four bits.</summary>
    Direct = 2,
}

/// <summary>
/// A cell's colour as the host expressed it, which is deliberately not a colour.
///
/// <para><b>Default is a distinct state from any concrete colour.</b> A host that never sets a
/// foreground means "whatever this terminal's is", and a terminal that resolves that to the theme's
/// current value at the moment of writing has thrown the distinction away — so changing the theme
/// leaves every line already on screen painted in the old one. That is the bug this type exists to
/// make impossible, and it is why the resolution happens when a frame is built and not before.</para>
///
/// <para>The same applies to a palette index: <c>SGR 31</c> means "red as this terminal spells it",
/// and OSC 4 can change what that is at any moment.</para>
/// </summary>
public readonly record struct Colour
{
    private readonly uint _packed;

    private Colour(uint packed) => _packed = packed;

    /// <summary>The theme's foreground or background, resolved when a frame is built.</summary>
    public static Colour Default => new(0);

    /// <summary>One of the palette's 256 entries.</summary>
    public static Colour Indexed(byte index) => new(((uint)ColourKind.Indexed << 24) | index);

    /// <summary>A colour the host stated in full.</summary>
    public static Colour Direct(Rgb rgb) => new(((uint)ColourKind.Direct << 24) | rgb.Packed);

    /// <summary>A colour the host stated in full, from its three channels.</summary>
    public static Colour Direct(byte red, byte green, byte blue) => Direct(new Rgb(red, green, blue));

    /// <summary>Which of the three this is.</summary>
    public ColourKind Kind => (ColourKind)(_packed >> 24);

    /// <summary>Whether this is the theme's, rather than anything concrete.</summary>
    public bool IsDefault => Kind == ColourKind.Default;

    /// <summary>The palette entry, where this is one.</summary>
    public byte Index => (byte)_packed;

    /// <summary>The stated colour, where this is one.</summary>
    public Rgb Rgb => new((byte)(_packed >> 16), (byte)(_packed >> 8), (byte)_packed);

    /// <summary>The kind and value in one word, which is what a cell stores.</summary>
    public uint Packed => _packed;

    /// <summary>Rebuilds a colour from what a cell stored.</summary>
    public static Colour FromPacked(uint packed) => new(packed);

    /// <summary>A stated colour, so existing callers that mean one need not say so twice.</summary>
    public static implicit operator Colour(Rgb rgb) => Direct(rgb);
}

/// <summary>
/// What the indices and the two defaults actually look like.
///
/// <para>It is a value the renderer consults when it builds a frame, never something the buffer
/// baked into a cell. Change it and the whole screen repaints, scrollback included, which is the
/// behaviour a theme switch is supposed to have.</para>
/// </summary>
public sealed class Palette
{
    private readonly Rgb[] _entries = new Rgb[256];

    /// <summary>Builds the standard palette: the base sixteen, the 6x6x6 cube, the greyscale ramp.</summary>
    public Palette()
    {
        // The sixteen everyone recognises. These are xterm's, which is what a host assumes when it
        // says "red" and what every other terminal on the machine will have drawn.
        ReadOnlySpan<uint> basic =
        [
            0x000000, 0xCD0000, 0x00CD00, 0xCDCD00, 0x0000EE, 0xCD00CD, 0x00CDCD, 0xE5E5E5,
            0x7F7F7F, 0xFF0000, 0x00FF00, 0xFFFF00, 0x5C5CFF, 0xFF00FF, 0x00FFFF, 0xFFFFFF,
        ];

        for (int index = 0; index < 16; index++)
        {
            _entries[index] = Unpack(basic[index]);
        }

        // 16..231: a 6x6x6 cube. The levels are not evenly spaced - the first step is larger - and
        // copying that is what makes a 256-colour program look the same here as elsewhere.
        ReadOnlySpan<byte> levels = [0, 95, 135, 175, 215, 255];

        for (int index = 0; index < 216; index++)
        {
            _entries[16 + index] = new Rgb(levels[index / 36], levels[index / 6 % 6], levels[index % 6]);
        }

        // 232..255: twenty-four greys, neither of them black or white.
        for (int index = 0; index < 24; index++)
        {
            byte level = (byte)(8 + (index * 10));
            _entries[232 + index] = new Rgb(level, level, level);
        }
    }

    /// <summary>The theme's foreground, which every default-foreground cell resolves to.</summary>
    public Rgb Foreground { get; set; } = new(214, 219, 228);

    /// <summary>The theme's background.</summary>
    public Rgb Background { get; set; } = new(16, 18, 24);

    /// <summary>The cursor's colour, which OSC 12 sets and a block cursor inverts against.</summary>
    public Rgb Cursor { get; set; } = new(220, 220, 220);

    /// <summary>One palette entry, readable and settable, which is what OSC 4 will write.</summary>
    public Rgb this[byte index]
    {
        get => _entries[index];
        set => _entries[index] = value;
    }

    /// <summary>
    /// What a colour looks like right now. <paramref name="background"/> says which default this
    /// slot takes, because the two are different colours and a cell knows only that it wanted one.
    /// </summary>
    public Rgb Resolve(Colour colour, bool background = false) => colour.Kind switch
    {
        ColourKind.Indexed => _entries[colour.Index],
        ColourKind.Direct => colour.Rgb,
        _ => background ? Background : Foreground,
    };

    private static Rgb Unpack(uint packed) =>
        new((byte)(packed >> 16), (byte)(packed >> 8), (byte)packed);
}
