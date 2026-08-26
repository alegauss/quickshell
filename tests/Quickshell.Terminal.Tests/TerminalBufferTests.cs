using System.Runtime.InteropServices;
using Quickshell.Terminal;
using Xunit;

namespace Quickshell.Terminal.Tests;

/// <summary>
/// The ring, the window onto it, and the two screens.
/// </summary>
public sealed class TerminalBufferTests
{
    private static readonly Rgb White = new(255, 255, 255);
    private static readonly Rgb Black = new(0, 0, 0);

    // ---- The falsification: scrolling is bounded ----

    /// <summary>
    /// The design's own falsification: scrolling a full buffer by one line must not copy more than a
    /// bounded amount of memory. Two buffers of the same width and wildly different depth are
    /// scrolled the same number of times, and the cells written must be identical — which is only
    /// true if the depth never enters the cost.
    /// </summary>
    [Fact]
    public void ScrollingAFullBufferCostsTheSameHoweverDeepItIs()
    {
        TerminalBuffer shallow = Filled(columns: 80, rows: 24, scrollback: 10);
        TerminalBuffer deep = Filled(columns: 80, rows: 24, scrollback: 100_000);

        // Measured from here: filling a ten-line ring and a hundred-thousand-line one costs
        // different amounts by definition, and it is what a scroll costs once full that is the claim.
        long shallowStart = shallow.CellsWrittenByScrolling;
        long deepStart = deep.CellsWrittenByScrolling;

        for (int scroll = 0; scroll < 500; scroll++)
        {
            shallow.ScrollUp();
            deep.ScrollUp();
        }

        long shallowCost = shallow.CellsWrittenByScrolling - shallowStart;
        long deepCost = deep.CellsWrittenByScrolling - deepStart;

        Assert.Equal(shallowCost, deepCost);
        Assert.Equal(500L * 80, deepCost);
    }

    /// <summary>One scroll writes one row, and that is the whole claim in one number.</summary>
    [Fact]
    public void OneScrollWritesOneRow()
    {
        TerminalBuffer buffer = Filled(columns: 120, rows: 40, scrollback: 5000);
        long before = buffer.CellsWrittenByScrolling;

        buffer.ScrollUp();

        Assert.Equal(120, buffer.CellsWrittenByScrolling - before);
    }

    [Fact]
    public void ACellIsSixteenBytes()
    {
        Assert.Equal(Cell.Size, Marshal.SizeOf<Cell>());
        Assert.Equal(16, Marshal.SizeOf<Cell>());
    }

    // ---- The ring and its window ----

    [Fact]
    public void AFreshBufferIsAScreenOfBlanksWithNoScrollback()
    {
        TerminalBuffer buffer = new(80, 24, scrollback: 100);

        Assert.Equal(24, buffer.LineCount);
        Assert.Equal(0, buffer.ScrollbackLines);
        Assert.Equal(124, buffer.Capacity);
        Assert.True(buffer.Screen(0).ToArray().All(cell => cell.IsBlank));
    }

    [Fact]
    public void ScrollingMovesTheTopLineIntoTheScrollbackRatherThanLosingIt()
    {
        TerminalBuffer buffer = new(10, 3, scrollback: 5);
        buffer.Write(0, 0, Cell.For('a', White, Black));

        buffer.ScrollUp();

        Assert.Equal(1, buffer.ScrollbackLines);
        Assert.Equal('a', buffer.Line(0)[0].Codepoint);
        Assert.True(buffer.Screen(2).ToArray().All(cell => cell.IsBlank));
    }

    [Fact]
    public void AFullRingEvictsTheOldestLineByOverwritingIt()
    {
        TerminalBuffer buffer = new(4, 2, scrollback: 2);   // capacity 4

        for (int line = 0; line < 6; line++)
        {
            buffer.Write(buffer.Rows - 1, 0, Cell.For('0' + line, White, Black));
            buffer.ScrollUp();
        }

        Assert.Equal(buffer.Capacity, buffer.LineCount);

        // Six lines were written into a ring of four, so the oldest are gone and the rest are in
        // order - which is what says the origin moved rather than the contents.
        //
        // The trailing blank is the loop's own doing and not a lost line: each pass writes the
        // bottom row and then scrolls, so the last scroll leaves a fresh blank row below '5'.
        Assert.Equal("345 ", new string([.. Enumerable.Range(0, 4).Select(line => (char)buffer.Line(line)[0].Codepoint)]));
    }

