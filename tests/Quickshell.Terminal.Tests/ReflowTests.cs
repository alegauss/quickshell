using System.Text;
using Quickshell.Terminal;
using Xunit;

namespace Quickshell.Terminal.Tests;

/// <summary>
/// Reflow: the behaviour terminals most reliably get wrong, tested without a window because that is
/// the whole reason it is a function.
/// </summary>
public sealed class ReflowTests
{
    private const char Escape = (char)0x1B;
    private static readonly string Csi = new([Escape, '[']);

    /// <summary>Widths to run every property against, narrow and wide of the original eighty.</summary>
    public static TheoryData<int> Widths => [4, 7, 13, 20, 33, 40, 79, 80, 81, 120, 200];

    // ---- The falsification ----

    /// <summary>
    /// The design's own falsification: <em>falsified when narrowing and widening again does not
    /// restore the original line breaks</em>.
    /// </summary>
    [Theory]
    [MemberData(nameof(Widths))]
    public void NarrowingAndWideningAgainRestoresTheOriginalRows(int width)
    {
        Emulator emulator = Fed(Paragraphs);
        string[] before = Rows(emulator);
        bool[] wraps = Wraps(emulator);

        emulator.Resize(width, 24);
        emulator.Resize(80, 24);

        Assert.Equal(before, Rows(emulator));
        Assert.Equal(wraps, Wraps(emulator));
    }

    /// <summary>And through a whole sequence of them, which is what a window drag actually is.</summary>
    [Fact]
    public void ADragThroughManyWidthsStillComesBack()
    {
        Emulator emulator = Fed(Paragraphs);
        string[] before = Rows(emulator);

        foreach (int width in new[] { 70, 61, 55, 48, 37, 41, 52, 66, 75, 90, 110, 83 })
        {
            emulator.Resize(width, 24);
        }

        emulator.Resize(80, 24);

        Assert.Equal(before, Rows(emulator));
    }

    // ---- The cursor is on the same character ----

    /// <summary>
    /// The task's own criterion: <em>the character under the cursor is invariant across any resize
    /// sequence</em>. A cursor left at the same coordinates is one that has silently moved to a
    /// different letter, and a shell's line editing is then wrong about where the user is.
    /// </summary>
    [Theory]
    [MemberData(nameof(Widths))]
    public void TheCharacterUnderTheCursorSurvivesAResize(int width)
    {
        Emulator emulator = Tall();

        // Onto the "h" of "than", in a paragraph long enough to have wrapped at eighty.
        emulator.Feed(Encoding.UTF8.GetBytes(Csi + "1;45H"));

        Assert.Equal("h", Under(emulator));

        emulator.Resize(width, 60);

        Assert.Equal("h", Under(emulator));
    }

    [Fact]
    public void TheCharacterUnderTheCursorSurvivesAWholeSequence()
    {
        Emulator emulator = Tall();
        emulator.Feed(Encoding.UTF8.GetBytes(Csi + "1;45H"));

        foreach (int width in new[] { 33, 120, 7, 200, 41, 80, 13 })
        {
            emulator.Resize(width, 60);

            Assert.Equal("h", Under(emulator));
        }
    }

    /// <summary>
    /// The one case the invariant cannot hold, said plainly rather than left to be discovered.
    ///
    /// <para>The visible screen is the last rows of the ring and there is no separate viewport yet, so
    /// a cursor parked well above a lot of text can end up on a line that narrowing has pushed off the
    /// top. Its line is right and the screen cannot show it, so it clamps to the top row. No text is
    /// lost, and a host that has just been told the new size is about to reposition the cursor
    /// anyway. When scrollback viewing lands, this clamp becomes a viewport that follows the
    /// cursor.</para>
    /// </summary>
    [Fact]
    public void ACursorPushedOffTheTopClampsRatherThanLying()
    {
        Emulator emulator = Fed(Paragraphs);
        emulator.Feed(Encoding.UTF8.GetBytes(Csi + "1;45H"));
        string all = Logical(emulator);

        emulator.Resize(7, 24);

        Assert.True(emulator.Buffer.ScrollbackLines > 0);
        Assert.Equal(0, emulator.Buffer.CursorRow);
        Assert.Equal(all, Logical(emulator));
    }

