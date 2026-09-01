using System.Text;
using System.Windows;
using Quickshell.App;
using Quickshell.Render;
using Quickshell.Terminal;
using Xunit;

namespace Quickshell.App.Tests;

/// <summary>
/// What every pane shares, and QS49's falsification: <em>falsified when atlas memory in use scales
/// with the number of open panes</em>.
///
/// <para><b>Against real panes with real swapchains</b>, because the claim is about a device. Four
/// panes are opened on four child windows, each with its own surface, and what is counted is what
/// they held between them — one device, one atlas, one cache of glyphs.</para>
///
/// <para>These need a graphics device, which a desk without one does not have. There is no skip:
/// every other render case in this repository assumes the device opens, and a client that could not
/// open one has a larger problem than this file.</para>
/// </summary>
public sealed class TerminalShareTests
{
    /// <summary>
    /// Four panes hold one device, one atlas and one copy of every glyph between them.
    ///
    /// <para>The cache count is the reading that matters. Four private atlases would each rasterise
    /// the same characters and each hold them, so the number would be four times what one pane
    /// needs; one atlas answers with the number of distinct characters and nothing more.</para>
    /// </summary>
    [Fact]
    public void FourPanesShareOneDeviceOneAtlasAndOneCopyOfEveryGlyph()
    {
        (int one, int four, int panes, bool same) = OnPanes(4, (share, _, views) =>
        {
            Emulator model = new(80, 25);

            model.Feed(Encoding.UTF8.GetBytes("the quick brown fox jumps over the lazy dog"));

            // One pane draws it, and the atlas now holds what that text needs.
            views[0].DrawIfNeeded(model);

            int afterFirst = share.Atlas!.CachedGlyphs;

            // The other three draw the same text. Sharing means they add nothing: every glyph they
            // want is already there, rasterised once.
            foreach (TerminalView view in views.Skip(1))
            {
                Emulator other = new(80, 25);

                other.Feed(Encoding.UTF8.GetBytes("the quick brown fox jumps over the lazy dog"));

                view.DrawIfNeeded(other);
            }

            bool oneDevice = views.All(view => ReferenceEquals(view.Device, share.Device));

            return (afterFirst, share.Atlas.CachedGlyphs, share.Panes, oneDevice);
        });

        Assert.True(one > 10, $"the atlas held {one} glyphs after drawing a pangram");

        // The claim, in one line: three more panes drawing the same text cost nothing.
        Assert.Equal(one, four);

        Assert.Equal(4, panes);
        Assert.True(same, "the panes did not all draw with the same device");
    }

    /// <summary>
    /// One loop draws every pane, and a pane taken off it stops being drawn while the rest go on.
    ///
    /// <para>The second half is what a closed pane needs to be true: its swapchain is released, and
    /// a loop still holding it would present into a surface that has gone.</para>
    /// </summary>
    [Fact]
    public void OneLoopDrawsEveryPaneAndForgetsTheOnesThatClose()
    {
        (int drawn, int after, int left) = OnPanes(3, (share, tab, views) =>
        {
            // Every pane owes a first frame, and one pass over the lot is what draws them.
            int first = share.DrawOnce();

            // The models the loop was registered against, and not new ones: what is under test is
            // that one pass reaches every pane the share was given.
            foreach (TerminalLeaf leaf in tab.Leaves)
            {
                leaf.Emulator.Feed(Encoding.UTF8.GetBytes("something"));
            }

            share.Forget(views[0]);
            views[0].Dispose();

            int without = share.DrawOnce();

            return (first, without, share.Panes);
        });

        Assert.Equal(3, drawn);

        // Two, because the third was taken off the loop before this pass.
        Assert.Equal(2, after);
        Assert.Equal(2, left);
    }

    /// <summary>
    /// Block C's criterion, read against a client that has more than one pane: an idle pane issues
    /// no draw calls, however busy the pane beside it is.
    ///
    /// <para><b>This is the case QS165 existed to make writable.</b> Before it, every pane reported
    /// the shared renderer's total, so a pane that drew nothing answered with what its neighbour
    /// drew — and the assertion below would have failed on a client that was behaving perfectly.
    /// </para>
    /// </summary>
    [Fact]
    public void AnIdlePaneIssuesNoDrawCallsWhileTheOneBesideItIsBusy()
    {
        (long busy, long idle) = OnPanes(2, (share, tab, views) =>
        {
            foreach (TerminalView view in views)
            {
                view.Renderer.Blink.Enabled = false;
            }

            // Both owe a first frame; after this pass neither owes anything.
            share.DrawOnce();

            long before = views[1].Draws;

            // One host prints, twenty times over, and the loop is asked twenty times.
            for (int said = 0; said < 20; said++)
            {
                tab.Leaves[0].Emulator.Feed(Encoding.UTF8.GetBytes($"line {said}\r\n"));

                share.DrawOnce();
            }

            return (views[0].Draws, views[1].Draws - before);
        });

        // The busy one drew its first frame and one per batch after it.
        Assert.Equal(21, busy);

        // And the idle one drew nothing at all, which is the claim.
        Assert.Equal(0, idle);
    }

