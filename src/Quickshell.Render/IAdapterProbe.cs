namespace Quickshell.Render;

/// <summary>
/// The three questions the adapter chain asks, in order. Splitting them out is what lets the
/// chain's order be tested without a machine whose driver is actually broken - the case the chain
/// exists for is the one that cannot be arranged on demand.
/// </summary>
public interface IAdapterProbe
{
    /// <summary>The adapter the output window sits on, or null if that cannot be determined.</summary>
    AdapterInfo? ForOutputWindow(nint outputWindow);

    /// <summary>The default hardware adapter, or null if there is none this process can open.</summary>
    AdapterInfo? DefaultHardware();

    /// <summary>The software rasteriser. It is the last link and it does not get to fail.</summary>
    AdapterInfo Warp();
}

/// <summary>
/// The chain itself, which is pure decision and no D3D: window, then default hardware, then WARP.
/// </summary>
public static class AdapterChain
{
    /// <summary>Walks the chain and reports which link answered.</summary>
    public static AdapterChoice Choose(IAdapterProbe probe, nint outputWindow)
    {
        ArgumentNullException.ThrowIfNull(probe);

        List<string> skipped = [];

        if (outputWindow != nint.Zero)
        {
            AdapterInfo? window = probe.ForOutputWindow(outputWindow);

            if (window is not null)
            {
                return new AdapterChoice(AdapterKind.OutputWindow, window, skipped);
            }

            skipped.Add("no adapter owns the output window");
        }
        else
        {
            skipped.Add("no output window was named");
        }

        AdapterInfo? hardware = probe.DefaultHardware();

        if (hardware is not null)
        {
            return new AdapterChoice(AdapterKind.DefaultHardware, hardware, skipped);
        }

        skipped.Add("no hardware adapter could be opened");

        return new AdapterChoice(AdapterKind.Warp, probe.Warp(), skipped);
    }
}
