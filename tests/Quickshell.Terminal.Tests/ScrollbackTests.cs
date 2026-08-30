using System.Text;
using Quickshell.Terminal;
using Xunit;

namespace Quickshell.Terminal.Tests;

/// <summary>
/// A viewport onto the ring, and finding something in it.
/// </summary>
public sealed class ScrollbackTests
{
    private const char Escape = (char)0x1B;
    private static readonly string Csi = new([Escape, '[']);

    // ---- The falsification ----

    /// <summary>
    /// The design's own falsification: <em>falsified when a match spanning a wrapped line is not
    /// found</em>.
    ///
    /// <para>A terminal breaks a long line across rows. A search that worked row by row would miss a
    /// word the wrap fell inside — and the user searching for it can see it on their screen.</para>
    /// </summary>
    [Fact]
    public void AMatchSpanningAWrappedLineIsFound()
    {
        // Twenty columns, and the word straddles the break.
        Emulator emulator = new(20, 6);
        emulator.Feed(Encoding.UTF8.GetBytes("aaaaaaaaaaaaaaaaaaneedle-here bbb"));

        Assert.True(emulator.Buffer.IsScreenWrapped(0), "the fixture did not actually wrap");

        Assert.True(
            Search.TryFind(emulator.Buffer, "needle-here", First(emulator), 0, true, false, out Match match));

        // It starts on the first row, two columns before the break.
        Assert.Equal(emulator.Buffer.AbsoluteLine(0), match.Line);
        Assert.Equal(18, match.Column);
        Assert.Equal(11, match.Cells);
    }

    /// <summary>And a match that would need to cross a line the host actually ended is not one.</summary>
    [Fact]
    public void AMatchIsNotCarriedAcrossALineTheHostEnded()
    {
        Emulator emulator = new(20, 6);
        emulator.Feed(Encoding.UTF8.GetBytes("abcdef\r\nghijkl"));

        Assert.False(
            Search.TryFind(emulator.Buffer, "defghi", First(emulator), 0, true, false, out _));
    }

    // ---- Finding things ----

    [Fact]
    public void SearchIsCaseInsensitiveUnlessAskedOtherwise()
    {
        Emulator emulator = new(40, 6);
        emulator.Feed(Encoding.UTF8.GetBytes("Connection Refused"));

        Assert.True(Search.TryFind(emulator.Buffer, "refused", First(emulator), 0, true, false, out _));
        Assert.False(Search.TryFind(emulator.Buffer, "refused", First(emulator), 0, true, true, out _));
        Assert.True(Search.TryFind(emulator.Buffer, "Refused", First(emulator), 0, true, true, out _));
    }

    [Fact]
    public void SearchFindsTheNextMatchAndThenTheOneAfterIt()
    {
        Emulator emulator = new(40, 8);
        emulator.Feed(Encoding.UTF8.GetBytes("error one\r\nfine\r\nerror two"));

        Assert.True(Search.TryFind(emulator.Buffer, "error", First(emulator), 0, true, false, out Match one));
        Assert.Equal(emulator.Buffer.AbsoluteLine(0), one.Line);

        Assert.True(
            Search.TryFind(emulator.Buffer, "error", one.Line + 1, 0, true, false, out Match two));
        Assert.Equal(emulator.Buffer.AbsoluteLine(2), two.Line);
    }

    [Fact]
    public void SearchGoesBackwardsToo()
    {
        Emulator emulator = new(40, 8);
        emulator.Feed(Encoding.UTF8.GetBytes("error one\r\nfine\r\nerror two"));

        Assert.True(
            Search.TryFind(emulator.Buffer, "error", emulator.Buffer.AbsoluteLine(2), 40, false, false, out Match found));

        Assert.Equal(emulator.Buffer.AbsoluteLine(2), found.Line);

        Assert.True(
            Search.TryFind(emulator.Buffer, "error", found.Line - 1, 40, false, false, out Match earlier));

        Assert.Equal(emulator.Buffer.AbsoluteLine(0), earlier.Line);
    }

    /// <summary>The count is what a search box shows, and it counts the scrollback and not the
    /// screen.</summary>
    [Fact]
    public void TheCountCoversTheWholeHistory()
    {
        Emulator emulator = new(40, 4);

        for (int line = 0; line < 30; line++)
        {
            emulator.Feed(Encoding.UTF8.GetBytes($"line {line} error\r\n"));
        }

        Assert.True(emulator.Buffer.ScrollbackLines > 0);
        Assert.Equal(30, Search.Count(emulator.Buffer, "error", caseSensitive: false));
    }

    [Fact]
    public void NothingIsFoundForNothing()
    {
        Emulator emulator = new(40, 4);
        emulator.Feed(Encoding.UTF8.GetBytes("some text"));

        Assert.False(Search.TryFind(emulator.Buffer, default, First(emulator), 0, true, false, out _));
        Assert.Equal(0, Search.Count(emulator.Buffer, default, false));
        Assert.False(Search.TryFind(emulator.Buffer, "absent", First(emulator), 0, true, false, out _));
    }

    /// <summary>A wide character is one match position and two cells, so a match's length is in
    /// cells.</summary>
    [Fact]
    public void AMatchAcrossWideCharactersIsMeasuredInCells()
    {
        Emulator emulator = new(20, 4);
        emulator.Feed(Encoding.UTF8.GetBytes("x日本y"));

        Assert.True(Search.TryFind(emulator.Buffer, "日本", First(emulator), 0, true, false, out Match match));

        Assert.Equal(1, match.Column);
        Assert.Equal(4, match.Cells);
    }

    // ---- The viewport ----

