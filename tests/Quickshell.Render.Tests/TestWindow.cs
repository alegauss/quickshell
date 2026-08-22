using System.Runtime.InteropServices;

namespace Quickshell.Render.Tests;

/// <summary>
/// A real, visible window for the swapchain tests to present into.
///
/// It is visible on purpose. DXGI does not advance a swapchain's frame statistics for a window
/// nobody can see - an occluded present returns <c>DXGI_STATUS_OCCLUDED</c> and shows nothing - so
/// a hidden window would make the queue-depth measurement report whatever the test wanted.
/// </summary>
internal sealed class TestWindow : IDisposable
{
    private const uint WsOverlapped = 0x00CF0000;
    private const uint WsVisible = 0x10000000;
    private const uint WsExTopmost = 0x00000008;
    private const uint WsExNoActivate = 0x08000000;
    private static readonly nint HwndTopmost = -1;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoActivate = 0x0010;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowExW(
        uint exStyle, string className, string? windowName, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(nint window);

    [DllImport("user32.dll")]
    private static extern bool UpdateWindow(nint window);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(nint window, nint insertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandleW(string? name);

    public TestWindow(int width, int height)
    {
        // Topmost, and that is the measurement rather than a preference: DXGI reports an occluded
        // present as DXGI_STATUS_OCCLUDED and stops advancing frame statistics, so a window this
        // editor happens to cover turns the queue-depth check into a check of nothing.
        Handle = CreateWindowExW(
            WsExTopmost | WsExNoActivate, "STATIC", "quickshell present surface test", WsOverlapped | WsVisible,
            40, 40, width, height, nint.Zero, nint.Zero, GetModuleHandleW(null), nint.Zero);

        if (Handle == nint.Zero)
        {
            throw new InvalidOperationException($"CreateWindowEx failed: {Marshal.GetLastWin32Error()}");
        }

        SetWindowPos(Handle, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
        UpdateWindow(Handle);
    }

    public nint Handle { get; private set; }

    public void Dispose()
    {
        if (Handle != nint.Zero)
        {
            DestroyWindow(Handle);
            Handle = nint.Zero;
        }
    }
}
