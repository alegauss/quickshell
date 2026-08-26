using System.Text;
using Quickshell.Terminal;
using Xunit;

namespace Quickshell.Terminal.Tests;

/// <summary>
/// The two pieces of terminal behaviour most often left out, and the tab stops nobody keeps.
/// </summary>
public sealed class WrapAndMarginTests
{
    // ---- The falsification ----

    /// <summary>
    /// The design's own falsification, and the whole reason pending wrap exists: a line of exactly
    /// terminal width must not be followed by a blank line nothing sent.
    /// </summary>
    [Fact]
    public void ALineOfExactlyTerminalWidthGrowsNoBlankLineAfterIt()
    {
        Emulator emulator = new(5, 4, scrollback: 0);
        emulator.Feed(Encoding.UTF8.GetBytes("abcde"));

        // The cursor is still on the line it filled, owing a wrap it has not taken.
        Assert.Equal(0, emulator.Buffer.CursorRow);
        Assert.Equal(4, emulator.Buffer.CursorColumn);
        Assert.True(emulator.PendingWrap);

        emulator.Feed(Encoding.UTF8.GetBytes("\r\n"));
        emulator.Feed(Encoding.UTF8.GetBytes("next"));

        Assert.Equal("abcde", Row(emulator, 0));
        Assert.Equal("next ", Row(emulator, 1));
        Assert.True(Row(emulator, 2).Trim().Length == 0, "a blank line appeared that nothing sent");
    }

    [Fact]
    public void OnlyAPrintableCharacterTakesTheOwedWrap()
    {
        Emulator emulator = new(5, 4, scrollback: 0);
        emulator.Feed(Encoding.UTF8.GetBytes("abcdef"));

        Assert.Equal("abcde", Row(emulator, 0));
        Assert.Equal("f    ", Row(emulator, 1));
        Assert.True(emulator.Buffer.IsScreenWrapped(0));
    }

    [Theory]
    [InlineData("\r")]
    [InlineData("\b")]
    [InlineData("\u001b[C")]
    [InlineData("\u001b[D")]
    [InlineData("\u001b[K")]
    [InlineData("\u001b[1;1H")]
    public void AnythingButAPrintableCharacterCancelsTheOwedWrap(string sequence)
    {
        Emulator emulator = new(5, 4, scrollback: 0);
        emulator.Feed(Encoding.UTF8.GetBytes("abcde"));
        Assert.True(emulator.PendingWrap);

        emulator.Feed(Encoding.UTF8.GetBytes(sequence));

        Assert.False(emulator.PendingWrap);
        Assert.Equal(0, emulator.Buffer.CursorRow);
    }

    [Fact]
    public void WithWrappingOffTheLastColumnOverwritesItself()
    {
        Emulator emulator = new(5, 4, scrollback: 0);
        emulator.Feed(Encoding.UTF8.GetBytes("\u001b[?7labcdefgh"));

        Assert.False(emulator.AutoWrap);
        Assert.Equal("abcdh", Row(emulator, 0));
        Assert.Equal(0, emulator.Buffer.CursorRow);
    }

    [Fact]
    public void AWideCharacterWithOneColumnLeftWrapsRatherThanSplitting()
    {
        Emulator emulator = new(5, 4, scrollback: 0);
        emulator.Feed(Encoding.UTF8.GetBytes("abcd中"));

        Assert.Equal("abcd ", Row(emulator, 0));
        Assert.Equal('中', emulator.Buffer.Screen(1)[0].Codepoint);
        Assert.Equal(2, emulator.Buffer.Screen(1)[0].Width);
    }

    // ---- The scrolling region ----

    [Fact]
    public void ScrollingInsideARegionLeavesTheRowsOutsideItAlone()
    {
        Emulator emulator = new(4, 5, scrollback: 0);
        emulator.Feed(Encoding.UTF8.GetBytes("aaaa\nbbbb\ncccc\ndddd\neeee"));

        // Rows two to four become the region; the cursor homes into it.
        emulator.Feed(Encoding.UTF8.GetBytes("\u001b[2;4r"));
        Assert.Equal(1, emulator.MarginTop);
        Assert.Equal(3, emulator.MarginBottom);

        emulator.Feed(Encoding.UTF8.GetBytes("\u001b[4;1H\nxxxx"));

        Assert.Equal("aaaa", Row(emulator, 0));
        Assert.Equal("cccc", Row(emulator, 1));
        Assert.Equal("dddd", Row(emulator, 2));
        Assert.Equal("xxxx", Row(emulator, 3));
        Assert.Equal("eeee", Row(emulator, 4));
    }

