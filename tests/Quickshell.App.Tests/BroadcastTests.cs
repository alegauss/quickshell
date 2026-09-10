using System.Text;
using System.Windows.Automation.Peers;
using System.Windows.Input;
using Quickshell.App;
using Quickshell.Terminal;
using Xunit;

// Both namespaces name a Key. Here the window's is meant: these are chords a person presses.
using Key = System.Windows.Input.Key;

namespace Quickshell.App.Tests;

/// <summary>
/// QS53: typing once into every pane of a tab, and never into a pane that does not say so.
///
/// <para><b>The falsification is an invariant and it is checked as one.</b> <em>Falsified when
/// broadcast is on and any receiving pane is not visibly marked.</em> So every test here asks the
/// same question of every pane in the tab: did this keystroke reach it, and is it marked — the edge
/// on the glass and the sentence a screen reader finds — and the two answers must agree, pane by
/// pane, in both directions. A marked pane that received nothing is the other half of the same lie.
/// </para>
///
/// <para>No device opens in any of this, for the reason <see cref="TabTests"/> gives: a pane with no
/// handle opens nothing. What the mark looks like on the glass is the renderer's, and
/// <c>CellRendererTests</c> reads it off the back buffer.</para>
/// </summary>
public sealed class BroadcastTests
{
    /// <summary>The one device, atlas and render loop, which these panes never ask to open.</summary>
    private static readonly TerminalShare Shared = new();

    private const ModifierKeys Both = ModifierKeys.Control | ModifierKeys.Shift;

    /// <summary>
    /// One command typed once reaches all three panes, each pane is marked, and the marks and the
    /// receiving agree pane by pane.
    /// </summary>
    [Fact]
    public void BroadcastingTypesIntoEveryPaneAndMarksEachOne()
    {
        Pane[] panes = OnStaThread(() =>
        {
            (MainWindow window, TerminalTab tab) = Split(3);
            Dictionary<TerminalLeaf, StringBuilder> heard = Listen(tab);

            Step(window, Key.B, Both);

            window.Type("uptime", ModifierKeys.None);
            window.Press(Key.Return, ModifierKeys.None);

            return Read(tab, heard);
        });

        Assert.Equal(3, panes.Length);

        foreach (Pane pane in panes)
        {
            Assert.Equal("uptime\r", pane.Heard);
            Assert.True(pane.Marked, "a pane received broadcast typing with no mark on its edge");
            Assert.Equal(TerminalLeaf.ReceivingSays, pane.Told);
        }
    }

    /// <summary>
    /// Off again, the focused pane is the only one typed into and no pane is marked.
    ///
    /// <para>The half that stops a mode from outliving the moment it was wanted: somebody who turned
    /// it off and typed a command meant for one host must not find it ran on three.</para>
    /// </summary>
    [Fact]
    public void TurnedOffOnlyTheFocusedPaneHearsAndNoMarkIsLeft()
    {
        (Pane[] panes, bool focused) = OnStaThread(() =>
        {
            (MainWindow window, TerminalTab tab) = Split(2);
            Dictionary<TerminalLeaf, StringBuilder> heard = Listen(tab);

            Step(window, Key.B, Both);
            window.Type("a", ModifierKeys.None);

            Step(window, Key.B, Both);
            window.Type("b", ModifierKeys.None);

            return (Read(tab, heard), tab.Leaves[1] == tab.Focused);
        });

        Assert.True(focused, "the split did not leave the keyboard on the new pane");

        Assert.Equal("a", panes[0].Heard);
        Assert.Equal("ab", panes[1].Heard);

        Assert.All(panes, pane =>
        {
            Assert.False(pane.Marked, "a pane kept its mark after broadcasting was turned off");
            Assert.Equal(string.Empty, pane.Told);
        });
    }

