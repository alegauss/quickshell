using Vortice.Direct3D11;

namespace Quickshell.Render;

/// <summary>
/// Something that lives on the GPU and therefore dies with the device.
///
/// Device loss is a certainty rather than an edge case - a driver update, a TDR, an external GPU
/// unplugged, a session disconnected and reattached - so everything holding GPU memory registers
/// here and is rebuilt from nothing when the device comes back. What is deliberately *not* here is
/// the terminal's own state: the buffer and the parser live in an assembly this one cannot
/// reference, which is what makes surviving a loss a property of the layering rather than a rule
/// somebody has to remember.
/// </summary>
public interface IDeviceResource
{
    /// <summary>Build everything this holds on the GPU, from CPU-side state that survived.</summary>
    void Create(ID3D11Device device);

    /// <summary>Release every GPU object. Called before the device goes, and again on shutdown.</summary>
    void Release();
}
