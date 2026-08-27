using System.Text;
using Quickshell.Terminal;
using Xunit;

namespace Quickshell.Terminal.Tests;

/// <summary>
/// What changed: the generation counter, the line identity a scroll moves, and the dirty bits that
/// are an optimisation on top of both.
/// </summary>
public sealed class DamageTests
{
    private const char Escape = (char)0x1B;
    private static readonly string Csi = new([Escape, '[']);

    // ---- The generation is the correctness ----

    [Fact]
    public void PrintingMovesTheGeneration()
    {
        Emulator emulator = new(80, 24);
        long before = emulator.Buffer.Generation;

        emulator.Feed("x"u8);

        Assert.True(emulator.Buffer.Generation > before);
    }

    /// <summary>
    /// A host that asks a question has not changed the screen, and a renderer woken by the read must
    /// be able to establish that without looking at a cell.
    /// </summary>
    [Fact]
    public void AHostAskingTheCursorPositionChangesNothing()
    {
        Emulator emulator = Fed("hello");
        Damage before = emulator.Damage;

        emulator.Feed(Encoding.UTF8.GetBytes(Csi + "6n"));

        Assert.Equal(before, emulator.Damage);
        Assert.NotEmpty(emulator.Reply.ToArray());
    }

    [Fact]
    public void AnEmptyReadChangesNothing()
    {
        Emulator emulator = Fed("hello");
        Damage before = emulator.Damage;

        emulator.Feed([]);

        Assert.Equal(before, emulator.Damage);
    }

    // ---- The scroll, which is the trap ----

    /// <summary>
    /// The design's own trap: a scroll changes every row's position and no row's content. A scheme
    /// that only asked "did row three change" would report the whole screen dirty on the single
    /// operation a terminal performs most.
    /// </summary>
    [Fact]
    public void APureScrollDirtiesOneRowAndMovesTheTopLine()
    {
        Emulator emulator = Fed(Rows(24));
        emulator.Buffer.ClearDamage();
        long top = emulator.Buffer.TopLine;

        emulator.Buffer.ScrollUp();

        Assert.Equal(top + 1, emulator.Buffer.TopLine);
        Assert.Equal(1, emulator.Buffer.DirtyRows);
        Assert.True(emulator.Buffer.IsScreenDirty(23));
        Assert.False(emulator.Buffer.IsScreenDirty(0));
    }

    /// <summary>The rows that moved keep the generation they were written at, so a consumer keyed on
    /// the line rather than the position finds them unchanged.</summary>
    [Fact]
    public void TheRowsAScrollMovedKeepTheirGeneration()
    {
        Emulator emulator = Fed(Rows(24));
        long[] generations = [.. Enumerable.Range(0, 24).Select(emulator.Buffer.ScreenGenerationOf)];

        emulator.Buffer.ScrollUp();

        for (int row = 0; row < 23; row++)
        {
            Assert.Equal(generations[row + 1], emulator.Buffer.ScreenGenerationOf(row));
        }
    }

    /// <summary>And the line each row is showing is the same line, one position higher.</summary>
    [Fact]
    public void AScrollMovesEveryRowsLineByOnePosition()
    {
        Emulator emulator = Fed(Rows(24));
        long[] lines = [.. Enumerable.Range(0, 24).Select(emulator.Buffer.AbsoluteLine)];

        emulator.Buffer.ScrollUp();

        for (int row = 0; row < 23; row++)
        {
            Assert.Equal(lines[row + 1], emulator.Buffer.AbsoluteLine(row));
        }
    }

    /// <summary>
    /// A row written and not yet drawn, and then a scroll: the bit has to follow the content down,
    /// or a consumer redraws the position the content left and leaves the position it arrived at
    /// stale.
    /// </summary>
    [Fact]
    public void ADirtyBitFollowsItsContentThroughAScroll()
    {
        Emulator emulator = Fed(Rows(24));
        emulator.Buffer.ClearDamage();

        emulator.Feed(Encoding.UTF8.GetBytes(Csi + "6;1Hchanged"));

        Assert.True(emulator.Buffer.IsScreenDirty(5));

        emulator.Buffer.ScrollUp();

        Assert.True(emulator.Buffer.IsScreenDirty(4));
        Assert.False(emulator.Buffer.IsScreenDirty(5));
    }

    /// <summary>A bit that scrolls off the top is gone, and the count says so.</summary>
    [Fact]
    public void ABitThatScrollsOffTheTopIsNotStillCounted()
    {
        Emulator emulator = Fed(Rows(24));
        emulator.Buffer.ClearDamage();

        emulator.Feed(Encoding.UTF8.GetBytes(Csi + "1;1Htop"));

        Assert.Equal(1, emulator.Buffer.DirtyRows);

        emulator.Buffer.ScrollUp();

        // The row that was dirty left, and the new bottom arrived: one either way.
        Assert.Equal(1, emulator.Buffer.DirtyRows);
        Assert.True(emulator.Buffer.IsScreenDirty(23));
    }

