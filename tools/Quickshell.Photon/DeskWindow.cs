using System.Runtime.InteropServices;

namespace Quickshell.Photon;

/// <summary>
/// A real, visible, topmost window for the swapchain to present into.
///
/// <para><b>Visible because the measurement is the display.</b> DXGI advances a swapchain's frame
/// statistics only for frames that reached the glass, and an occluded present reaches nothing, so a
/// hidden window would have no photon end to time.</para>
///
/// <para><b>Never activated.</b> It takes no foreground and no input from whoever is at the desk; it
/// sits on top of what they are doing for the length of a run and goes.</para>
/// </summary>
internal sealed class DeskWindow : IPhotonHost
{
    private const uint WsPopup = 0x80000000;
    private const uint WsVisible = 0x10000000;
    private const uint WsExTopmost = 0x00000008;
    private const uint WsExNoActivate = 0x08000000;
    private const uint WsExToolWindow = 0x00000080;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowExW(
        uint exStyle, string className, string? windowName, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(nint window);

    [DllImport("user32.dll")]
    private static extern bool UpdateWindow(nint window);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandleW(string? name);

    internal DeskWindow(int width, int height)
    {
        // A popup and not an overlapped window, so the client area is exactly the swapchain's size
        // and no frame the caption bar adds is something the grid has to be measured around.
        Handle = CreateWindowExW(
            WsExTopmost | WsExNoActivate | WsExToolWindow, "STATIC", "quickshell photon",
            WsPopup | WsVisible, 40, 40, width, height, nint.Zero, nint.Zero, GetModuleHandleW(null),
            nint.Zero);

        if (Handle == nint.Zero)
        {
            throw new InvalidOperationException($"CreateWindowEx failed: {Marshal.GetLastWin32Error()}");
        }

        UpdateWindow(Handle);

        Width = (uint)width;
        Height = (uint)height;
    }

    public nint Handle { get; private set; }

    public uint Width { get; }

    public uint Height { get; }

    public void Dispose()
    {
        if (Handle != nint.Zero)
        {
            DestroyWindow(Handle);
            Handle = nint.Zero;
        }
    }
}
