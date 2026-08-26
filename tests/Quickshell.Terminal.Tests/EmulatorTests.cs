using System.Text;
using Quickshell.Terminal;
using Xunit;

namespace Quickshell.Terminal.Tests;

/// <summary>
/// The sequences a shell prompt uses before the user has typed anything.
/// </summary>
public sealed class EmulatorTests
{
    // ---- The falsification: an absent parameter and a zero parameter are one instruction ----

    /// <summary>
    /// The design's own falsification. Every movement below is written three ways — absent, zero and
    /// one — and all three must land in the same place. A client that reads the blank as zero and
    /// zero as zero drifts by a row or a column every time a program is terse, which is every
    /// program.
    /// </summary>
    [Theory]
    [InlineData("A")]   // up
    [InlineData("B")]   // down
    [InlineData("C")]   // forward
    [InlineData("D")]   // back
    [InlineData("E")]   // next line
    [InlineData("F")]   // previous line
    [InlineData("G")]   // column absolute
    [InlineData("d")]   // line absolute
    [InlineData("L")]   // insert line
    [InlineData("M")]   // delete line
    [InlineData("@")]   // insert character
    [InlineData("P")]   // delete character
    [InlineData("X")]   // erase character
    [InlineData("S")]   // scroll up
    [InlineData("T")]   // scroll down
    public void AnAbsentParameterAndAZeroParameterAreTheSameInstruction(string final)
    {
        (int Row, int Column) absent = After($"\u001b[{final}");
        (int Row, int Column) zero = After($"\u001b[0{final}");
        (int Row, int Column) one = After($"\u001b[1{final}");

        Assert.Equal(one, absent);
        Assert.Equal(one, zero);
    }

    /// <summary>The same claim for the two-parameter forms, in every combination of blank and zero.</summary>
    [Theory]
    [InlineData("H")]
    [InlineData("f")]
    public void ACursorPositionTreatsBlankAndZeroAsOne(string final)
    {
        Assert.Equal((0, 0), After($"\u001b[{final}"));
        Assert.Equal((0, 0), After($"\u001b[0;0{final}"));
        Assert.Equal((0, 0), After($"\u001b[1;1{final}"));
        Assert.Equal((0, 4), After($"\u001b[;5{final}"));
        Assert.Equal((4, 0), After($"\u001b[5;{final}"));
    }

    private static (int Row, int Column) After(string sequence)
    {
        Emulator emulator = new(20, 10, scrollback: 0);

        // Somewhere in the middle, so a movement in any direction has room to be wrong in.
        emulator.Feed(Encoding.UTF8.GetBytes("\u001b[5;10H"));
        emulator.Feed(Encoding.UTF8.GetBytes(sequence));

        return (emulator.Buffer.CursorRow, emulator.Buffer.CursorColumn);
    }

    // ---- Clamping ----

    [Fact]
    public void AMovementClampsAtTheMarginRatherThanWrapping()
    {
        Emulator emulator = new(20, 10, scrollback: 0);

        emulator.Feed(Encoding.UTF8.GetBytes("\u001b[2;3H\u001b[10A"));
        Assert.Equal(0, emulator.Buffer.CursorRow);

        emulator.Feed(Encoding.UTF8.GetBytes("\u001b[99B"));
        Assert.Equal(9, emulator.Buffer.CursorRow);

        emulator.Feed(Encoding.UTF8.GetBytes("\u001b[99C"));
        Assert.Equal(19, emulator.Buffer.CursorColumn);

        emulator.Feed(Encoding.UTF8.GetBytes("\u001b[99D"));
        Assert.Equal(0, emulator.Buffer.CursorColumn);
    }

    [Fact]
    public void APositionBeyondTheScreenLandsOnItsEdge()
    {
        Emulator emulator = new(20, 10, scrollback: 0);
        emulator.Feed(Encoding.UTF8.GetBytes("\u001b[999;999H"));

        Assert.Equal(9, emulator.Buffer.CursorRow);
        Assert.Equal(19, emulator.Buffer.CursorColumn);
    }

