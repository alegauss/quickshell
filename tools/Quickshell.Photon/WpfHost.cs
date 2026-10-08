using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace Quickshell.Photon;

/// <summary>Where a run's swapchain presents: a window handle and the size of its client area.</summary>
internal interface IPhotonHost : IDisposable
{
    /// <summary>The window the swapchain is created for.</summary>
    nint Handle { get; }

    /// <summary>Its client area in pixels, which the swapchain matches exactly.</summary>
    uint Width { get; }

    /// <summary>See <see cref="Width"/>.</summary>
    uint Height { get; }
}

/// <summary>
/// The client's own window host, rebuilt for timing: a WPF window with its ordinary chrome and the
/// client's theme mode, a <c>WS_CHILD</c> window inside it through <see cref="HwndHost"/>, and the
/// swapchain on the child — which is <c>TerminalPane</c>'s arrangement (QS201).
///
/// <para><b>Its own STA thread, as the client's UI thread is its own.</b> WPF pumps its messages
/// there and the run presents from the caller's thread, which is the split the client's render
/// loop has: the swapchain belongs to the device, and the window only to the thread that made it.
/// </para>
///
/// <para><b>The swapchain matches the child's real client rect</b>, read after layout, and not the
/// size asked for: WPF lays out in device-independent units, and a chain a pixel larger than its
/// window is one the compositor has to clip, which is a reason by itself to compose it.</para>
/// </summary>
internal sealed class WpfHost : IPhotonHost
{
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new();
    private System.Windows.Threading.Dispatcher? _dispatcher;
    private Exception? _failed;

    internal WpfHost(int width, int height)
    {
        _thread = new Thread(() => Pump(width, height)) { IsBackground = true, Name = "photon wpf host" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();

        if (!_ready.Wait(TimeSpan.FromSeconds(30)))
        {
            throw new InvalidOperationException("the WPF host did not come up in thirty seconds");
        }

        if (_failed is not null)
        {
            throw new InvalidOperationException("the WPF host failed to come up", _failed);
        }
    }

    public nint Handle { get; private set; }

    public uint Width { get; private set; }

    public uint Height { get; private set; }

    public void Dispose()
    {
        _dispatcher?.InvokeShutdown();
        _thread.Join(TimeSpan.FromSeconds(10));
        _ready.Dispose();
    }

    private void Pump(int width, int height)
    {
        try
        {
            _dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;

            ChildHost child = new();

            // The client's window, as far as presentation can tell: ordinary chrome, the system's
            // theme mode, never activated so whoever is at the desk keeps the foreground.
            Window window = new()
            {
                Title = "quickshell photon (wpf host)",
                Left = 40,
                Top = 40,
                SizeToContent = SizeToContent.WidthAndHeight,
                ResizeMode = ResizeMode.NoResize,
                Topmost = true,
                ShowActivated = false,
                ShowInTaskbar = false,
                ThemeMode = ThemeMode.System,
                Content = child,
            };

            window.SourceInitialized += (_, _) =>
            {
                // Pixels asked for, in the units WPF lays out in.
                double scale = VisualTreeHelper.GetDpi(window).DpiScaleX;

                child.Width = width / scale;
                child.Height = height / scale;
            };

            window.ContentRendered += (_, _) =>
            {
                Handle = child.Handle;
                _ = GetClientRect(Handle, out ClientRect rect);
                Width = (uint)(rect.Right - rect.Left);
                Height = (uint)(rect.Bottom - rect.Top);
                _ready.Set();
            };

            window.Show();
            System.Windows.Threading.Dispatcher.Run();
            window.Close();
        }
        catch (Exception failed)
        {
            _failed = failed;
            _ready.Set();
        }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(nint window, out ClientRect rect);

    [StructLayout(LayoutKind.Sequential)]
    private struct ClientRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    /// <summary>The child window, made the way <c>PaneClass</c> makes the pane's.</summary>
    private sealed class ChildHost : HwndHost
    {
        private const uint WsChild = 0x40000000;
        private const uint WsVisible = 0x10000000;

        protected override HandleRef BuildWindowCore(HandleRef hwndParent)
        {
            nint child = CreateWindowExW(0, "STATIC", string.Empty, WsChild | WsVisible, 0, 0, 1, 1,
                                         hwndParent.Handle, 0, 0, 0);

            if (child == 0)
            {
                throw new InvalidOperationException($"CreateWindowEx failed: {Marshal.GetLastWin32Error()}");
            }

            return new HandleRef(this, child);
        }

        protected override void DestroyWindowCore(HandleRef hwnd) => DestroyWindow(hwnd.Handle);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern nint CreateWindowExW(uint exStyle, string className, string? windowName, uint style,
                                                   int x, int y, int width, int height, nint parent, nint menu,
                                                   nint instance, nint param);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DestroyWindow(nint window);
    }
}
