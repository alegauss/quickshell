using System.Diagnostics;
using System.Text;
using Quickshell.Terminal;
using Vortice.Mathematics;
using Xunit;

namespace Quickshell.Render.Tests;

/// <summary>
/// The present path, against a real device and a real visible window. The flags are only worth
/// having if the queue they control is actually one deep, so that is measured rather than asserted
/// from the value that was set.
/// </summary>
public sealed class PresentSurfaceTests
{
    /// <summary>
    /// How long to wait for DXGI to report a present count before deciding it never will.
    ///
    /// <para><b>Time and not frames, which is QS145.</b> Both measurements here need statistics
    /// DXGI produces on its own schedule, and both used to give up after ninety frames — a second
    /// and a half at vsync. On one run of the suite that was too few, both tests skipped, and a
    /// green run said nothing about the idle draw-call figure it was supposed to prove. Six seconds
    /// costs nothing on a good run and makes a skip mean the machine genuinely cannot answer.</para>
    /// </summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(6);

    [Fact]
    public void TheSurfaceOpensWithItsWaitHandle()
    {
        using TestWindow window = new(320, 200);
        using GraphicsDevice device = GraphicsDevice.Open(outputWindow: window.Handle);
        using PresentSurface surface = PresentSurface.For(device, window.Handle, 320, 200);

        Assert.NotEqual(nint.Zero, surface.FrameLatencyWaitHandle);
        Assert.NotNull(surface.View);
        Assert.Equal(320u, surface.Width);
    }

    /// <summary>
    /// The queue does not grow: the application never gets more than one frame ahead of the
    /// display.
    ///
    /// <para><b>What this used to claim, and why it was wrong.</b> It asserted a mean depth of at
    /// most 1.5 and failed on roughly one run in eight with no change to the tree. The stated cause
    /// was that <c>FrameStatistics.PresentCount</c> refreshes at vblank, so a sample between
    /// vblanks reads one frame stale. Measured over thirty runs, that story does not survive:
    /// <c>PresentCount</c> advances on <em>every</em> sample, so nothing is stale, and swapping the
    /// process-side counter for DXGI's own <c>LastPresentCount</c> — the fix the roadmap line
    /// proposed — gives byte-identical numbers on the good runs and the bad ones alike.</para>
    ///
    /// <para><b>What was actually wrong.</b> Two things. The warm-up: <c>frame &gt; 10</c> assumed
    /// statistics exist by then, and they often do not — <c>PresentCount</c> is still zero, so the
    /// first sample read <b>eleven</b> and dragged the mean up. Waiting for the statistics to appear
    /// and then discarding ten more frames takes the ordinary run from 1.20 to a flat 1.00. And the
    /// residue: after that, the difference between the two counters is a <em>constant</em> for the
    /// whole run, one or two depending on where startup left them. It is a phase, not a depth, and
    /// no mean over it means anything.</para>
    ///
    /// <para><b>So this asserts growth, not depth.</b> An absolute bound was tried and did not
    /// survive contact with a second environment: thirty runs on this desk never exceeded two, and
    /// QS95's VMware guest reaches three on the first run, because a synthesised display returns
    /// from <c>Present</c> on a schedule the host decides and there is no vblank to count against.
    /// The depth's absolute value is a property of the presentation pipeline. What is a property of
    /// the <em>renderer</em> is whether the queue grows, so that is what is asserted: the second
    /// half of a run must not average deeper than the first. An application getting ahead of the
    /// display climbs; one that cannot get ahead sits wherever startup left it.</para>
    ///
    /// <para>The absolute bound that remains is deliberately loose, and is there for the case a
    /// growth test cannot see: a queue already deep at the first sample and flat thereafter.</para>
    ///
    /// <para><b>What it deliberately does not claim.</b> Not that the wait is worth anything: two
    /// controls were tried and neither discriminated, because one clear per frame with a vsync
    /// present is a workload that can never get ahead of the display at all. The figure the flags
    /// were bought for is input to photon, it needs a frame with real work in it, and it is QS86's
    /// to measure rather than this test's to imply.</para>
    ///
    /// <para><b>And it cannot fire on this workload.</b> One clear per frame with a vsync present is
    /// a frame that cannot get ahead of the display, so the growth this asserts is growth nothing
    /// here can produce — QS86 tried two controls and neither discriminated, for that reason. This
    /// is a guard for the workload QS86 will bring, not a check that is proving anything today.</para>
    /// </summary>
    [Fact]
    public void TheFrameQueueNeverGetsAheadOfTheDisplay()
    {
        using TestWindow window = new(320, 200);
        using GraphicsDevice device = GraphicsDevice.Open(outputWindow: window.Handle);
        using PresentSurface surface = PresentSurface.For(device, window.Handle, 320, 200);

        int settled = -1;
        int frame = 0;
        List<long> depths = [];

        // Bounded by time and not by a frame count, which is QS145: statistics arrive when DXGI
        // decides, ninety frames is a second and a half at vsync, and a run where they arrived a
        // little late used to skip and say nothing. The wait is long enough that giving up means
        // the machine genuinely cannot answer.
        Stopwatch waiting = Stopwatch.StartNew();

        while (waiting.Elapsed < Patience && depths.Count < 60)
        {
            surface.WaitForNextFrame();

            // Ten frames after the first real reading is where the counters have stopped moving
            // relative to each other.
            if (settled < 0 && surface.PresentedOnGlass() > 0)
            {
                settled = frame + 10;
            }

            if (settled >= 0 && frame >= settled)
            {
                depths.Add(surface.QueueDepth());
            }

            device.Context.ClearRenderTargetView(surface.View, new Color4(0.02f, 0.02f, 0.08f, 1.0f));
            surface.Present();

            frame++;
        }

        Assert.SkipWhen(surface.Occlusions > 0 || depths.Count < 30,
            $"the window was covered for {surface.Occlusions} frames and DXGI gave {depths.Count} " +
            $"usable samples in {waiting.Elapsed.TotalSeconds:F1} s, so the queue could not be " +
            "measured on this run");

        double early = depths.Take(depths.Count / 2).Average();
        double late = depths.Skip(depths.Count / 2).Average();

        Assert.True(late <= early + 1.0,
            $"the frame queue averaged {early:F2} over the first half of the run and {late:F2} over " +
            "the second, so it is growing - the application is getting ahead of the display");

        // A loose absolute bound, so a queue that is deep from the first sample and stays there
        // still fails. It is deliberately far above both environments measured: this test's claim
        // is about growth, and the depth's absolute value is a startup phase QS87 measured and a
        // presentation pipeline QS97 measured again.
        Assert.True(depths.Max() <= 8,
            $"the frame queue reached {depths.Max()}, which is deeper than any presentation path " +
            "this has been measured on and deeper than a latency of one can explain");
    }

