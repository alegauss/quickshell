using System.Runtime.InteropServices;

namespace HostProbe.Core;

/// <summary>
/// A child HWND with its own window class and its own message loop entry, which is what the
/// child-window host gives each pane. It receives the click itself, because that is exactly
/// the airspace it takes: nothing on the WPF side sees a mouse event over this rectangle.
/// </summary>
public sealed class ChildWindow
{
    private delegate nint WndProcDelegate(nint hwnd, uint msg, nint wParam, nint lParam);

    private const uint WmLeftButtonDown = 0x0201;
    private const uint WsChild = 0x40000000;
    private const uint WsVisible = 0x10000000;
    private const uint WsClipSiblings = 0x04000000;

    // The delegate must outlive the window, or the first message lands in freed memory.
    private static WndProcDelegate? _procKeepAlive;
    private static bool _registered;
    private static Action? _onClick;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClassEx
    {
        public uint Size;
        public uint Style;
        public nint WndProc;
        public int ClassExtra;
        public int WindowExtra;
        public nint Instance;
        public nint Icon;
        public nint Cursor;
        public nint Background;
        public nint MenuName;
        public nint ClassName;
        public nint IconSmall;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassExW(ref WndClassEx wndClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowExW(
        uint exStyle, string className, string? windowName, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint DefWindowProcW(nint hwnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(nint hwnd);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandleW(string? name);

    private const string ClassName = "QuickshellProbePane";

    public static nint Create(nint parent, int width, int height, Action onClick)
    {
        _onClick = onClick;
        EnsureClass();

        return CreateWindowExW(
            0, ClassName, null, WsChild | WsVisible | WsClipSiblings,
            0, 0, width, height, parent, nint.Zero, GetModuleHandleW(null), nint.Zero);
    }

    public static void Destroy(nint hwnd)
    {
        if (hwnd != nint.Zero)
        {
            DestroyWindow(hwnd);
        }
    }

    private static void EnsureClass()
    {
        if (_registered)
        {
            return;
        }

        _procKeepAlive = Proc;

        nint className = Marshal.StringToHGlobalUni(ClassName);

        WndClassEx description = new()
        {
            Size = (uint)Marshal.SizeOf<WndClassEx>(),
            Style = 0,
            WndProc = Marshal.GetFunctionPointerForDelegate(_procKeepAlive),
            Instance = GetModuleHandleW(null),
            ClassName = className,
        };

        if (RegisterClassExW(ref description) == 0)
        {
            throw new InvalidOperationException($"RegisterClassEx failed: {Marshal.GetLastWin32Error()}");
        }

        _registered = true;
    }

    private static nint Proc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        if (msg == WmLeftButtonDown)
        {
            _onClick?.Invoke();
            return nint.Zero;
        }

        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }
}