    /// <summary>
    /// Every change to what is on screen ends it: another tab, a split, a zoom, a closed pane.
    ///
    /// <para>The set a user turned this on for is the panes in front of them. A pane added later is
    /// one they never chose, a pane hidden by a zoom is one they cannot see, and a tab they left is
    /// one they are not looking at — and each of those is a keystroke reaching a host the user did
    /// not have in mind.</para>
    /// </summary>
    [Fact]
    public void AnotherTabASplitAZoomOrAClosedPaneEachEndIt()
    {
        (bool leaving, bool returning, bool splitting, bool zooming, bool closing) = OnStaThread(() =>
        {
            (MainWindow window, TerminalTab first) = Split(2);

            window.Add(TerminalTab.Open(Settings.Default, Shared, "other"));
            window.Active = 0;

            Step(window, Key.B, Both);
            Step(window, Key.Tab, ModifierKeys.Control);

            bool left = Quiet(first);

            Step(window, Key.Tab, ModifierKeys.Control);

            bool back = Quiet(first);

            Step(window, Key.B, Both);
            Step(window, Key.OemMinus, Both);

            bool split = Quiet(first);

            Step(window, Key.B, Both);
            Step(window, Key.Z, Both);

            bool zoomed = Quiet(first);

            Step(window, Key.Z, Both);
            Step(window, Key.B, Both);
            window.ClosePane();

            bool closed = Quiet(first);

            return (left, back, split, zoomed, closed);
        });

        Assert.True(leaving, "bringing another tab forward left the first one broadcasting");
        Assert.True(returning, "coming back to the tab found the mode still on");
        Assert.True(splitting, "a split left the tab broadcasting, and the new pane was never chosen");
        Assert.True(zooming, "a zoom left the tab broadcasting into panes nobody can see");
        Assert.True(closing, "closing a pane left the tab broadcasting to a set the user is not looking at");
    }

    /// <summary>
    /// Turning it on in a zoomed tab puts every pane back on screen first.
    ///
    /// <para>The zoom is hiding exactly the panes this is about to type into, and a keystroke that
    /// reaches a pane nobody can see is the state the mark exists to rule out.</para>
    /// </summary>
    [Fact]
    public void TurningItOnGivesAZoomedTabItsPanesBack()
    {
        (int zoomed, bool broadcasting) = OnStaThread(() =>
        {
            (MainWindow window, TerminalTab tab) = Split(2);

            Step(window, Key.Z, Both);
            Step(window, Key.B, Both);

            return (tab.Zoomed, tab.Broadcasting);
        });

        Assert.True(broadcasting, "the chord did not turn broadcasting on in a zoomed tab");
        Assert.Equal(-1, zoomed);
    }

    /// <summary>
    /// A tab of one pane has nothing to broadcast to, so the chord does nothing and the palette does
    /// not offer it — and once it is on, the palette offers the way off instead.
    /// </summary>
    [Fact]
    public void ThePaletteOffersItOnlyWhereThereIsSomethingToBroadcastTo()
    {
        (bool alone, string[] single, string[] split, string[] running) = OnStaThread(() =>
        {
            MainWindow window = new();
            TerminalTab tab = TerminalTab.Open(Settings.Default, Shared, "cmd.exe");

            window.Add(tab);

            Step(window, Key.B, Both);

            bool on = tab.Broadcasting;
            string[] one = Offered(window);

            Step(window, Key.Oem5, Both);

            string[] two = Offered(window);

            Step(window, Key.B, Both);

            return (on, one, two, Offered(window));
        });

        Assert.False(alone, "a tab of one pane turned broadcasting on");
        Assert.Empty(single);
        Assert.Equal(["Broadcast typing to every pane in this tab"], split);
        Assert.Equal(["Stop broadcasting typing", "Leave this pane out of broadcast typing"], running);
    }

    /// <summary>
    /// QS53's own criterion: a pane left out hears nothing typed into the others and carries no mark,
    /// and the two still in hear the keystroke and are marked.
    /// </summary>
    [Fact]
    public void APaneLeftOutHearsNothingTypedIntoTheOthersAndIsNotMarked()
    {
        Pane[] panes = OnStaThread(() =>
        {
            (MainWindow window, TerminalTab tab) = Split(3);
            Dictionary<TerminalLeaf, StringBuilder> heard = Listen(tab);

            Step(window, Key.B, Both);

            tab.Focus(tab.Layout.Panes[0]);
            Run(window, "Leave this pane out of broadcast typing");

            tab.Focus(tab.Layout.Panes[1]);
            window.Type("uptime", ModifierKeys.None);

            return Read(tab, heard);
        });

        Assert.Equal(string.Empty, panes[0].Heard);
        Assert.False(panes[0].Marked, "a pane left out of the broadcast kept its edge");
        Assert.Equal(string.Empty, panes[0].Told);

        foreach (Pane pane in panes[1..])
        {
            Assert.Equal("uptime", pane.Heard);
            Assert.True(pane.Marked, "a pane still in the broadcast lost its edge");
            Assert.Equal(TerminalLeaf.ReceivingSays, pane.Told);
        }
    }