    /// <summary>
    /// Figure 4's first half, measured on the GPU rather than on the gate's own counter: an idle
    /// render loop submits nothing.
    ///
    /// <para><b>Why this is not <c>RedrawGateTests</c> again.</b> That test asks the gate whether it
    /// would authorise a frame, and it answers correctly a thousand times. What it cannot say is
    /// whether the loop around it presents anyway — the two counters that matter are DXGI's, and this
    /// is the assembly that has a real device to ask. The loop here is written the way a renderer's
    /// is: ask, and present only when told.</para>
    ///
    /// <para><b>Not tautological, because the loop is the subject.</b> A loop that presented
    /// unconditionally would fail this, and that is the defect being guarded against — one which
    /// would not look like a defect, only like a laptop that runs warm.</para>
    /// </summary>
    [Fact]
    public void AnIdleRenderLoopPresentsNothingToTheGpu()
    {
        using TestWindow window = new(320, 200);
        using GraphicsDevice device = GraphicsDevice.Open(outputWindow: window.Handle);
        using PresentSurface surface = PresentSurface.For(device, window.Handle, 320, 200);

        Emulator emulator = new(80, 25);

        emulator.Feed(Encoding.UTF8.GetBytes("a screen with something on it\r\n"));

        RedrawGate gate = new();

        // Until DXGI's statistics appear, bounded by time rather than by a frame count — QS145,
        // because a present count of zero is not a reading and ninety frames turned out to be
        // sometimes too few on this very machine. The screen is changed each time, so the gate
        // keeps authorising and the counter moves.
        // A busy phase, so the gate has authorised frames and there is something to have stopped
        // doing. Sixty is enough and this waits on nothing: what this test claims does not need
        // DXGI to have said anything.
        for (int line = 0; line < 60; line++)
        {
            emulator.Feed(Encoding.UTF8.GetBytes($"line {line}\r\n"));

            if (gate.Claim(emulator.Damage, cursorShowing: true))
            {
                surface.WaitForNextFrame();
                device.Context.ClearRenderTargetView(surface.View,
                                                     new Color4(0.02f, 0.02f, 0.08f, 1.0f));
                surface.Present();
            }
        }

        long onGlassBefore = surface.PresentedOnGlass();
        long authorisedBefore = gate.Frames;
        int presentedWhileIdle = 0;

        Assert.True(authorisedBefore > 0, "the busy phase authorised no frames, so there is nothing "
                                          + "for the idle phase to be quieter than");

        // Three hundred wake-ups with the host silent, which is what a window nobody is typing into
        // does. Nothing is fed, so nothing changed.
        for (int tick = 0; tick < 300; tick++)
        {
            if (gate.Claim(emulator.Damage, cursorShowing: true))
            {
                surface.WaitForNextFrame();
                device.Context.ClearRenderTargetView(surface.View,
                                                     new Color4(0.02f, 0.02f, 0.08f, 1.0f));
                surface.Present();
                presentedWhileIdle++;
            }
        }

        // The number is zero, not "small": the budget says so, and this is the figure the project is
        // built on. None of these three needs DXGI to have said anything, which is the point — the
        // claim is about what the loop submitted.
        Assert.Equal(0, presentedWhileIdle);
        Assert.Equal(authorisedBefore, gate.Frames);
        Assert.Equal(300, gate.Skipped);

        // And where DXGI did produce statistics, it agrees. Conditional because frame statistics are
        // not always available — a sleeping display has no vblank to count against, and this desk
        // reported none for six seconds while nobody was at it. QS145 is what that cost: the whole
        // test used to skip on it, so the figure above went unproven on exactly the unattended runs
        // a soak or a CI job is made of.
        if (onGlassBefore > 0)
        {
            Assert.Equal(onGlassBefore, surface.PresentedOnGlass());
        }
    }

