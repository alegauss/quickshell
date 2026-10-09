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

        // A palette no scheme has painted yet is its own baseline, so a reset always has one.
        Remember();
    }

    /// <summary>The theme's foreground, which every default-foreground cell resolves to.</summary>
    public Rgb Foreground { get; set; } = Brand.Ink;

    /// <summary>The theme's background.</summary>
    public Rgb Background { get; set; } = Brand.Ground;

    /// <summary>The cursor's colour, which OSC 12 sets and a block cursor inverts against.</summary>
    public Rgb Cursor { get; set; } = Brand.Cursor;

    /// <summary>One palette entry, readable and settable, which is what OSC 4 will write.</summary>
    public Rgb this[byte index]
    {
        get => _entries[index];
        set => _entries[index] = value;
    }

    // ---- What a host can set and ask about beyond the 256 (QS234) ----

    /// <summary>How many special colours OSC 5 addresses: bold, underline, blink, reverse, italic.</summary>
    public const int SpecialColours = 5;

    // Held so a host that sets one can read it back and reset it, as xterm keeps them. None of them is
    // drawn: bold here is a weight and not a colour, and the rest name things this client has none of
    // (a Tektronix window, a separate mouse pointer colour). A program asking is answered honestly
    // with what it set.
    private readonly Rgb[] _special = new Rgb[SpecialColours];
    private readonly Rgb[] _dynamic = new Rgb[10];

    private Rgb[] _rememberedEntries = [];
    private Rgb[] _rememberedSpecial = [];
    private Rgb[] _rememberedDynamic = [];

    /// <summary>A special colour, OSC 5's <paramref name="index"/> or OSC 4's 256 plus it.</summary>
    public Rgb Special(int index) => _special[index];

    /// <summary>Sets a special colour.</summary>
    public void SetSpecial(int index, Rgb colour) => _special[index] = colour;

    /// <summary>
    /// A dynamic colour by its OSC number, 10 to 19: 10, 11 and 12 are the foreground, background
    /// and cursor this palette draws with; 13 to 19 are held and reported.
    /// </summary>
    public Rgb Dynamic(int command) => command switch
    {
        10 => Foreground,
        11 => Background,
        12 => Cursor,
        _ => _dynamic[command - 10],
    };

    /// <summary>Sets a dynamic colour by its OSC number.</summary>
    public void SetDynamic(int command, Rgb colour)
    {
        switch (command)
        {
            case 10:
                Foreground = colour;
                break;

            case 11:
                Background = colour;
                break;

            case 12:
                Cursor = colour;
                break;

            default:
                _dynamic[command - 10] = colour;
                break;
        }
    }

    /// <summary>
    /// Takes what this palette holds now as the colours a reset returns to: the session's own scheme,
    /// which <see cref="ColourScheme.ApplyTo"/> calls this after painting. A host's OSC 104, 105 or
    /// 110 to 119 then undoes what the host set, and never what the user chose.
    /// </summary>
    public void Remember()
    {
        // The held colours follow the drawn ones, so a host that asks before setting is answered with
        // something it can recognise rather than black, and a scheme change carries them along.
        Array.Fill(_special, Foreground);
        _dynamic[3] = Foreground;
        _dynamic[4] = Background;
        _dynamic[5] = Foreground;
        _dynamic[6] = Background;
        _dynamic[7] = Foreground;
        _dynamic[8] = Cursor;
        _dynamic[9] = Background;

        _rememberedEntries = [.. _entries];
        _rememberedSpecial = [.. _special];
        _rememberedDynamic = [.. _dynamic];
        _rememberedDynamic[0] = Foreground;
        _rememberedDynamic[1] = Background;
        _rememberedDynamic[2] = Cursor;
    }

    /// <summary>OSC 104 with an index: one entry back to the scheme's.</summary>
    public void ResetEntry(byte index) => _entries[index] = _rememberedEntries[index];

    /// <summary>OSC 104 alone: every entry back.</summary>
    public void ResetEntries() => _rememberedEntries.CopyTo(_entries, 0);

    /// <summary>OSC 105 with an index: one special colour back.</summary>
    public void ResetSpecial(int index) => _special[index] = _rememberedSpecial[index];

    /// <summary>OSC 105 alone: every special colour back.</summary>
    public void ResetSpecials() => _rememberedSpecial.CopyTo(_special, 0);

    /// <summary>OSC 110 to 119: the dynamic colour of that number less a hundred, back.</summary>
    public void ResetDynamic(int command) => SetDynamic(command, _rememberedDynamic[command - 10]);

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
