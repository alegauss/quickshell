using System.Text;
using Quickshell.Terminal;
using Xunit;

namespace Quickshell.Terminal.Tests;

/// <summary>
/// Character protection (QS235): DECSCA and the selective erases that respect it, SPA and EPA and
/// the ordinary erases that respect those, and DECSERA, which respects only the first.
/// </summary>
public sealed class ProtectionTests
{
    private const char Escape = (char)0x1B;
    private const char Quote = '"';
    private static readonly string Csi = new([Escape, '[']);
    private static readonly string Spa = new([Escape, 'V']);
    private static readonly string Epa = new([Escape, 'W']);
    private static readonly string Dcs = new([Escape, 'P']);
    private static readonly string St = new([Escape, (char)0x5C]);

    private static string Protect(int mode) => Csi + mode + Quote + "q";

    // ---- The falsification ----

    /// <summary>The design's own: <em>falsified when DECSED erases a cell DECSCA protected</em>.</summary>
    [Fact]
    public void DecsedLeavesWhatDecscaProtected()
    {
        Emulator emulator = Fed(Protect(1) + "ab" + Protect(0) + "cd" + Csi + "1;1H" + Csi + "?J");

        Assert.Equal("ab  ", Row(emulator, 0)[..4]);
    }

    // ---- DEC protection ----

    [Theory]
    [InlineData("?K", "abcd  ")]
    [InlineData("?0K", "abcd  ")]
    [InlineData("?1K", "ab   f")]
    [InlineData("?2K", "ab    ")]
    public void DecselErasesOnlyWhatIsNotProtected(string sequence, string expected)
    {
        // "ab" protected, "cdef" not, the cursor on the e.
        Emulator emulator = Fed(Protect(1) + "ab" + Protect(0) + "cdef" + Csi + "1;5H" + Csi + sequence);

        Assert.Equal(expected, Row(emulator, 0)[..6]);
    }

    [Fact]
    public void WithNothingProtectedTheSelectiveErasesAreThePlainOnes()
    {
        Emulator emulator = Fed("aaaa\r\nbbbb\r\ncccc" + Csi + "2;3H" + Csi + "?J");

        Assert.Equal("aaaa", Row(emulator, 0)[..4]);
        Assert.Equal("bb  ", Row(emulator, 1)[..4]);
        Assert.Equal("    ", Row(emulator, 2)[..4]);
    }

    [Fact]
    public void DecscaTwoStopsProtectingLikeZero()
    {
        Emulator emulator = Fed(Protect(1) + "a" + Protect(2) + "b" + Csi + "?2K");

        Assert.Equal("a ", Row(emulator, 0)[..2]);
    }

    [Theory]
    [InlineData("J")]
    [InlineData("2K")]
    [InlineData("3X")]
    public void TheOrdinaryErasesIgnoreDecProtection(string erase)
    {
        Emulator emulator = Fed(Protect(1) + "abc" + Protect(0) + Csi + "1;1H" + Csi + erase);

        Assert.Equal("   ", Row(emulator, 0)[..3]);
    }

    [Fact]
    public void SgrZeroLeavesProtectionOn()
    {
        Emulator emulator = Fed(Protect(1) + Csi + "1m" + Csi + "0m" + "ab" + Csi + "?2K");

        Assert.Equal("ab", Row(emulator, 0)[..2]);
        Assert.Equal(CellFlags.Protected, emulator.Buffer.Screen(0)[0].Flags);
    }

    [Fact]
    public void ProtectionIsNotPartOfTheReportedPen()
    {
        Assert.Equal(Dcs + "1$r0m" + St, Sent(Fed(Protect(1) + Dcs + "$qm" + St)));
    }

    [Fact]
    public void DecrqssReportsDecsca()
    {
        Assert.Equal(Dcs + "1$r1\"q" + St, Sent(Fed(Protect(1) + Dcs + "$q\"q" + St)));
        Assert.Equal(Dcs + "1$r0\"q" + St, Sent(Fed(Dcs + "$q\"q" + St)));
    }

    [Fact]
    public void ResetForgetsProtection()
    {
        Emulator emulator = Fed(Protect(1) + Escape + "c" + "ab" + Csi + "?2K");

        Assert.Equal("  ", Row(emulator, 0)[..2]);
    }

    [Fact]
    public void DecstrStopsProtectingWhatIsPrintedNext()
    {
        Emulator emulator = Fed(Protect(1) + Csi + "!p" + "X" + Csi + "?2J");

        Assert.Equal(" ", Row(emulator, 0)[..1]);
        Assert.Equal(Dcs + "1$r0\"q" + St, Sent(Fed(Protect(1) + Csi + "!p" + Dcs + "$q\"q" + St)));
    }