    // ---- Printing ----

    [Fact]
    public void TextLandsWhereTheCursorIsAndMovesIt()
    {
        Emulator emulator = Fed("hello");

        Assert.Equal("hello", Row(emulator, 0)[..5]);
        Assert.Equal(5, emulator.Buffer.CursorColumn);
    }

    [Fact]
    public void ALineThatRunsOffTheRightIsRecordedAsWrapped()
    {
        Emulator emulator = new(5, 4, scrollback: 0);
        emulator.Feed(Encoding.UTF8.GetBytes("abcdefg"));

        Assert.True(emulator.Buffer.IsScreenWrapped(0),
                    "the record that one logical line continued into the next was not kept");
        Assert.Equal("abcde", Row(emulator, 0));
        Assert.Equal("fg", Row(emulator, 1)[..2]);
    }

    [Fact]
    public void ACarriageReturnAndLineFeedAreSeparateThings()
    {
        Emulator emulator = Fed("ab\rc");
        Assert.Equal("cb", Row(emulator, 0)[..2]);

        emulator = Fed("ab\nc");
        Assert.Equal("ab", Row(emulator, 0)[..2]);
        Assert.Equal("c", Row(emulator, 1)[..1]);
    }

    [Fact]
    public void PrintingPastTheLastRowScrollsRatherThanOverwriting()
    {
        Emulator emulator = new(6, 3, scrollback: 10);
        emulator.Feed(Encoding.UTF8.GetBytes("one\ntwo\nthree\nfour"));

        Assert.Equal(1, emulator.Buffer.ScrollbackLines);
        Assert.Equal("one", Text(emulator.Buffer.Line(0))[..3]);
        Assert.Equal("four", Row(emulator, 2)[..4]);
    }

    // ---- Erasing ----

    [Theory]
    [InlineData("\u001b[K", "ab   ")]
    [InlineData("\u001b[0K", "ab   ")]
    [InlineData("\u001b[1K", "   de")]
    [InlineData("\u001b[2K", "     ")]
    public void EraseLineTakesTheThreeFormsItIsDefinedWith(string sequence, string expected)
    {
        Emulator emulator = new(5, 3, scrollback: 0);
        emulator.Feed(Encoding.UTF8.GetBytes("abcde\u001b[1;3H" + sequence));

        Assert.Equal(expected, Row(emulator, 0));
    }

    [Fact]
    public void EraseDisplayBelowLeavesWhatIsAboveTheCursor()
    {
        Emulator emulator = new(4, 3, scrollback: 0);
        emulator.Feed(Encoding.UTF8.GetBytes("aaaa\nbbbb\ncccc\u001b[2;3H\u001b[J"));

        Assert.Equal("aaaa", Row(emulator, 0));
        Assert.Equal("bb  ", Row(emulator, 1));
        Assert.Equal("    ", Row(emulator, 2));
    }

    [Fact]
    public void EraseDisplayAboveLeavesWhatIsBelowTheCursor()
    {
        Emulator emulator = new(4, 3, scrollback: 0);
        emulator.Feed(Encoding.UTF8.GetBytes("aaaa\nbbbb\ncccc\u001b[2;3H\u001b[1J"));

        Assert.Equal("    ", Row(emulator, 0));
        Assert.Equal("   b", Row(emulator, 1));
        Assert.Equal("cccc", Row(emulator, 2));
    }

    [Fact]
    public void ErasingTheScrollbackLeavesTheScreenAlone()
    {
        Emulator emulator = new(4, 2, scrollback: 20);
        emulator.Feed(Encoding.UTF8.GetBytes("aa\nbb\ncc\ndd"));

        Assert.True(emulator.Buffer.ScrollbackLines > 0);

        emulator.Feed(Encoding.UTF8.GetBytes("\u001b[3J"));

        Assert.Equal(0, emulator.Buffer.ScrollbackLines);
        Assert.Equal(2, emulator.Buffer.LineCount);
    }

