using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using HostProbe.Core;

namespace HostProbe.WpfHwnd;

/// <summary>
/// WPF hosting a child HWND per pane: the pane owns its swapchain and its own present clock,
/// and pays for it in airspace - nothing WPF draws can appear over that rectangle unless it is
/// itself HWND-backed. The dropdown and the modal in this window are the test of whether that
/// exception is enough.
/// </summary>
public sealed class ProbeWindow : Window, IProbeHost
{
    private readonly PaneHost _paneHost = new();
    private readonly ComboBox _dropdown = new();
    private SwapChainRenderer? _renderer;
    private Window? _modal;

    public ProbeWindow()
    {
        Title = "quickshell host probe - WPF child HWND";
        Width = 900;
        Height = 620;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Brushes.DimGray;

        // Topmost is not cosmetic: the driver injects real clicks at the pane's screen point, and
        // a window that is not in front turns that into a click on whatever is.
        Topmost = true;

        _dropdown.Width = 240;
        _dropdown.Margin = new Thickness(12);
        _dropdown.HorizontalAlignment = HorizontalAlignment.Left;

        for (int item = 0; item < 12; item++)
        {
            _dropdown.Items.Add($"Session {item + 1} - overlapping the pane");
        }

        _dropdown.SelectedIndex = 0;

        Grid layout = new();
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        Grid.SetRow(_dropdown, 0);
        Grid.SetRow(_paneHost, 1);
        layout.Children.Add(_dropdown);
        layout.Children.Add(_paneHost);

        Content = layout;
        _paneHost.Clicked = Pane.Click;

        Loaded += (_, _) =>
        {
            (int _, int _, int width, int height) = PaneScreenRect;
            _renderer = SwapChainRenderer.ForWindow(Pane, _paneHost.PaneHandle, width, height);
            _renderer.Start();
        };

        Closed += (_, _) => _renderer?.Dispose();
    }

    public string HostName => "wpf-child-hwnd";

    public Pane Pane { get; } = new();

    public nint WindowHandle => new WindowInteropHelper(this).Handle;

    public void RunOnUi(Action action) => Dispatcher.Invoke(action);

    public (int X, int Y, int Width, int Height) PaneScreenRect
    {
        get
        {
            Point topLeft = _paneHost.PointToScreen(new Point(0, 0));
            double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
            return ((int)topLeft.X, (int)topLeft.Y,
                    (int)(_paneHost.ActualWidth * scale), (int)(_paneHost.ActualHeight * scale));
        }
    }

    public void OpenDropdown() => _dropdown.IsDropDownOpen = true;

    public void CloseDropdown() => _dropdown.IsDropDownOpen = false;

    public void ShowModal()
    {
        _modal = new Window
        {
            Owner = this,
            Title = "Modal over a running pane",
            Width = 380,
            Height = 200,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            Content = new TextBlock
            {
                Text = "This dialog is modal and it overlaps a pane that is still presenting.",
                Margin = new Thickness(24),
                TextWrapping = TextWrapping.Wrap,
                FontSize = 16,
            },
        };

        // ShowDialog pumps messages, so the driver's Dispatcher.Invoke calls are still serviced
        // while it is up - which is why the run does not deadlock on its own modal.
        _ = Dispatcher.BeginInvoke(() => _modal.ShowDialog());
    }

    public void CloseModal()
    {
        _modal?.Close();
        _modal = null;
    }

    public void Shutdown()
    {
        _renderer?.Dispose();
        _renderer = null;
        Application.Current.Shutdown();
    }

    private sealed class PaneHost : HwndHost
    {
        public nint PaneHandle { get; private set; }

        public Action? Clicked { get; set; }

        protected override HandleRef BuildWindowCore(HandleRef hwndParent)
        {
            PaneHandle = ChildWindow.Create(hwndParent.Handle, 800, 500, () => Clicked?.Invoke());
            return new HandleRef(this, PaneHandle);
        }

        protected override void DestroyWindowCore(HandleRef hwnd)
        {
            ChildWindow.Destroy(hwnd.Handle);
            PaneHandle = nint.Zero;
        }
    }
}
