using System.Runtime.InteropServices;

namespace HostProbe.Core;

/// <summary>
/// The click is injected and the pixel is read off the composited desktop, which is what
/// makes the number a measurement of the whole path rather than of the application's half
/// of it: whatever the compositor adds after the present is inside the interval.
/// </summary>
internal static class Native
{
    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public nint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public MouseInput Mouse;
        // The union is as wide as KEYBDINPUT/HARDWAREINPUT; MOUSEINPUT is the widest on x64,
        // so nothing further is needed here.
    }

    private const uint InputMouse = 0;
    private const uint MouseEventMove = 0x0001;
    private const uint MouseEventLeftDown = 0x0002;
    private const uint MouseEventLeftUp = 0x0004;
    private const uint MouseEventAbsolute = 0x8000;
    private const uint MouseEventVirtualDesk = 0x4000;

    private const int SmXVirtualScreen = 76;
    private const int SmYVirtualScreen = 77;
    private const int SmCxVirtualScreen = 78;
    private const int SmCyVirtualScreen = 79;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, Input[] inputs, int size);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern nint GetDC(nint hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(nint hWnd, nint hdc);

    [DllImport("gdi32.dll")]
    private static extern uint GetPixel(nint hdc, int x, int y);

    [DllImport("user32.dll")]
    internal static extern bool SetForegroundWindow(nint hWnd);

    /// <summary>Moves the pointer to a screen point and clicks, as one input batch.</summary>
    internal static void ClickAt(int screenX, int screenY)
    {
        int left = GetSystemMetrics(SmXVirtualScreen);
        int top = GetSystemMetrics(SmYVirtualScreen);
        int width = GetSystemMetrics(SmCxVirtualScreen);
        int height = GetSystemMetrics(SmCyVirtualScreen);

        int dx = (int)((screenX - left) * 65535.0 / (width - 1));
        int dy = (int)((screenY - top) * 65535.0 / (height - 1));

        uint absolute = MouseEventAbsolute | MouseEventVirtualDesk;

        Input[] batch =
        [
            Mouse(dx, dy, MouseEventMove | absolute),
            Mouse(dx, dy, MouseEventLeftDown | absolute),
            Mouse(dx, dy, MouseEventLeftUp | absolute),
        ];

        SendInput((uint)batch.Length, batch, Marshal.SizeOf<Input>());
    }

    /// <summary>Parks the pointer somewhere that overlaps nothing the probe is looking at.</summary>
    internal static void ParkPointer(int screenX, int screenY) => SetCursorPos(screenX, screenY);

    /// <summary>Reads one pixel off the composited desktop. Returns 0x00BBGGRR.</summary>
    internal static uint ReadScreenPixel(int screenX, int screenY)
    {
        nint hdc = GetDC(nint.Zero);

        try
        {
            return GetPixel(hdc, screenX, screenY);
        }
        finally
        {
            ReleaseDC(nint.Zero, hdc);
        }
    }

    private static Input Mouse(int dx, int dy, uint flags) => new()
    {
        Type = InputMouse,
        Mouse = new MouseInput { Dx = dx, Dy = dy, Flags = flags },
    };
}
