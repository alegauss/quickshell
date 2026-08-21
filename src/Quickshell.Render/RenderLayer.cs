namespace Quickshell.Render;

/// <summary>
/// Names the layer this assembly is. The D3D11 code lands here; it may read what the
/// terminal holds and may never see a network type.
/// </summary>
public static class RenderLayer
{
    /// <summary>The layer's name, as the layering test and the diagnostics both spell it.</summary>
    public const string Name = "render";
}
