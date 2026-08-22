using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Quickshell.Render;

/// <summary>
/// One pane's swapchain and the three flags that buy its latency.
///
/// <para><b>Flip-discard</b> is the baseline: the blt model copies through the desktop compositor
/// and spends a frame for nothing.</para>
///
/// <para><b>The waitable object</b> with a maximum frame latency of one is where the latency is
/// actually won. The render thread waits on the swapchain's own handle rather than on a timer, so
/// it starts a frame when the display is ready for it instead of a queue depth earlier. Left
/// unbought, the runtime buffers up to three frames ahead and every one of those is a keystroke
/// the user has already typed.</para>
///
/// <para><b>Allow-tearing</b>, behind its capability check, is what makes the variable-refresh
/// case honest: on a 144 Hz panel a torn frame arriving now beats a whole frame arriving later.
/// This is a terminal, not a film.</para>
/// </summary>
public sealed class PresentSurface : IDeviceResource, IDisposable
{
    /// <summary>DXGI_STATUS_OCCLUDED: a success code meaning the frame was not shown.</summary>
    private const int DxgiStatusOccluded = unchecked((int)0x087A0001);

    private readonly nint _window;
    private readonly GraphicsDevice _graphics;

    private IDXGISwapChain2? _swapChain;
    private ID3D11RenderTargetView? _view;
    private long _presented;

    private PresentSurface(GraphicsDevice graphics, nint window, uint width, uint height, bool tearing)
    {
        _graphics = graphics;
        _window = window;
        Width = width;
        Height = height;
        TearingAllowed = tearing;
    }

    /// <summary>
    /// Opens a surface on a window and registers it with the device that owns it.
    /// <paramref name="maximumFrameLatency"/> is one for every real surface; it is a parameter so a
    /// measurement can open a second surface at the runtime's default and have something to
    /// compare against.
    /// </summary>
    public static PresentSurface For(GraphicsDevice graphics, nint window, uint width, uint height,
                                     uint maximumFrameLatency = 1)
    {
        ArgumentNullException.ThrowIfNull(graphics);

        PresentSurface surface = new(graphics, window, width, height, SupportsTearing())
        {
            MaximumFrameLatency = maximumFrameLatency,
        };

        graphics.Register(surface);
        return surface;
    }

    /// <summary>How many frames the runtime may queue ahead. One, unless a measurement says otherwise.</summary>
    public uint MaximumFrameLatency { get; private init; } = 1;

    /// <summary>Whether this machine reported <c>DXGI_FEATURE_PRESENT_ALLOW_TEARING</c>.</summary>
    public bool TearingAllowed { get; }

    /// <summary>The back buffer's width in pixels.</summary>
    public uint Width { get; private set; }

    /// <summary>The back buffer's height in pixels.</summary>
    public uint Height { get; private set; }

    /// <summary>The swapchain's own wait handle. The render thread waits here and nowhere else.</summary>
    public nint FrameLatencyWaitHandle { get; private set; }

    /// <summary>The target to draw this frame into.</summary>
    public ID3D11RenderTargetView View => _view ?? throw new InvalidOperationException("the surface has no device");

    /// <summary>Presents this surface made that DXGI accepted for display.</summary>
    public long Presented => _presented;

    /// <summary>
    /// Presents DXGI answered <c>DXGI_STATUS_OCCLUDED</c> to: the window was covered and the frame
    /// went nowhere. They are counted apart and not as presents, because a frame that never
    /// reached the glass is not a frame in a queue - counting it made the queue depth climb by one
    /// permanently the first time a notification covered the window.
    /// </summary>
    public long Occlusions { get; private set; }

    /// <summary>
    /// Presents issued but not yet on the glass.
    ///
    /// <para>Read it <b>after</b> <see cref="WaitForNextFrame"/> and before the frame is drawn.
    /// That is where a maximum frame latency of one means something: at the instant the wait
    /// returns, at most one frame is outstanding. Sampled after a present instead, the frame just
    /// submitted is counted too and two is the ordinary answer.</para>
    /// </summary>
    public long QueueDepth()
    {
        if (_swapChain is null)
        {
            return 0;
        }

        if (_swapChain.GetFrameStatistics(out FrameStatistics statistics).Failure)
        {
            // DXGI refuses statistics until a frame has actually been shown, which is a state and
            // not an error: nothing is queued if nothing has been presented.
            return 0;
        }

        return Math.Max(0, _presented - statistics.PresentCount);
    }

    /// <summary>
    /// Frames DXGI says have actually been shown. It is published so a queue-depth check can tell
    /// a shallow queue from statistics that were never available: without it, a machine where DXGI
    /// refuses statistics would pass that check by reporting nothing at all.
    /// </summary>
    public long PresentedOnGlass()
    {
        if (_swapChain is null || _swapChain.GetFrameStatistics(out FrameStatistics statistics).Failure)
        {
            return 0;
        }

        return statistics.PresentCount;
    }

