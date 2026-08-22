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
    /// The line this comes from is falsified by a frame-latency measurement showing a queue depth
    /// above one. This measures it, and the three attempts that came first are recorded because
    /// each was wrong in a way that looked right.
    ///
    /// <para><b>Sampled after the present</b>, the frame just submitted is counted, so two is the
    /// ordinary answer. <b>Sampled after the wait</b>, it still read two about one run in eight,
    /// and not from occlusion - that is counted separately now and was zero on those runs. The
    /// cause is the instrument: <c>FrameStatistics.PresentCount</c> is refreshed at vblank, so a
    /// sample taken between vblanks is up to one frame stale and the difference overstates the
    /// queue by up to one. Hence the allowance of 1.5 below rather than a hard 1.</para>
    ///
    /// <para><b>What this deliberately does not claim.</b> Two controls were tried and neither
    /// discriminated: latency 1 against latency 3 both averaged 0.98, and waiting on the handle
    /// against not waiting also both averaged 0.98. The reason is the workload - one clear per
    /// frame with a vsync present, where <c>Present</c> blocks on the flip anyway and the
    /// application can never get ahead of the display. The queue this measures is one deep, which
    /// is what the line asks; <b>how much the wait is worth</b> needs a frame with real work in it
    /// and is not measured here.</para>
    /// </summary>
    [Fact]
    public void TheFrameQueueIsOneDeep()
    {
        using TestWindow window = new(320, 200);
        using GraphicsDevice device = GraphicsDevice.Open(outputWindow: window.Handle);
        using PresentSurface surface = PresentSurface.For(device, window.Handle, 320, 200);

        long total = 0;
        int samples = 0;

        for (int frame = 0; frame < 60; frame++)
        {
            surface.WaitForNextFrame();

            if (frame > 10)
            {
                total += surface.QueueDepth();
                samples++;
            }

            device.Context.ClearRenderTargetView(surface.View, new Color4(0.02f, 0.02f, 0.08f, 1.0f));
            surface.Present();
        }

        Assert.SkipWhen(surface.Occlusions > 0 || surface.PresentedOnGlass() == 0,
            $"the test window was covered for {surface.Occlusions} of the frames, so the frame " +
            "queue could not be measured on this run");

        double mean = (double)total / samples;

        Assert.True(mean <= 1.5,
            $"the frame queue averaged {mean:F2}, which is more than one plus the instrument's own " +
            "one-frame skew");
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