    /// <summary>A pen saved while protected does not bring protection back after a soft reset.</summary>
    [Fact]
    public void DecstrClearsProtectionFromTheSavedCursorToo()
    {
        Emulator emulator = Fed(Protect(1) + Escape + "7" + Csi + "!p" + Escape + "8" + "X" + Csi + "?2J");

        Assert.Equal(" ", Row(emulator, 0)[..1]);
    }

    /// <summary>
    /// A soft reset ends ISO protection too, so the next plain erase clears a cell an earlier
    /// program protected - the leak that failed esctest's ED_0 after ECH's ISO case.
    /// </summary>
    [Fact]
    public void DecstrEndsIsoProtectionForTheErasesThatFollow()
    {
        Emulator emulator = Fed("ab" + Spa + "c" + Epa + Csi + "!p" + Csi + "2J");

        Assert.Equal("   ", Row(emulator, 0)[..3]);
    }

    [Fact]
    public void DecsedThreeDropsTheScrollbackAndLeavesTheScreen()
    {
        Emulator emulator = new(4, 2, scrollback: 20);
        emulator.Feed(Encoding.UTF8.GetBytes("aa\r\nbb\r\ncc\r\ndd"));

        Assert.True(emulator.Buffer.ScrollbackLines > 0);

        emulator.Feed(Encoding.UTF8.GetBytes(Csi + "?3J"));

        Assert.Equal(0, emulator.Buffer.ScrollbackLines);
        Assert.Equal("cc  ", Row(emulator, 0));
        Assert.Equal("dd  ", Row(emulator, 1));
    }

    // ---- ISO protection ----

    [Theory]
    [InlineData("J")]
    [InlineData("2J")]
    [InlineData("2K")]
    [InlineData("3X")]
    public void TheOrdinaryErasesRespectIsoProtection(string erase)
    {
        Emulator emulator = Fed("ab" + Spa + "c" + Epa + Csi + "1;1H" + Csi + erase);

        Assert.Equal("  c", Row(emulator, 0)[..3]);
    }

    [Fact]
    public void DecseraErasesWhatIsoProtected()
    {
        Emulator emulator = Fed("a" + Spa + "b" + Epa + Csi + "1;1;1;2${");

        Assert.Equal("  ", Row(emulator, 0)[..2]);
    }

    // ---- DECSERA ----

    [Fact]
    public void DecseraErasesTheRectangleAndLeavesWhatDecscaProtected()
    {
        Emulator emulator = Fed(
            "abcd\r\n" + Protect(1) + "efgh" + Protect(0) + "\r\nijkl" + Csi + "1;2;3;3${");

        Assert.Equal("a  d", Row(emulator, 0)[..4]);
        Assert.Equal("efgh", Row(emulator, 1)[..4]);
        Assert.Equal("i  l", Row(emulator, 2)[..4]);
    }

    [Fact]
    public void DecseraWithNoArgumentsIsTheWholeScreenAndTheCursorStays()
    {
        Emulator emulator = new(4, 3, scrollback: 0);
        emulator.Feed(Encoding.UTF8.GetBytes("abcd\r\nefgh\r\nijkl" + Csi + "2;3H" + Csi + "${"));

        Assert.Equal("    ", Row(emulator, 0));
        Assert.Equal("    ", Row(emulator, 2));
        Assert.Equal((1, 2), (emulator.Buffer.CursorRow, emulator.Buffer.CursorColumn));
    }

    [Fact]
    public void AnInvertedRectangleErasesNothing()
    {
        Emulator emulator = Fed("abcd" + Csi + "1;3;1;2${");

        Assert.Equal("abcd", Row(emulator, 0)[..4]);
    }

    [Fact]
    public void DecseraCountsFromTheMarginsUnderOriginMode()
    {
        Emulator emulator = new(6, 4, scrollback: 0);
        emulator.Feed(Encoding.UTF8.GetBytes(
            "abcdef\r\nghijkl\r\nmnopqr\r\nstuvwx"
            + Csi + "?69h" + Csi + "2;5s" + Csi + "2;4r" + Csi + "?6h"
            + Csi + "1;1;2;9${"));

        Assert.Equal("abcdef", Row(emulator, 0));
        Assert.Equal("g    l", Row(emulator, 1));
        Assert.Equal("m    r", Row(emulator, 2));
        Assert.Equal("stuvwx", Row(emulator, 3));
    }

    private static Emulator Fed(string stream)
    {
        Emulator emulator = new(80, 24);
        emulator.Feed(Encoding.UTF8.GetBytes(stream));

        return emulator;
    }

    private static string Sent(Emulator emulator) => Encoding.ASCII.GetString(emulator.Reply);

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
