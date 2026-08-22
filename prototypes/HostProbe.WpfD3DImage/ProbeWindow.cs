using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using HostProbe.Core;

namespace HostProbe.WpfD3DImage;

/// <summary>
/// WPF with <c>D3DImage</c>: the pane is a WPF element like any other, so it composes perfectly
/// and there is no airspace to work around. What it gives up is the present clock.
/// </summary>
public sealed class ProbeWindow : Window, IProbeHost
{
    private readonly Image _paneImage = new();
    private readonly ComboBox _dropdown = new();
    private SharedSurfaceRenderer? _renderer;
    private Window? _modal;

    public ProbeWindow()
    {
        Title = "quickshell host probe - WPF D3DImage";
        Width = 900;
        Height = 620;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Brushes.DimGray;
        Topmost = true;

        _dropdown.Width = 240;
        _dropdown.Margin = new Thickness(12);
        _dropdown.HorizontalAlignment = HorizontalAlignment.Left;

        for (int item = 0; item < 12; item++)
        {
            _dropdown.Items.Add($"Session {item + 1} - overlapping the pane");
        }

        _dropdown.SelectedIndex = 0;

        _paneImage.Stretch = Stretch.Fill;
        _paneImage.MouseLeftButtonDown += (_, _) => Pane.Click();

        Grid layout = new();
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        Grid.SetRow(_dropdown, 0);
        Grid.SetRow(_paneImage, 1);
        layout.Children.Add(_dropdown);
        layout.Children.Add(_paneImage);

        Content = layout;

        Loaded += (_, _) =>
        {
            (int _, int _, int width, int height) = PaneScreenRect;
            _renderer = new SharedSurfaceRenderer(Pane, new WindowInteropHelper(this).Handle, width, height);
            _paneImage.Source = _renderer.Image;
            CompositionTarget.Rendering += OnRendering;
        };

        Closed += (_, _) =>
        {
            CompositionTarget.Rendering -= OnRendering;
            _renderer?.Dispose();
        };
    }

    public string HostName => "wpf-d3dimage";

    public Pane Pane { get; } = new();

    public nint WindowHandle => new WindowInteropHelper(this).Handle;

    public void RunOnUi(Action action) => Dispatcher.Invoke(action);

    public (int X, int Y, int Width, int Height) PaneScreenRect
    {
        get
        {
            Point topLeft = _paneImage.PointToScreen(new Point(0, 0));
            double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
            return ((int)topLeft.X, (int)topLeft.Y,
                    (int)(_paneImage.ActualWidth * scale), (int)(_paneImage.ActualHeight * scale));
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

        _ = Dispatcher.BeginInvoke(() => _modal.ShowDialog());
    }

    public void CloseModal()
    {
        _modal?.Close();
        _modal = null;
    }

    public void Shutdown()
    {
        CompositionTarget.Rendering -= OnRendering;
        _renderer?.Dispose();
        _renderer = null;
        Application.Current.Shutdown();
    }

    private void OnRendering(object? sender, EventArgs e) => _renderer?.RenderOnce();
}
