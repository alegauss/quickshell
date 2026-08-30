using System.Text;
using Quickshell.Terminal;
using Xunit;

namespace Quickshell.Terminal.Tests;

/// <summary>
/// Text an input method is composing: on screen, never in the buffer, and measured in cells.
/// </summary>
public sealed class CompositionTests
{
    // ---- It is not in the buffer ----

    /// <summary>
    /// The trap this class exists to make impossible: a composition that had been written into the
    /// buffer would leave characters behind when it was cancelled. Nothing is written, so nothing is
    /// left.
    /// </summary>
    [Fact]
    public void ACancelledCompositionLeavesNothingOnTheScreen()
    {
        Emulator emulator = new(20, 4);
        emulator.Feed("prompt> "u8);

        string before = Row(emulator, 0);

        Composition composition = new();
        composition.Start();
        composition.Update("にほんご", 4);
        composition.Cancel();

        Assert.False(composition.IsActive);
        Assert.Empty(composition.Text.ToArray());
        Assert.Equal(before, Row(emulator, 0));
    }

    /// <summary>And the same while it is still going: composing writes to nothing at all.</summary>
    [Fact]
    public void ComposingWritesNothingToTheBuffer()
    {
        Emulator emulator = new(20, 4);
        emulator.Feed("prompt> "u8);

        long generation = emulator.Buffer.Generation;

        Composition composition = new();
        composition.Start();

        foreach (string step in new[] { "n", "ni", "にi", "には" })
        {
            composition.Update(step, step.Length);
        }

        Assert.Equal(generation, emulator.Buffer.Generation);
        Assert.Equal("prompt>", Row(emulator, 0).TrimEnd());
    }

    // ---- Width is cells and never characters ----

    /// <summary>
    /// The falsification's own arithmetic: a candidate list placed by counting characters lands in
    /// the middle of what the user is reading. Four Japanese characters are eight columns.
    /// </summary>
    [Fact]
    public void WidthIsCountedInCellsAndNotInCharacters()
    {
        Composition composition = new();
        composition.Start();
        composition.Update("にほんご", 4);

        Assert.Equal(4, composition.Text.Length);
        Assert.Equal(8, composition.Cells);
        Assert.Equal(8, composition.CellsBeforeCaret);
    }

    [Fact]
    public void TheCandidateListSitsAtTheCaretsRealWidth()
    {
        Composition composition = new();
        composition.Start();
        composition.Update("にほんご", 2);

        // Two characters composed of four, so four columns along from the cursor and not two.
        Assert.Equal(new CandidatePlacement(14, 3), composition.Candidate(10, 3, 80));
    }

    [Fact]
    public void ACandidateListFollowsTheCompositionAroundTheMargin()
    {
        Composition composition = new();
        composition.Start();
        composition.Update("にほんご", 4);

        // Seventy-six plus eight cells is past a eighty-column screen, so it wraps like the text does.
        Assert.Equal(new CandidatePlacement(4, 6), composition.Candidate(76, 5, 80));
    }

    [Fact]
    public void AnEmptyCompositionPutsTheListAtTheCursor()
    {
        Composition composition = new();
        composition.Start();

        Assert.Equal(new CandidatePlacement(12, 7), composition.Candidate(12, 7, 80));
    }

    [Fact]
    public void AMixedCompositionMeasuresEachCharacterOnItsOwnWidth()
    {
        Composition composition = new();
        composition.Start();

        // Two narrow, two wide: six cells, not four.
        composition.Update("ab日本", 4);

        Assert.Equal(6, composition.Cells);
    }

    // ---- What it does with what it is given ----

    [Fact]
    public void UpdatingReplacesRatherThanAppending()
    {
        Composition composition = new();
        composition.Start();
        composition.Update("nihon", 5);
        composition.Update("にほん", 3);

        Assert.Equal("にほん", new string(composition.Text));
        Assert.Equal(3, composition.Caret);
    }

    [Fact]
    public void AStartAfterACompositionForgetsTheOldOne()
    {
        Composition composition = new();
        composition.Start();
        composition.Update("first", 5);
        composition.Start();

        Assert.True(composition.IsActive);
        Assert.Empty(composition.Text.ToArray());
        Assert.Equal(0, composition.Caret);
    }

    [Fact]
    public void ACaretOutsideTheTextIsBroughtInside()
    {
        Composition composition = new();
        composition.Start();
        composition.Update("abc", 99);

        Assert.Equal(3, composition.Caret);

        composition.Update("abc", -4);

        Assert.Equal(0, composition.Caret);
    }

    /// <summary>
    /// The length is somebody else's choice, so it has a ceiling — and an input method driven by a
    /// script is still somebody else.
    /// </summary>
    [Fact]
    public void AnOverlongCompositionIsTruncatedRatherThanRefused()
    {
        Composition composition = new();
        composition.Start();
        composition.Update(new string('a', Composition.MaximumLength * 4), 10);

        Assert.Equal(Composition.MaximumLength, composition.Text.Length);
        Assert.Equal(10, composition.Caret);
    }

    /// <summary>Half a character is not one, so a truncation never splits a pair.</summary>
    [Fact]
    public void TruncatingNeverSplitsASurrogatePair()
    {
        Composition composition = new();
        composition.Start();

        // Every character a surrogate pair, so the ceiling falls between the halves of one.
        composition.Update(string.Concat(Enumerable.Repeat("\U0001F600", Composition.MaximumLength)), 0);

        Assert.False(char.IsHighSurrogate(composition.Text[^1]));
        Assert.Equal(0, composition.Text.Length % 2);
    }

    // ---- What is committed goes down the ordinary path ----

    [Fact]
    public void CommittingHandsBackTheBytesAndClosesTheComposition()
    {
        Composition composition = new();
        Span<byte> buffer = stackalloc byte[64];

        composition.Start();
        composition.Update("にほん", 3);

        int written = composition.Commit("日本語", buffer);

        Assert.Equal("日本語", Encoding.UTF8.GetString(buffer[..written]));
        Assert.False(composition.IsActive);
        Assert.Empty(composition.Text.ToArray());
    }

    [Fact]
    public void CommittingNothingSendsNothing()
    {
        Composition composition = new();
        Span<byte> buffer = stackalloc byte[64];

        composition.Start();
        composition.Update("abc", 3);

        Assert.Equal(0, composition.Commit(default, buffer));
        Assert.False(composition.IsActive);
    }

    // ---- The path it goes down ----

    /// <summary>Composing allocates nothing: it runs on every keystroke of a phrase.</summary>
    [Fact]
    public void ComposingAllocatesNothing()
    {
        Composition composition = new();
        composition.Start();
        composition.Update("にほんご", 4);

        long before = GC.GetAllocatedBytesForCurrentThread();

        for (int keystroke = 0; keystroke < 1000; keystroke++)
        {
            composition.Update("にほんご", keystroke % 5);
            _ = composition.Cells;
            _ = composition.Candidate(10, 3, 80);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    private static string Row(Emulator emulator, int row)
    {
        StringBuilder text = new();

        foreach (Cell cell in emulator.Buffer.Screen(row))
        {
            if (cell.Width != 0)
            {
                text.Append(emulator.Buffer.TextOf(cell));
            }
        }

        return text.ToString();
    }
}
