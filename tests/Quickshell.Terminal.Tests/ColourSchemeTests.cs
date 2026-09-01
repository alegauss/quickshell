using System.Text;
using Quickshell.Terminal;
using Xunit;

namespace Quickshell.Terminal.Tests;

/// <summary>
/// QS51's falsification: <em>falsified when applying a scheme leaves existing scrollback in the
/// previous palette</em>.
///
/// <para>That claim is only true because a cell stores the colour <em>role</em> the host asked for
/// rather than a resolved value, so the test that proves it has to go through a real buffer: text is
/// written first, the scheme is applied second, and what the already-written cells resolve to is the
/// answer.</para>
/// </summary>
public sealed class ColourSchemeTests
{
    /// <summary>The escape, written as its code point because this repository holds no raw ones.</summary>
    private const char Esc = (char)0x1b;

    /// <summary>A scheme with nothing of the default's in it, so a stale value cannot pass.</summary>
    private static ColourScheme Loud() => new()
    {
        Name = "loud",
        Palette =
        [
            new Rgb(1, 1, 1), new Rgb(200, 10, 10), new Rgb(10, 200, 10), new Rgb(200, 200, 10),
            new Rgb(10, 10, 200), new Rgb(200, 10, 200), new Rgb(10, 200, 200), new Rgb(180, 180, 180),
            new Rgb(90, 90, 90), new Rgb(255, 40, 40), new Rgb(40, 255, 40), new Rgb(255, 255, 40),
            new Rgb(40, 40, 255), new Rgb(255, 40, 255), new Rgb(40, 255, 255), new Rgb(250, 250, 250),
        ],
        Foreground = new Rgb(240, 230, 220),
        Background = new Rgb(20, 10, 30),
        Cursor = new Rgb(255, 128, 0),
    };

    // ---- The falsification ----

    /// <summary>
    /// A line written before the scheme arrived is painted in the new scheme.
    ///
    /// <para>Both kinds of colour a scheme owns are checked: the cell that took the default because
    /// the host never set one, and the cell the host asked for by index.</para>
    /// </summary>
    [Fact]
    public void ApplyingASchemeRepaintsWhatIsAlreadyOnScreen()
    {
        Emulator emulator = new(20, 4, 100);

        // Written first: one cell in the default colours, one the host asked for as red.
        emulator.Feed(Encoding.UTF8.GetBytes($"plain{Esc}[31mred{Esc}[0m"));

        Cell plain = emulator.Buffer.Line(0)[0];
        Cell red = emulator.Buffer.Line(0)[5];

        Assert.True(plain.Foreground.IsDefault);
        Assert.Equal(1, red.Foreground.Index);

        ColourScheme scheme = Loud();

        scheme.ApplyTo(emulator.Palette);

        // The cells themselves never moved — the scheme reaches them through what they resolve to.
        Assert.Equal(scheme.Foreground, emulator.Palette.Resolve(plain.Foreground));
        Assert.Equal(scheme.Background, emulator.Palette.Resolve(plain.Background, background: true));
        Assert.Equal(scheme.Palette[1], emulator.Palette.Resolve(red.Foreground));
        Assert.Equal(scheme.Cursor, emulator.Palette.Cursor);
    }

    /// <summary>
    /// The cube and the greyscale ramp are not a scheme's to change.
    ///
    /// <para>No format states them and every terminal derives them the same way, so a scheme that
    /// moved one would make a 256-colour program look wrong here and correct everywhere else — which
    /// is the hardest kind of rendering complaint to act on.</para>
    /// </summary>
    [Fact]
    public void ASchemeLeavesTheCubeAndTheGreysAlone()
    {
        Palette palette = new();

        Rgb cube = palette[100];
        Rgb grey = palette[240];

        Loud().ApplyTo(palette);

        Assert.Equal(cube, palette[100]);
        Assert.Equal(grey, palette[240]);
    }

    // ---- Contrast: reported, never enforced ----

    /// <summary>Black on white is WCAG's maximum, and a colour against itself is its minimum.</summary>
    [Fact]
    public void ContrastIsWhatWcagSaysItIs()
    {
        Assert.Equal(21.0, ColourScheme.Contrast(Rgb.Black, Rgb.White), 1);
        Assert.Equal(21.0, ColourScheme.Contrast(Rgb.White, Rgb.Black), 1);
        Assert.Equal(1.0, ColourScheme.Contrast(Rgb.White, Rgb.White), 3);
    }

    /// <summary>A scheme whose text vanishes into its ground says so, and still loads.</summary>
    [Fact]
    public void AnUnreadableSchemeIsNamedRatherThanRefused()
    {
        ColourScheme awful = Loud() with
        {
            Foreground = new Rgb(24, 14, 34),
            Background = new Rgb(20, 10, 30),
        };

        Unreadable named = Assert.Single(
            awful.Unreadable, one => one.What.Contains("foreground", StringComparison.Ordinal));

        Assert.True(named.Ratio < ColourScheme.Readable);

        // And it applies regardless, because this is the user's choice to make.
        Palette palette = new();

        awful.ApplyTo(palette);

        Assert.Equal(awful.Foreground, palette.Foreground);
    }

    /// <summary>
    /// The built-in scheme's own text is readable, and the report still names black.
    ///
    /// <para><b>That black is on the list is the check working, not the check being wrong.</b> Black
    /// on a dark ground is unreadable in every dark scheme ever published, and a report that hid the
    /// fact because it is common would be one a user could not use to explain what they are seeing.
    /// It is a list and not an alarm.</para>
    /// </summary>
    [Fact]
    public void TheSchemeThisClientShipsIsReadableWhereItMatters()
    {
        IReadOnlyList<Unreadable> named = ColourScheme.Default.Unreadable;

        Assert.DoesNotContain(named, one => one.What.Contains("foreground", StringComparison.Ordinal));
        Assert.Contains(named, one => one.What.Contains("(black)", StringComparison.Ordinal));
    }

    /// <summary>A scheme is sixteen entries, and a list that is not is refused rather than padded.</summary>
    [Fact]
    public void APaletteThatIsNotSixteenEntriesIsRefused()
    {
        Assert.Throws<ArgumentException>(() => new ColourScheme { Palette = [Rgb.Black] });
    }
}