    /// <summary>
    /// Where a cursor usually is: just after the last character of the line being edited. That is
    /// where it has to still be, or a prompt redraws over the text the user typed.
    /// </summary>
    [Theory]
    [MemberData(nameof(Widths))]
    public void ACursorJustAfterAPromptStaysJustAfterIt(int width)
    {
        Emulator emulator = Fed("$ git commit --amend");

        Assert.Equal(20, emulator.Buffer.CursorColumn);

        emulator.Resize(width, 24);

        // The cursor is at the end of the text: on the last row the line produced, with nothing but
        // blanks after it. Not "column twenty", which some widths cannot express — a row exactly full
        // has no column after its last, and a terminal answers that by staying on the last one.
        ReadOnlySpan<Cell> row = emulator.Buffer.Screen(emulator.Buffer.CursorRow);

        Assert.False(emulator.Buffer.IsScreenWrapped(emulator.Buffer.CursorRow));

        for (int column = emulator.Buffer.CursorColumn + 1; column < width; column++)
        {
            Assert.True(row[column].IsBlank);
        }

        Assert.Equal("$ git commit --amend", Logical(emulator));
    }

    // ---- What is not reflowed ----

    /// <summary>
    /// The alternate screen is not reflowed and does not need to be: a full-screen program has just
    /// been told the new size and is about to redraw all of it.
    /// </summary>
    [Fact]
    public void TheAlternateScreenIsNotReflowedButReplaced()
    {
        Emulator emulator = Fed("shell output");
        emulator.Feed(Encoding.UTF8.GetBytes(Csi + "?1049h"));
        emulator.Feed(Encoding.UTF8.GetBytes(new string('z', 200)));

        emulator.Resize(40, 24);

        Assert.True(emulator.Screens.IsAlternate);
        Assert.All(
            Enumerable.Range(0, 24),
            row => Assert.True(emulator.Buffer.Screen(row).ToArray().All(cell => cell.IsBlank)));

        // And the primary behind it kept its text, re-wrapped.
        emulator.Feed(Encoding.UTF8.GetBytes(Csi + "?1049l"));

        Assert.StartsWith("shell output", Row(emulator, 0), StringComparison.Ordinal);
    }

    // ---- Wide characters against the new margin ----

    /// <summary>
    /// A wide character with one column left cannot be split, so it moves down whole and the row it
    /// leaves is a column short.
    /// </summary>
    [Fact]
    public void AWideCharacterThatNoLongerFitsMovesDownWhole()
    {
        // Nine columns of Latin then a CJK pair: at width ten the pair sits at columns nine and ten,
        // and at width nine there is one column left and it cannot go there.
        Emulator emulator = new(20, 4);
        emulator.Feed(Encoding.UTF8.GetBytes("abcdefghi一jkl"));

        emulator.Resize(10, 4);

        Assert.Equal("abcdefghi", Row(emulator, 0).TrimEnd());
        Assert.True(emulator.Buffer.IsScreenWrapped(0));
        Assert.StartsWith("一", Row(emulator, 1), StringComparison.Ordinal);

        // The pair is a pair wherever it landed: a cell of width two beside one of width zero.
        Assert.Equal(2, emulator.Buffer.Screen(1)[0].Width);
        Assert.Equal(0, emulator.Buffer.Screen(1)[1].Width);
    }

    /// <summary>A terminal narrower than the character is. It keeps the column it has rather than
    /// being dropped, because losing text is the one thing reflow may not do.</summary>
    [Fact]
    public void AWideCharacterInAOneColumnTerminalIsKeptRatherThanLost()
    {
        Emulator emulator = new(20, 4);
        emulator.Feed(Encoding.UTF8.GetBytes("a一b"));

        emulator.Resize(1, 4);

        Assert.Equal("a", Row(emulator, 0));
        Assert.Equal("一", Row(emulator, 1));
        Assert.Equal("b", Row(emulator, 2));
    }

