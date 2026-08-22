using System.Runtime.InteropServices;
using Vortice.DXGI;

namespace Quickshell.Render;

/// <summary>The chain's three questions, answered by DXGI.</summary>
public sealed class DxgiAdapterProbe : IAdapterProbe
{
    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint window, uint flags);

    private const uint MonitorDefaultToNearest = 2;

    /// <summary>
    /// The adapter whose output contains the window's monitor. DXGI has no direct answer, so the
    /// window's monitor is found first and then matched against every output of every adapter.
    /// </summary>
    public AdapterInfo? ForOutputWindow(nint outputWindow)
    {
        if (outputWindow == nint.Zero)
        {
            return null;
        }

        nint monitor = MonitorFromWindow(outputWindow, MonitorDefaultToNearest);

        if (monitor == nint.Zero)
        {
            return null;
        }

        using IDXGIFactory1 factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();

        for (uint index = 0; factory.EnumAdapters1(index, out IDXGIAdapter1 adapter).Success; index++)
        {
            using (adapter)
            {
                for (uint outputIndex = 0; adapter.EnumOutputs(outputIndex, out IDXGIOutput output).Success; outputIndex++)
                {
                    using (output)
                    {
                        if (output.Description.Monitor == monitor)
                        {
                            return Describe(adapter);
                        }
                    }
                }
            }
        }

        return null;
    }

    /// <summary>The first non-software adapter DXGI enumerates.</summary>
    public AdapterInfo? DefaultHardware()
    {
        using IDXGIFactory1 factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();

        for (uint index = 0; factory.EnumAdapters1(index, out IDXGIAdapter1 adapter).Success; index++)
        {
            using (adapter)
            {
                // The software adapter is enumerated alongside the hardware ones and is not what
                // this link is for: reaching it here would hide the fact that WARP was the answer.
                if ((adapter.Description1.Flags & AdapterFlags.Software) != 0)
                {
                    continue;
                }

                return Describe(adapter);
            }
        }

        return null;
    }

    /// <summary>The software rasteriser, which is always available.</summary>
    public AdapterInfo Warp() => new("WARP software rasteriser", 0);

    private static AdapterInfo Describe(IDXGIAdapter1 adapter) =>
        new(adapter.Description1.Description, (uint)adapter.Description1.VendorId);
}
