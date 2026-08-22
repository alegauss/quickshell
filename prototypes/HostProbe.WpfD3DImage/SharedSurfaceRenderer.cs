using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using HostProbe.Core;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.Direct3D9;
using Vortice.Mathematics;

namespace HostProbe.WpfD3DImage;

/// <summary>
/// D3D11 drawing into a texture that a D3D9Ex device owns, handed to WPF as a
/// <see cref="D3DImage"/>. This is the whole interop path the option is judged on: the pane has
/// no swapchain of its own, so nothing here decides when a frame reaches the glass - WPF's
/// compositor does, and the measurement is what that costs.
/// </summary>
public sealed class SharedSurfaceRenderer : IDisposable
{
    private readonly Pane _pane;
    private readonly int _width;
    private readonly int _height;

    private readonly IDirect3D9Ex _d3d9;
    private readonly IDirect3DDevice9Ex _device9;
    private readonly IDirect3DTexture9 _texture9;
    private readonly IDirect3DSurface9 _surface9;

    private readonly ID3D11Device _device11;
    private readonly ID3D11DeviceContext _context11;
    private readonly ID3D11Texture2D _shared11;
    private readonly ID3D11RenderTargetView _view;

    public SharedSurfaceRenderer(Pane pane, nint windowHandle, int width, int height)
    {
        _pane = pane;
        _width = Math.Max(1, width);
        _height = Math.Max(1, height);

        _d3d9 = D3D9.Direct3DCreate9Ex();

        PresentParameters parameters = new()
        {
            Windowed = true,
            SwapEffect = Vortice.Direct3D9.SwapEffect.Discard,
            DeviceWindowHandle = windowHandle,
            PresentationInterval = PresentInterval.Immediate,
            BackBufferFormat = Vortice.Direct3D9.Format.Unknown,
            BackBufferWidth = 1,
            BackBufferHeight = 1,
        };

        _device9 = _d3d9.CreateDeviceEx(
            0,
            Vortice.Direct3D9.DeviceType.Hardware,
            windowHandle,
            CreateFlags.HardwareVertexProcessing | CreateFlags.Multithreaded | CreateFlags.FpuPreserve,
            parameters);

        nint shared = nint.Zero;
        _texture9 = _device9.CreateTexture(
            (uint)_width, (uint)_height, 1,
            Vortice.Direct3D9.Usage.RenderTarget,
            Vortice.Direct3D9.Format.A8R8G8B8,
            Pool.Default,
            ref shared);

        _surface9 = _texture9.GetSurfaceLevel(0);

        D3D11.D3D11CreateDevice(
            null,
            DriverType.Hardware,
            DeviceCreationFlags.BgraSupport,
            [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0],
            out _device11,
            out _context11).CheckError();

        _shared11 = _device11.OpenSharedResource<ID3D11Texture2D>(shared);
        _view = _device11.CreateRenderTargetView(_shared11);

        Image = new D3DImage();
        Image.Lock();
        Image.SetBackBuffer(D3DResourceType.IDirect3DSurface9, _surface9.NativePointer);
        Image.Unlock();
    }

    public D3DImage Image { get; }

    /// <summary>
    /// Draws and publishes one frame. It is called from <c>CompositionTarget.Rendering</c>,
    /// because that is the only clock this host has.
    /// </summary>
    public void RenderOnce()
    {
        if (!Image.IsFrontBufferAvailable)
        {
            return;
        }

        (float r, float g, float b) = _pane.Colour();
        _context11.ClearRenderTargetView(_view, new Color4(r, g, b, 1.0f));
        _context11.Flush();

        Image.Lock();
        Image.AddDirtyRect(new Int32Rect(0, 0, _width, _height));
        Image.Unlock();

        _pane.CountFrame();
    }

    public void Dispose()
    {
        _view.Dispose();
        _shared11.Dispose();
        _context11.Dispose();
        _device11.Dispose();
        _surface9.Dispose();
        _texture9.Dispose();
        _device9.Dispose();
        _d3d9.Dispose();
    }
}
