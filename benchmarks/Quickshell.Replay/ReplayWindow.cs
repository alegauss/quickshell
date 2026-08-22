using System.Runtime.InteropServices;

namespace Quickshell.Replay;

/// <summary>
/// A window for the render arm's swapchain to exist against.
///
/// <para>Deliberately <b>not</b> visible and never presented into. This arm measures what a frame
/// costs to build and draw; a present would put the display's refresh rate in the middle of that
/// number and cap the whole replay at sixty frames a second whatever the code does.</para>
/// </summary>
internal sealed class ReplayWindow : IDisposable
{
    private const uint WsOverlapped = 0x00CF0000;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowExW(
        uint exStyle, string className, string? windowName, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(nint window);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandleW(string? name);

    internal ReplayWindow(int width, int height)
    {
        Handle = CreateWindowExW(
            0, "STATIC", "quickshell replay", WsOverlapped,
            0, 0, width, height, nint.Zero, nint.Zero, GetModuleHandleW(null), nint.Zero);

        if (Handle == nint.Zero)
        {
            throw new InvalidOperationException($"CreateWindowEx failed: {Marshal.GetLastWin32Error()}");
        }
    }

    internal nint Handle { get; private set; }

    public void Dispose()
    {
        if (Handle != nint.Zero)
        {
            DestroyWindow(Handle);
            Handle = nint.Zero;
        }
    }
}
