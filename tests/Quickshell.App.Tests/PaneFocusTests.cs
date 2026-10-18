using Quickshell.App;
using Xunit;

namespace Quickshell.App.Tests;

/// <summary>
/// QS367: in a split tab only the pane with the keyboard blinks, and only it is framed in the accent.
/// </summary>
public sealed class PaneFocusTests
{
    private static readonly TerminalShare Shared = new();

    /// <summary>
    /// The falsification: split, and the new pane is the focused one and the framed one, the other
    /// neither; move the keyboard back, and the two swap.
    /// </summary>
    [Fact]
    public void OnlyThePaneWithTheKeyboardIsFocusedAndFramed()
    {
        (bool[] split, bool[] framed, bool[] moved, bool[] reframed) = OnShownWindow(window =>
        {
            window.SplitPane(Divide.Beside);
            window.UpdateLayout();

            TerminalLeaf[] leaves = [.. window.Current!.Leaves];
            bool[] focus = [.. leaves.Select(leaf => leaf.Terminal.Focused)];
            bool[] frame = [.. leaves.Select(window.PaneFramed)];

            window.FocusPane(Toward.Left);

            return (focus, frame, leaves.Select(leaf => leaf.Terminal.Focused).ToArray(), leaves.Select(window.PaneFramed).ToArray());
        });

        Assert.Equal([false, true], split);
        Assert.Equal([false, true], framed);
        Assert.Equal([true, false], moved);
        Assert.Equal([true, false], reframed);
    }

    /// <summary>A tab of one pane is focused, so its cursor blinks, and has no frame: there is nothing to tell it from.</summary>
    [Fact]
    public void ALonePaneIsFocusedAndUnframed()
    {
        (bool focused, bool framed) = OnShownWindow(window =>
        {
            TerminalLeaf leaf = window.Current!.Focused;

            return (leaf.Terminal.Focused, window.PaneFramed(leaf));
        });

        Assert.True(focused, "a lone pane does not have the keyboard");
        Assert.False(framed, "a lone pane is framed");
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
