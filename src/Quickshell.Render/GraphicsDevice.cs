using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Quickshell.Render;

/// <summary>
/// The one D3D11 device this process draws with, the chain that chose its adapter, and the path
/// back from losing it.
///
/// Feature level 11_0 and nothing above it: a cell grid never approaches the draw-call ceiling
/// D3D12 and Vulkan exist to raise, and both are refused in the non-goals.
/// </summary>
public sealed class GraphicsDevice : IDisposable
{
    private readonly IAdapterProbe _probe;
    private readonly nint _outputWindow;
    private readonly bool _debugLayer;
    private readonly List<IDeviceResource> _resources = [];

    private ID3D11Device _device;
    private ID3D11DeviceContext _context;

    private GraphicsDevice(IAdapterProbe probe, nint outputWindow, bool debugLayer,
                           ID3D11Device device, ID3D11DeviceContext context, AdapterChoice choice)
    {
        _probe = probe;
        _outputWindow = outputWindow;
        _debugLayer = debugLayer;
        _device = device;
        _context = context;
        Adapter = choice;
    }

    /// <summary>Which adapter answered, and what was skipped reaching it.</summary>
    public AdapterChoice Adapter { get; private set; }

    /// <summary>The device itself, valid until the next <see cref="Recover"/>.</summary>
    public ID3D11Device Device => _device;

    /// <summary>The immediate context, valid until the next <see cref="Recover"/>.</summary>
    public ID3D11DeviceContext Context => _context;

    /// <summary>How many times this device has been rebuilt after a loss.</summary>
    public int Recoveries { get; private set; }

    /// <summary>
    /// Opens the device. <paramref name="debugLayer"/> defaults to on in a debug build: a
    /// validation message ignored today is a vendor-specific crash in somebody's bug report later.
    /// </summary>
    public static GraphicsDevice Open(IAdapterProbe? probe = null, nint outputWindow = 0, bool? debugLayer = null)
    {
        probe ??= new DxgiAdapterProbe();

        bool debug = debugLayer ?? IsDebugBuild();
        AdapterChoice choice = AdapterChain.Choose(probe, outputWindow);
        (ID3D11Device device, ID3D11DeviceContext context) = Create(choice, debug);

        return new GraphicsDevice(probe, outputWindow, debug, device, context, choice);
    }

    /// <summary>Registers a resource and builds it now, so a caller never has to do both.</summary>
    public void Register(IDeviceResource resource)
    {
        ArgumentNullException.ThrowIfNull(resource);

        _resources.Add(resource);
        resource.Create(_device);
    }

    /// <summary>
    /// Why the device was removed, or null while it is alive. This is the call every frame is
    /// expected to make: <c>DXGI_ERROR_DEVICE_REMOVED</c> comes back from a call that had no other
    /// reason to fail.
    /// </summary>
    public Result? RemovedReason()
    {
        Result reason = _device.DeviceRemovedReason;
        return reason.Success ? null : reason;
    }

    /// <summary>
    /// Throws away every GPU resource, opens a new device on a freshly walked chain, and rebuilds
    /// them. The adapter is chosen again rather than reused: a device is often lost precisely
    /// because the adapter it was on went away.
    /// </summary>
    public void Recover()
    {
        foreach (IDeviceResource resource in _resources)
        {
            resource.Release();
        }

        _context.Dispose();
        _device.Dispose();

        Adapter = AdapterChain.Choose(_probe, _outputWindow);
        (_device, _context) = Create(Adapter, _debugLayer);
        Recoveries++;

        foreach (IDeviceResource resource in _resources)
        {
            resource.Create(_device);
        }
    }

    /// <summary>Releases every registered resource, then the context and the device.</summary>
    public void Dispose()
    {
        foreach (IDeviceResource resource in _resources)
        {
            resource.Release();
        }

        _resources.Clear();
        _context.Dispose();
        _device.Dispose();
    }

    private static (ID3D11Device, ID3D11DeviceContext) Create(AdapterChoice choice, bool debugLayer)
    {
        DeviceCreationFlags flags = DeviceCreationFlags.BgraSupport;

        if (debugLayer)
        {
            flags |= DeviceCreationFlags.Debug;
        }

        FeatureLevel[] levels = [FeatureLevel.Level_11_0];

        // WARP is asked for by driver type; the two hardware links let D3D11 take the adapter DXGI
        // named, which is what makes the chain's answer and the device's adapter the same thing.
        DriverType driverType = choice.Kind == AdapterKind.Warp ? DriverType.Warp : DriverType.Hardware;

        Result result = D3D11.D3D11CreateDevice(
            null, driverType, flags, levels,
            out ID3D11Device device, out ID3D11DeviceContext context);

        if (result.Failure && debugLayer)
        {
            // The debug layer is not installed on every machine, and refusing to start over a
            // missing optional SDK component would be worse than losing the validation.
            result = D3D11.D3D11CreateDevice(
                null, driverType, flags & ~DeviceCreationFlags.Debug, levels,
                out device, out context);
        }

        result.CheckError();
        return (device, context);
    }

    private static bool IsDebugBuild()
    {
#if DEBUG
        return true;
#else
        return false;
#endif
    }
}