    /// <summary>CJK round-trips too, which is the case the column arithmetic is easiest to get wrong
    /// in.</summary>
    [Theory]
    [MemberData(nameof(Widths))]
    public void WideCharactersRoundTrip(int width)
    {
        Emulator emulator = new(80, 24);
        emulator.Feed(Encoding.UTF8.GetBytes(
            "一二三ascii四五六 more text 七八九\r\nsecond line\r\n"));

        string[] before = Rows(emulator);

        emulator.Resize(width, 24);
        emulator.Resize(80, 24);

        Assert.Equal(before, Rows(emulator));
    }

    // ---- Text is not lost ----

    /// <summary>
    /// Reflowing must not lose a character. The whole text of the buffer, read as one string with the
    /// wrap points removed, is the same text at every width.
    /// </summary>
    [Theory]
    [MemberData(nameof(Widths))]
    public void NoCharacterIsLostAtAnyWidth(int width)
    {
        Emulator emulator = Fed(Paragraphs);
        string before = Logical(emulator);

        emulator.Resize(width, 24);

        Assert.Equal(before, Logical(emulator));
    }

    /// <summary>A space the host coloured is content, not padding, and does not get trimmed with the
    /// blanks.</summary>
    [Fact]
    public void AColouredSpaceIsNotTrimmedAsPadding()
    {
        Emulator emulator = new(20, 4);
        emulator.Feed(Encoding.UTF8.GetBytes("ab" + Csi + "41m" + "   " + Csi + "0m"));

        emulator.Resize(10, 4);

        Assert.Equal(ColourKind.Indexed, emulator.Buffer.Screen(0)[4].Background.Kind);
        Assert.Equal(1, emulator.Buffer.Screen(0)[4].Background.Index);
        Assert.True(emulator.Buffer.Screen(0)[5].IsBlank);
    }

    // ---- Structure ----

    /// <summary>Narrowing makes more lines out of the same text, and the newest are the ones the ring
    /// keeps.</summary>
    [Fact]
    public void NarrowingMakesMoreLinesAndKeepsTheNewest()
    {
        // A four-row screen, so the line count is the content's and not the screen's minimum.
        Emulator emulator = new(80, 4);
        emulator.Feed(Encoding.UTF8.GetBytes(Paragraphs));

        int before = emulator.Buffer.LineCount;

        emulator.Resize(20, 4);

        Assert.True(emulator.Buffer.LineCount > before);
        Assert.Contains("second", Logical(emulator), StringComparison.Ordinal);
    }

    /// <summary>A height-only change needs no re-wrap at all, so it is not one: the rows come through
    /// untouched.</summary>
    [Fact]
    public void AHeightOnlyChangeLeavesTheRowsAlone()
    {
        Emulator emulator = Fed(Paragraphs);
        string[] before = Rows(emulator);

        emulator.Resize(80, 30);

        Assert.Equal(before, Rows(emulator)[..before.Length]);
    }

    /// <summary>Reflowing an empty buffer is a resize and not a crash.</summary>
    [Theory]
    [MemberData(nameof(Widths))]
    public void AnEmptyBufferReflowsToNothing(int width)
    {
        Emulator emulator = new(80, 24);

        emulator.Resize(width, 24);

        Assert.Equal(width, emulator.Buffer.Columns);
        Assert.Equal(0, emulator.Buffer.CursorRow);
        Assert.Equal(0, emulator.Buffer.CursorColumn);
    }

    /// <summary>Blank lines the host sent are content and survive: a paragraph break is a line.</summary>
    [Fact]
    public void BlankLinesTheHostSentAreKept()
    {
        Emulator emulator = Fed("one\r\n\r\n\r\ntwo");

        emulator.Resize(20, 24);

        Assert.Equal("one", Row(emulator, 0).TrimEnd());
        Assert.Equal(string.Empty, Row(emulator, 1).TrimEnd());
        Assert.Equal(string.Empty, Row(emulator, 2).TrimEnd());
        Assert.Equal("two", Row(emulator, 3).TrimEnd());
    }