    // ---- Editing ----

    [Fact]
    public void InsertingCharactersPushesTheRestOfTheRowRight()
    {
        Emulator emulator = new(6, 2, scrollback: 0);
        emulator.Feed(Encoding.UTF8.GetBytes("abcdef\u001b[1;3H\u001b[2@"));

        Assert.Equal("ab  cd", Row(emulator, 0));
    }

    [Fact]
    public void DeletingCharactersPullsTheRestOfTheRowLeft()
    {
        Emulator emulator = new(6, 2, scrollback: 0);
        emulator.Feed(Encoding.UTF8.GetBytes("abcdef\u001b[1;3H\u001b[2P"));

        Assert.Equal("abef  ", Row(emulator, 0));
    }

    [Fact]
    public void ErasingCharactersBlanksInPlaceWithoutMovingAnything()
    {
        Emulator emulator = new(6, 2, scrollback: 0);
        emulator.Feed(Encoding.UTF8.GetBytes("abcdef\u001b[1;3H\u001b[2X"));

        Assert.Equal("ab  ef", Row(emulator, 0));
    }

    [Fact]
    public void InsertingAndDeletingLinesMoveTheRowsBelowTheCursor()
    {
        Emulator emulator = new(4, 4, scrollback: 0);
        emulator.Feed(Encoding.UTF8.GetBytes("aaaa\nbbbb\ncccc\ndddd\u001b[2;1H\u001b[L"));

        Assert.Equal("aaaa", Row(emulator, 0));
        Assert.Equal("    ", Row(emulator, 1));
        Assert.Equal("bbbb", Row(emulator, 2));

        emulator.Feed(Encoding.UTF8.GetBytes("\u001b[M"));
        Assert.Equal("bbbb", Row(emulator, 1));
    }

    [Fact]
    public void RepeatWritesTheLastCharacterAgain()
    {
        Emulator emulator = Fed("-\u001b[4b");

        Assert.Equal("-----", Row(emulator, 0)[..5]);
    }

    [Fact]
    public void AReverseIndexAtTheTopScrollsTheScreenDown()
    {
        Emulator emulator = new(4, 3, scrollback: 0);
        emulator.Feed(Encoding.UTF8.GetBytes("aaaa\nbbbb\u001b[1;1H\u001bM"));

        Assert.Equal("    ", Row(emulator, 0));
        Assert.Equal("aaaa", Row(emulator, 1));
    }

    // ---- Saving and restoring ----

    /// <summary>
    /// The whole state, not the position. A program that restores expects its colours back, and one
    /// that gets only the position paints the rest of its screen in whatever was left set.
    /// </summary>
    [Fact]
    public void SaveAndRestoreCarryTheAttributesAsWellAsThePosition()
    {
        Emulator emulator = new(20, 10, scrollback: 0);

        emulator.Feed(Encoding.UTF8.GetBytes("\u001b[3;7H\u001b[1;31m\u001b7"));
        emulator.Feed(Encoding.UTF8.GetBytes("\u001b[9;19H\u001b[0m\u001b[32m"));

        Assert.Equal(Colour.Indexed(2), emulator.Pen.Foreground);

        emulator.Feed(Encoding.UTF8.GetBytes("\u001b8"));

        Assert.Equal(2, emulator.Buffer.CursorRow);
        Assert.Equal(6, emulator.Buffer.CursorColumn);
        Assert.Equal(Colour.Indexed(1), emulator.Pen.Foreground);
        Assert.True(emulator.Pen.Flags.HasFlag(CellFlags.Bold));
    }

    // ---- SGR ----

