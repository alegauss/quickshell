using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Automation.Peers;
using Quickshell.App;
using Quickshell.Render;
using Quickshell.Terminal;
using Xunit;

namespace Quickshell.App.Tests;

/// <summary>
/// The three halves, joined — a swapchain on a pane's handle and a loop that draws into it.
///
/// <para><b>Against a real pane on a real STA thread.</b> The claim QS116 makes is about
/// <see cref="TerminalPane.PaneHandle"/> specifically: a swapchain on WPF's own window would draw
/// over the tab strip, and one on a test window would prove nothing about the client. So these build
/// the window the client builds, take the handle the pane made, and open a device on it.</para>
///
/// <para><b>What the pixels are is not asserted here and is not unasserted anywhere.</b> The golden
/// suite already puts cells through this renderer and compares images, and
/// <c>GridPainterTests</c> already puts a parsed screen into those cells. What was never checked is
/// the join: that a frame is drawn into the pane's own surface, that it happens when the parser says
/// so, and that it stops happening when nothing changes.</para>
/// </summary>
public sealed class TerminalViewTests
{
    /// <summary>Long enough for a loop on a busy machine, short enough that a hang is a failure.</summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);


    /// <summary>
    /// A swapchain opens on the pane's handle and the first frame is drawn without being asked.
    ///
    /// <para>The first frame is unconditional because the handle starts as a rectangle the colour of
    /// whatever was behind it: there is no damage to wait for, and a loop that waited would leave the
    /// window looking exactly as broken as it did before any of this existed.</para>
    /// </summary>
    [Fact]
    public void APaneGetsASwapchainAndAFirstFrame()
    {
        (long draws, long frames, uint width, uint height, int columns, int rows) = OnPane(pane =>
        {
            using TerminalView view = Open(pane);

            Emulator emulator = new(view.Columns, view.Rows);

            emulator.Feed(Encoding.UTF8.GetBytes("a session printed this"));

            Assert.True(view.DrawIfNeeded(emulator), "the first frame was refused");

            return (view.Draws, view.Frames, view.Surface.Width, view.Surface.Height,
                    view.Columns, view.Rows);
        });

        Assert.Equal(1, draws);
        Assert.Equal(1, frames);

        // The surface is the pane's size, which is what makes it the pane's surface and not a
        // rectangle that happens to be over it.
        Assert.True(width > 0 && height > 0, $"the surface was {width}x{height}");

        // And a grid came out of it, which is the other half of the same claim: a window this size
        // holds this many cells, and that is the size the far end is told about.
        Assert.True(columns > 1 && rows > 1, $"the grid was {columns}x{rows}");
    }

    /// <summary>
    /// An idle window issues no draw calls, which is Block C's criterion measured on the client's
    /// own loop rather than on the gate in isolation.
    ///
    /// <para>The blink is off for this, and that is the honest form of the claim: a blinking cursor
    /// <em>is</em> a change on screen, so a window showing one draws twice a second by design. What
    /// must never happen is a frame with nothing behind it at all.</para>
    /// </summary>
    [Fact]
    public void AnIdleWindowIssuesNoDrawCalls()
    {
        (long first, long after) = OnPane(pane =>
        {
            using TerminalView view = Open(pane);

            view.Renderer.Blink.Enabled = false;

            Emulator emulator = new(view.Columns, view.Rows);

            emulator.Feed(Encoding.UTF8.GetBytes("hello"));

            view.DrawIfNeeded(emulator);

            long drawn = view.Draws;

            // A hundred wake-ups over a screen nobody touched. Every one of them is a frame the
            // window would have drawn if the loop ran on a clock.
            for (int wake = 0; wake < 100; wake++)
            {
                Assert.False(view.DrawIfNeeded(emulator), $"wake-up {wake} drew an idle frame");
            }

            return (drawn, view.Draws);
        });

        Assert.Equal(1, first);
        Assert.Equal(first, after);
    }

    /// <summary>
    /// The loop draws when the signal says something changed, and not otherwise.
    ///
    /// <para>Both halves are the test. A loop that never woke would leave a window stale, and a loop
    /// that woke on its own would spend a battery on a screen nobody is changing — and only one of
    /// those two is visible from a screenshot.</para>
    /// </summary>
    [Fact]
    public void TheLoopDrawsWhatTheSignalSaysAndNothingElse()
    {
        (long afterFirst, long afterPrint, long afterSilence) = OnPane(pane =>
        {
            using TerminalView view = Open(pane);

            view.Renderer.Blink.Enabled = false;

            Emulator emulator = new(view.Columns, view.Rows);
            DamageSignal damage = new();

            using CancellationTokenSource stop = new();

            Task loop = Task.Run(() => view.RunAsync(emulator, damage, stop.Token));

            long first = Settled(view, atLeast: 1);

            // What a pipeline does: parse a batch, then say so once.
            emulator.Feed(Encoding.UTF8.GetBytes("the host said something"));
            damage.Set();

            long printed = Settled(view, atLeast: first + 1);

            // And now nothing at all, which the loop must sleep through. Half a second is longer
            // than any wake-up a clock-driven loop would take.
            Thread.Sleep(500);

            long silent = view.Draws;

            stop.Cancel();

            Assert.True(loop.Wait(Patience), "the loop did not stop when it was cancelled");

            return (first, printed, silent);
        });

        Assert.Equal(1, afterFirst);
        Assert.Equal(2, afterPrint);
        Assert.Equal(afterPrint, afterSilence);
    }

