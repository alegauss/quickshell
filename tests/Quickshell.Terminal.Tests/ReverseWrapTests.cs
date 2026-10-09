using System.Text;
using Quickshell.Terminal;
using Xunit;

namespace Quickshell.Terminal.Tests;

/// <summary>
/// Reverse wraparound as xterm has had it since 2023 (QS242): mode 45 walks back through a line
/// that wrapped and no further, 1045 crosses any row and the top margin, and a backspace from the
/// owed-wrap position spends its step on the wrap.
/// </summary>
public sealed class ReverseWrapTests
{
    private const string E = "\u001b";

    // ---- The falsification ----

    /// <summary>The design's own: <em>falsified when BS at column one with 45 reset moves the cursor up</em>.</summary>
    [Fact]
    public void WithoutTheModeABackspaceStopsAtTheEdge()
    {
        Emulator emulator = Fed(10, E + "[?7h" + E + "[3;1H\b");

        Assert.Equal((2, 0), Cursor(emulator));
    }

    // ---- Mode 45 ----

    [Fact]
    public void ABackspaceWalksBackIntoTheRowThatWrapped()
    {
        // Twelve characters on a ten-column screen wrap row 0 into row 1.
        Emulator emulator = Fed(10, E + "[?7h" + E + "[?45h" + "0123456789ab" + "\b\b\b");

        Assert.Equal((0, 9), Cursor(emulator));
    }

    [Fact]
    public void ABackspaceDoesNotCrossARowThatEndedWithANewline()
    {
        Emulator emulator = Fed(10, E + "[?7h" + E + "[?45h" + "abc\r\n\b");

        Assert.Equal((1, 0), Cursor(emulator));
    }

    [Fact]
    public void TheModeNeedsAutowrap()
    {
        Emulator emulator = Fed(10, E + "[?45h" + "0123456789ab" + E + "[?7l" + E + "[2;1H\b");

        Assert.Equal((1, 0), Cursor(emulator));
    }

    [Fact]
    public void CubWalksBackAcrossTheWrapAsFarAsItCounts()
    {
        Emulator emulator = Fed(10, E + "[1;9H" + "abcd" + E + "[?45h" + E + "[?7h" + E + "[4D");

        Assert.Equal((0, 8), Cursor(emulator));
    }

    // ---- Mode 1045 ----

    [Fact]
    public void ExtendedWrapCrossesARowThatDidNotWrap()
    {
        Emulator emulator = Fed(10, E + "[?7h" + E + "[?1045h" + E + "[3;1H\b");

        Assert.Equal((1, 9), Cursor(emulator));
    }

    [Fact]
    public void ExtendedWrapGoesFromTheTopMarginToTheBottomOne()
    {
        Emulator emulator = Fed(10, E + "[?7h" + E + "[?1045h" + E + "[2;5r" + E + "[2;1H\b");

        Assert.Equal((4, 9), Cursor(emulator));
    }

    [Fact]
    public void ExtendedWrapLandsOnTheRightMargin()
    {
        Emulator emulator = Fed(20, E + "[?7h" + E + "[?1045h" + E + "[?69h" + E + "[5;10s" + E + "[3;5H\b");

        Assert.Equal((1, 9), Cursor(emulator));
    }

    // ---- The owed wrap ----

    [Fact]
    public void ABackspaceFromTheOwedWrapCancelsItAndStays()
    {
        Emulator emulator = Fed(10, E + "[?7h" + E + "[?45h" + E + "[1;9H" + "ab\bX");

        Assert.Equal("aX", Row(emulator)[8..10]);
    }

    [Fact]
    public void AskingWhereTheCursorIsLeavesTheWrapOwed()
    {
        Emulator emulator = Fed(10, E + "[1;9H" + "ab" + E + "[6n" + "c");

        Assert.Equal((1, 1), Cursor(emulator));
    }

    [Fact]
    public void DecstrAndResetTurnBothModesOff()
    {
        Emulator emulator = Fed(10, E + "[?45h" + E + "[?1045h" + E + "[!p");

        Assert.False(emulator.ReverseWrap);
        Assert.False(emulator.ReverseWrapExtended);
    }

    private static (int Row, int Column) Cursor(Emulator emulator) =>
        (emulator.Buffer.CursorRow, emulator.Buffer.CursorColumn);

    private static Emulator Fed(int columns, string stream)
    {
        Emulator emulator = new(columns, 6, scrollback: 0);
        emulator.Feed(Encoding.ASCII.GetBytes(stream));

        return emulator;
    }

    private static string Row(Emulator emulator)
    {
        StringBuilder text = new();

        foreach (Cell cell in emulator.Buffer.Screen(0))
        {
            text.Append((char)cell.Codepoint);
        }

        return text.ToString();
    }
}
