namespace Quickshell.Terminal;

/// <summary>
/// One pair this scheme paints text in that cannot be read against its own background.
/// </summary>
/// <param name="What">Which colour, named the way a user would look for it.</param>
/// <param name="Ratio">Its contrast against the background, by WCAG's relative luminance.</param>
public readonly record struct Unreadable(string What, double Ratio);

/// <summary>
/// Nineteen colours: the base sixteen, the default foreground and background, and the cursor.
///
/// <para><b>QS51: nobody types nineteen colours.</b> Schemes circulate as files, and a user who has
/// to approximate the one they already use will resent the result quietly for as long as they keep
/// using this client. Reading the two formats that already circulate is what makes that not happen.
/// </para>
///
/// <para><b>Nineteen and not twenty, because the selection colour is deliberately not one.</b> Both
/// formats carry one and this client ignores it: the grid painter swaps a selected cell's two
/// colours rather than choosing a highlight, and that decision was made for the case this type is
/// about — a scheme this client did not write, where a fixed blue over a blue ground selects text
/// into invisibility. Reading a selection colour and then not painting with it would be a field
/// that lied about what the client does.</para>
///
/// <para><b>Applying one repaints scrollback</b>, which works only because a cell stores the colour
/// <em>role</em> the host asked for rather than a resolved value. This is where that decision, made
/// in <see cref="Colour"/> long before there was anything to apply, is spent.</para>
/// </summary>
public sealed record ColourScheme
{
    /// <summary>How many entries the base palette has, which every format agrees on.</summary>
    public const int Entries = 16;

    /// <summary>
    /// Below this, a colour is reported as unreadable against its own background.
    ///
    /// <para>Three to one is WCAG's bar for large text rather than the 4.5 it asks of body text. A
    /// terminal scheme is held to the looser one on purpose: half the published schemes put their
    /// dim colours deliberately close to the ground, and a check that called Solarized broken would
    /// be a check nobody reads twice.</para>
    /// </summary>
    public const double Readable = 3.0;

    /// <summary>The names a user would look for, which are the names every format uses.</summary>
    private static readonly string[] Names =
    [
        "black", "red", "green", "yellow", "blue", "magenta", "cyan", "white",
        "bright black", "bright red", "bright green", "bright yellow",
        "bright blue", "bright magenta", "bright cyan", "bright white",
    ];

    /// <summary>xterm's sixteen, taken from the palette rather than written down twice.</summary>
    private static readonly Rgb[] Standard = Base();

    private readonly Rgb[] _entries = [.. Standard];

    /// <summary>What a terminal looks like before anybody has chosen anything.</summary>
    public static ColourScheme Default { get; } = new();

    /// <summary>What the scheme is called, as its file named it.</summary>
    public string Name { get; init; } = "quickshell";

    /// <summary>The base sixteen, in the order every terminal numbers them.</summary>
    public IReadOnlyList<Rgb> Palette
    {
        get => _entries;
        init
        {
            ArgumentNullException.ThrowIfNull(value);

            if (value.Count != Entries)
            {
                throw new ArgumentException(
                    $"a scheme has {Entries} palette entries, not {value.Count}", nameof(value));
            }

            _entries = [.. value];
        }
    }

    /// <summary>Default text.</summary>
    public Rgb Foreground { get; init; } = new(214, 219, 228);

    /// <summary>The ground behind it.</summary>
    public Rgb Background { get; init; } = new(16, 18, 24);

    /// <summary>The cursor. Where a file omits it, it is the foreground — see the reference.</summary>
    public Rgb Cursor { get; init; } = new(220, 220, 220);

    /// <summary>
    /// Every colour this scheme paints text in that cannot be read against its own background.
    ///
    /// <para><b>Reported and never enforced.</b> A scheme with an unreadable pair in it is the
    /// user's choice to make — half of them are unreadable somewhere on purpose — and naming which
    /// pair is more use to somebody than a refusal to load the file they asked for.</para>
    /// </summary>
    public IReadOnlyList<Unreadable> Unreadable
    {
        get
        {
            List<Unreadable> found = [];

            double against = Contrast(Foreground, Background);

            if (against < Readable)
            {
                found.Add(new Unreadable("the default foreground", against));
            }

            for (int index = 0; index < Entries; index++)
            {
                double ratio = Contrast(_entries[index], Background);

                if (ratio < Readable)
                {
                    found.Add(new Unreadable($"colour {index} ({Names[index]})", ratio));
                }
            }

            return found;
        }
    }

    /// <summary>
    /// Paints this scheme onto a live palette, which repaints everything already on screen.
    ///
    /// <para>The 6x6x6 cube and the greyscale ramp above index 15 are left alone. No format states
    /// them, every terminal derives them the same way, and a scheme that quietly changed them would
    /// make a 256-colour program look wrong here and nowhere else.</para>
    /// </summary>
    /// <param name="palette">The palette a renderer is already resolving colours against.</param>
    public void ApplyTo(Palette palette)
    {
        ArgumentNullException.ThrowIfNull(palette);

        for (int index = 0; index < Entries; index++)
        {
            palette[(byte)index] = _entries[index];
        }

        palette.Foreground = Foreground;
        palette.Background = Background;
        palette.Cursor = Cursor;
    }

    /// <summary>
    /// How far apart two colours are, as WCAG counts it: from 1 for identical to 21 for black on
    /// white.
    /// </summary>
    /// <param name="one">A colour.</param>
    /// <param name="other">The one it is read against.</param>
    public static double Contrast(Rgb one, Rgb other)
    {
        double first = Luminance(one);
        double second = Luminance(other);

        return first > second
                   ? (first + 0.05) / (second + 0.05)
                   : (second + 0.05) / (first + 0.05);
    }

    /// <summary>xterm's sixteen, read off a fresh palette so there is one copy of them.</summary>
    private static Rgb[] Base()
    {
        Palette standard = new();
        Rgb[] entries = new Rgb[Entries];

        for (int index = 0; index < Entries; index++)
        {
            entries[index] = standard[(byte)index];
        }

        return entries;
    }

    /// <summary>WCAG's relative luminance, which is not the same as a channel average.</summary>
    private static double Luminance(Rgb colour) =>
        (0.2126 * Linear(colour.Red)) + (0.7152 * Linear(colour.Green)) + (0.0722 * Linear(colour.Blue));

    /// <summary>One channel with the display's gamma taken back out of it.</summary>
    private static double Linear(byte channel)
    {
        double value = channel / 255.0;

        return value <= 0.03928 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
    }
}
