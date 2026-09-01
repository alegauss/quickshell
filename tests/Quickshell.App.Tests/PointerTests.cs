using System.Windows.Input;
using Quickshell.App;
using Quickshell.Render;
using Quickshell.Terminal;
using Xunit;

namespace Quickshell.App.Tests;

/// <summary>
/// The gestures, as arithmetic: a pixel becomes a cell, a run of clicks becomes a mode, and a drag
/// past the edge becomes a number of lines.
///
/// <para><b>None of this drives a window</b>, because none of it needs one. What the pane does with
/// a mouse message is three lines; what those three lines are given is every way this can be wrong,
/// and each of them is a number.</para>
/// </summary>
public sealed class PointerTests
{
    /// <summary>Deliberately not square: a transposed multiplication is invisible against a square.</summary>
    private static readonly CellMetrics Box = new(8, 17, 13);

    /// <summary>A pixel inside the pane lands on the cell it is drawn in.</summary>
    [Fact]
    public void APixelLandsOnTheCellItIsDrawnIn()
    {
        TerminalBuffer buffer = Screen();

        // Halfway into column 3, halfway down row 2.
        SelectionPoint at = Pointer.CellAt((3 * 8) + 4, (2 * 17) + 8, Box, buffer);

        Assert.Equal(3, at.Column);
        Assert.Equal(buffer.AbsoluteLine(2), at.Line);
    }

    /// <summary>
    /// A drag above the pane reaches the top row rather than staying on it.
    ///
    /// <para><b>The failure this catches is integer division truncating towards zero.</b> One pixel
    /// above the top divides to row 0 either way; sixteen pixels above divides to 0 as well, and a
    /// drag upward would then be a drag that never leaves the first row. Only a division towards
    /// negative infinity says how far above it is, which is what the scroll below reads.</para>
    /// </summary>
    [Fact]
    public void ADragAboveThePaneIsAboveItAndNotOnTheFirstRow()
    {
        Assert.Equal(-1, Pointer.ScrollFor(-1, 400, Box));
        Assert.Equal(-1, Pointer.ScrollFor(-16, 400, Box));
        Assert.Equal(-2, Pointer.ScrollFor(-18, 400, Box));

        // And inside it, nothing scrolls at all.
        Assert.Equal(0, Pointer.ScrollFor(0, 400, Box));
        Assert.Equal(0, Pointer.ScrollFor(399, 400, Box));

        // Below, one line per cell past the edge, starting at one.
        Assert.Equal(1, Pointer.ScrollFor(400, 400, Box));
        Assert.Equal(2, Pointer.ScrollFor(417, 400, Box));
    }

    /// <summary>
    /// A drag off the edge still names a cell, clamped to the screen.
    ///
    /// <para>A drag leaves the pane constantly — that is how a person selects past the bottom — and
    /// a point that refused would end the selection at the edge.</para>
    /// </summary>
    [Fact]
    public void APointOutsideThePaneStillNamesACell()
    {
        TerminalBuffer buffer = Screen();

        Assert.Equal(0, Pointer.CellAt(-500, -500, Box, buffer).Column);
        Assert.Equal(buffer.AbsoluteLine(0), Pointer.CellAt(-500, -500, Box, buffer).Line);

        Assert.Equal(buffer.Columns, Pointer.CellAt(100_000, 100_000, Box, buffer).Column);
        Assert.Equal(buffer.AbsoluteLine(buffer.Rows - 1),
                     Pointer.CellAt(100_000, 100_000, Box, buffer).Line);
    }

    /// <summary>
    /// One, two and three clicks in the same spot are character, word and line; a fourth starts over.
    /// </summary>
    [Fact]
    public void ARunOfClicksInOneSpotGrowsTheMode()
    {
        Pointer pointer = new() { Within = 500 };

        Assert.Equal(1, pointer.Clicked(40, 40, 1000));
        Assert.Equal(2, pointer.Clicked(40, 40, 1100));
        Assert.Equal(3, pointer.Clicked(40, 40, 1200));

        // A fourth is a first again, which is what every editor does.
        Assert.Equal(1, pointer.Clicked(40, 40, 1300));

        Assert.Equal(SelectionMode.Character, Pointer.ModeFor(1, ModifierKeys.None));
        Assert.Equal(SelectionMode.Word, Pointer.ModeFor(2, ModifierKeys.None));
        Assert.Equal(SelectionMode.Line, Pointer.ModeFor(3, ModifierKeys.None));
    }

    /// <summary>
    /// A run is broken by waiting, and survives a hand that is not perfectly still.
    ///
    /// <para>The tolerance is the half that matters. A double click broken by two pixels of hand
    /// movement selects one character, and the user tries again harder.</para>
    /// </summary>
    [Fact]
    public void ARunSurvivesAShakyHandAndNotAPause()
    {
        Pointer pointer = new() { Within = 500 };

        pointer.Clicked(40, 40, 1000);

        // Inside the slop, so still the same run.
        Assert.Equal(2, pointer.Clicked(40 + Pointer.Slop, 40 - Pointer.Slop, 1100));

        pointer.Rest();
        pointer.Clicked(40, 40, 2000);

        // Too far, so a new run.
        Assert.Equal(1, pointer.Clicked(40 + Pointer.Slop + 1, 40, 2100));

        pointer.Rest();
        pointer.Clicked(40, 40, 3000);

        // Too slow, likewise.
        Assert.Equal(1, pointer.Clicked(40, 40, 3501));
    }

    /// <summary>
    /// Alt takes a rectangle whatever the click count is, because that is a decision already made.
    /// </summary>
    [Fact]
    public void AltAlwaysMeansABlock()
    {
        Assert.Equal(SelectionMode.Block, Pointer.ModeFor(1, ModifierKeys.Alt));
        Assert.Equal(SelectionMode.Block, Pointer.ModeFor(2, ModifierKeys.Alt));
        Assert.Equal(SelectionMode.Block, Pointer.ModeFor(3, ModifierKeys.Alt));

        // And shift does not, because shift already means extend.
        Assert.Equal(SelectionMode.Character, Pointer.ModeFor(1, ModifierKeys.Shift));
    }

    private static TerminalBuffer Screen()
    {
        Emulator emulator = new(80, 25);

        emulator.Feed("hello there"u8);

        return emulator.Buffer;
    }
}