    [Fact]
    public void EveryIndexGoesThroughTheOriginSoTheWindowStaysCorrectAfterWrapping()
    {
        TerminalBuffer buffer = new(4, 2, scrollback: 3);

        for (int scroll = 0; scroll < 20; scroll++)
        {
            buffer.Write(1, 0, Cell.For('a' + (scroll % 26), White, Black));
            buffer.ScrollUp();
        }

        // The screen's top row is always the line after the scrollback, however many times the ring
        // has been round.
        Assert.Equal(buffer.Line(buffer.ScrollbackLines).ToArray(), buffer.Screen(0).ToArray());
        Assert.Equal(buffer.LineCount - buffer.Rows, buffer.ScrollbackLines);
    }

    // ---- The wrapped flag ----

    [Fact]
    public void TheWrappedFlagSurvivesScrollingIntoTheScrollback()
    {
        TerminalBuffer buffer = new(10, 3, scrollback: 10);

        buffer.SetScreenWrapped(0, true);
        buffer.ScrollUp();

        Assert.True(buffer.IsWrapped(0), "the record that a logical line continued was lost on scroll");
    }

    [Fact]
    public void ANewBottomRowIsNeverWrapped()
    {
        TerminalBuffer buffer = new(10, 3, scrollback: 2);

        buffer.SetScreenWrapped(2, true);
        buffer.ScrollUp();

        Assert.False(buffer.IsScreenWrapped(2));
    }

    // ---- Regions ----

    [Fact]
    public void AScrollingRegionMovesOnlyTheRowsInsideIt()
    {
        TerminalBuffer buffer = new(4, 5, scrollback: 0);

        for (int row = 0; row < 5; row++)
        {
            buffer.Write(row, 0, Cell.For('0' + row, White, Black));
        }

        buffer.ScrollRegionUp(1, 3);

        Assert.Equal('0', buffer.Screen(0)[0].Codepoint);
        Assert.Equal('2', buffer.Screen(1)[0].Codepoint);
        Assert.Equal('3', buffer.Screen(2)[0].Codepoint);
        Assert.True(buffer.Screen(3)[0].IsBlank);
        Assert.Equal('4', buffer.Screen(4)[0].Codepoint);
    }

    [Fact]
    public void ClearingARunLeavesTheRestOfTheRowAlone()
    {
        TerminalBuffer buffer = new(6, 2, scrollback: 0);

        for (int column = 0; column < 6; column++)
        {
            buffer.Write(0, column, Cell.For('a' + column, White, Black));
        }

        buffer.Clear(0, 2, 3);

        Assert.Equal('a', buffer.Screen(0)[0].Codepoint);
        Assert.True(buffer.Screen(0)[2].IsBlank);
        Assert.True(buffer.Screen(0)[4].IsBlank);
        Assert.Equal('f', buffer.Screen(0)[5].Codepoint);
    }

    // ---- Clusters ----

    [Fact]
    public void AClusterTooBigForACellLivesInTheTableAndComesBackWhole()
    {
        TerminalBuffer buffer = new(4, 2, scrollback: 0);

        int index = buffer.InternCluster("é");
        Cell cell = Cell.ForCluster(index, White, Black);

        Assert.True(cell.IsCluster);
        Assert.Equal("é", buffer.TextOf(cell));
        Assert.Equal(1, buffer.ClusterCount);
    }

    [Fact]
    public void TheSameClusterTwiceIsOneEntry()
    {
        TerminalBuffer buffer = new(4, 2, scrollback: 0);

        Assert.Equal(buffer.InternCluster("\U0001F469‍\U0001F4BB"),
                     buffer.InternCluster("\U0001F469‍\U0001F4BB"));
        Assert.Equal(1, buffer.ClusterCount);
    }

    /// <summary>
    /// A host that generates distinct clusters forever must not be able to grow this without bound.
    /// Beyond the ceiling the table answers -1 and the cell keeps its base codepoint.
    /// </summary>
    [Fact]
    public void TheClusterTableStopsGrowingRatherThanLettingAHostExhaustMemory()
    {
        TerminalBuffer buffer = new(4, 2, scrollback: 0);

        for (int index = 0; index < TerminalBuffer.MaximumClusters + 100; index++)
        {
            buffer.InternCluster($"á{index}");
        }

        Assert.True(buffer.ClustersExhausted);
        Assert.Equal(TerminalBuffer.MaximumClusters, buffer.ClusterCount);
        Assert.Equal(-1, buffer.InternCluster("something new"));
    }

