using System.Text;
using Quickshell.Terminal;
using Xunit;

namespace Quickshell.Terminal.Tests;

/// <summary>
/// The designated character sets, and the operating system commands a host uses to tell the terminal
/// about itself.
/// </summary>
public sealed class CharacterSetAndOscTests
{
    /// <summary>ESC, spelled so a reader can see it and a diff can show it. QS98.</summary>
    private const string E = "\u001b";

    // ---- The falsification ----

    /// <summary>
    /// The design's own falsification. A program drawing a box sends the designation and then ASCII
    /// letters; a terminal that ignores it draws <c>lqqqk</c> where the user expects a corner.
    /// </summary>
    [Fact]
    public void TheDesignationTurnsAsciiLettersIntoABoxCorner()
    {
        Emulator emulator = Fed(E + "(0" + "lqqqk");

        Assert.Equal("┌───┐", Row(emulator, 0)[..5]);
    }

    [Fact]
    public void WithoutTheDesignationTheSameLettersAreLetters()
    {
        Emulator emulator = Fed("lqqqk");

        Assert.Equal("lqqqk", Row(emulator, 0)[..5]);
    }

    [Fact]
    public void TheWholeSpecialGraphicsTableIsTheOneDecPublished()
    {
        Emulator emulator = Fed(E + "(0" + "jklmnqtuvwx");

        // Corners, junctions and the two rules, in the order the set spells them.
        Assert.Equal("┘┐┌└┼─├┤┴┬│", Row(emulator, 0)[..11]);
    }

    [Fact]
    public void ADesignationLeavesDigitsAndSpacesAlone()
    {
        Emulator emulator = Fed(E + "(0" + "12 34");

        Assert.Equal("12 34", Row(emulator, 0)[..5]);
    }

    [Fact]
    public void ReturningToAsciiEndsTheRemapping()
    {
        Emulator emulator = Fed(E + "(0" + "q" + E + "(B" + "q");

        Assert.Equal("─q", Row(emulator, 0)[..2]);
    }

    /// <summary>Shift-out and shift-in pick between the two slots without redesignating either.</summary>
    [Fact]
    public void ShiftOutAndShiftInSelectBetweenTheTwoSlots()
    {
        Emulator emulator = Fed(E + ")0" + "q" + "\u000e" + "q" + "\u000f" + "q");

        Assert.Equal(CharacterSet.Ascii, emulator.ActiveCharacterSet);
        Assert.Equal("q─q", Row(emulator, 0)[..3]);
    }

    [Fact]
    public void TheUnitedKingdomSetDiffersInExactlyOnePlace()
    {
        Emulator emulator = Fed(E + "(A" + "#5");

        Assert.Equal("£5", Row(emulator, 0)[..2]);
    }

    // ---- The window title ----

    [Theory]
    [InlineData("0")]
    [InlineData("2")]
    public void TheTitleIsSetByEitherOfItsCommands(string command)
    {
        Emulator emulator = Fed(E + "]" + command + ";a window title\a");

        Assert.Equal("a window title", emulator.Title);
    }

    /// <summary>Both terminators are in the wild, so both are accepted.</summary>
    [Fact]
    public void EitherTerminatorEndsTheCommand()
    {
        Assert.Equal("bel", Fed(E + "]2;bel\a").Title);
        Assert.Equal("st", Fed(E + "]2;st" + E + "\\").Title);
    }

    [Fact]
    public void ATitleWithNonAsciiInItArrivesAsText()
    {
        Emulator emulator = Fed(E + "]2;~/código 中文\a");

        Assert.Equal("~/código 中文", emulator.Title);
    }

    [Fact]
    public void ATitleLongerThanTheCeilingIsRefusedRatherThanKept()
    {
        Emulator emulator = Fed(E + "]2;" + new string('x', Emulator.MaximumOscLength + 100) + "\a");

        Assert.Equal(string.Empty, emulator.Title);
        Assert.True(emulator.Unhandled > 0);
    }

    // ---- Colours ----

    [Fact]
    public void APaletteEntryIsSetByItsCommand()
    {
        Emulator emulator = Fed(E + "]4;1;rgb:ff/00/00\a");

        Assert.Equal(new Rgb(255, 0, 0), emulator.Palette[1]);
    }

    [Fact]
    public void SeveralPaletteEntriesArriveInOneCommand()
    {
        Emulator emulator = Fed(E + "]4;1;#ff0000;2;#00ff00;3;#0000ff\a");

        Assert.Equal(new Rgb(255, 0, 0), emulator.Palette[1]);
        Assert.Equal(new Rgb(0, 255, 0), emulator.Palette[2]);
        Assert.Equal(new Rgb(0, 0, 255), emulator.Palette[3]);
    }

