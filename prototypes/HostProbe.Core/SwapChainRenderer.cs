using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace HostProbe.Core;

/// <summary>
/// A D3D11 device presenting through a DXGI swapchain on its own thread, which is the shape
/// both the child-HWND host and the SwapChainPanel host use. The D3DImage host cannot: it has
/// no swapchain, and that difference is the measurement.
/// </summary>
public sealed class SwapChainRenderer : IDisposable
{
    private readonly Pane _pane;
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly IDXGISwapChain1 _swapChain;
    private ID3D11RenderTargetView _view;
    private Thread? _loop;
    private volatile bool _running;

    private SwapChainRenderer(Pane pane, ID3D11Device device, ID3D11DeviceContext context, IDXGISwapChain1 swapChain)
    {
        _pane = pane;
        _device = device;
        _context = context;
        _swapChain = swapChain;
        _view = CreateView();
    }

    /// <summary>The swapchain, for a host that has to hand it to a XAML panel.</summary>
    public IDXGISwapChain1 SwapChain => _swapChain;

    public static SwapChainRenderer ForWindow(Pane pane, nint hwnd, int width, int height)
    {
        (ID3D11Device device, ID3D11DeviceContext context) = CreateDevice();
        using IDXGIFactory2 factory = DXGI.CreateDXGIFactory1<IDXGIFactory2>();
        IDXGISwapChain1 swapChain = factory.CreateSwapChainForHwnd(device, hwnd, Describe(width, height));
        return new SwapChainRenderer(pane, device, context, swapChain);
    }

    public static SwapChainRenderer ForComposition(Pane pane, int width, int height)
    {
        (ID3D11Device device, ID3D11DeviceContext context) = CreateDevice();
        using IDXGIFactory2 factory = DXGI.CreateDXGIFactory1<IDXGIFactory2>();
        IDXGISwapChain1 swapChain = factory.CreateSwapChainForComposition(device, Describe(width, height));
        return new SwapChainRenderer(pane, device, context, swapChain);
    }

    public void Start()
    {
        _running = true;
        _loop = new Thread(Loop) { IsBackground = true, Name = "probe-present" };
        _loop.Start();
    }

    public void Dispose()
    {
        _running = false;
        _loop?.Join(500);
        _view.Dispose();
        _swapChain.Dispose();
        _context.Dispose();
        _device.Dispose();
    }

    private void Loop()
    {
        while (_running)
        {
            (float r, float g, float b) = _pane.Colour();
            _context.ClearRenderTargetView(_view, new Color4(r, g, b, 1.0f));
            _swapChain.Present(1, PresentFlags.None);
            _pane.CountFrame();
        }
    }

    private ID3D11RenderTargetView CreateView()
    {
        using ID3D11Texture2D backBuffer = _swapChain.GetBuffer<ID3D11Texture2D>(0);
        return _device.CreateRenderTargetView(backBuffer);
    }

    private static SwapChainDescription1 Describe(int width, int height) => new()
    {
        Width = (uint)Math.Max(1, width),
        Height = (uint)Math.Max(1, height),
        Format = Format.B8G8R8A8_UNorm,
        BufferCount = 2,
        BufferUsage = Usage.RenderTargetOutput,
        SwapEffect = SwapEffect.FlipSequential,
        SampleDescription = new SampleDescription(1, 0),
        AlphaMode = Vortice.DXGI.AlphaMode.Ignore,
        Scaling = Scaling.Stretch,
    };

    private static (ID3D11Device, ID3D11DeviceContext) CreateDevice()
    {
        D3D11.D3D11CreateDevice(
            null,
            DriverType.Hardware,
            DeviceCreationFlags.BgraSupport,
            [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0],
            out ID3D11Device device,
            out ID3D11DeviceContext context).CheckError();

        return (device, context);
    }
}