    /// <summary>
    /// A line leaving a region inside the screen has not left the screen, so it must not join the
    /// history — otherwise a program's own scrolling interleaves with the shell's output behind it.
    /// </summary>
    [Fact]
    public void ScrollingARegionDoesNotReachTheScrollback()
    {
        Emulator emulator = new(4, 5, scrollback: 50);
        emulator.Feed(Encoding.UTF8.GetBytes("\u001b[2;4r\u001b[4;1H"));

        for (int line = 0; line < 10; line++)
        {
            emulator.Feed(Encoding.UTF8.GetBytes("\nrow"));
        }

        Assert.Equal(0, emulator.Buffer.ScrollbackLines);
    }

    [Fact]
    public void ScrollingTheWholeScreenStillReachesTheScrollback()
    {
        Emulator emulator = new(4, 3, scrollback: 50);
        emulator.Feed(Encoding.UTF8.GetBytes("\u001b[1;3r"));

        for (int line = 0; line < 5; line++)
        {
            emulator.Feed(Encoding.UTF8.GetBytes("row\n"));
        }

        Assert.True(emulator.RegionIsWholeScreen);
        Assert.True(emulator.Buffer.ScrollbackLines > 0);
    }

    [Fact]
    public void AReverseIndexAtTheTopMarginScrollsTheRegionDown()
    {
        Emulator emulator = new(4, 5, scrollback: 0);
        emulator.Feed(Encoding.UTF8.GetBytes("aaaa\nbbbb\ncccc\ndddd\neeee"));
        emulator.Feed(Encoding.UTF8.GetBytes("\u001b[2;4r\u001b[2;1H\u001bM"));

        Assert.Equal("aaaa", Row(emulator, 0));
        Assert.Equal("    ", Row(emulator, 1));
        Assert.Equal("bbbb", Row(emulator, 2));
        Assert.Equal("cccc", Row(emulator, 3));
        Assert.Equal("eeee", Row(emulator, 4));
    }

    [Fact]
    public void ARegionThatIsNotTwoRowsTallIsRefusedRatherThanHonoured()
    {
        Emulator emulator = new(4, 5, scrollback: 0);
        emulator.Feed(Encoding.UTF8.GetBytes("\u001b[3;3r"));

        Assert.True(emulator.RegionIsWholeScreen);
    }

    [Fact]
    public void SettingTheRegionHomesTheCursor()
    {
        Emulator emulator = new(10, 6, scrollback: 0);
        emulator.Feed(Encoding.UTF8.GetBytes("\u001b[5;8H\u001b[2;5r"));

        Assert.Equal(0, emulator.Buffer.CursorRow);
        Assert.Equal(0, emulator.Buffer.CursorColumn);
    }

    // ---- Origin mode ----

    [Fact]
    public void OriginModeMakesRowOneMeanTheTopMargin()
    {
        Emulator emulator = new(10, 8, scrollback: 0);
        emulator.Feed(Encoding.UTF8.GetBytes("\u001b[3;6r\u001b[?6h"));

        Assert.True(emulator.OriginMode);
        Assert.Equal(2, emulator.Buffer.CursorRow);

        emulator.Feed(Encoding.UTF8.GetBytes("\u001b[1;1H"));
        Assert.Equal(2, emulator.Buffer.CursorRow);

        emulator.Feed(Encoding.UTF8.GetBytes("\u001b[2;1H"));
        Assert.Equal(3, emulator.Buffer.CursorRow);
    }

    [Fact]
    public void OriginModeClampsInsideTheRegionRatherThanTheScreen()
    {
        Emulator emulator = new(10, 8, scrollback: 0);
        emulator.Feed(Encoding.UTF8.GetBytes("\u001b[3;6r\u001b[?6h\u001b[99;1H"));

        Assert.Equal(5, emulator.Buffer.CursorRow);
    }

    [Fact]
    public void WithoutOriginModeRowOneIsTheTopOfTheScreen()
    {
        Emulator emulator = new(10, 8, scrollback: 0);
        emulator.Feed(Encoding.UTF8.GetBytes("\u001b[3;6r\u001b[1;1H"));

        Assert.False(emulator.OriginMode);
        Assert.Equal(0, emulator.Buffer.CursorRow);
    }

    // ---- Tab stops ----

    [Fact]
    public void TheDefaultStopsAreEveryEighthColumn()
    {
        Emulator emulator = new(30, 3, scrollback: 0);

        Assert.True(emulator.IsTabStop(8));
        Assert.True(emulator.IsTabStop(16));
        Assert.False(emulator.IsTabStop(4));

        emulator.Feed(Encoding.UTF8.GetBytes("\t"));
        Assert.Equal(8, emulator.Buffer.CursorColumn);
    }

