using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Quickshell.App;
using Xunit;

namespace Quickshell.App.Tests;

/// <summary>
/// QS163: a divider is a handle in a real gap, and dragging it resizes the panes either side.
///
/// <para>A real window with two real panes, and the handle moved by the event a drag raises — what
/// is not exercised is a person holding the button down, which needs one.</para>
/// </summary>
public sealed class DividerTests
{
    private static readonly TerminalShare Shared = new();

    /// <summary>
    /// The falsification: a pane can be resized without closing it and splitting again. The panes
    /// leave a gap exactly a handle wide, the handle sits in it, and dragging it forty pixels
    /// right moves the edge forty pixels and gives the other pane forty fewer.
    /// </summary>
    [Fact]
    public void DraggingTheHandleInTheGapResizesBothPanes()
    {
        (double gap, double handleAt, double leftEdge, double grew, double shrank, int handles) = OnShownWindow(window =>
        {
            window.SplitPane(Divide.Beside);
            window.UpdateLayout();

            TerminalPane left = window.Current!.In(window.Current.Layout.Panes[0])!.Pane;
            TerminalPane right = window.Current.In(window.Current.Layout.Panes[1])!.Pane;

            double leftWidth = left.Width;
            double rightWidth = right.Width;

            double edge = Canvas.GetLeft(left) + left.Width;
            double between = Canvas.GetLeft(right) - edge;

            Thumb handle = window.DividerHandles.Single();
            double placed = Canvas.GetLeft(handle);

            handle.RaiseEvent(new DragDeltaEventArgs(40, 0));
            window.UpdateLayout();

            return (between, placed, edge, left.Width - leftWidth, rightWidth - right.Width,
                    window.DividerHandles.Count);
        });

        Assert.Equal(4, gap);
        Assert.Equal(leftEdge, handleAt);
        Assert.Equal(1, handles);

        // Whole pixels, so a pixel either way of forty is the rounding and not the drag.
        Assert.InRange(grew, 39, 41);
        Assert.InRange(shrank, 39, 41);
    }

    /// <summary>One pane has no divider, so there is no handle and no gap.</summary>
    [Fact]
    public void ATabWithOnePaneHasNoHandle()
    {
        int handles = OnShownWindow(window => window.DividerHandles.Count);

        Assert.Equal(0, handles);
    }

    private static T OnShownWindow<T>(Func<MainWindow, T> work) => Sta.Run(() =>
    {
        MainWindow window = new()
        {
            Width = 640,
            Height = 360,
            ShowInTaskbar = false,
            ShowActivated = false,
        };

        try
        {
            window.Add(TerminalTab.Open(Settings.Default, Shared, "cmd.exe"));
            window.Show();
            window.UpdateLayout();

            return work(window);
        }
        finally
        {
            window.Close();
        }
    });
}