    /// <summary>
    /// Typing into the pane that was left out reaches it and nothing else, and bringing it back makes
    /// it one of them again.
    ///
    /// <para>What leaving a pane out is for: checking one host on its own without ending the mode for
    /// the rest. A left-out pane that went deaf instead would be a pane the user types into and
    /// watches nothing happen in, while the keystrokes land somewhere else.</para>
    /// </summary>
    [Fact]
    public void APaneLeftOutIsPrivateUntilItIsBroughtBack()
    {
        (Pane[] apart, Pane[] back) = OnStaThread(() =>
        {
            (MainWindow window, TerminalTab tab) = Split(2);
            Dictionary<TerminalLeaf, StringBuilder> heard = Listen(tab);

            Step(window, Key.B, Both);

            tab.Focus(tab.Layout.Panes[0]);
            Run(window, "Leave this pane out of broadcast typing");
            window.Type("a", ModifierKeys.None);

            Pane[] first = Read(tab, heard);

            Run(window, "Include this pane in broadcast typing");
            window.Type("b", ModifierKeys.None);

            return (first, Read(tab, heard));
        });

        Assert.Equal("a", apart[0].Heard);
        Assert.Equal(string.Empty, apart[1].Heard);
        Assert.False(apart[0].Marked, "the pane left out is still marked");
        Assert.True(apart[1].Marked, "the pane still in lost its mark");

        Assert.Equal("ab", back[0].Heard);
        Assert.Equal("b", back[1].Heard);
        Assert.All(back, pane => Assert.True(pane.Marked, "a pane brought back is not marked"));
    }

    /// <summary>
    /// Leaving every pane out ends the mode, which is what a broadcast to nobody is — and the choice
    /// is offered only while there is a broadcast to choose from, named for what it will do.
    /// </summary>
    [Fact]
    public void LeavingEveryPaneOutEndsItAndTheChoiceIsOfferedOnlyWhileItRuns()
    {
        (string[] before, string[] during, string[] apart, bool ended) = OnStaThread(() =>
        {
            (MainWindow window, TerminalTab tab) = Split(2);

            string[] none = Choices(window);

            Step(window, Key.B, Both);

            string[] offered = Choices(window);

            tab.Focus(tab.Layout.Panes[0]);
            Run(window, "Leave this pane out of broadcast typing");

            string[] left = Choices(window);

            tab.Focus(tab.Layout.Panes[1]);
            Run(window, "Leave this pane out of broadcast typing");

            return (none, offered, left, Quiet(tab));
        });

        Assert.Empty(before);
        Assert.Equal(["Leave this pane out of broadcast typing"], during);
        Assert.Equal(["Include this pane in broadcast typing"], apart);
        Assert.True(ended, "leaving every pane out left the tab broadcasting, or a pane marked");
    }

    /// <summary>
    /// A paste reaches every pane too, and is shown first unless every one of them asked for
    /// bracketing.
    ///
    /// <para><b>All of them or it is asked.</b> One program that did not turn bracketed paste on is
    /// one that runs a pasted newline as it arrives, and while broadcasting that is one host among
    /// several running something the user has not read.</para>
    /// </summary>
    [Fact]
    public void APasteReachesEveryPaneAndIsShownUnlessEveryOneOfThemBrackets()
    {
        (bool askedMixed, string[] mixed, bool askedAll, string[] all) = OnStaThread(() =>
        {
            (MainWindow window, TerminalTab tab) = Split(2);
            Dictionary<TerminalLeaf, StringBuilder> heard = Listen(tab);
            bool asked = false;

            window.AskingToPaste = _ =>
            {
                asked = true;

                return true;
            };

            Step(window, Key.B, Both);

            tab.Leaves[0].Emulator.Feed("\e[?2004h"u8);

            Clipboard("echo hi\n");
            window.PasteFromClipboard();

            bool first = asked;
            string[] once = [.. tab.Leaves.Select(leaf => heard[leaf].ToString())];

            asked = false;

            foreach (StringBuilder each in heard.Values)
            {
                each.Clear();
            }

            tab.Leaves[1].Emulator.Feed("\e[?2004h"u8);

            // Put back and read back before the second paste as before the first: something on a
            // working desk opens the clipboard after every write, and a paste that lands while it
            // holds it reads nothing. QS180 is that, and this narrows it rather than fixing it.
            Clipboard("echo hi\n");
            window.PasteFromClipboard();

            string[] twice = [.. tab.Leaves.Select(leaf => heard[leaf].ToString())];

            return (first, once, asked, twice);
        });

        Assert.True(askedMixed, "a paste with a newline reached a program that never asked for bracketing, unshown");
        Assert.Equal(["echo hi\r", "echo hi\r"], mixed);

        Assert.False(askedAll, "every program asked for bracketing and the user was asked anyway");
        Assert.Equal(["\e[200~echo hi\r\e[201~", "\e[200~echo hi\r\e[201~"], all);
    }