    /// <summary>
    /// A program that sets its own stops and then tabs is testing whether the set exists. A modulo
    /// -eight assumption passes every test written by somebody who also assumed eight.
    /// </summary>
    [Fact]
    public void AProgramsOwnStopsAreWhereATabLands()
    {
        Emulator emulator = new(30, 3, scrollback: 0);

        emulator.Feed(Encoding.UTF8.GetBytes("\u001b[3g"));       // clear every stop there is
        emulator.Feed(Encoding.UTF8.GetBytes("\u001b[1;4H\u001bH"));  // one at column three
        emulator.Feed(Encoding.UTF8.GetBytes("\u001b[1;12H\u001bH")); // one at column eleven
        emulator.Feed(Encoding.UTF8.GetBytes("\u001b[1;1H\t"));

        Assert.Equal(3, emulator.Buffer.CursorColumn);

        emulator.Feed(Encoding.UTF8.GetBytes("\t"));
        Assert.Equal(11, emulator.Buffer.CursorColumn);
    }

    [Fact]
    public void ClearingOneStopLeavesTheRest()
    {
        Emulator emulator = new(30, 3, scrollback: 0);

        emulator.Feed(Encoding.UTF8.GetBytes("\u001b[1;9H\u001b[g"));
        Assert.False(emulator.IsTabStop(8));
        Assert.True(emulator.IsTabStop(16));

        emulator.Feed(Encoding.UTF8.GetBytes("\u001b[1;1H\t"));
        Assert.Equal(16, emulator.Buffer.CursorColumn);
    }

    [Fact]
    public void ForwardAndBackTabsMoveWholeStops()
    {
        Emulator emulator = new(40, 3, scrollback: 0);

        emulator.Feed(Encoding.UTF8.GetBytes("\u001b[3I"));
        Assert.Equal(24, emulator.Buffer.CursorColumn);

        emulator.Feed(Encoding.UTF8.GetBytes("\u001b[2Z"));
        Assert.Equal(8, emulator.Buffer.CursorColumn);
    }

    [Fact]
    public void ATabAtTheEndStopsAtTheLastColumn()
    {
        Emulator emulator = new(10, 3, scrollback: 0);
        emulator.Feed(Encoding.UTF8.GetBytes("\u001b[1;10H\t"));

        Assert.Equal(9, emulator.Buffer.CursorColumn);
    }

    // ---- The alternate screen, as the mode a program actually sends ----

    [Fact]
    public void TheAlternateScreenIsEnteredAndLeftByItsMode()
    {
        Emulator emulator = new(10, 4, scrollback: 20);
        emulator.Feed(Encoding.UTF8.GetBytes("shell\u001b[3;5H"));

        emulator.Feed(Encoding.UTF8.GetBytes("\u001b[?1049h"));
        Assert.True(emulator.Screens.IsAlternate);

        emulator.Feed(Encoding.UTF8.GetBytes("full screen"));
        emulator.Feed(Encoding.UTF8.GetBytes("\u001b[?1049l"));

        Assert.False(emulator.Screens.IsAlternate);
        Assert.Equal("shell     ", Row(emulator, 0));
        Assert.Equal(2, emulator.Buffer.CursorRow);
        Assert.Equal(4, emulator.Buffer.CursorColumn);
    }

    [Fact]
    public void AProgramsRegionDoesNotFollowItBackToTheShell()
    {
        Emulator emulator = new(10, 6, scrollback: 20);

        emulator.Feed(Encoding.UTF8.GetBytes("\u001b[?1049h\u001b[2;4r"));
        Assert.False(emulator.RegionIsWholeScreen);

        emulator.Feed(Encoding.UTF8.GetBytes("\u001b[?1049l"));
        Assert.True(emulator.RegionIsWholeScreen);
    }

    [Fact]
    public void HidingTheCursorIsAModeTheHostCanSet()
    {
        Emulator emulator = new(10, 4, scrollback: 0);

        Assert.True(emulator.CursorVisible);
        emulator.Feed(Encoding.UTF8.GetBytes("\u001b[?25l"));
        Assert.False(emulator.CursorVisible);
        emulator.Feed(Encoding.UTF8.GetBytes("\u001b[?25h"));
        Assert.True(emulator.CursorVisible);
    }

    private static string Row(Emulator emulator, int row)
    {
        StringBuilder text = new();

        foreach (Cell cell in emulator.Buffer.Screen(row))
        {
            if (cell.Width != 0)
            {
                text.Append((char)cell.Codepoint);
            }
        }

        return text.ToString();
    }
}