    [Fact]
    public void TheBaseSixteenAreIndicesRatherThanColours()
    {
        Emulator emulator = Fed("\u001b[31m");
        Assert.Equal(Colour.Indexed(1), emulator.Pen.Foreground);

        emulator = Fed("\u001b[91m");
        Assert.Equal(Colour.Indexed(9), emulator.Pen.Foreground);

        emulator = Fed("\u001b[44m");
        Assert.Equal(Colour.Indexed(4), emulator.Pen.Background);
    }

    /// <summary>
    /// Default is a state, not a colour. A cell that stored the theme's current value would stop
    /// matching the theme the moment it changed, and old scrollback is where that shows.
    /// </summary>
    [Fact]
    public void DefaultIsItsOwnStateAndFollowsTheThemeAfterTheFact()
    {
        Emulator emulator = Fed("plain");
        Cell cell = emulator.Buffer.Screen(0)[0];

        Assert.True(cell.Foreground.IsDefault);
        Assert.Equal(ColourKind.Default, cell.Background.Kind);

        Rgb before = emulator.Palette.Resolve(cell.Foreground);
        emulator.Palette.Foreground = new Rgb(1, 2, 3);

        Assert.NotEqual(before, emulator.Palette.Resolve(cell.Foreground));
        Assert.Equal(new Rgb(1, 2, 3), emulator.Palette.Resolve(cell.Foreground));
    }

    [Fact]
    public void APaletteIndexAlsoFollowsTheThemeRatherThanBeingResolvedOnWrite()
    {
        Emulator emulator = Fed("\u001b[31mred");
        Cell cell = emulator.Buffer.Screen(0)[0];

        Assert.Equal(ColourKind.Indexed, cell.Foreground.Kind);

        emulator.Palette[1] = new Rgb(9, 9, 9);
        Assert.Equal(new Rgb(9, 9, 9), emulator.Palette.Resolve(cell.Foreground));
    }

    /// <summary>Both spellings are real, and a modern host emits the second.</summary>
    [Theory]
    [InlineData("\u001b[38;2;255;0;0m")]
    [InlineData("\u001b[38:2:255:0:0m")]
    [InlineData("\u001b[38:2::255:0:0m")]
    public void TwentyFourBitColourArrivesInEitherSpelling(string sequence)
    {
        Emulator emulator = Fed(sequence);

        Assert.Equal(ColourKind.Direct, emulator.Pen.Foreground.Kind);
        Assert.Equal(new Rgb(255, 0, 0), emulator.Pen.Foreground.Rgb);
    }

    [Theory]
    [InlineData("\u001b[38;5;196m")]
    [InlineData("\u001b[38:5:196m")]
    public void TheColourCubeArrivesInEitherSpelling(string sequence)
    {
        Emulator emulator = Fed(sequence);

        Assert.Equal(Colour.Indexed(196), emulator.Pen.Foreground);
    }

    [Fact]
    public void ABackgroundInEitherSpellingLandsInTheBackground()
    {
        Assert.Equal(Colour.Direct(1, 2, 3), Fed("\u001b[48;2;1;2;3m").Pen.Background);
        Assert.Equal(Colour.Indexed(200), Fed("\u001b[48:5:200m").Pen.Background);
    }

    /// <summary>
    /// Each attribute has its own reset, and programs turn one off expecting the others to survive.
    /// </summary>
    [Fact]
    public void TurningOneAttributeOffLeavesTheOthersAlone()
    {
        Emulator emulator = Fed("\u001b[1;3;4;7;9m");

        Assert.True(emulator.Pen.Flags.HasFlag(CellFlags.Bold));
        Assert.True(emulator.Pen.Flags.HasFlag(CellFlags.Slant));
        Assert.True(emulator.Pen.Flags.HasFlag(CellFlags.Inverse));
        Assert.True(emulator.Pen.Flags.HasFlag(CellFlags.Strike));
        Assert.Equal(UnderlineStyle.Single, emulator.Pen.Underline);

        emulator.Feed(Encoding.UTF8.GetBytes("\u001b[24m"));

        Assert.Equal(UnderlineStyle.None, emulator.Pen.Underline);
        Assert.True(emulator.Pen.Flags.HasFlag(CellFlags.Bold));
        Assert.True(emulator.Pen.Flags.HasFlag(CellFlags.Slant));
        Assert.True(emulator.Pen.Flags.HasFlag(CellFlags.Inverse));
        Assert.True(emulator.Pen.Flags.HasFlag(CellFlags.Strike));
    }