    /// <summary>A region really does move rows between positions, so that one is honestly all of them.</summary>
    [Fact]
    public void AScrollingRegionDirtiesTheRegionAndNothingOutsideIt()
    {
        Emulator emulator = Fed(Rows(24));
        emulator.Buffer.ClearDamage();

        emulator.Buffer.ScrollRegionUp(5, 9);

        Assert.Equal(5, emulator.Buffer.DirtyRows);
        Assert.True(emulator.Buffer.IsScreenDirty(5));
        Assert.True(emulator.Buffer.IsScreenDirty(9));
        Assert.False(emulator.Buffer.IsScreenDirty(4));
        Assert.False(emulator.Buffer.IsScreenDirty(10));
    }

    // ---- The cursor, which is damage with no mutation behind it ----

    /// <summary>
    /// A program that only moves the cursor has still changed what is on screen, and no cell was
    /// written for a generation counter to notice.
    /// </summary>
    [Fact]
    public void MovingTheCursorIsDamageWithoutAMutation()
    {
        Emulator emulator = Fed("hello");
        Damage before = emulator.Damage;

        emulator.Feed(Encoding.UTF8.GetBytes(Csi + "10;20H"));

        Assert.NotEqual(before, emulator.Damage);
        Assert.Equal(before.Generation, emulator.Damage.Generation);
    }

    [Fact]
    public void HidingTheCursorIsDamage()
    {
        Emulator emulator = Fed("hello");
        Damage before = emulator.Damage;

        emulator.Feed(Encoding.UTF8.GetBytes(Csi + "?25l"));

        Assert.NotEqual(before, emulator.Damage);
        Assert.False(emulator.Damage.CursorVisible);
    }

    // ---- The screen switch ----

    /// <summary>
    /// The two screens count their own generations, so a switch between them can leave every number
    /// equal while the whole picture changed.
    /// </summary>
    [Fact]
    public void EnteringTheAlternateScreenIsDamage()
    {
        Emulator emulator = Fed("hello");
        Damage before = emulator.Damage;

        emulator.Feed(Encoding.UTF8.GetBytes(Csi + "?1049h"));

        Assert.NotEqual(before, emulator.Damage);
        Assert.True(emulator.Damage.Alternate);
    }

    /// <summary>And leaving it, which restores a cursor to exactly where it was on a buffer nothing
    /// wrote to — the case where every other field really is equal.</summary>
    [Fact]
    public void LeavingTheAlternateScreenIsDamageEvenWhenNothingElseMoved()
    {
        Emulator emulator = Fed("hello");
        Damage primary = emulator.Damage;

        emulator.Feed(Encoding.UTF8.GetBytes(Csi + "?1049h"));
        Damage inside = emulator.Damage;
        emulator.Feed(Encoding.UTF8.GetBytes(Csi + "?1049l"));

        Assert.NotEqual(inside, emulator.Damage);
        Assert.Equal(primary, emulator.Damage);
    }

    // ---- Erases and edits mark what they touched ----

    [Fact]
    public void AnEraseInLineMarksOnlyThatRow()
    {
        Emulator emulator = Fed(Rows(24));
        emulator.Buffer.ClearDamage();

        emulator.Feed(Encoding.UTF8.GetBytes(Csi + "4;1H" + Csi + "2K"));

        Assert.Equal(1, emulator.Buffer.DirtyRows);
        Assert.True(emulator.Buffer.IsScreenDirty(3));
    }

    [Fact]
    public void AnEraseOfTheWholeScreenMarksAllOfIt()
    {
        Emulator emulator = Fed(Rows(24));
        emulator.Buffer.ClearDamage();

        emulator.Feed(Encoding.UTF8.GetBytes(Csi + "2J"));

        Assert.Equal(24, emulator.Buffer.DirtyRows);
    }

    /// <summary>
    /// <c>CSI @</c> and <c>CSI P</c> shift a row's cells. They are the buffer's own operations
    /// because a mutation done through a handed-out span is one the record never saw.
    /// </summary>
    [Theory]
    [InlineData("3@")]
    [InlineData("3P")]
    public void ShiftingCellsInARowIsRecorded(string sequence)
    {
        Emulator emulator = Fed(Rows(24));
        emulator.Buffer.ClearDamage();
        long before = emulator.Buffer.Generation;

        emulator.Feed(Encoding.UTF8.GetBytes(Csi + "7;3H" + Csi + sequence));

        Assert.True(emulator.Buffer.Generation > before);
        Assert.True(emulator.Buffer.IsScreenDirty(6));
        Assert.Equal(1, emulator.Buffer.DirtyRows);
    }