    [Fact]
    public void ANewViewportIsAtTheBottom()
    {
        Emulator emulator = Scrolled(30);
        Viewport viewport = new();

        Assert.True(viewport.IsAtBottom);
        Assert.Equal(emulator.Buffer.TopLine, viewport.Top(emulator.Buffer));
        Assert.Equal(0, viewport.Depth(emulator.Buffer));
    }

    [Fact]
    public void ScrollingBackMovesTheTopAndScrollingDownReturns()
    {
        Emulator emulator = Scrolled(30);
        Viewport viewport = new();

        Assert.True(viewport.ScrollBy(emulator.Buffer, -5));
        Assert.False(viewport.IsAtBottom);
        Assert.Equal(5, viewport.Depth(emulator.Buffer));

        Assert.True(viewport.ScrollBy(emulator.Buffer, 5));
        Assert.True(viewport.IsAtBottom);
    }

    [Fact]
    public void ScrollingPastTheHistoryStopsAtIt()
    {
        Emulator emulator = Scrolled(30);
        Viewport viewport = new();

        viewport.ScrollBy(emulator.Buffer, -10_000);

        Assert.Equal(emulator.Buffer.ScrollbackLines, viewport.Depth(emulator.Buffer));
    }

    /// <summary>
    /// The behaviour the whole design turns on: somebody reading does not want the screen stolen.
    /// New output leaves the view exactly where it was, on the text it was showing.
    /// </summary>
    [Fact]
    public void OutputArrivingDoesNotStealTheScreenFromAReader()
    {
        Emulator emulator = Scrolled(30);
        Viewport viewport = new();

        viewport.ScrollBy(emulator.Buffer, -8);

        long looking = viewport.Top(emulator.Buffer);

        for (int line = 0; line < 6; line++)
        {
            emulator.Feed(Encoding.UTF8.GetBytes($"new {line}\r\n"));
            viewport.Produced();
        }

        Assert.Equal(looking, viewport.Top(emulator.Buffer));
        Assert.False(viewport.IsAtBottom);
        Assert.True(viewport.HasUnseenOutput);
    }

    /// <summary>And typing means the reading is finished.</summary>
    [Fact]
    public void TypingReturnsToTheBottom()
    {
        Emulator emulator = Scrolled(30);
        Viewport viewport = new();

        viewport.ScrollBy(emulator.Buffer, -8);
        viewport.Produced();
        viewport.Typed();

        Assert.True(viewport.IsAtBottom);
        Assert.False(viewport.HasUnseenOutput);
        Assert.Equal(emulator.Buffer.TopLine, viewport.Top(emulator.Buffer));
    }

    [Fact]
    public void OutputArrivingWhileFollowingIsNotUnseen()
    {
        Emulator emulator = Scrolled(30);
        Viewport viewport = new();

        emulator.Feed(Encoding.UTF8.GetBytes("more\r\n"));
        viewport.Produced();

        Assert.False(viewport.HasUnseenOutput);
    }

    /// <summary>
    /// A reader who stayed still while a great deal was printed is not an error: the anchor is
    /// clamped to what the ring still holds.
    /// </summary>
    [Fact]
    public void AnAnchorTheRingHasEvictedIsClampedRatherThanBroken()
    {
        Emulator emulator = new(20, 4, scrollback: 8);

        for (int line = 0; line < 20; line++)
        {
            emulator.Feed(Encoding.UTF8.GetBytes($"line {line}\r\n"));
        }

        Viewport viewport = new();
        viewport.ScrollBy(emulator.Buffer, -8);

        for (int line = 0; line < 50; line++)
        {
            emulator.Feed(Encoding.UTF8.GetBytes($"flood {line}\r\n"));
        }

        long top = viewport.Top(emulator.Buffer);

        Assert.InRange(
            top,
            emulator.Buffer.TopLine - emulator.Buffer.ScrollbackLines,
            emulator.Buffer.TopLine);
    }

    /// <summary>The alternate screen has no history, so there is nothing to scroll into.</summary>
    [Fact]
    public void TheAlternateScreenHasNothingToScrollBackTo()
    {
        Emulator emulator = Scrolled(30);
        emulator.Feed(Encoding.UTF8.GetBytes(Csi + "?1049h"));

        Viewport viewport = new();

        Assert.False(viewport.ScrollBy(emulator.Buffer, -10));
        Assert.True(viewport.IsAtBottom);
    }

    // ---- Where a wheel notch goes ----

    /// <summary>
    /// This is what makes the wheel scroll inside <c>less</c> and <c>man</c> rather than scrolling
    /// the terminal out from under them.
    /// </summary>
    [Fact]
    public void AWheelNotchGoesToTheHistoryOnlyOnTheOrdinaryScreen()
    {
        Emulator emulator = Scrolled(30);

        Assert.Equal(WheelGoes.ToScrollback, Viewport.Wheel(emulator));

        emulator.Feed(Encoding.UTF8.GetBytes(Csi + "?1049h"));

        Assert.Equal(WheelGoes.ToArrowKeys, Viewport.Wheel(emulator));

        emulator.Feed(Encoding.UTF8.GetBytes(Csi + "?1000h"));

        Assert.Equal(WheelGoes.ToTheProgram, Viewport.Wheel(emulator));
    }

    // ---- Helpers ----

    private static Emulator Scrolled(int lines)
    {
        Emulator emulator = new(40, 6);

        for (int line = 0; line < lines; line++)
        {
            emulator.Feed(Encoding.UTF8.GetBytes($"history line {line}\r\n"));
        }

        return emulator;
    }

    private static long First(Emulator emulator) =>
        emulator.Buffer.TopLine - emulator.Buffer.ScrollbackLines;
}
