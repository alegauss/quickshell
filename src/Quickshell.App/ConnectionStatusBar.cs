using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Shapes;

namespace Quickshell.App;

/// <summary>
/// The bar along the bottom of the window while a remote tab is on screen (QS269): the connection's
/// state and route, whether it reconnects, the encoding, the terminal type and the grid.
///
/// <para><b>Twenty-eight pixels that follow the tab, not a setting.</b> It comes with a remote
/// session and goes with it, so a window of local shells has none — and, like the session bar, the
/// element is not built until the first remote tab is on screen.</para>
/// </summary>
public sealed class ConnectionStatusBar : Border
{
    /// <summary>Its height, from the approved design.</summary>
    public const double BarHeight = 28;

    private readonly Ellipse _dot;
    private readonly TextBlock _status;
    private readonly TextBlock _reconnects;
    private readonly TextBlock _encoding;
    private readonly TextBlock _terminal;
    private readonly TextBlock _grid;

    /// <summary>Builds the bar, empty until the window hands it a view.</summary>
    public ConnectionStatusBar()
    {
        Height = BarHeight;
        Padding = new Thickness(14, 0, 14, 0);
        BorderThickness = new Thickness(0, 1, 0, 0);
        SetResourceReference(BorderBrushProperty, "DividerStrokeColorDefaultBrush");

        AutomationProperties.SetAutomationId(this, "StatusBar");
        AutomationProperties.SetName(this, "Connection status");

        _dot = new Ellipse { Width = 7, Height = 7, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };

        _status = Words("ConnectionStatus");
        _reconnects = Words("Reconnects");
        _encoding = Words("Encoding");
        _terminal = Words("TerminalType");
        _grid = Words("GridSize");

        StackPanel left = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

        left.Children.Add(_dot);
        left.Children.Add(_status);
        _reconnects.Margin = new Thickness(18, 0, 0, 0);
        left.Children.Add(_reconnects);

        StackPanel right = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

        right.Children.Add(_encoding);
        _terminal.Margin = new Thickness(18, 0, 0, 0);
        right.Children.Add(_terminal);
        _grid.Margin = new Thickness(18, 0, 0, 0);
        right.Children.Add(_grid);

        DockPanel line = new() { LastChildFill = false };

        DockPanel.SetDock(left, Dock.Left);
        DockPanel.SetDock(right, Dock.Right);

        line.Children.Add(left);
        line.Children.Add(right);

        Child = line;
    }

    /// <summary>What it is showing, or null before the first view.</summary>
    public ConnectionView? Showing { get; private set; }

    /// <summary>Every word on it, left to right, which is what a test reads.</summary>
    public IReadOnlyList<string> Said => [_status.Text, _reconnects.Text, _encoding.Text, _terminal.Text, _grid.Text];

    /// <summary>Says this, where it differs from what it said last.</summary>
    public void Show(ConnectionView view)
    {
        ArgumentNullException.ThrowIfNull(view);

        if (view.Equals(Showing))
        {
            return;
        }

        if (Showing?.State != view.State)
        {
            _dot.SetResourceReference(Shape.FillProperty, view.State switch
            {
                ConnectionState.Connected => "SystemFillColorSuccessBrush",
                ConnectionState.Reconnecting => "SystemFillColorCautionBrush",
                _ => "SystemFillColorCriticalBrush",
            });
        }

        Showing = view;

        Set(_status, view.Status);
        Set(_reconnects, view.Reconnecting);
        Set(_encoding, ConnectionView.Encoding);
        Set(_terminal, view.TerminalType);
        Set(_grid, view.Grid);
    }

    private static void Set(TextBlock words, string text)
    {
        if (!string.Equals(words.Text, text, StringComparison.Ordinal))
        {
            words.Text = text;
            AutomationProperties.SetName(words, text);
        }
    }

    private static TextBlock Words(string id)
    {
        TextBlock words = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center };

        words.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        AutomationProperties.SetAutomationId(words, id);

        return words;
    }
}
