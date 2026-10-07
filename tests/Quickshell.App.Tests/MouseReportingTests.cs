using System.Runtime.InteropServices;
using System.Text;
using Quickshell.App;
using Quickshell.Render;
using Xunit;

namespace Quickshell.App.Tests;

/// <summary>
/// QS155: a program that asks for the mouse gets it, and the wheel goes where the screen says.
///
/// <para><b>A real pane with a real handle, sent the messages Windows sends</b> — a button, a wheel —
/// and what is asserted is the bytes that leave for the host on the keystroke path. What is not
/// exercised is a person pressing a button, which needs one.</para>
/// </summary>
public sealed class MouseReportingTests
{
    private static readonly TerminalShare Shared = new();

    /// <summary>
    /// The falsification: a program with mouse tracking on can tell a click from silence — the
    /// press and the release of the left button, and a right press, each in SGR at the cell clicked.
    /// </summary>
    [Fact]
    public void AProgramThatAskedForTheMouseHearsEachButton()
    {
        string heard = OnShownLeaf((leaf, box) =>
        {
            leaf.Emulator.Feed(Encoding.ASCII.GetBytes("\u001b[?1000h\u001b[?1006h"));

            nint at = Point(box, column: 4, row: 2);

            SendMessageW(leaf.Pane.PaneHandle, LeftDown, 1, at);
            SendMessageW(leaf.Pane.PaneHandle, LeftUp, 0, at);
            SendMessageW(leaf.Pane.PaneHandle, RightDown, 2, at);
            SendMessageW(leaf.Pane.PaneHandle, RightUp, 0, at);
        });

        Assert.Equal("\u001b[<0;5;3M\u001b[<0;5;3m\u001b[<2;5;3M\u001b[<2;5;3m", heard);
    }

    /// <summary>
    /// The wheel is the program's too while it has the mouse: one wheel press per notch, at the cell
    /// the pointer was last over.
    /// </summary>
    [Fact]
    public void TheWheelGoesToAProgramThatHasTheMouse()
    {
        string heard = OnShownLeaf((leaf, box) =>
        {
            leaf.Emulator.Feed(Encoding.ASCII.GetBytes("\u001b[?1049h\u001b[?1000h\u001b[?1006h"));

            SendMessageW(leaf.Pane.PaneHandle, Moved, 0, Point(box, column: 1, row: 1));
            SendMessageW(leaf.Pane.PaneHandle, Wheel, 120 << 16, 0);
            SendMessageW(leaf.Pane.PaneHandle, Wheel, -240 << 16, 0);
        });

        Assert.Equal("\u001b[<64;2;2M\u001b[<65;2;2M\u001b[<65;2;2M", heard);
    }

    /// <summary>
    /// A full-screen program that never asked for a mouse still scrolls: a notch is three arrow
    /// presses, which is what makes the wheel work inside a pager.
    /// </summary>
    [Fact]
    public void UnderAFullScreenProgramTheWheelIsArrowKeys()
    {
        string heard = OnShownLeaf((leaf, _) =>
        {
            leaf.Emulator.Feed(Encoding.ASCII.GetBytes("\u001b[?1049h"));

            SendMessageW(leaf.Pane.PaneHandle, Wheel, 120 << 16, 0);
            SendMessageW(leaf.Pane.PaneHandle, Wheel, -120 << 16, 0);
        });

        Assert.Equal("\u001b[A\u001b[A\u001b[A\u001b[B\u001b[B\u001b[B", heard);
    }

    /// <summary>With nobody asking, a click is the user's own and selects; nothing goes to the host.</summary>
    [Fact]
    public void WithNobodyAskingAClickSendsNothing()
    {
        string heard = OnShownLeaf((leaf, box) =>
        {
            nint at = Point(box, column: 4, row: 2);

            SendMessageW(leaf.Pane.PaneHandle, LeftDown, 1, at);
            SendMessageW(leaf.Pane.PaneHandle, LeftUp, 0, at);
            SendMessageW(leaf.Pane.PaneHandle, RightDown, 2, at);
        });

        Assert.Equal(string.Empty, heard);
    }

    // ---- plumbing ----

    /// <summary>The middle of a cell, as a mouse message packs it.</summary>
    private static nint Point(CellMetrics box, int column, int row) =>
        ((row * box.Height) + (box.Height / 2)) << 16 | ((column * box.Width) + (box.Width / 2));

    /// <summary>
    /// A shown window with one pane whose view has opened, its sending path captured, and the work
    /// done on its thread; answers what was sent.
    /// </summary>
    private static string OnShownLeaf(Action<TerminalLeaf, CellMetrics> work)
    {
        string result = string.Empty;
        Exception? failed = null;

        Thread thread = new(() =>
        {
            MainWindow? window = null;

            try
            {
                window = new MainWindow
                {
                    Width = 640,
                    Height = 360,
                    ShowInTaskbar = false,
                    ShowActivated = false,
                };

                window.Add(TerminalTab.Open(Settings.Default, Shared, "cmd.exe"));
                window.Show();
                window.UpdateLayout();

                TerminalLeaf leaf = window.Current!.Leaves[0];

                Assert.NotNull(leaf.Terminal.View);

                StringBuilder heard = new();

                leaf.Terminal.Sending = bytes =>
                {
                    heard.Append(Encoding.ASCII.GetString(bytes.Span));

                    return ValueTask.CompletedTask;
                };

                work(leaf, leaf.Terminal.View!.Renderer.Metrics);

                result = heard.ToString();
            }
            catch (Exception error)
            {
                failed = error;
            }
            finally
            {
                window?.Close();
                System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();

        Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "the STA thread never finished");

        if (failed is not null)
        {
            throw new InvalidOperationException("the window could not be built", failed);
        }

        return result;
    }

    private const uint Moved = 0x0200;
    private const uint LeftDown = 0x0201;
    private const uint LeftUp = 0x0202;
    private const uint RightDown = 0x0204;
    private const uint RightUp = 0x0205;
    private const uint Wheel = 0x020A;

    [DllImport("user32.dll")]
    private static extern nint SendMessageW(nint window, uint message, nint wide, nint low);
}