    [Fact]
    public void ACellHoldingOneCodepointNeedsNoTable()
    {
        TerminalBuffer buffer = new(4, 2, scrollback: 0);
        Cell cell = Cell.For('中', White, Black, width: 2);

        Assert.False(cell.IsCluster);
        Assert.Equal("中", buffer.TextOf(cell));
        Assert.Equal(2, cell.Width);
        Assert.Equal(0, buffer.ClusterCount);
    }

    // ---- Resize ----

    [Fact]
    public void ResizingKeepsTheNewestLinesAndTheCursorInsideTheScreen()
    {
        TerminalBuffer buffer = new(10, 3, scrollback: 10);

        for (int line = 0; line < 8; line++)
        {
            buffer.Write(2, 0, Cell.For('0' + line, White, Black));
            buffer.ScrollUp();
        }

        buffer.CursorRow = 2;
        buffer.CursorColumn = 9;
        buffer.Resize(4, 2);

        Assert.Equal(4, buffer.Columns);
        Assert.Equal(2, buffer.Rows);
        Assert.Equal(1, buffer.CursorRow);
        Assert.Equal(3, buffer.CursorColumn);
        Assert.Equal('7', buffer.Line(buffer.LineCount - 2)[0].Codepoint);
    }

    // ---- The two screens ----

    [Fact]
    public void TheAlternateScreenHasNoScrollbackAndLeavesThePrimaryUntouched()
    {
        Screens screens = new(10, 3, scrollback: 50);

        screens.Active.Write(0, 0, Cell.For('p', White, Black));
        screens.EnterAlternate();

        Assert.True(screens.IsAlternate);
        Assert.Equal(0, screens.Active.Capacity - screens.Active.Rows);

        screens.Active.Write(0, 0, Cell.For('a', White, Black));

        Assert.Equal('a', screens.Active.Screen(0)[0].Codepoint);
        Assert.Equal('p', screens.Primary.Screen(0)[0].Codepoint);
    }

    /// <summary>
    /// vim exiting and leaving the prompt exactly where it was. A client that saved the buffer and
    /// not the cursor puts the prompt in the wrong place every single time.
    /// </summary>
    [Fact]
    public void LeavingTheAlternateScreenPutsTheCursorBackWhereTheProgramFoundIt()
    {
        Screens screens = new(80, 24);

        screens.Primary.CursorRow = 7;
        screens.Primary.CursorColumn = 13;

        screens.EnterAlternate();
        screens.Active.CursorRow = 20;
        screens.Active.CursorColumn = 40;
        screens.LeaveAlternate();

        Assert.False(screens.IsAlternate);
        Assert.Equal(7, screens.Primary.CursorRow);
        Assert.Equal(13, screens.Primary.CursorColumn);
    }

    [Fact]
    public void EnteringTheAlternateScreenTwiceDoesNotSaveTheCursorAgain()
    {
        Screens screens = new(80, 24);

        screens.Primary.CursorRow = 5;
        screens.EnterAlternate();

        screens.Active.CursorRow = 20;
        screens.EnterAlternate();   // a program setting the mode it is already in
        screens.LeaveAlternate();

        Assert.Equal(5, screens.Primary.CursorRow);
        Assert.Equal(1, screens.Entries);
    }

    [Fact]
    public void ScrollingTheAlternateScreenDiscardsRatherThanRetains()
    {
        Screens screens = new(10, 3, scrollback: 50);
        screens.EnterAlternate();

        for (int scroll = 0; scroll < 10; scroll++)
        {
            screens.Active.ScrollUp();
        }

        Assert.Equal(0, screens.Active.ScrollbackLines);
        Assert.Equal(3, screens.Active.LineCount);
    }

    private static TerminalBuffer Filled(int columns, int rows, int scrollback)
    {
        TerminalBuffer buffer = new(columns, rows, scrollback);

        // Run the ring right round, so every later scroll is an eviction rather than a fill.
        while (buffer.LineCount < buffer.Capacity)
        {
            buffer.ScrollUp();
        }

        return buffer;
    }
}
