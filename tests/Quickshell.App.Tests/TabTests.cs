using System.Text;
using System.Windows.Input;
using Quickshell.App;
using Quickshell.Terminal;
using Xunit;

// Both namespaces name a Key. Here the window's is meant: these are chords a person presses.
using Key = System.Windows.Input.Key;

namespace Quickshell.App.Tests;

/// <summary>
/// Tabs, and the one claim underneath all of them: a session lives in the tab and not in the window.
///
/// <para><b>What that buys is checked here rather than asserted in prose.</b> A window with two tabs
/// answers for the one on screen — its keyboard, its selection, its scrollback and its find — and a
/// client that got this wrong would answer for whichever tab was current when it was built, which is
/// right until the first switch and then silently wrong for ever.</para>
///
/// <para>No devices open in any of this. <c>TerminalTab.Open</c> attaches a view to a pane, and a
/// pane with no handle and no size opens nothing — which is the same reason the client can show a
/// window before it has a terminal.</para>
/// </summary>
public sealed class TabTests
{
    /// <summary>Three tabs, because two cannot tell a wrap from a clamp.</summary>
    private static readonly string[] Three = ["a", "b", "c"];

    /// <summary>Switching tabs moves the keyboard, the selection and the find with it.</summary>
    [Fact]
    public void EverySurfaceAnswersForTheTabOnScreen()
    {
        (bool first, bool second, string firstName, string secondName) = OnStaThread(() =>
        {
            MainWindow window = new();

            TerminalTab one = TerminalTab.Open(Settings.Default, "one");
            TerminalTab two = TerminalTab.Open(Settings.Default, "two");

            window.Add(one);
            window.Add(two);

            // The second is on screen because it was added last.
            bool onSecond = ReferenceEquals(window.Typing, two.Typist);
            string named = window.Current!.Title;

            window.Active = 0;

            bool onFirst = ReferenceEquals(window.Typing, one.Typist);

            return (onFirst, onSecond, window.Current!.Title, named);
        });

        Assert.True(second, "a freshly added tab was not the one the keyboard answered for");
        Assert.True(first, "switching tabs left the keyboard on the tab before");

        Assert.Equal("one", firstName);
        Assert.Equal("two", secondName);
    }

    /// <summary>
    /// Next wraps past the last, and an index past the end does nothing.
    ///
    /// <para>The second half is the one worth writing down: Alt+7 in a window with three tabs is a
    /// mistake, and wrapping it onto the first would look like the chord meant something else.</para>
    /// </summary>
    [Fact]
    public void NextWrapsAndAnIndexPastTheEndDoesNothing()
    {
        int[] landed = OnStaThread(() =>
        {
            MainWindow window = new();

            foreach (string host in Three)
            {
                window.Add(TerminalTab.Open(Settings.Default, host));
            }

            List<int> at = [];

            Step(window, Key.Tab, ModifierKeys.Control);
            at.Add(window.Active);

            Step(window, Key.Tab, ModifierKeys.Control | ModifierKeys.Shift);
            at.Add(window.Active);

            Step(window, Key.D2, ModifierKeys.Alt);
            at.Add(window.Active);

            // Alt+7 with three tabs open.
            Step(window, Key.D7, ModifierKeys.Alt);
            at.Add(window.Active);

            return at.ToArray();
        });

        // Added last, so the third is current; next wraps to the first.
        Assert.Equal(0, landed[0]);

        // And previous from the first wraps back to the third.
        Assert.Equal(2, landed[1]);

        // Alt+2 is the second, counting the way a person does.
        Assert.Equal(1, landed[2]);

        // Alt+7 is not a tab, so nothing moved.
        Assert.Equal(1, landed[3]);
    }

    /// <summary>
    /// The title comes from three places, and each outranks the one after it.
    ///
    /// <para>The middle source is why tabs are worth building on the OSC work at all: a shell
    /// reporting its directory turns a strip of identical host names into information.</para>
    /// </summary>
    [Fact]
    public void TheTitleRanksTheUsersNameOverTheHostsOverWhatItIsConnectedTo()
    {
        (string connected, string written, string named) = OnStaThread(() => Ranked());

        Assert.Equal("cmd.exe", connected);
        Assert.Equal("~/work", written);
        Assert.Equal("prod-db", named);
    }

    /// <summary>The three sources in turn, on the thread a pane can be built on.</summary>
    private static (string Connected, string Written, string Named) Ranked()
    {
        TerminalTab tab = TerminalTab.Open(Settings.Default, "cmd.exe");

        // Nothing said yet, so what it is connected to.
        string connected = tab.Title;

        // The host writes one through OSC, which outranks that.
        tab.Emulator.Feed(Encoding.UTF8.GetBytes("\u001b]0;~/work\u0007"));

        string written = tab.Title;

        // And a name the user set outranks the host, because they said so.
        tab.Named = "prod-db";

        return (connected, written, tab.Title);
    }