    /// <summary>
    /// X allows one to four hex digits a channel, and they scale rather than truncate. Truncating is
    /// how a one-digit spelling comes out almost black.
    /// </summary>
    [Theory]
    [InlineData("rgb:f/f/f", 255, 255, 255)]
    [InlineData("rgb:ff/ff/ff", 255, 255, 255)]
    [InlineData("rgb:ffff/ffff/ffff", 255, 255, 255)]
    [InlineData("rgb:0/0/0", 0, 0, 0)]
    [InlineData("rgb:80/40/20", 128, 64, 32)]
    [InlineData("#804020", 128, 64, 32)]
    public void EveryChannelSpellingScalesToTheSameColour(string spelling, int red, int green, int blue)
    {
        Emulator emulator = Fed(E + "]4;7;" + spelling + "\a");

        Assert.Equal(new Rgb((byte)red, (byte)green, (byte)blue), emulator.Palette[7]);
    }

    [Fact]
    public void TheThreeDefaultColoursHaveTheirOwnCommands()
    {
        Emulator emulator = Fed(E + "]10;#111111\a" + E + "]11;#222222\a" + E + "]12;#333333\a");

        Assert.Equal(new Rgb(0x11, 0x11, 0x11), emulator.Palette.Foreground);
        Assert.Equal(new Rgb(0x22, 0x22, 0x22), emulator.Palette.Background);
        Assert.Equal(new Rgb(0x33, 0x33, 0x33), emulator.Palette.Cursor);
    }

    /// <summary>
    /// The whole point of storing colour roles rather than resolved values: text already on screen
    /// repaints when the host changes what its colour means.
    /// </summary>
    [Fact]
    public void ChangingAPaletteEntryRepaintsTextAlreadyWritten()
    {
        Emulator emulator = Fed(E + "[31mred text");
        Cell cell = emulator.Buffer.Screen(0)[0];

        emulator.Feed(Encoding.UTF8.GetBytes(E + "]4;1;#0000ff\a"));

        Assert.Equal(new Rgb(0, 0, 255), emulator.Palette.Resolve(cell.Foreground));
    }

    [Fact]
    public void AColourSpellingNothingUnderstandsIsCountedRatherThanApproximated()
    {
        Emulator emulator = Fed(E + "]4;1;chartreuse\a");

        Assert.True(emulator.Unhandled > 0);
        Assert.Equal(new Rgb(0xCD, 0, 0), emulator.Palette[1]);
    }

    // ---- The working directory ----

    [Fact]
    public void TheWorkingDirectoryIsWhatTheHostReports()
    {
        Emulator emulator = Fed(E + "]7;file://host/home/user/src\a");

        Assert.Equal("file://host/home/user/src", emulator.WorkingDirectory);
    }

    // ---- Hyperlinks ----

    [Fact]
    public void AHyperlinkCoversTheRunOfCellsAfterIt()
    {
        Emulator emulator = Fed(E + "]8;;https://example.com\a" + "link" + E + "]8;;\a" + "plain");

        Assert.Equal("https://example.com", emulator.Buffer.LinkOf(emulator.Buffer.Screen(0)[0]));
        Assert.Equal("https://example.com", emulator.Buffer.LinkOf(emulator.Buffer.Screen(0)[3]));
        Assert.Equal(string.Empty, emulator.Buffer.LinkOf(emulator.Buffer.Screen(0)[4]));
    }

    [Fact]
    public void OneUriOverAHundredCellsIsOneEntry()
    {
        Emulator emulator = Fed(E + "]8;;https://example.com\a" + new string('x', 40));

        Assert.Equal(1, emulator.Buffer.LinkCount);
    }

    [Fact]
    public void ACellIsStillSixteenBytesWithALinkOnIt()
    {
        Emulator emulator = Fed(E + "]8;;https://example.com\a" + "x");
        Cell cell = emulator.Buffer.Screen(0)[0];

        Assert.True(cell.Link > 0);
        Assert.Equal(16, System.Runtime.InteropServices.Marshal.SizeOf<Cell>());
    }

    // ---- What this line deliberately does not answer ----

    /// <summary>
    /// OSC 52 writes the local clipboard from the remote side. That is a security decision rather
    /// than an emulation one, and answering it here would be making it quietly.
    /// </summary>
    [Fact]
    public void TheClipboardCommandIsCountedAndNotObeyed()
    {
        Emulator emulator = Fed(E + "]52;c;aGVsbG8=\a");

        Assert.True(emulator.Unhandled > 0);
    }

    [Fact]
    public void AnUnknownCommandIsCountedRatherThanThrown()
    {
        Emulator emulator = Fed(E + "]9999;whatever\a" + "after");

        Assert.True(emulator.Unhandled > 0);
        Assert.Equal("after", Row(emulator, 0)[..5]);
    }

    [Fact]
    public void ACommandWithNoNumberIsCountedRatherThanThrown()
    {
        Emulator emulator = Fed(E + "]notanumber\a");

        Assert.True(emulator.Unhandled > 0);
    }

    private static Emulator Fed(string stream)
    {
        Emulator emulator = new(20, 5, scrollback: 10);
        emulator.Feed(Encoding.UTF8.GetBytes(stream));

        return emulator;
    }

    private static string Row(Emulator emulator, int row)
    {
        StringBuilder text = new();

        foreach (Cell cell in emulator.Buffer.Screen(row))
        {
            if (cell.Width != 0)
            {
                text.Append(emulator.Buffer.TextOf(cell));
            }
        }

        return text.ToString();
    }
}
