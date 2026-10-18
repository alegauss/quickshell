using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace Quickshell.App;

/// <summary>
/// Full screen (QS257): the window borderless at its monitor's exact bounds, with nothing of the client's
/// own chrome on screen, so a single pane's swapchain covers the whole monitor. That is the one window
/// shape QS225 timed echoes reaching the glass as an independent flip in, at 24 ms against 33 windowed
/// (<c>benchmarks/results/photon-h.md</c>); every windowed chain was composed.
///
/// <para><b>Hidden and not drawn over.</b> A WPF element over a pane makes its chain composed again, and
/// the strip, the sidebar and the bars are WPF. They are collapsed while the mode lasts and come back as
/// they were, so a window that leaves full screen is the window that entered it.</para>
/// </summary>
public sealed partial class MainWindow
{
    private const uint MonitorDefaultToNearest = 2;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpFrameChanged = 0x0020;

    /// <summary>What the window was before it went full screen, put back when it leaves.</summary>
    private sealed record Windowed(WindowStyle Style, ResizeMode Resize, WindowState State, Rect Bounds,
                                   bool Topmost, Visibility Sidebar, Visibility? MenuBar, Thickness Inset);

    private Windowed? _windowed;

    /// <summary>Whether the window is full screen.</summary>
    public bool FullScreen => _windowed is not null;

    /// <summary>The palette's and F11's command, named for what it will do now.</summary>
    private sealed class GoingFullScreen(MainWindow window) : Doing(window)
    {
        /// <inheritdoc/>
        public override string Name => Window.FullScreen ? "Leave full screen" : "Full screen";

        /// <inheritdoc/>
        public override void Execute(object? parameter) => Window.ToggleFullScreen();
    }

    /// <summary>Binds F11, which is the key Windows Terminal and every browser on this platform use for it.</summary>
    private void BindFullScreen() =>
        InputBindings.Add(new KeyBinding(new GoingFullScreen(this), Key.F11, ModifierKeys.None));

    /// <summary>Enters full screen, or leaves it.</summary>
    public void ToggleFullScreen()
    {
        if (_windowed is null)
        {
            EnterFullScreen();
        }
        else
        {
            LeaveFullScreen();
        }
    }

    private void EnterFullScreen()
    {
        nint handle = new WindowInteropHelper(this).Handle;

        if (handle == nint.Zero || !MonitorPixels(handle, out Native.Rect monitor))
        {
            return;
        }

        bool maximised = WindowState == WindowState.Maximized;

        _windowed = new Windowed(WindowStyle, ResizeMode, WindowState,
                                 maximised ? RestoreBounds : new Rect(Left, Top, Width, Height),
                                 Topmost, _sidebar.Visibility, _menuBar?.Visibility, _terminalInset.Padding);

        // Normal first: WPF keeps a maximised window inside the work area, and the taskbar is not ours.
        WindowState = WindowState.Normal;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;

        _stripFrame.Visibility = Visibility.Collapsed;
        _sidebar.Visibility = Visibility.Collapsed;
        _terminalInset.Padding = new Thickness(0);

        if (_menuBar is not null)
        {
            _menuBar.Visibility = Visibility.Collapsed;
        }

        if (_sessionBar is not null)
        {
            _sessionBar.Visibility = Visibility.Collapsed;
            _statusBar!.Visibility = Visibility.Collapsed;
        }

        // In pixels and not through Left and Width: a size that rounds a pixel short of the monitor is a
        // chain that does not cover it, and gets composed like any windowed one.
        _ = Native.SetWindowPos(handle, nint.Zero, monitor.Left, monitor.Top,
                                monitor.Right - monitor.Left, monitor.Bottom - monitor.Top,
                                SwpNoZOrder | SwpFrameChanged);

        // Topmost only while it is the window in use, so another window brought forward is not hidden
        // behind a terminal the person left.
        Topmost = IsActive;
        Activated += TopmostWhileActive;
        Deactivated += TopmostWhileActive;
    }

    private void LeaveFullScreen()
    {
        Windowed was = _windowed!;

        _windowed = null;

        Activated -= TopmostWhileActive;
        Deactivated -= TopmostWhileActive;
        Topmost = was.Topmost;

        WindowStyle = was.Style;
        ResizeMode = was.Resize;

        _sidebar.Visibility = was.Sidebar;
        _terminalInset.Padding = was.Inset;

        if (_menuBar is not null && was.MenuBar is { } menu)
        {
            _menuBar.Visibility = menu;
        }

        Strip();
        ShowConnection();

        Left = was.Bounds.Left;
        Top = was.Bounds.Top;
        Width = was.Bounds.Width;
        Height = was.Bounds.Height;
        WindowState = was.State;
    }

    private void TopmostWhileActive(object? sender, EventArgs e) => Topmost = IsActive;

    /// <summary>The window's rectangle in pixels, which is what a test compares with the monitor's.</summary>
    public Int32Rect PixelBounds()
    {
        nint handle = new WindowInteropHelper(this).Handle;

        return Native.GetWindowRect(handle, out Native.Rect rect)
            ? new Int32Rect(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top)
            : Int32Rect.Empty;
    }

    /// <summary>The rectangle of the monitor the window is on, in pixels.</summary>
    public Int32Rect MonitorBounds()
    {
        nint handle = new WindowInteropHelper(this).Handle;

        return MonitorPixels(handle, out Native.Rect rect)
            ? new Int32Rect(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top)
            : Int32Rect.Empty;
    }

    private static bool MonitorPixels(nint handle, out Native.Rect bounds)
    {
        Native.MonitorInfo info = new() { Size = Marshal.SizeOf<Native.MonitorInfo>() };
        nint monitor = Native.MonitorFromWindow(handle, MonitorDefaultToNearest);

        if (monitor == nint.Zero || !Native.GetMonitorInfo(monitor, ref info))
        {
            bounds = default;

            return false;
        }

        bounds = info.Monitor;

        return true;
    }

    private static partial class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct Rect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct MonitorInfo
        {
            public int Size;
            public Rect Monitor;
            public Rect Work;
            public uint Flags;
        }

        [LibraryImport("user32.dll")]
        internal static partial nint MonitorFromWindow(nint window, uint flags);

        [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool GetMonitorInfo(nint monitor, ref MonitorInfo info);

        [LibraryImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);

        [LibraryImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool GetWindowRect(nint window, out Rect rect);
    }
}