    /// <summary>
    /// Output arriving in a tab nobody is looking at marks it, and looking at it clears the mark.
    /// </summary>
    [Fact]
    public void ATabNobodyIsLookingAtSaysSomethingHappened()
    {
        (bool quiet, bool stirred, bool looked) = OnStaThread(() =>
        {
            MainWindow window = new();

            TerminalTab background = TerminalTab.Open(Settings.Default, "background");

            window.Add(background);
            window.Add(TerminalTab.Open(Settings.Default, "foreground"));

            bool before = background.HasActivity;

            background.Emulator.Feed(Encoding.UTF8.GetBytes("something happened\r\n"));

            bool after = background.HasActivity;

            window.Active = 0;

            return (before, after, background.HasActivity);
        });

        Assert.False(quiet, "a tab that had seen nothing was marked anyway");
        Assert.True(stirred, "output in a background tab left no mark");
        Assert.False(looked, "the mark survived the user looking at the tab");
    }

    /// <summary>
    /// Closing a tab with a live session asks; closing one whose session ended does not.
    ///
    /// <para>Asking about a tab whose shell already exited is asking permission to tidy up, and a
    /// client that does that is one whose questions stop being read.</para>
    /// </summary>
    [Fact]
    public void ClosingALiveTabAsksAndClosingADeadOneDoesNot()
    {
        (int asked, int open) = OnStaThread(() =>
        {
            // Built here and not outside, because a pane is a FrameworkElement and there is no
            // thread but this one it can exist on. Connecting is awaited by blocking, which is safe
            // for the one reason that matters: nothing on that path ever posts back to this thread.
            TerminalTab live = Connected(Settings.Default, "cmd.exe", null);

            Assert.True(live.IsLive, "the shell this test needs did not start");

            TerminalTab dead = Connected(Settings.Default, "no-such-program-at-all",
                                         "no-such-program-at-all");

            Assert.False(dead.IsLive);
            Assert.NotNull(dead.Ended);

            int times = 0;

            MainWindow window = new()
            {
                Guard = CloseGuard.Asking(),
                AskingToClose = _ =>
                {
                    times++;

                    return new ClosingAnswer(Close: true, NeverAgain: false);
                },
            };

            window.Add(live);
            window.Add(dead);

            // The dead one is on screen, and goes without a question.
            window.CloseTab();

            int afterDead = times;

            Assert.Equal(0, afterDead);

            // The live one is now, and does not.
            window.CloseTab();

            // Ends is unset, so the window removed them and left them alive — which is what the
            // shutdown below is for and is also exactly what detaching a tab will need.
            live.DisposeAsync().AsTask().GetAwaiter().GetResult();
            dead.DisposeAsync().AsTask().GetAwaiter().GetResult();

            return (times, window.Held.Count);
        });

        Assert.Equal(1, asked);
        Assert.Equal(0, open);
    }

    /// <summary>
    /// A tab whose session never started stays open carrying the reason.
    ///
    /// <para>A tab that vanished would take the message with it, and the message is the one thing
    /// the user needed.</para>
    /// </summary>
    [Fact]
    public void ATabWhoseSessionDiedKeepsWhyOnScreen()
    {
        string screen = OnStaThread(() =>
        {
            TerminalTab tab = Connected(Settings.Default, "no-such-program-at-all",
                                        "no-such-program-at-all");

            Assert.NotNull(tab.Ended);
            Assert.False(tab.IsLive);

            string said = Screen(tab.Emulator);

            tab.DisposeAsync().AsTask().GetAwaiter().GetResult();

            return said;
        });

        // The reason is where a user is already looking, not only in a property.
        Assert.Contains("no-such-program-at-all", screen, StringComparison.Ordinal);
    }

    /// <summary>
    /// A tab with its shell already started, waited for on the thread that built it.
    ///
    /// <para>Blocking is safe here and nowhere near safe in general: every await underneath
    /// <see cref="TerminalTab.ConnectAsync"/> continues on the thread pool, so nothing is waiting
    /// for the thread doing the waiting.</para>
    /// </summary>
    private static TerminalTab Connected(Settings settings, string host, string? commandLine)
    {
        TerminalTab tab = TerminalTab.Open(settings, host);

        tab.ConnectAsync(commandLine).GetAwaiter().GetResult();

        return tab;
    }

    /// <summary>Presses a chord through the binding the window actually carries.</summary>
    private static void Step(MainWindow window, Key key, ModifierKeys modifiers) =>
        window.InputBindings.OfType<KeyBinding>()
              .Single(bound => bound.Key == key && bound.Modifiers == modifiers)
              .Command.Execute(null);

    /// <summary>The whole screen as one string.</summary>
    private static string Screen(Emulator emulator)
    {
        StringBuilder text = new();

        for (int row = 0; row < emulator.Buffer.Rows; row++)
        {
            foreach (Cell cell in emulator.Buffer.Screen(row))
            {
                if (cell.Width != 0)
                {
                    text.Append(emulator.Buffer.TextOf(cell));
                }
            }
        }

        return text.ToString();
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
                // A window builds a dispatcher on this thread, and one that is never shut down keeps
                // a foreground thread alive after the test has finished — one per case, until the
                // runner refuses to exit and reports a suite that passed as a failure.
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