    [Fact]
    public void ResizeReallocatesAndDrawsBeforeItReturns()
    {
        using TestWindow window = new(320, 200);
        using GraphicsDevice device = GraphicsDevice.Open(outputWindow: window.Handle);
        using PresentSurface surface = PresentSurface.For(device, window.Handle, 320, 200);

        surface.WaitForNextFrame();
        device.Context.ClearRenderTargetView(surface.View, new Color4(0.02f, 0.02f, 0.08f, 1.0f));
        surface.Present();

        bool drewAtTheNewSize = false;

        surface.Resize(480, 300, resized =>
        {
            // Drawn while the window is still showing the old frame, which is what keeps a resize
            // from flashing.
            drewAtTheNewSize = resized.Width == 480 && resized.Height == 300;
            device.Context.ClearRenderTargetView(resized.View, new Color4(0.08f, 0.02f, 0.02f, 1.0f));
            resized.Present();
        });

        Assert.True(drewAtTheNewSize);
        Assert.Equal(480u, surface.Width);
        Assert.Equal(300u, surface.Height);
    }

    [Fact]
    public void ADeviceLossRebuildsTheSwapChain()
    {
        using TestWindow window = new(320, 200);
        using GraphicsDevice device = GraphicsDevice.Open(outputWindow: window.Handle);
        using PresentSurface surface = PresentSurface.For(device, window.Handle, 320, 200);

        surface.WaitForNextFrame();
        device.Context.ClearRenderTargetView(surface.View, new Color4(0.02f, 0.02f, 0.08f, 1.0f));
        surface.Present();

        nint before = surface.FrameLatencyWaitHandle;

        device.Recover();

        Assert.NotEqual(nint.Zero, surface.FrameLatencyWaitHandle);
        Assert.NotEqual(before, surface.FrameLatencyWaitHandle);
        Assert.Equal(0, surface.Presented);

        surface.WaitForNextFrame();
        device.Context.ClearRenderTargetView(surface.View, new Color4(0.02f, 0.08f, 0.02f, 1.0f));
        surface.Present();

        Assert.Equal(1, surface.Presented);
    }

    /// <summary>
    /// Tearing is a capability, not an assumption. This records what this machine answered rather
    /// than requiring an answer, because a machine without it is a machine the client still runs on.
    /// </summary>
    [Fact]
    public void TearingIsAskedForRatherThanAssumed()
    {
        using TestWindow window = new(320, 200);
        using GraphicsDevice device = GraphicsDevice.Open(outputWindow: window.Handle);
        using PresentSurface surface = PresentSurface.For(device, window.Handle, 320, 200);

        surface.WaitForNextFrame();
        device.Context.ClearRenderTargetView(surface.View, new Color4(0.02f, 0.02f, 0.08f, 1.0f));

        // With vsync off the tearing flag rides along only where the capability was reported, and
        // presenting either way must not throw.
        surface.Present(vsync: false);

        Assert.Equal(1, surface.Presented);
    }
}
