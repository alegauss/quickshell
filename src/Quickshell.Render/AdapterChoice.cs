namespace Quickshell.Render;

/// <summary>Which link of the adapter chain answered.</summary>
public enum AdapterKind
{
    /// <summary>The adapter the output window actually sits on.</summary>
    OutputWindow,

    /// <summary>The default hardware adapter, when the window's own could not be determined.</summary>
    DefaultHardware,

    /// <summary>
    /// The software rasteriser. Not a curiosity: it is what runs inside an RDP session and on a
    /// machine whose driver is mid-update.
    /// </summary>
    Warp,
}

/// <summary>One adapter the chain considered, as DXGI describes it.</summary>
/// <param name="Description">The adapter's own name.</param>
/// <param name="VendorId">The PCI vendor id, or 0 for WARP.</param>
public sealed record AdapterInfo(string Description, uint VendorId);

/// <summary>
/// The adapter the device opened on, which link of the chain it came from, and what was skipped to
/// get there. The skipped list is not diagnostics for its own sake: a client silently running on
/// WARP is a client that is slow for a reason nobody can see.
/// </summary>
/// <param name="Kind">Which link answered.</param>
/// <param name="Adapter">The adapter itself, or null where DXGI named none and D3D11 chose.</param>
/// <param name="Skipped">Each earlier link that did not answer, and why.</param>
public sealed record AdapterChoice(AdapterKind Kind, AdapterInfo? Adapter, IReadOnlyList<string> Skipped)
{
    /// <summary>One line for a log or a diagnostics pane.</summary>
    public override string ToString()
    {
        string name = Adapter?.Description ?? "unnamed";
        string skipped = Skipped.Count == 0 ? "" : $" (skipped: {string.Join("; ", Skipped)})";
        return $"{Kind}: {name}{skipped}";
    }
}