    /// <summary>Blocks until the swapchain is ready for the next frame.</summary>
    public void WaitForNextFrame(int timeoutMilliseconds = 1000)
    {
        if (FrameLatencyWaitHandle == nint.Zero)
        {
            return;
        }

        _ = Native.WaitForSingleObjectEx(FrameLatencyWaitHandle, (uint)timeoutMilliseconds, false);
    }

    /// <summary>
    /// Puts the frame on the glass. With <paramref name="vsync"/> off and tearing available the
    /// tearing flag goes with it, which is the whole point of asking for the capability.
    /// </summary>
    public void Present(bool vsync = true)
    {
        if (_swapChain is null)
        {
            throw new InvalidOperationException("the surface has no device");
        }

        PresentFlags flags = !vsync && TearingAllowed ? PresentFlags.AllowTearing : PresentFlags.None;
        SharpGen.Runtime.Result result = _swapChain.Present(vsync ? 1u : 0u, flags);

        if (result.Code == DxgiStatusOccluded)
        {
            Occlusions++;
            return;
        }

        result.CheckError();
        _presented++;
    }

    /// <summary>
    /// Reallocates the buffers at a new size and draws the first frame at that size before
    /// returning, which is what keeps a resize from flashing: the caller's <paramref name="draw"/>
    /// runs while the window is still showing the old frame.
    /// </summary>
    public void Resize(uint width, uint height, Action<PresentSurface>? draw = null)
    {
        if (_swapChain is null || (width == Width && height == Height))
        {
            Width = width;
            Height = height;
            return;
        }

        _view?.Dispose();
        _view = null;

        // ResizeBuffers, never a stretch: the swapchain is Scaling.None, so a stretched frame is
        // not something this surface can accidentally show.
        _swapChain.ResizeBuffers(0, width, height, Format.Unknown, Flags());

        Width = width;
        Height = height;
        _view = CreateView(_graphics.Device, _swapChain);

        draw?.Invoke(this);
    }

    void IDeviceResource.Create(ID3D11Device device)
    {
        using IDXGIFactory2 factory = DXGI.CreateDXGIFactory1<IDXGIFactory2>();

        SwapChainDescription1 description = new()
        {
            Width = Width,
            Height = Height,
            Format = Format.B8G8R8A8_UNorm,
            BufferCount = 2,
            BufferUsage = Usage.RenderTargetOutput,
            SwapEffect = SwapEffect.FlipDiscard,
            SampleDescription = new SampleDescription(1, 0),
            AlphaMode = Vortice.DXGI.AlphaMode.Ignore,
            Scaling = Scaling.None,
            Flags = Flags(),
        };

        using IDXGISwapChain1 created = factory.CreateSwapChainForHwnd(device, _window, description);
        _swapChain = created.QueryInterface<IDXGISwapChain2>();

        // One, and not the runtime's default of three. Everything above is a frame of somebody's
        // typing sitting in a queue.
        _swapChain.MaximumFrameLatency = MaximumFrameLatency;
        FrameLatencyWaitHandle = _swapChain.FrameLatencyWaitableObject;

        _view = CreateView(device, _swapChain);
        _presented = 0;
        Occlusions = 0;
    }

    void IDeviceResource.Release()
    {
        _view?.Dispose();
        _view = null;
        _swapChain?.Dispose();
        _swapChain = null;
        FrameLatencyWaitHandle = nint.Zero;
    }

    /// <summary>Releases the swapchain. The device it was registered with is not disposed here.</summary>
    public void Dispose() => ((IDeviceResource)this).Release();

    private static SwapChainFlags Flags()
    {
        SwapChainFlags flags = SwapChainFlags.FrameLatencyWaitableObject;

        if (SupportsTearing())
        {
            flags |= SwapChainFlags.AllowTearing;
        }

        return flags;
    }

    private static ID3D11RenderTargetView CreateView(ID3D11Device device, IDXGISwapChain2 swapChain)
    {
        using ID3D11Texture2D backBuffer = swapChain.GetBuffer<ID3D11Texture2D>(0);
        return device.CreateRenderTargetView(backBuffer);
    }

    private static bool SupportsTearing()
    {
        try
        {
            using IDXGIFactory5 factory = DXGI.CreateDXGIFactory1<IDXGIFactory5>();
            return factory.PresentAllowTearing;
        }
        catch (SharpGen.Runtime.SharpGenException)
        {
            // No IDXGIFactory5 means a Windows old enough not to have the feature at all.
            return false;
        }
    }
}
