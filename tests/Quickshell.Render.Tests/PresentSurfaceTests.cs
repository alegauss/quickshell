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
    /// <para><b>So this asserts what the instrument can see.</b> Every sample sits at one or two —
    /// a queued frame and the one on the glass — and never at three, which is what a renderer
    /// getting ahead of the display would look like. Over thirty runs the deepest sample was two,
    /// twenty-one times one.</para>
    ///
    /// <para><b>What it deliberately does not claim.</b> Not that the wait is worth anything: two
    /// controls were tried and neither discriminated, because one clear per frame with a vsync
    /// present is a workload that can never get ahead of the display at all. The figure the flags
    /// were bought for is input to photon, it needs a frame with real work in it, and it is QS86's
    /// to measure rather than this test's to imply.</para>
    /// </summary>
    [Fact]
    public void TheFrameQueueNeverGetsAheadOfTheDisplay()
    {
        using TestWindow window = new(320, 200);
        using GraphicsDevice device = GraphicsDevice.Open(outputWindow: window.Handle);
        using PresentSurface surface = PresentSurface.For(device, window.Handle, 320, 200);

        int settled = -1;
        long deepest = 0;
        int samples = 0;

        for (int frame = 0; frame < 90; frame++)
        {
            surface.WaitForNextFrame();

            // Statistics arrive when DXGI decides they do, not on a frame count. Ten frames after
            // the first real reading is where the counters have stopped moving relative to
            // each other.
            if (settled < 0 && surface.PresentedOnGlass() > 0)
            {
                settled = frame + 10;
            }

            if (settled >= 0 && frame >= settled)
            {
                deepest = Math.Max(deepest, surface.QueueDepth());
                samples++;
            }

            device.Context.ClearRenderTargetView(surface.View, new Color4(0.02f, 0.02f, 0.08f, 1.0f));
            surface.Present();
        }

        Assert.SkipWhen(surface.Occlusions > 0 || samples < 30,
            $"the window was covered for {surface.Occlusions} frames and DXGI gave {samples} usable " +
            "samples, so the queue could not be measured on this run");

        Assert.True(deepest <= 2,
            $"the frame queue reached {deepest}, which is deeper than one queued frame and the one " +
            "being scanned out - the application is getting ahead of the display");
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
