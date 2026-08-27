using System.Text;
using Quickshell.Render;
using Quickshell.Terminal;
using Xunit;

namespace Quickshell.Render.Tests;

/// <summary>
/// The decision not to draw, which is where the idle-cost figure is won.
/// </summary>
public sealed class RedrawGateTests
{
    private const char Escape = (char)0x1B;
    private static readonly string Csi = new([Escape, '[']);

    // ---- The falsification ----

    /// <summary>
    /// The design's own falsification: <em>falsified when a window nobody is typing into issues a
    /// draw call</em>.
    ///
    /// <para>A whole screen's worth of output, one frame for it, and then a thousand wake-ups with
    /// the host silent. One frame is the whole budget.</para>
    /// </summary>
    [Fact]
    public void AnIdleWindowIssuesNoDrawCalls()
    {
        Emulator emulator = Busy();
        RedrawGate gate = new();

        Assert.True(gate.Claim(emulator.Damage, cursorShowing: true));

        for (int tick = 0; tick < 1000; tick++)
        {
            Assert.False(gate.Claim(emulator.Damage, cursorShowing: true));
        }

        Assert.Equal(1, gate.Frames);
        Assert.Equal(1000, gate.Skipped);
    }

    /// <summary>
    /// And the same with the cursor hidden, which is what a full-screen program does. A blink phase
    /// that reached the comparison would wake the window twice a second to draw the same picture.
    /// </summary>
    [Fact]
    public void AHiddenCursorDoesNotBlinkTheWindowAwake()
    {
        Emulator emulator = Busy();
        emulator.Feed(Encoding.UTF8.GetBytes(Csi + "?25l"));

        RedrawGate gate = new();
        gate.Claim(emulator.Damage, cursorShowing: true);

        for (int tick = 0; tick < 100; tick++)
        {
            Assert.False(gate.Claim(emulator.Damage, cursorShowing: tick % 2 == 0));
        }

        Assert.Equal(1, gate.Frames);
    }

    // ---- What does earn a frame ----

    [Fact]
    public void AScreenTheGateHasNeverSeenIsDrawn()
    {
        Assert.True(new RedrawGate().Claim(new Emulator(80, 24).Damage, cursorShowing: true));
    }

    [Fact]
    public void AByteFromTheHostEarnsAFrame()
    {
        Emulator emulator = Busy();
        RedrawGate gate = new();
        gate.Claim(emulator.Damage, cursorShowing: true);

        emulator.Feed("x"u8);

        Assert.True(gate.Claim(emulator.Damage, cursorShowing: true));
        Assert.Equal(2, gate.Frames);
    }

    /// <summary>A blinking cursor is the one thing that legitimately wakes an idle window.</summary>
    [Fact]
    public void TheBlinkEarnsAFrameWhileTheCursorIsShown()
    {
        Emulator emulator = Busy();
        RedrawGate gate = new();

        gate.Claim(emulator.Damage, cursorShowing: true);

        Assert.True(gate.Claim(emulator.Damage, cursorShowing: false));
        Assert.True(gate.Claim(emulator.Damage, cursorShowing: true));
        Assert.Equal(3, gate.Frames);
    }

    /// <summary>A cursor that moved without a cell being written is still a frame.</summary>
    [Fact]
    public void AMovedCursorEarnsAFrame()
    {
        Emulator emulator = Busy();
        RedrawGate gate = new();
        gate.Claim(emulator.Damage, cursorShowing: true);

        emulator.Feed(Encoding.UTF8.GetBytes(Csi + "1;1H"));

        Assert.True(gate.Claim(emulator.Damage, cursorShowing: true));
    }

    /// <summary>A scroll moves no row's content and is still a different screen.</summary>
    [Fact]
    public void AScrollEarnsAFrame()
    {
        Emulator emulator = Busy();
        RedrawGate gate = new();
        gate.Claim(emulator.Damage, cursorShowing: true);

        emulator.Buffer.ScrollUp();

        Assert.True(gate.Claim(emulator.Damage, cursorShowing: true));
    }

    /// <summary>
    /// The changes the terminal knows nothing about — a lost device, a reloaded font, a window the
    /// user dragged wider. Each leaves the damage identical and the picture wrong.
    /// </summary>
    [Fact]
    public void InvalidatingEarnsTheNextFrame()
    {
        Emulator emulator = Busy();
        RedrawGate gate = new();
        gate.Claim(emulator.Damage, cursorShowing: true);

        Assert.False(gate.Claim(emulator.Damage, cursorShowing: true));

        gate.Invalidate();

        Assert.True(gate.Claim(emulator.Damage, cursorShowing: true));
    }

    /// <summary>
    /// The gate records what it authorised and not what happened afterwards, so asking twice without
    /// drawing loses a frame. Documented, and asserted so it stays a decision rather than a surprise.
    /// </summary>
    [Fact]
    public void AskingTwiceWithoutDrawingAnswersNoTheSecondTime()
    {
        Emulator emulator = Busy();
        RedrawGate gate = new();

        Assert.True(gate.Claim(emulator.Damage, cursorShowing: true));
        Assert.False(gate.Claim(emulator.Damage, cursorShowing: true));
    }

    /// <summary>The blink's own answer, composed with the gate as a window's loop would compose them.</summary>
    [Fact]
    public void BlinkingTurnedOffLeavesNothingToWakeFor()
    {
        Emulator emulator = Busy();
        CursorBlink blink = new() { Enabled = false };
        RedrawGate gate = new();

        gate.Claim(emulator.Damage, blink.IsShowingAt(TimeSpan.Zero));

        for (int tick = 0; tick < 100; tick++)
        {
            TimeSpan elapsed = TimeSpan.FromMilliseconds(tick * 100);

            Assert.Null(blink.NextChangeAfter(elapsed));
            Assert.False(gate.Claim(emulator.Damage, blink.IsShowingAt(elapsed)));
        }

        Assert.Equal(1, gate.Frames);
    }

    /// <summary>A screen with output on it, a cursor somewhere in the middle, and scrollback behind.</summary>
    private static Emulator Busy()
    {
        Emulator emulator = new(80, 24);
        emulator.Feed(Encoding.UTF8.GetBytes(
            string.Join(string.Empty, Enumerable.Range(0, 40).Select(row => $"row {row}\r\n"))));

        return emulator;
    }
}
