using System.Runtime.InteropServices;

namespace Quickshell.Render;

/// <summary>The few Win32 calls the render layer makes directly.</summary>
internal static class Native
{
    /// <summary>
    /// Waits on the swapchain's frame-latency handle. It is a raw handle from DXGI rather than a
    /// managed <c>WaitHandle</c>, so this is the wait rather than a wrapper around one.
    /// </summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern uint WaitForSingleObjectEx(nint handle, uint milliseconds, bool alertable);
}
