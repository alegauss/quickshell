using System.Text;
using Quickshell.Terminal;
using Xunit;

namespace Quickshell.Terminal.Tests;

/// <summary>
/// What is selected, what it copies as, and what a paste is allowed to carry.
/// </summary>
public sealed class SelectionTests
{
    private const char Escape = (char)0x1B;
    private static readonly string Csi = new([Escape, '[']);

    // ---- The falsification ----

    /// <summary>
    /// The design's own falsification: <em>falsified when a wrapped line copies with a line break
    /// inside it</em>.
    ///
    /// <para>A user who copies a long path out of a terminal and finds a newline in the middle of it
    /// has been handed something that does not work.</para>
    /// </summary>
    [Fact]
    public void AWrappedLineCopiesWithNoBreakInsideIt()
    {
        // Forty columns and a path far longer, so the host's one line is three of the terminal's.
        const string Path = "/usr/local/share/quickshell/very/long/path/that/wraps/three/times/x.txt";

        Emulator emulator = new(40, 10);
        emulator.Feed(Encoding.UTF8.GetBytes(Path));

        Assert.True(emulator.Buffer.IsScreenWrapped(0), "the fixture did not actually wrap");

        Selection selection = All(emulator);

        Assert.Equal(Path, Copy(emulator, selection));
        Assert.DoesNotContain('\n', Copy(emulator, selection));
    }

    /// <summary>And two logical lines still copy as two, so the break that belongs is kept.</summary>
    [Fact]
    public void TwoLinesCopyAsTwo()
    {
        Emulator emulator = new(40, 10);
        emulator.Feed(Encoding.UTF8.GetBytes("first line\r\nsecond line"));

        Assert.Equal("first line\nsecond line", Copy(emulator, All(emulator)));
    }

    // ---- Padding is not text ----

    /// <summary>
    /// A terminal pads rows the user did not type, so a copy does not hand that padding back.
    /// </summary>
    [Fact]
    public void TheTerminalsOwnPaddingIsNotCopied()
    {
        Emulator emulator = new(40, 10);
        emulator.Feed(Encoding.UTF8.GetBytes("short\r\nalso short"));

        string copied = Copy(emulator, All(emulator));

        Assert.Equal("short\nalso short", copied);
        Assert.DoesNotContain("  ", copied, StringComparison.Ordinal);
    }

    /// <summary>
    /// But a space in the last column of a row that continues is a space the host printed — the one
    /// between two words the wrap fell between. QS23 found this in reflow and it is the same rule.
    /// </summary>
    [Fact]
    public void ASpaceAtAWrapPointSurvivesTheCopy()
    {
        // Ten columns: "word word2" is exactly ten, so the space lands mid-line and the wrap follows.
        Emulator emulator = new(10, 6);
        emulator.Feed(Encoding.UTF8.GetBytes("hello therexy"));

        Assert.True(emulator.Buffer.IsScreenWrapped(0));
        Assert.Equal("hello therexy", Copy(emulator, All(emulator)));
    }

    // ---- The modes a gesture means ----

    [Fact]
    public void ACharacterSelectionTakesExactlyWhatItCovered()
    {
        Emulator emulator = new(40, 4);
        emulator.Feed(Encoding.UTF8.GetBytes("abcdefghij"));

        Selection selection = new();
        selection.Begin(emulator.Buffer, At(emulator, 0, 2), SelectionMode.Character);
        selection.Extend(emulator.Buffer, At(emulator, 0, 6));

        Assert.Equal("cdef", Copy(emulator, selection));
    }

    /// <summary>A double click grows to the whole word, and a path is one word — a gesture that
    /// stopped at the first slash is one nobody wanted.</summary>
    [Fact]
    public void AWordSelectionGrowsToTheWholeWord()
    {
        Emulator emulator = new(60, 4);
        emulator.Feed(Encoding.UTF8.GetBytes("run /usr/local/bin/tool --flag"));

        Selection selection = new();
        selection.Begin(emulator.Buffer, At(emulator, 0, 8), SelectionMode.Word);

        Assert.Equal("/usr/local/bin/tool", Copy(emulator, selection));
    }

    [Fact]
    public void AWordSelectionOnASpaceTakesTheSpace()
    {
        Emulator emulator = new(60, 4);
        emulator.Feed(Encoding.UTF8.GetBytes("one two"));

        Selection selection = new();
        selection.Begin(emulator.Buffer, At(emulator, 0, 3), SelectionMode.Word);

        Assert.Equal(" ", Copy(emulator, selection));
    }

    /// <summary>A triple click takes the logical line, which is all three rows of a wrapped one.</summary>
    [Fact]
    public void ALineSelectionTakesTheWholeLogicalLine()
    {
        Emulator emulator = new(20, 8);
        emulator.Feed(Encoding.UTF8.GetBytes("aaaaaaaaaabbbbbbbbbbcccc\r\nnext"));

        Selection selection = new();

        // Started on the second row of the wrapped line, which is the middle of what the user sees.
        selection.Begin(emulator.Buffer, At(emulator, 1, 3), SelectionMode.Line);

        Assert.Equal("aaaaaaaaaabbbbbbbbbbcccc", Copy(emulator, selection));
    }

