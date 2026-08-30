using System.Text;
using Quickshell.Terminal;
using Xunit;

namespace Quickshell.Terminal.Tests;

/// <summary>
/// The buffer as something other than a camera can read.
///
/// <para>The falsification is the first test: a line of output that is on screen must be readable.
/// Everything else is what makes that useful — the scrollback being part of the same document, and
/// a reader being able to move through it in the units a person hears.</para>
/// </summary>
public sealed class TerminalDocumentTests
{
    /// <summary>A buffer with these lines printed into it, in order.</summary>
    private static Emulator Printed(int columns, int rows, params string[] lines)
    {
        Emulator emulator = new(columns, rows);

        emulator.Feed(Encoding.UTF8.GetBytes(string.Join("\r\n", lines)));

        return emulator;
    }

    // ---- The falsification ----

    /// <summary>
    /// The line's own falsification, word for word: a line of output that is on screen can be read.
    /// </summary>
    [Fact]
    public void ALineOfOutputOnScreenCanBeRead()
    {
        Emulator emulator = Printed(80, 25, "quickshell $ uname -a", "Linux host 6.8.0");

        TerminalDocument document = new(emulator.Buffer);

        string all = document.Text(0, document.Length);

        Assert.Contains("quickshell $ uname -a", all, StringComparison.Ordinal);
        Assert.Contains("Linux host 6.8.0", all, StringComparison.Ordinal);
    }

    /// <summary>
    /// Scrollback and screen are one document. A reader that could only see the visible rows could
    /// not review what just scrolled past, which is most of what a history is for.
    /// </summary>
    [Fact]
    public void WhatScrolledPastIsInTheSameDocumentAsWhatIsOnScreen()
    {
        // More lines than rows, so the first ones are only in the scrollback.
        string[] lines = [.. Enumerable.Range(0, 40).Select(number => $"line {number}")];

        Emulator emulator = Printed(80, 10, lines);
        TerminalDocument document = new(emulator.Buffer);

        string all = document.Text(0, document.Length);

        Assert.Contains("line 0", all, StringComparison.Ordinal);
        Assert.Contains("line 39", all, StringComparison.Ordinal);
        Assert.True(document.Lines > emulator.Buffer.Rows,
                    "the document is only as tall as the screen, so the scrollback is unreachable");
    }

    /// <summary>Trailing padding is not text: a terminal pads every row and a reader should not hear it.</summary>
    [Fact]
    public void TheBlanksATerminalPadsWithAreNotRead()
    {
        Emulator emulator = Printed(80, 5, "short");

        TerminalDocument document = new(emulator.Buffer);

        Assert.Equal("short", document.LineAt(0));
        Assert.DoesNotContain("short   ", document.Text(0, document.Length), StringComparison.Ordinal);
    }

    // ---- The caret, which is what lets a reader follow a prompt ----

    /// <summary>The cursor is where the caret is, and it moves as somebody types.</summary>
    [Fact]
    public void TheCaretIsWhereTheCursorIs()
    {
        Emulator emulator = new(80, 25);

        emulator.Feed("quickshell $ "u8);

        TerminalDocument document = new(emulator.Buffer);

        int before = document.Caret;

        Assert.Equal("quickshell $ ".Length, before);

        emulator.Feed("ls"u8);

        Assert.Equal(before + 2, document.Caret);
    }

    /// <summary>And it stays inside the line it is on, whatever the buffer says about a column.</summary>
    [Fact]
    public void TheCaretStaysInsideItsOwnLine()
    {
        Emulator emulator = Printed(80, 25, "one", "two");

        TerminalDocument document = new(emulator.Buffer);

        Assert.InRange(document.Caret, 0, document.Length);
        Assert.Equal(document.LineOf(document.Caret), document.LineOf(document.Caret));
    }

    // ---- Moving through it, in the units a person hears ----

    [Fact]
    public void MovingByCharacterMovesOne()
    {
        TerminalDocument document = new(Printed(80, 5, "abc").Buffer);

        Assert.Equal(1, document.Move(0, TextStep.Character, 1));
        Assert.Equal(3, document.Move(0, TextStep.Character, 3));
        Assert.Equal(0, document.Move(3, TextStep.Character, -3));
    }

    /// <summary>And stops at the ends rather than running past them.</summary>
    [Fact]
    public void MovingPastAnEndStopsThere()
    {
        TerminalDocument document = new(Printed(80, 5, "abc").Buffer);

        Assert.Equal(0, document.Move(0, TextStep.Character, -10));
        Assert.Equal(document.Length, document.Move(0, TextStep.Character, 100_000));
    }