    /// <summary>The scrollback is re-wrapped with the screen, or there is a discontinuity exactly at
    /// the boundary the user is looking at.</summary>
    [Fact]
    public void TheScrollbackIsReflowedWithTheScreen()
    {
        Emulator emulator = new(80, 4);
        emulator.Feed(Encoding.UTF8.GetBytes(
            string.Join(string.Empty, Enumerable.Range(0, 20).Select(line => new string((char)('a' + (line % 26)), 60) + "\r\n"))));

        Assert.True(emulator.Buffer.ScrollbackLines > 0);

        emulator.Resize(30, 4);

        // Every retained line is either full to the margin and wrapped, or the tail of one.
        for (int line = 0; line < emulator.Buffer.LineCount; line++)
        {
            if (emulator.Buffer.IsWrapped(line))
            {
                Assert.DoesNotContain(true, emulator.Buffer.Line(line).ToArray().Select(cell => cell.IsBlank));
            }
        }
    }

    // ---- Helpers ----

    /// <summary>Text long enough to have wrapped at eighty, with a short line and a blank between.</summary>
    private const string Paragraphs =
        "the first paragraph is deliberately longer than eighty columns so that it has to wrap at "
        + "least twice before it ends, and it ends here.\r\n"
        + "short\r\n"
        + "\r\n"
        + "a second paragraph, also long enough to wrap once against an eighty column terminal, ends "
        + "on this line.\r\n"
        + "$ ";

    private static Emulator Fed(string stream)
    {
        Emulator emulator = new(80, 24);
        emulator.Feed(Encoding.UTF8.GetBytes(stream));

        return emulator;
    }

    /// <summary>
    /// One paragraph on a screen tall enough to hold it even at four columns wide, so that the
    /// cursor's line is on the screen at every width the theory runs — see
    /// <see cref="ACursorPushedOffTheTopClampsRatherThanLying"/> for the case where it is not.
    /// </summary>
    private static Emulator Tall()
    {
        Emulator emulator = new(80, 60);
        emulator.Feed(Encoding.UTF8.GetBytes(
            "the first paragraph is deliberately longer than eighty columns so it wraps once.\r\n"
            + "second"));

        return emulator;
    }

    /// <summary>Every retained line as text, trailing blanks kept off so a width change is visible.</summary>
    private static string[] Rows(Emulator emulator) =>
        [.. Enumerable.Range(0, emulator.Buffer.LineCount).Select(line => Text(emulator, emulator.Buffer.Line(line)).TrimEnd())];

    private static bool[] Wraps(Emulator emulator) =>
        [.. Enumerable.Range(0, emulator.Buffer.LineCount).Select(emulator.Buffer.IsWrapped)];

    /// <summary>
    /// The whole buffer as logical lines, which is what must not change at any width.
    ///
    /// <para>Trailing empty lines are dropped: a buffer always holds at least a screenful, so how
    /// many blank ones sit under the last thing the host printed is a fact about the window's height
    /// and the text's width, not about the text.</para>
    /// </summary>
    private static string Logical(Emulator emulator)
    {
        StringBuilder text = new();

        for (int line = 0; line < emulator.Buffer.LineCount; line++)
        {
            string row = Text(emulator, emulator.Buffer.Line(line));

            // A wrapped row is not trimmed. Its last column can hold a real space — the one between
            // two words that the wrap fell between — and trimming it here would have the helper lose
            // the character it is checking nothing lost.
            if (emulator.Buffer.IsWrapped(line))
            {
                text.Append(row);
            }
            else
            {
                text.Append(row.TrimEnd()).Append('\n');
            }
        }

        return text.ToString().TrimEnd('\n');
    }

    private static string Row(Emulator emulator, int row) => Text(emulator, emulator.Buffer.Screen(row));

    private static string Text(Emulator emulator, ReadOnlySpan<Cell> cells)
    {
        StringBuilder text = new();

        foreach (Cell cell in cells)
        {
            if (cell.Width != 0)
            {
                text.Append(emulator.Buffer.TextOf(cell));
            }
        }

        return text.ToString();
    }

    /// <summary>The character the cursor is on, stepping back over the trailing half of a pair.</summary>
    private static string Under(Emulator emulator)
    {
        int column = emulator.Buffer.CursorColumn;
        ReadOnlySpan<Cell> row = emulator.Buffer.Screen(emulator.Buffer.CursorRow);

        while (column > 0 && row[column].Width == 0)
        {
            column--;
        }

        return emulator.Buffer.TextOf(row[column]);
    }
}