    /// <summary>
    /// Block selection, which is the only way to copy one column out of tabular output.
    /// </summary>
    [Fact]
    public void ABlockSelectionTakesARectangle()
    {
        Emulator emulator = new(30, 6);
        emulator.Feed(Encoding.UTF8.GetBytes("alpha   111\r\nbeta    222\r\ngamma   333"));

        Selection selection = new();
        selection.Begin(emulator.Buffer, At(emulator, 0, 8), SelectionMode.Block);
        selection.Extend(emulator.Buffer, At(emulator, 2, 11));

        Assert.Equal("111\n222\n333", Copy(emulator, selection));
    }

    // ---- Ends that survive the screen moving ----

    /// <summary>
    /// The ends are absolute line numbers, so output arriving under a selection does not slide it up
    /// the screen onto text it was never about.
    /// </summary>
    [Fact]
    public void ASelectionStaysOnItsTextWhileTheScreenScrolls()
    {
        Emulator emulator = new(40, 4);
        emulator.Feed(Encoding.UTF8.GetBytes("target line\r\n"));

        Selection selection = new();
        selection.Begin(emulator.Buffer, At(emulator, 0, 0), SelectionMode.Line);

        // Four more lines, so what was row zero is now off the top of the screen.
        emulator.Feed(Encoding.UTF8.GetBytes("a\r\nb\r\nc\r\nd\r\n"));

        Assert.Equal("target line", Copy(emulator, selection));
    }

    [Fact]
    public void ASelectionOfLinesTheRingHasEvictedCopiesWhatIsLeft()
    {
        Emulator emulator = new(20, 3, scrollback: 2);

        emulator.Feed(Encoding.UTF8.GetBytes("gone\r\n"));

        Selection selection = new();
        selection.Begin(emulator.Buffer, At(emulator, 0, 0), SelectionMode.Character);
        selection.Extend(emulator.Buffer, new SelectionPoint(emulator.Buffer.AbsoluteLine(2), 20));

        for (int line = 0; line < 20; line++)
        {
            emulator.Feed(Encoding.UTF8.GetBytes($"line {line}\r\n"));
        }

        // The lines it named are gone; what comes back is empty rather than an exception.
        Assert.Equal(string.Empty, Copy(emulator, selection));
    }

    // ---- Housekeeping ----

    [Fact]
    public void ClearingLeavesNothingSelected()
    {
        Emulator emulator = new(20, 4);
        emulator.Feed("text"u8);

        Selection selection = All(emulator);
        selection.Clear();

        Assert.False(selection.IsActive);
        Assert.Equal(string.Empty, Copy(emulator, selection));
        Assert.False(selection.Contains(0, 0));
    }

    [Fact]
    public void DraggingBackwardsSelectsTheOtherWayRound()
    {
        Emulator emulator = new(40, 4);
        emulator.Feed(Encoding.UTF8.GetBytes("abcdefghij"));

        Selection selection = new();
        selection.Begin(emulator.Buffer, At(emulator, 0, 6), SelectionMode.Character);
        selection.Extend(emulator.Buffer, At(emulator, 0, 2));

        Assert.Equal("cdef", Copy(emulator, selection));
    }

    [Fact]
    public void TheRendererIsToldWhichCellsAreIn()
    {
        Emulator emulator = new(40, 4);
        emulator.Feed(Encoding.UTF8.GetBytes("abcdefghij"));

        Selection selection = new();
        selection.Begin(emulator.Buffer, At(emulator, 0, 2), SelectionMode.Character);
        selection.Extend(emulator.Buffer, At(emulator, 0, 6));

        long line = emulator.Buffer.AbsoluteLine(0);

        Assert.False(selection.Contains(line, 1));
        Assert.True(selection.Contains(line, 2));
        Assert.True(selection.Contains(line, 5));
        Assert.False(selection.Contains(line, 6));
    }

    [Fact]
    public void MeasuringAgreesWithCopying()
    {
        Emulator emulator = new(40, 6);
        emulator.Feed(Encoding.UTF8.GetBytes("one\r\ntwo\r\nthree"));

        Selection selection = All(emulator);

        Assert.Equal(Copy(emulator, selection).Length, selection.MeasureCopy(emulator.Buffer));
    }

    [Fact]
    public void ADestinationTooSmallIsRefusedRatherThanTruncated()
    {
        Emulator emulator = new(40, 4);
        emulator.Feed(Encoding.UTF8.GetBytes("plenty of text here"));

        Selection selection = All(emulator);

        Assert.Equal(-1, selection.CopyTo(emulator.Buffer, new char[2]));
    }

