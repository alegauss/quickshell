using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Windows.Automation.Provider;
using System.Windows.Automation.Text;
using Quickshell.App;
using Quickshell.Terminal;
using Xunit;

namespace Quickshell.App.Tests;

/// <summary>
/// What assistive technology finds when it looks at a texture.
///
/// <para>The ranges are exercised directly rather than through a screen reader, because a screen
/// reader is a person's ear and not a test harness — but they are the real provider objects a reader
/// would be handed, calling the real interface, over a real buffer.</para>
/// </summary>
public sealed class TerminalAutomationTests
{
    /// <summary>The break a line unit carries, spelled rather than written as a literal byte.</summary>
    private const string Newline = "\n";

    private static TerminalDocument Printed(params string[] lines)
    {
        Emulator emulator = new(80, 25);

        emulator.Feed(Encoding.UTF8.GetBytes(string.Join("\r\n", lines)));

        return new TerminalDocument(emulator.Buffer);
    }

    /// <summary>
    /// The whole document, as the interface a screen reader is handed.
    ///
    /// <para>Typed as the interface deliberately: these tests exist to exercise what a reader calls,
    /// and asserting against the concrete class would be asserting against the half a reader cannot
    /// reach.</para>
    /// </summary>
    [SuppressMessage("Performance", "CA1859:Use concrete types when possible",
                     Justification = "The interface is what a screen reader is handed, and asserting "
                                   + "through it is what these tests are for.")]
    private static ITextRangeProvider Whole(TerminalDocument document) =>
        new TerminalTextRange(document, 0, document.Length);

    // ---- The falsification, through the interface a reader actually calls ----

    /// <summary>
    /// A line of output that is on screen is readable through <c>ITextRangeProvider.GetText</c>,
    /// which is the call a screen reader makes.
    /// </summary>
    [Fact]
    public void ALineOnScreenIsReadableThroughTheProvider()
    {
        ITextRangeProvider range = Whole(Printed("quickshell $ uname -a", "Linux host 6.8.0"));

        string read = range.GetText(-1);

        Assert.Contains("quickshell $ uname -a", read, StringComparison.Ordinal);
        Assert.Contains("Linux host 6.8.0", read, StringComparison.Ordinal);
    }

    /// <summary>And a reader that asks for a bounded amount gets that much and no more.</summary>
    [Fact]
    public void AReaderAskingForPartOfItGetsThatMuch()
    {
        ITextRangeProvider range = Whole(Printed("hello world"));

        Assert.Equal("hello", range.GetText(5));

        // The whole document is the whole screen, blank rows included — a terminal is twenty-five
        // rows whether or not anybody printed into them.
        Assert.StartsWith("hello world", range.GetText(-1), StringComparison.Ordinal);
    }

    // ---- Moving, which is how a reader gets through it ----

    [Fact]
    public void ARangeExpandsToTheLineItIsOn()
    {
        TerminalDocument document = Printed("first line", "second line", "third line");

        TerminalTextRange range = new(document, 3, 3);

        range.ExpandToEnclosingUnit(TextUnit.Line);

        // The break is part of the line, which is what UI Automation means by the unit: a reader
        // asking for a line and getting one without its ending cannot tell where it stopped.
        Assert.Equal("first line" + Newline, range.GetText(-1));
    }

    [Fact]
    public void ARangeExpandsToTheWordItIsOn()
    {
        TerminalDocument document = Printed("the quick brown fox");

        TerminalTextRange range = new(document, 5, 5);

        range.ExpandToEnclosingUnit(TextUnit.Word);

        Assert.Contains("quick", range.GetText(-1), StringComparison.Ordinal);
    }

    /// <summary>Moving by line is a reader stepping down the output, and it lands on whole lines.</summary>
    [Fact]
    public void MovingByLineStepsThroughTheOutput()
    {
        TerminalDocument document = Printed("first line", "second line", "third line");

        TerminalTextRange range = new(document, 0, 0);

        range.ExpandToEnclosingUnit(TextUnit.Line);

        Assert.Equal("first line" + Newline, range.GetText(-1));

        Assert.Equal(1, range.Move(TextUnit.Line, 1));
        Assert.Equal("second line" + Newline, range.GetText(-1));

        Assert.Equal(1, range.Move(TextUnit.Line, 1));
        Assert.Equal("third line" + Newline, range.GetText(-1));
    }

    /// <summary>
    /// Moving past the end answers zero rather than pretending. A reader told a range moved when it
    /// did not will keep asking, forever.
    /// </summary>
    [Fact]
    public void MovingPastTheEndAnswersZero()
    {
        TerminalDocument document = Printed("only line");

        TerminalTextRange range = new(document, document.Length, document.Length);

        Assert.Equal(0, range.Move(TextUnit.Line, 5));
        Assert.Equal(0, range.MoveEndpointByUnit(TextPatternRangeEndpoint.End, TextUnit.Character, 99));
    }

    [Fact]
    public void EndpointsCompareAndRangesClone()
    {
        TerminalDocument document = Printed("something to read");

        TerminalTextRange first = new(document, 0, 4);
        ITextRangeProvider copy = first.Clone();

        Assert.True(first.Compare(copy));
        Assert.Equal(0, first.CompareEndpoints(TextPatternRangeEndpoint.Start, copy,
                                               TextPatternRangeEndpoint.Start));

        TerminalTextRange later = new(document, 5, 9);

        Assert.False(first.Compare(later));
        Assert.True(first.CompareEndpoints(TextPatternRangeEndpoint.Start, later,
                                           TextPatternRangeEndpoint.Start) < 0);
    }

    /// <summary>Finding text is what a reader's own search is built on.</summary>
    [Fact]
    public void FindingTextAnswersWithTheRangeItIsIn()
    {
        TerminalDocument document = Printed("error: cannot open file");

        ITextRangeProvider? found = Whole(document).FindText("cannot", backward: false, ignoreCase: false);

        Assert.NotNull(found);
        Assert.Equal("cannot", found.GetText(-1));

        Assert.Null(Whole(document).FindText("nowhere", backward: false, ignoreCase: false));
    }

    /// <summary>
    /// Bounding rectangles are empty and that is the honest answer: the pane is a texture and there
    /// is no map from an offset to a rectangle. A highlight over the wrong words is worse than none.
    /// </summary>
    [Fact]
    public void BoundingRectanglesAreEmptyRatherThanGuessed()
    {
        Assert.Empty(Whole(Printed("anything")).GetBoundingRectangles());
        Assert.Empty(Whole(Printed("anything")).GetChildren());
    }

    // ---- Telling a reader, at a rate it can use ----

    /// <summary>
    /// The throttle, through the peer. A screenful of output is one announcement, not a thousand —
    /// and the last of a burst is still announced, so a reader is never left believing the screen is
    /// as it was.
    /// </summary>
    [Fact]
    public void AFloodIsOneAnnouncementAndTheLastOneStillArrives()
    {
        TextChanges changes = new(TimeSpan.FromMilliseconds(500));

        Assert.True(changes.Changed(TimeSpan.Zero));

        for (int row = 0; row < 2_000; row++)
        {
            Assert.False(changes.Changed(TimeSpan.FromMilliseconds(row % 300)));
        }

        Assert.Equal(1, changes.Announced);
        Assert.True(changes.Waiting);
        Assert.True(changes.Due(TimeSpan.FromSeconds(2)));
        Assert.Equal(2, changes.Announced);
    }
}
