using Vortice.Direct3D11;
using Vortice.DirectComposition;
using Vortice.DXGI;

namespace Quickshell.Photon;

/// <summary>
/// A DirectComposition tree of one visual on a window, for a chain made for composition rather
/// than for the window (QS201).
///
/// <para><b>Why it is a question at all.</b> A chain made for a window is drawn into the desktop by
/// the compositor, and every echo photon timed that way was composed: shown at the vblank after the
/// compositor's own pass. A chain that is a visual's content is one the compositor may instead hand
/// to a hardware overlay plane, which shows it at the next vblank. Whether it does, on this desk, is
/// what the <c>shown as</c> column answers.</para>
/// </summary>
internal sealed class Composer : IDisposable
{
    private readonly IDCompositionDevice _device;
    private readonly IDCompositionTarget _target;
    private readonly IDCompositionVisual _visual;

    internal Composer(ID3D11Device device, nint window)
    {
        using IDXGIDevice dxgi = device.QueryInterface<IDXGIDevice>();

        _device = DComp.DCompositionCreateDevice<IDCompositionDevice>(dxgi);
        _device.CreateTargetForHwnd(window, true, out _target).CheckError();
        _device.CreateVisual(out _visual).CheckError();
        _target.SetRoot(_visual).CheckError();
    }

    /// <summary>Makes a chain the visual's content and commits the tree.</summary>
    internal void Bind(IDXGISwapChain1 chain)
    {
        _visual.SetContent(chain).CheckError();
        _device.Commit().CheckError();
    }

    public void Dispose()
    {
        _visual.Dispose();
        _target.Dispose();
        _device.Dispose();
    }
}