    /// <summary>
    /// QS166's falsification: a window showing one tab presents no frame for another.
    ///
    /// <para>Eight tabs of busy hosts would otherwise be eight panes drawn and one shown, and seven
    /// of those presents are a queue slot and a copy for a window nobody can see. What resumes a
    /// pane is coming forward, and it forgets the frame on the glass when it does — a swapchain
    /// nobody has drawn into holds whatever the host left there.</para>
    /// </summary>
    [Fact]
    public void APaneBehindAnotherTabIsNotDrawnAtAll()
    {
        (long hidden, long shown) = OnTwoTabs((share, background, front) =>
        {
            share.DrawOnce();

            long before = background.Focused.Terminal.View!.Draws;

            // The tab nobody is looking at prints, twenty times over.
            for (int said = 0; said < 20; said++)
            {
                background.Focused.Emulator.Feed(Encoding.UTF8.GetBytes($"line {said}\r\n"));

                share.DrawOnce();
            }

            long whileHidden = background.Focused.Terminal.View!.Draws - before;

            // And it comes forward, which is what resumes it.
            front.Active = 0;

            share.DrawOnce();

            return (whileHidden, background.Focused.Terminal.View!.Draws - before);
        });

        Assert.Equal(0, hidden);

        // One frame, drawn when it came forward, carrying everything that arrived while it was away.
        Assert.Equal(1, shown);
    }

    /// <summary>Builds a window with two tabs and hands the first one and the window to the work.</summary>
    private static T OnTwoTabs<T>(Func<TerminalShare, TerminalTab, MainWindow, T> work)
    {
        T result = default!;
        Exception? failed = null;

        Thread thread = new(() =>
        {
            MainWindow? client = null;
            TerminalShare share = new() { Looping = false };

            try
            {
                client = new MainWindow();

                TerminalTab first = TerminalTab.Open(Settings.Default, share, "cmd.exe");

                client.Add(first);
                client.Show();
                client.UpdateLayout();

                client.Add(TerminalTab.Open(Settings.Default, share, "cmd.exe"));
                client.UpdateLayout();

                Assert.NotNull(first.Focused.Terminal.View);

                result = work(share, first, client);
            }
            catch (Exception error)
            {
                failed = error;
            }
            finally
            {
                client?.Close();
                share.Dispose();

                System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        thread.Join();

        if (failed is not null)
        {
            throw new InvalidOperationException("the work on the STA thread failed", failed);
        }

        return result;
    }

    /// <summary>
    /// Builds a window with that many panes in it, opens a view on each, and hands them to the work.
    ///
    /// <para>Shown, because <c>HwndHost</c> builds its child window during layout and there is no
    /// layout for a window that was never on screen. It does not take the foreground: a test that
    /// stole the desk would be a test nobody could run while working.</para>
    /// </summary>
    private static T OnPanes<T>(int how, Func<TerminalShare, TerminalTab, TerminalView[], T> work)
    {
        T result = default!;
        Exception? failed = null;

        Thread thread = new(() =>
        {
            MainWindow? client = null;
            // Not looping: this counts what one pass draws, and a thread drawing them first would
            // leave every pass with nothing to do. The client always loops; a measurement paces
            // itself.
            TerminalShare share = new() { Looping = false };

            try
            {
                client = new MainWindow();

                client.Add(TerminalTab.Open(Settings.Default, share, "cmd.exe"));

                client.Show();
                client.UpdateLayout();

                // Through the window and not the tab, because it is the window that puts a new pane
                // in the canvas — a pane the tree knows about and the canvas does not never gets a
                // handle, and a pane with no handle has no swapchain.
                for (int pane = 1; pane < how; pane++)
                {
                    client.SplitPane(Divide.Beside);
                    client.UpdateLayout();
                }

                TerminalTab tab = client.Current!;

                // The panes are laid out by the window, and each one's view opens on the first size
                // it is given — so what is asked for here is the arrangement rather than a handle.
                TerminalView[] views = [.. tab.Leaves
                                             .Select(leaf => leaf.Terminal.View)
                                             .Where(view => view is not null)
                                             .Select(view => view!)];

                Assert.Equal(how, views.Length);

                result = work(share, tab, views);
            }
            catch (Exception error)
            {
                failed = error;
            }
            finally
            {
                client?.Close();
                share.Dispose();

                System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        thread.Join();

        if (failed is not null)
        {
            throw new InvalidOperationException("the work on the STA thread failed", failed);
        }

        return result;
    }
}
