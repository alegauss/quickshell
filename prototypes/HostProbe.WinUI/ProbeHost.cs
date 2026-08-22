using System.Runtime.InteropServices;
using HostProbe.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using WinRT;

namespace HostProbe.WinUI;

/// <summary>
/// WinUI 3 with a <c>SwapChainPanel</c>: the pane keeps its own swapchain and its own present
/// clock, and the XAML above it composes over the panel without airspace. What it costs is the
/// Windows App SDK dependency, which this project carries unpackaged on purpose.
/// </summary>
public sealed class ProbeHost : IProbeHost
{
    [ComImport]
    [Guid("63aad0b8-7c24-40ff-85a8-640d944cc325")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISwapChainPanelNative
    {
        void SetSwapChain(nint swapChain);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PointNative
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(nint hWnd, ref PointNative point);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(nint hWnd, nint insertAfter, int x, int y, int cx, int cy, uint flags);

    private static readonly nint HwndTopmost = -1;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;

    private readonly Window _window = new();
    private readonly SwapChainPanel _panel = new();
    private readonly ComboBox _dropdown = new();
    private SwapChainRenderer? _renderer;
    private ContentDialog? _modal;

    public ProbeHost()
    {
        _window.Title = "quickshell host probe - WinUI 3 SwapChainPanel";

        _dropdown.Width = 240;
        _dropdown.Margin = new Thickness(12);
        _dropdown.HorizontalAlignment = HorizontalAlignment.Left;

        for (int item = 0; item < 12; item++)
        {
            _dropdown.Items.Add($"Session {item + 1} - overlapping the pane");
        }

        _dropdown.SelectedIndex = 0;

        _panel.PointerPressed += OnPointerPressed;

        Grid layout = new();

        // A XAML element with no Background is not hit-testable. The SwapChainPanel has none and
        // must not get one, so the click is caught by the grid underneath it, which does: without
        // this every latency trial times out on a pane that is drawing perfectly.
        layout.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        layout.PointerPressed += OnPointerPressed;
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        Grid.SetRow(_dropdown, 0);
        Grid.SetRow(_panel, 1);
        layout.Children.Add(_dropdown);
        layout.Children.Add(_panel);

        _window.Content = layout;
        _panel.SizeChanged += OnPanelSized;
    }

    public string HostName => "winui-swapchainpanel";

    public Pane Pane { get; } = new();

    public nint WindowHandle => WinRT.Interop.WindowNative.GetWindowHandle(_window);

    public void Show()
    {
        _window.Activate();

        // As in the other two hosts: the driver injects real clicks at the pane's screen point, so
        // the window has to be the one that is actually there. This goes through SetWindowPos
        // rather than the presenter because reading AppWindow before the window is up crashed
        // Microsoft.UI.Xaml with a stowed E_FAIL, and the handle is available either way.
        SetWindowPos(WindowHandle, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
    }

    public void RunOnUi(Action action)
    {
        using ManualResetEventSlim done = new(false);

        if (!_window.DispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    action();
                }
                finally
                {
                    done.Set();
                }
            }))
        {
            return;
        }

        done.Wait(5000);
    }

    public (int X, int Y, int Width, int Height) PaneScreenRect
    {
        get
        {
            double scale = _panel.XamlRoot?.RasterizationScale ?? 1.0;
            Point offset = _panel.TransformToVisual(null).TransformPoint(new Point(0, 0));

            PointNative origin = new() { X = (int)(offset.X * scale), Y = (int)(offset.Y * scale) };
            ClientToScreen(WindowHandle, ref origin);

            return (origin.X, origin.Y, (int)(_panel.ActualWidth * scale), (int)(_panel.ActualHeight * scale));
        }
    }

    public void OpenDropdown() => _dropdown.IsDropDownOpen = true;

    public void CloseDropdown() => _dropdown.IsDropDownOpen = false;

    public void ShowModal()
    {
        _modal = new ContentDialog
        {
            XamlRoot = _panel.XamlRoot,
            Title = "Modal over a running pane",
            Content = "This dialog is modal and it overlaps a pane that is still presenting.",
            CloseButtonText = "Close",
        };

        _ = _modal.ShowAsync();
    }

    public void CloseModal()
    {
        _modal?.Hide();
        _modal = null;
    }

    public void Shutdown()
    {
        _renderer?.Dispose();
        _renderer = null;
        _window.Close();
        Application.Current.Exit();
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e) => Pane.Click();

    private void OnPanelSized(object sender, SizeChangedEventArgs e)
    {
        if (_renderer is not null)
        {
            return;
        }

        (int _, int _, int width, int height) = PaneScreenRect;

        if (width <= 0 || height <= 0)
        {
            return;
        }

        _renderer = SwapChainRenderer.ForComposition(Pane, width, height);
        _panel.As<ISwapChainPanelNative>().SetSwapChain(_renderer.SwapChain.NativePointer);
        _renderer.Start();
    }
}