    /// <summary>What one pane heard, and what it was marked with.</summary>
    private sealed record Pane(string Heard, bool Marked, string Told);

    /// <summary>A window holding one tab split into so many panes, side by side.</summary>
    private static (MainWindow Window, TerminalTab Tab) Split(int panes)
    {
        MainWindow window = new();
        TerminalTab tab = TerminalTab.Open(Settings.Default, Shared, "cmd.exe");

        window.Add(tab);

        for (int pane = 1; pane < panes; pane++)
        {
            Step(window, Key.Oem5, Both);
        }

        return (window, tab);
    }

    /// <summary>Gives every pane's keyboard somewhere to deliver to that a test can read back.</summary>
    private static Dictionary<TerminalLeaf, StringBuilder> Listen(TerminalTab tab)
    {
        Dictionary<TerminalLeaf, StringBuilder> heard = [];

        foreach (TerminalLeaf leaf in tab.Leaves)
        {
            StringBuilder into = new();

            heard[leaf] = into;

            leaf.Typist.Sending = bytes =>
            {
                into.Append(Encoding.UTF8.GetString(bytes.Span));

                return ValueTask.CompletedTask;
            };
        }

        return heard;
    }

    /// <summary>Every pane of a tab in screen order, as what it heard and how it is marked.</summary>
    private static Pane[] Read(TerminalTab tab, Dictionary<TerminalLeaf, StringBuilder> heard) =>
        [.. tab.Leaves.Select(leaf => new Pane(heard[leaf].ToString(), leaf.Terminal.Outlined,
                                               Says(leaf)))];

    /// <summary>Whether a tab is not broadcasting and not one of its panes is marked as though it were.</summary>
    private static bool Quiet(TerminalTab tab) =>
        !tab.Broadcasting
        && tab.Leaves.All(leaf => !leaf.Receiving && !leaf.Terminal.Outlined && Says(leaf).Length == 0);

    /// <summary>
    /// What a screen reader is told beside the pane's name, asked of the pane's own automation peer.
    ///
    /// <para>The peer and not the attached property it defaults to, because the peer is what UI
    /// Automation actually asks — and the pane's peer is built rather than inherited, so a peer that
    /// answered help text some other way would pass a test that read the property.</para>
    /// </summary>
    private static string Says(TerminalLeaf leaf) =>
        UIElementAutomationPeer.CreatePeerForElement(leaf.Pane)?.GetHelpText() ?? string.Empty;

    /// <summary>What the palette offers about broadcasting, right now.</summary>
    private static string[] Offered(MainWindow window) =>
        [.. window.Actions.Select(action => action.Name)
                          .Where(name => name.Contains("broadcast", StringComparison.OrdinalIgnoreCase))];

    /// <summary>What the palette offers about one pane's place in the broadcast, right now.</summary>
    private static string[] Choices(MainWindow window) =>
        [.. window.Actions.Select(action => action.Name)
                          .Where(name => name.Contains("this pane", StringComparison.Ordinal))];

    /// <summary>
    /// Runs a palette entry by its name, down the same path picking it in the palette takes — which
    /// is the only path an action bound to no key has.
    /// </summary>
    private static void Run(MainWindow window, string name) =>
        window.Actions.Single(action => action.Name == name).Run();

    /// <summary>Presses a chord through the binding the window actually carries.</summary>
    private static void Step(MainWindow window, Key key, ModifierKeys modifiers) =>
        window.InputBindings.OfType<KeyBinding>()
              .Single(bound => bound.Key == key && bound.Modifiers == modifiers)
              .Command.Execute(null);

    /// <summary>Puts text on the clipboard, waiting out whoever else has it open.</summary>
    private static void Clipboard(string text)
    {
        for (int attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                System.Windows.Clipboard.SetDataObject(text, copy: true);

                if (System.Windows.Clipboard.ContainsText()
                    && System.Windows.Clipboard.GetText() == text)
                {
                    return;
                }
            }
            catch (Exception)
            {
                // Somebody else has it open. Waiting is the whole remedy.
            }

            Thread.Sleep(50);
        }

        Assert.Fail("the clipboard would not hold what this test put on it");
    }

    /// <summary>Runs something on an STA thread, and shuts the dispatcher it built down after.</summary>
    private static T OnStaThread<T>(Func<T> work)
    {
        T result = default!;
        Exception? failed = null;

        Thread thread = new(() =>
        {
            try
            {
                result = work();
            }
            catch (Exception error)
            {
                failed = error;
            }
            finally
            {
                System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();

        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "the STA thread never finished");

        if (failed is not null)
        {
            throw new InvalidOperationException("the window could not be built", failed);
        }

        return result;
    }
}
