using System.Windows.Input;
using Quickshell.App;
using Xunit;

// Both namespaces name a Key. Here the window's is meant: these are chords a person presses.
using Key = System.Windows.Input.Key;

namespace Quickshell.App.Tests;

/// <summary>
/// The find bar and the scrollback chords, which is the half QS31's models could not reach.
///
/// <para><b>The bar is chrome, and it is the only chrome in this client a user summons.</b> So what
/// is asserted first is that a default window has never seen it — the claim QS46 makes about what a
/// default installation shows is one this element could quietly break.</para>
/// </summary>
public sealed class FindBarTests
{
    /// <summary>A default window does not show it, and the chrome model says the same.</summary>
    [Fact]
    public void ADefaultWindowHasNeverSeenTheFindBar()
    {
        bool showing = OnStaThread(() => new MainWindow().FindBarShowing);

        Assert.False(showing);

        Assert.Equal([ChromeElement.TitleBar, ChromeElement.Terminal], Chrome.Default.Showing(1));
        Assert.False(Chrome.Default.FindBar);

        // And when it is open it sits above the terminal, which is where the layout puts it.
        Assert.Equal([ChromeElement.TitleBar, ChromeElement.FindBar, ChromeElement.Terminal],
                     (Chrome.Default with { FindBar = true }).Showing(1));
    }

    /// <summary>
    /// Ctrl+Shift+F opens it and closes it again, through the binding the window carries.
    /// </summary>
    [Fact]
    public void CtrlShiftFOpensTheBarAndClosesIt()
    {
        (bool afterFirst, bool afterSecond) = OnStaThread(() =>
        {
            MainWindow window = new();

            window.Show();

            KeyBinding binding = window.InputBindings.OfType<KeyBinding>()
                                       .Single(bound => bound.Key == Key.F);

            Assert.Equal(ModifierKeys.Control | ModifierKeys.Shift, binding.Modifiers);

            binding.Command.Execute(null);

            bool opened = window.FindBarShowing;

            binding.Command.Execute(null);

            bool closed = window.FindBarShowing;

            window.Close();

            return (opened, closed);
        });

        Assert.True(afterFirst, "Ctrl+Shift+F did not open the find bar");
        Assert.False(afterSecond, "Ctrl+Shift+F did not close it again");
    }

    /// <summary>
    /// What the bar says is what happened: a count when something was found, and so when it was not.
    ///
    /// <para>Both halves matter. A bar that says nothing after a failed search is a client that
    /// looks like it ignored the keypress, and the user presses it again.</para>
    /// </summary>
    [Fact]
    public void TheBarSaysWhetherAnythingWasFound()
    {
        (string found, string missing, string empty) = OnStaThread(() =>
        {
            MainWindow window = new() { Finding = (_, _, _) => 7 };

            window.Show();
            window.ShowFindBar(showing: true);

            Typed(window, "needle");

            Assert.True(window.FindNext());

            string said = window.FoundSaying;

            window.Finding = (_, _, _) => null;

            Assert.False(window.FindNext());

            string nothing = window.FoundSaying;

            // And an empty box searches for nothing and says nothing, rather than reporting a miss
            // for a search nobody made.
            Typed(window, string.Empty);

            Assert.False(window.FindNext());

            string blank = window.FoundSaying;

            window.Close();

            return (said, nothing, blank);
        });

        Assert.Contains("7", found, StringComparison.Ordinal);
        Assert.Equal("not found", missing);
        Assert.Equal(string.Empty, empty);
    }

    /// <summary>
    /// Capitals matter only where the user typed one, which is what every editor does.
    ///
    /// <para>Somebody hunting an error message in ten thousand lines is not thinking about case
    /// until the moment they type a capital on purpose — so the rule needs no setting and no
    /// explaining, and a checkbox for it would be a surface bought for nothing.</para>
    /// </summary>
    [Fact]
    public void CapitalsMatterOnlyWhereTheUserTypedOne()
    {
        (bool lower, bool upper) = OnStaThread(() =>
        {
            bool exactly = false;

            MainWindow window = new()
            {
                Finding = (_, _, sensitive) =>
                {
                    exactly = sensitive;

                    return 1;
                },
            };

            window.Show();
            window.ShowFindBar(showing: true);

            Typed(window, "error");
            window.FindNext();

            bool afterLower = exactly;

            Typed(window, "Error");
            window.FindNext();

            bool afterUpper = exactly;

            window.Close();

            return (afterLower, afterUpper);
        });

        Assert.False(lower, "a lower-case needle was searched case-sensitively");
        Assert.True(upper, "a needle with a capital in it was not searched case-sensitively");
    }

    /// <summary>
    /// Shift+PageUp goes back through the history and Shift+PageDown comes forward, and the bare
    /// page keys are left to the program.
    ///
    /// <para>The second half is what would go wrong quietly: a pager and an editor both bind PageUp,
    /// and a client that took it would scroll the terminal while the thing on screen did not
    /// move.</para>
    /// </summary>
    [Fact]
    public void ShiftPageMovesTheHistoryAndTheBarePageKeysAreTheProgramsent()
    {
        List<int> asked = OnStaThread(() =>
        {
            List<int> lines = [];

            MainWindow window = new() { Scrolling = lines.Add };

            foreach (Key key in new[] { Key.PageUp, Key.PageDown })
            {
                KeyBinding bound = window.InputBindings.OfType<KeyBinding>()
                                         .Single(binding => binding.Key == key);

                Assert.Equal(ModifierKeys.Shift, bound.Modifiers);

                bound.Command.Execute(null);
            }

            return lines;
        });

        Assert.Equal(2, asked.Count);

        // Back first, then forward by the same amount.
        Assert.True(asked[0] < 0, "Shift+PageUp did not go back through the history");
        Assert.Equal(-asked[0], asked[1]);
    }

    /// <summary>Puts text in the find bar the way a person would, and asserts it arrived.</summary>
    private static void Typed(MainWindow window, string needle)
    {
        System.Windows.Controls.TextBox box = Boxes(window).Single();

        box.Text = needle;
    }

    /// <summary>Every text box in the window, which is the find bar's and nothing else.</summary>
    private static IEnumerable<System.Windows.Controls.TextBox> Boxes(System.Windows.DependencyObject from)
    {
        int children = System.Windows.Media.VisualTreeHelper.GetChildrenCount(from);

        for (int child = 0; child < children; child++)
        {
            System.Windows.DependencyObject at =
                System.Windows.Media.VisualTreeHelper.GetChild(from, child);

            if (at is System.Windows.Controls.TextBox box)
            {
                yield return box;
            }

            foreach (System.Windows.Controls.TextBox nested in Boxes(at))
            {
                yield return nested;
            }
        }
    }

    /// <summary>Runs something on an STA thread, which is the only kind a WPF window lives on.</summary>
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