    /// <summary>A word is what a person hears as one: a run of non-space.</summary>
    [Fact]
    public void MovingByWordLandsOnTheNextOne()
    {
        TerminalDocument document = new(Printed(80, 5, "the quick brown fox").Buffer);

        int atQuick = document.Move(0, TextStep.Word, 1);
        int atBrown = document.Move(0, TextStep.Word, 2);

        Assert.Equal("quick brown fox", document.Text(atQuick, 15));
        Assert.Equal("brown fox", document.Text(atBrown, 9));

        // And back again, to where it started.
        Assert.Equal(atQuick, document.Move(atBrown, TextStep.Word, -1));
    }

    [Fact]
    public void MovingByLineLandsOnTheStartOfTheNext()
    {
        TerminalDocument document = new(Printed(80, 10, "one", "two", "three").Buffer);

        int second = document.Move(0, TextStep.Line, 1);

        Assert.Equal("two", document.LineAt(second));
        Assert.Equal(second, document.StartOfLine(document.LineOf(second)));

        Assert.Equal("three", document.LineAt(document.Move(0, TextStep.Line, 2)));
        Assert.Equal("one", document.LineAt(document.Move(second, TextStep.Line, -1)));
    }

    // ---- The index, which is what keeps a reader cheap ----

    /// <summary>
    /// A reader asks for ranges constantly, so the buffer is walked once per change and not once per
    /// question. The generation is what says a walk is needed.
    /// </summary>
    [Fact]
    public void TheDocumentIsIndexedOncePerChangeRatherThanOncePerQuestion()
    {
        Emulator emulator = Printed(80, 25, "hello");
        TerminalDocument document = new(emulator.Buffer);

        long generation = emulator.Buffer.Generation;

        for (int asked = 0; asked < 100; asked++)
        {
            _ = document.Length;
            _ = document.Caret;
            _ = document.LineAt(0);
        }

        Assert.Equal(generation, emulator.Buffer.Generation);

        // And it follows the buffer when the buffer moves.
        emulator.Feed(" again"u8);

        Assert.Contains("hello again", document.Text(0, document.Length), StringComparison.Ordinal);
    }

    // ---- Telling a reader, without telling it too much ----

    /// <summary>
    /// The failure this throttle exists for: a screenful of output is one announcement, not a
    /// thousand. A reader given a thousand is still reading the first second a minute later.
    /// </summary>
    [Fact]
    public void AFloodOfOutputIsOneAnnouncement()
    {
        TextChanges changes = new(TimeSpan.FromMilliseconds(500));

        Assert.True(changes.Changed(TimeSpan.Zero));

        for (int row = 0; row < 5_000; row++)
        {
            Assert.False(changes.Changed(TimeSpan.FromMilliseconds(row % 400)));
        }

        Assert.Equal(1, changes.Announced);
        Assert.Equal(5_001, changes.Arrived);
    }

    /// <summary>
    /// And the last change in a burst is still announced. A reader told nothing after a flood would
    /// be left believing the screen is as it was, which is worse than being told too often.
    /// </summary>
    [Fact]
    public void TheLastChangeInABurstIsStillAnnounced()
    {
        TextChanges changes = new(TimeSpan.FromMilliseconds(500));

        changes.Changed(TimeSpan.Zero);
        changes.Changed(TimeSpan.FromMilliseconds(10));

        Assert.True(changes.Waiting);
        Assert.False(changes.Due(TimeSpan.FromMilliseconds(100)));

        Assert.True(changes.Due(TimeSpan.FromMilliseconds(600)));
        Assert.Equal(2, changes.Announced);
        Assert.False(changes.Waiting);

        // Nothing held is nothing due, however long anybody waits.
        Assert.False(changes.Due(TimeSpan.FromMinutes(5)));
    }

    /// <summary>A change after a quiet period is announced at once, which is a prompt appearing.</summary>
    [Fact]
    public void AChangeAfterQuietIsAnnouncedImmediately()
    {
        TextChanges changes = new(TimeSpan.FromMilliseconds(500));

        Assert.True(changes.Changed(TimeSpan.Zero));
        Assert.True(changes.Changed(TimeSpan.FromSeconds(10)));
        Assert.Equal(2, changes.Announced);
    }
}