    /// <summary>A wide character copies once, not once per cell it occupies.</summary>
    [Fact]
    public void AWideCharacterCopiesOnce()
    {
        Emulator emulator = new(20, 4);
        emulator.Feed(Encoding.UTF8.GetBytes("日本語"));

        Assert.Equal("日本語", Copy(emulator, All(emulator)));
    }

    // ---- The paste, which is the security half ----

    /// <summary>
    /// Text pasted into a shell runs the moment it contains a newline, so a paste with one has to be
    /// shown to the user — unless the program has said it will handle that itself.
    /// </summary>
    [Fact]
    public void APasteWithANewlineIsConfirmedUnlessTheProgramBracketsIt()
    {
        Assert.True(Paste.NeedsConfirming("rm -rf /\nyes", bracketed: false));
        Assert.False(Paste.NeedsConfirming("rm -rf /\nyes", bracketed: true));
        Assert.False(Paste.NeedsConfirming("just some text", bracketed: false));
    }

    /// <summary>
    /// Nothing legitimate pastes an escape sequence, and one that could is one that could set a mode
    /// or answer a query on the user's behalf.
    /// </summary>
    [Fact]
    public void ControlCharactersAreRemovedFromAPaste()
    {
        string hostile = "safe" + Csi + "2J" + Escape + "]0;title" + (char)0x07 + "more";

        Assert.Equal("safe[2J]0;titlemore", Clean(hostile));
    }

    /// <summary>Tab is text and survives, because a paste of indented code is an ordinary paste.</summary>
    [Fact]
    public void TabSurvivesAPaste()
    {
        Assert.Equal("if x:\r\tpass", Clean("if x:\n\tpass"));
    }

    /// <summary>
    /// Every line ending becomes a carriage return, which is what Enter sends. A paste that kept a
    /// Windows clipboard's pairs would run every line twice on some shells.
    /// </summary>
    [Fact]
    public void EveryLineEndingBecomesOneCarriageReturn()
    {
        Assert.Equal("one\rtwo\rthree\r", Clean("one\r\ntwo\nthree\r"));
    }

    [Fact]
    public void MeasuringAPasteAgreesWithCleaningIt()
    {
        const string Text = "line one\r\nline\ttwo\nline three";

        Assert.Equal(Clean(Text).Length, Paste.MeasureClean(Text));
    }

    [Fact]
    public void ADestinationTooSmallForAPasteIsRefused()
    {
        Assert.Equal(-1, Paste.Clean("more than two", new char[2]));
    }

    /// <summary>The mode the program sets, which is the one that decides which answer applies.</summary>
    [Fact]
    public void TheProgramTurnsBracketedPasteOnAndOff()
    {
        Emulator emulator = new(20, 4);

        Assert.False(emulator.BracketedPaste);

        emulator.Feed(Encoding.UTF8.GetBytes(Csi + "?2004h"));

        Assert.True(emulator.BracketedPaste);

        emulator.Feed(Encoding.UTF8.GetBytes(Csi + "?2004l"));

        Assert.False(emulator.BracketedPaste);
    }

    [Fact]
    public void AResetTurnsBracketedPasteOff()
    {
        Emulator emulator = new(20, 4);

        emulator.Feed(Encoding.UTF8.GetBytes(Csi + "?2004h"));
        emulator.Feed([(byte)Escape, (byte)'c']);

        Assert.False(emulator.BracketedPaste);
    }

    /// <summary>The markers are the ones a program watches for, built from a number rather than
    /// typed — QS100.</summary>
    [Fact]
    public void TheBracketsAreTheSequencesAProgramWatchesFor()
    {
        Assert.Equal(Csi + "200~", Paste.Start);
        Assert.Equal(Csi + "201~", Paste.Finish);
    }

    // ---- Helpers ----

    private static Selection All(Emulator emulator)
    {
        Selection selection = new();

        selection.Begin(emulator.Buffer, new SelectionPoint(First(emulator), 0), SelectionMode.Character);
        selection.Extend(
            emulator.Buffer,
            new SelectionPoint(First(emulator) + emulator.Buffer.LineCount - 1, emulator.Buffer.Columns));

        return selection;
    }

    private static long First(Emulator emulator) =>
        emulator.Buffer.TopLine - emulator.Buffer.ScrollbackLines;

    private static SelectionPoint At(Emulator emulator, int row, int column) =>
        new(emulator.Buffer.AbsoluteLine(row), column);

    private static string Copy(Emulator emulator, Selection selection)
    {
        int length = selection.MeasureCopy(emulator.Buffer);
        char[] buffer = new char[length];

        return new string(buffer, 0, selection.CopyTo(emulator.Buffer, buffer));
    }

    private static string Clean(string text)
    {
        char[] buffer = new char[text.Length];

        return new string(buffer, 0, Paste.Clean(text, buffer));
    }
}