    [Fact]
    public void EndingBoldAlsoEndsFaintBecauseOneCodeEndsBoth()
    {
        Emulator emulator = Fed("\u001b[1;2;4m\u001b[22m");

        Assert.False(emulator.Pen.Flags.HasFlag(CellFlags.Bold));
        Assert.False(emulator.Pen.Flags.HasFlag(CellFlags.Faint));
        Assert.Equal(UnderlineStyle.Single, emulator.Pen.Underline);
    }

    [Fact]
    public void ResetPutsEverythingBackAtOnce()
    {
        Emulator emulator = Fed("\u001b[1;4;31;44m\u001b[m");

        Assert.Equal(Pen.Default, emulator.Pen);
    }

    [Fact]
    public void SeveralAttributesInOneSequenceAllApply()
    {
        Emulator emulator = Fed("\u001b[1;38;5;42;48;2;7;8;9;4m");

        Assert.True(emulator.Pen.Flags.HasFlag(CellFlags.Bold));
        Assert.Equal(Colour.Indexed(42), emulator.Pen.Foreground);
        Assert.Equal(Colour.Direct(7, 8, 9), emulator.Pen.Background);
        Assert.Equal(UnderlineStyle.Single, emulator.Pen.Underline);
    }

    [Fact]
    public void WhatIsWrittenCarriesThePenThatWasSet()
    {
        Emulator emulator = Fed("\u001b[1;4;38;2;10;20;30max");
        Cell cell = emulator.Buffer.Screen(0)[0];

        Assert.Equal(Colour.Direct(10, 20, 30), cell.Foreground);
        Assert.True(cell.Flags.HasFlag(CellFlags.Bold));
        Assert.Equal(UnderlineStyle.Single, cell.Underline);
    }

    /// <summary>
    /// The model keeps attributes the renderer has no way to draw. Dropping one because the picture
    /// cannot show it would lose a fact about the session to a limitation of the display.
    /// </summary>
    [Fact]
    public void TheModelKeepsAttributesTheRendererCannotDraw()
    {
        Emulator emulator = Fed("\u001b[2;5;8mx");
        Cell cell = emulator.Buffer.Screen(0)[0];

        Assert.True(cell.Flags.HasFlag(CellFlags.Faint));
        Assert.True(cell.Flags.HasFlag(CellFlags.Blink));
        Assert.True(cell.Flags.HasFlag(CellFlags.Conceal));
    }

    // ---- Nothing a host sends may be an error ----

    [Fact]
    public void AnUnknownSequenceIsCountedRatherThanThrown()
    {
        Emulator emulator = Fed("\u001b[99999;99999`\u001b[?2004h\u001bZa");

        Assert.True(emulator.Unhandled > 0);
        Assert.Contains("a", Row(emulator, 0), StringComparison.Ordinal);
    }

    private static Emulator Fed(string stream)
    {
        Emulator emulator = new(20, 5, scrollback: 10);
        emulator.Feed(Encoding.UTF8.GetBytes(stream));

        return emulator;
    }

    private static string Row(Emulator emulator, int row) => Text(emulator.Buffer.Screen(row));

    private static string Text(ReadOnlySpan<Cell> cells)
    {
        StringBuilder text = new();

        foreach (Cell cell in cells)
        {
            if (cell.Width == 0)
            {
                continue;
            }

            text.Append((char)cell.Codepoint);
        }

        return text.ToString();
    }
}