    [Fact]
    public void PrintingMarksOnlyTheCursorsRow()
    {
        Emulator emulator = Fed(Rows(24));
        emulator.Buffer.ClearDamage();

        emulator.Feed(Encoding.UTF8.GetBytes(Csi + "9;1Hword"));

        Assert.Equal(1, emulator.Buffer.DirtyRows);
        Assert.True(emulator.Buffer.IsScreenDirty(8));
    }

    // ---- Clearing the bits, and what it deliberately does not clear ----

    /// <summary>
    /// The bits are a flag and the generation is a count. Clearing the first must not touch the
    /// second, or two consumers each remembering their own last-drawn number would erase each
    /// other's evidence.
    /// </summary>
    [Fact]
    public void ClearingTheDamageLeavesTheGenerationAlone()
    {
        Emulator emulator = Fed("hello");
        long generation = emulator.Buffer.Generation;

        emulator.Buffer.ClearDamage();

        Assert.Equal(0, emulator.Buffer.DirtyRows);
        Assert.Equal(generation, emulator.Buffer.Generation);
    }

    // ---- Structure ----

    /// <summary>Dropping the scrollback does not move the screen, so no position's content changed.</summary>
    [Fact]
    public void DroppingTheScrollbackLeavesTheTopLineWhereItWas()
    {
        Emulator emulator = Fed(Rows(40));
        emulator.Buffer.ClearDamage();
        long top = emulator.Buffer.TopLine;
        List<long> lines = [.. Enumerable.Range(0, 24).Select(emulator.Buffer.AbsoluteLine)];

        emulator.Feed(Encoding.UTF8.GetBytes(Csi + "3J"));

        Assert.Equal(top, emulator.Buffer.TopLine);
        Assert.Equal(0, emulator.Buffer.ScrollbackLines);
        Assert.Equal(lines, [.. Enumerable.Range(0, 24).Select(emulator.Buffer.AbsoluteLine)]);
    }

    /// <summary>A line keeps its number across a height change, which is what makes the number an
    /// identity rather than an index.</summary>
    [Fact]
    public void ALineKeepsItsNumberAcrossAHeightChange()
    {
        Emulator emulator = Fed(Rows(40));
        long top = emulator.Buffer.TopLine;

        emulator.Resize(80, 30);

        // The screen grew by six rows, so its top is six lines further back and every one of those
        // lines is the line it was.
        Assert.Equal(top - 6, emulator.Buffer.TopLine);
    }

    /// <summary>
    /// A width change cannot keep them, and does not pretend to: re-wrapping re-cuts the lines, so
    /// the line that was number a hundred may now be two and neither of them is it. The anchor jumps
    /// past every number ever issued rather than let a stale one match — QS23.
    /// </summary>
    [Fact]
    public void AWidthChangeIssuesFreshLineNumbers()
    {
        Emulator emulator = Fed(Rows(40));
        long top = emulator.Buffer.TopLine;

        emulator.Resize(40, 24);

        Assert.True(emulator.Buffer.TopLine > top);
    }

    [Fact]
    public void AResizeMarksEveryRow()
    {
        Emulator emulator = Fed(Rows(24));
        emulator.Buffer.ClearDamage();

        emulator.Resize(100, 30);

        Assert.Equal(30, emulator.Buffer.DirtyRows);
    }

    /// <summary>
    /// A scrollback that has wrapped the ring: the anchor moves with the eviction, so the line
    /// numbers keep counting up rather than restarting where the ring's origin did.
    /// </summary>
    [Fact]
    public void LineNumbersSurviveTheRingWrappingRound()
    {
        Emulator emulator = new(20, 4, scrollback: 4);

        for (int line = 0; line < 40; line++)
        {
            emulator.Buffer.ScrollUp();
        }

        // Forty scrolls onto a four-row screen is forty-four lines of life, of which the ring keeps
        // the last eight and the window shows the last four: lines forty to forty-three.
        Assert.Equal(40, emulator.Buffer.TopLine);
        Assert.Equal(43, emulator.Buffer.AbsoluteLine(3));
        Assert.Equal(4, emulator.Buffer.ScrollbackLines);
    }

    private static string Rows(int count) =>
        string.Join(string.Empty, Enumerable.Range(0, count).Select(row => $"row {row}\r\n"));

    private static Emulator Fed(string stream)
    {
        Emulator emulator = new(80, 24);
        emulator.Feed(Encoding.UTF8.GetBytes(stream));

        return emulator;
    }
}
