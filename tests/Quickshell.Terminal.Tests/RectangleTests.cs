using System.Text;
using Quickshell.Terminal;
using Xunit;

namespace Quickshell.Terminal.Tests;

/// <summary>DECFRA, DECERA, DECCRA and DECSACE (QS238), against the grid esctest uses.</summary>
public sealed class RectangleTests
{
    private const string E = "\u001b";

    private const string Grid =
        "abcdefgh\r\nijklmnop\r\nqrstuvwx\r\nyz012345\r\nABCDEFGH\r\nIJKLMNOP\r\nQRSTUVWX\r\nYZ6789!@";

    // ---- The falsification ----

    /// <summary>The design's own: <em>falsified when DECFRA leaves the rectangle unfilled</em>.</summary>
    [Fact]
    public void DecfraFillsTheRectangle()
    {
        Emulator emulator = Fed(Grid + E + "[37;5;5;7;7$x");

        Assert.Equal("yz012345", Row(emulator, 3));
        Assert.Equal("ABCD%%%H", Row(emulator, 4));
        Assert.Equal("QRST%%%X", Row(emulator, 6));
        Assert.Equal("YZ6789!@", Row(emulator, 7));
    }

    // ---- DECFRA ----

    [Fact]
    public void DecfraFillsInThePensAttributes()
    {
        Emulator emulator = Fed(Grid + E + "[1;31m" + E + "[37;1;1;1;1$x");
        Cell cell = emulator.Buffer.Screen(0)[0];

        Assert.Equal('%', cell.Codepoint);
        Assert.Equal(CellFlags.Bold, cell.Flags);
        Assert.Equal(Colour.Indexed(1), cell.Foreground);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    [InlineData(127)]
    [InlineData(300)]
    public void DecfraIgnoresACharacterThatIsNotPrintable(int character)
    {
        Emulator emulator = Fed(Grid + E + "[" + character + ";1;1;8;8$x");

        Assert.Equal("abcdefgh", Row(emulator, 0));
    }

    [Fact]
    public void DecfraLeavesTheCursorAndIgnoresTheMargins()
    {
        Emulator emulator = Fed(Grid + E + "[?69h" + E + "[3;6s" + E + "[3;6r" + E + "[4;3H"
                                + E + "[37;5;5;7;7$x");

        Assert.Equal("ABCD%%%H", Row(emulator, 4));
        Assert.Equal((3, 2), (emulator.Buffer.CursorRow, emulator.Buffer.CursorColumn));
    }

    [Fact]
    public void DecfraCountsFromTheMarginsUnderOriginMode()
    {
        Emulator emulator = Fed(Grid + E + "[?69h" + E + "[2;9s" + E + "[2;9r" + E + "[?6h"
                                + E + "[37;1;1;3;3$x");

        Assert.Equal("i%%%mnop", Row(emulator, 1));
        Assert.Equal("y%%%2345", Row(emulator, 3));
        Assert.Equal("ABCDEFGH", Row(emulator, 4));
    }

    // ---- DECERA ----

    [Fact]
    public void DeceraErasesTheRectangleWhateverIsProtected()
    {
        Emulator emulator = Fed(E + "[1\"q" + Grid + E + "[0\"q" + E + "[5;5;7;7$z");

        Assert.Equal("ABCD   H", Row(emulator, 4));
        Assert.Equal("QRST   X", Row(emulator, 6));
    }

    [Fact]
    public void AnInvertedRectangleErasesNothing()
    {
        Emulator emulator = Fed(Grid + E + "[5;5;4;4$z");

        Assert.Equal("ABCDEFGH", Row(emulator, 4));
    }

    // ---- DECCRA ----

    [Fact]
    public void DeccraCopiesTheSourceToTheDestination()
    {
        Emulator emulator = Fed(Grid + E + "[2;2;4;4;1;5;5;1$v");

        Assert.Equal("ABCDjklH", Row(emulator, 4));
        Assert.Equal("IJKLrstP", Row(emulator, 5));
        Assert.Equal("QRSTz01X", Row(emulator, 6));
        Assert.Equal("ijklmnop", Row(emulator, 1));
    }

    [Fact]
    public void AnOverlappingCopyCopiesWhatTheSourceHeld()
    {
        Emulator emulator = Fed(Grid + E + "[2;2;4;4;1;3;3;1$v");

        Assert.Equal("ijklmnop", Row(emulator, 1));
        Assert.Equal("qrjklvwx", Row(emulator, 2));
        Assert.Equal("yzrst345", Row(emulator, 3));
        Assert.Equal("ABz01FGH", Row(emulator, 4));
    }

    [Fact]
    public void TheDestinationDefaultsToTheTopLeft()
    {
        Emulator emulator = Fed(Grid + E + "[2;2;4;4;1$v");

        Assert.Equal("jkldefgh", Row(emulator, 0));
        Assert.Equal("z01tuvwx", Row(emulator, 2));
    }

    [Fact]
    public void WhatWouldLandPastTheEdgeIsDropped()
    {
        Emulator emulator = new(8, 8, scrollback: 0);
        emulator.Feed(Encoding.ASCII.GetBytes(Grid + E + "[2;2;4;4;1;7;7;1$v"));

        Assert.Equal("QRSTUVjk", Row(emulator, 6));
        Assert.Equal("YZ6789rs", Row(emulator, 7));
    }

    // ---- DECALN (QS244) ----

    [Fact]
    public void DecalnFillsTheScreenWithE()
    {
        Emulator emulator = new(8, 4, scrollback: 0);
        emulator.Feed(Encoding.ASCII.GetBytes(E + "[1;31m" + E + "#8"));

        Assert.Equal("EEEEEEEE", Row(emulator, 0));
        Assert.Equal("EEEEEEEE", Row(emulator, 3));
        Assert.Equal(Colour.Default, emulator.Buffer.Screen(3)[7].Foreground);
    }

    [Fact]
    public void DecalnHomesTheCursorAndClearsTheMargins()
    {
        Emulator emulator = Fed(E + "[?69h" + E + "[2;3s" + E + "[4;5r" + E + "[5;5H" + E + "#8");

        Assert.Equal((0, 0), (emulator.Buffer.CursorRow, emulator.Buffer.CursorColumn));
        Assert.Equal((0, 23), (emulator.MarginTop, emulator.MarginBottom));
        Assert.Equal(0, emulator.MarginLeft);
    }

    // ---- DECSACE ----

    [Theory]
    [InlineData("", "0")]
    [InlineData("[2*x", "2")]
    [InlineData("[1*x", "1")]
    public void DecsaceIsReportedAsItWasSet(string set, string reported)
    {
        string setup = set.Length == 0 ? string.Empty : E + set;
        Emulator emulator = Fed(setup + E + "P$q*x" + E + "\\");

        Assert.Equal(E + "P1$r" + reported + "*x" + E + "\\", Encoding.ASCII.GetString(emulator.Reply));
    }

    private static Emulator Fed(string stream)
    {
        Emulator emulator = new(80, 24);
        emulator.Feed(Encoding.ASCII.GetBytes(stream));

        return emulator;
    }

    private static string Row(Emulator emulator, int row)
    {
        StringBuilder text = new();

        foreach (Cell cell in emulator.Buffer.Screen(row)[..8])
        {
            text.Append(emulator.Buffer.TextOf(cell));
        }

        return text.ToString();
    }
}