    /// <summary>
    /// A resize reaches the swapchain and the grid, in that order, and only once.
    ///
    /// <para><b>QS32 settled the order and this is where the first of its three parties is.</b> The
    /// model reflows to the grid the window turned out to hold, so the grid has to be the one the
    /// swapchain accepted rather than the one the drag was passing through — which is why the event
    /// is raised after the buffers have been reallocated and not when the size arrives.</para>
    ///
    /// <para>Ten sizes before one frame, because a drag fires continuously: every one of them that
    /// reached the swapchain would be a buffer reallocation for a size the window has already left,
    /// and every one that raised the event would be a reflow of somebody's scrollback.</para>
    /// </summary>
    [Fact]
    public void AResizeReachesTheSwapchainOnceAndTheGridAfterIt()
    {
        (uint width, uint height, int columns, int rows, List<(int, int)> raised, long draws) =
            OnPane(pane =>
            {
                using TerminalView view = Open(pane);

                view.Renderer.Blink.Enabled = false;

                Emulator emulator = new(view.Columns, view.Rows);

                List<(int, int)> grids = [];

                view.GridChanged += (c, r) => grids.Add((c, r));

                view.DrawIfNeeded(emulator);

                // A drag: ten sizes, one frame. The last is the one the window ends on.
                for (uint step = 0; step < 10; step++)
                {
                    view.Resize(300 + (step * 10), 200 + (step * 10));
                }

                Assert.True(view.DrawIfNeeded(emulator), "the frame a resize owes was not drawn");

                return (view.Surface.Width, view.Surface.Height, view.Columns, view.Rows, grids,
                        view.Draws);
            });

        Assert.Equal(390u, width);
        Assert.Equal(290u, height);

        // One event for ten sizes, carrying the grid the surface ended at rather than any it passed.
        (int, int) only = Assert.Single(raised);

        Assert.Equal((columns, rows), only);
        Assert.Equal(2, draws);
    }

    /// <summary>
    /// Pixels that move without the grid moving redraw the window and tell nobody.
    ///
    /// <para>Most of a slow drag is this. A frame is owed, because the swapchain is a different size
    /// and the picture in it is stretched or clipped until something draws — but the model holds the
    /// same grid, and a reflow it did not need is scrollback rewrapped for nothing.</para>
    /// </summary>
    [Fact]
    public void PixelsThatMoveWithoutTheGridTellNobody()
    {
        (int raised, long draws) = OnPane(pane =>
        {
            using TerminalView view = Open(pane);

            view.Renderer.Blink.Enabled = false;

            Emulator emulator = new(view.Columns, view.Rows);

            view.DrawIfNeeded(emulator);

            int told = 0;

            view.GridChanged += (_, _) => told++;

            // Half a cell past an exact fit, which is the same grid by construction. Not "one pixel
            // more": a single pixel changes the grid whenever the old size sat on a cell boundary,
            // and how much was added is not what decides it — where the boundary is, is.
            CellMetrics box = view.Renderer.Metrics;

            uint width = (uint)((view.Columns * box.Width) + (box.Width / 2));
            uint height = (uint)((view.Rows * box.Height) + (box.Height / 2));

            Assert.True(width != view.Surface.Width || height != view.Surface.Height,
                        "the resize under test was not a resize");

            view.Resize(width, height);

            Assert.True(view.DrawIfNeeded(emulator), "a resized window kept its old frame");

            Assert.Equal(width, view.Surface.Width);

            return (told, view.Draws);
        });

        Assert.Equal(0, raised);
        Assert.Equal(2, draws);
    }

    /// <summary>
    /// Waits for the loop to have drawn at least this many frames, and answers how many it drew.
    /// </summary>
    private static long Settled(TerminalView view, long atLeast)
    {
        Stopwatch waited = Stopwatch.StartNew();

        while (view.Draws < atLeast && waited.Elapsed < Patience)
        {
            Thread.Sleep(10);
        }

        Assert.True(view.Draws >= atLeast,
                    $"the loop drew {view.Draws} frames in {Patience.TotalSeconds:F0}s, wanted {atLeast}");

        return view.Draws;
    }

    /// <summary>Opens a view on the pane at the pane's own size, in the client's own font.</summary>
    private static TerminalView Open(TerminalPane pane) =>
        TerminalView.Open(pane.PaneHandle,
                          (uint)Math.Max(1d, pane.ActualWidth),
                          (uint)Math.Max(1d, pane.ActualHeight),
                          new FontSettings("Consolas", 16f, 96f),
                          new Palette());

    /// <summary>
    /// Builds the client's window with a pane in it, and hands the pane to the work.
    ///
    /// <para>Shown, because <c>HwndHost</c> builds its child window during layout and there is no
    /// layout for a window that was never on screen — and the handle is the whole subject here. It
    /// does not take the foreground: a test that stole the desk would be a test nobody could run
    /// while working.</para>
    /// </summary>
    private static T OnPane<T>(Func<TerminalPane, T> work)
    {
        T result = default!;
        Exception? failed = null;

        Thread thread = new(() =>
        {
            Window? window = null;

            try
            {
                MainWindow client = new()
                {
                    Width = 480,
                    Height = 320,
                    ShowInTaskbar = false,
                    ShowActivated = false,
                };

                window = client;

                TerminalPane pane = new();

                client.Show(pane);
                client.Show();
                client.UpdateLayout();

                Assert.True(pane.PaneHandle != nint.Zero, "the pane never built a handle");

                result = work(pane);
            }
            catch (Exception error)
            {
                failed = error;
            }
            finally
            {
                window?.Close();
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();

        Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "the STA thread never finished");

        if (failed is not null)
        {
            throw new InvalidOperationException("the pane could not be drawn into", failed);
        }

        return result;
    }
}
