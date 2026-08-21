namespace Quickshell.Transport;

/// <summary>
/// Names the layer this assembly is. The protocol seam lands here, and no type from it may
/// reach the render thread.
/// </summary>
public static class TransportLayer
{
    /// <summary>The layer's name, as the layering test and the diagnostics both spell it.</summary>
    public const string Name = "transport";
}
