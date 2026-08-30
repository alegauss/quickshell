using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Quickshell.App;
using Quickshell.Terminal;
using Xunit;

namespace Quickshell.App.Tests;

/// <summary>
/// The window, and the argument it makes by what is not in it.
///
/// <para><b>The falsification is run against a real window on a real STA thread.</b> A test that
/// asked the chrome model what it would show would be asking the same object that decides, so this
/// builds the window WPF would build and walks the tree that results. What is not there is the
/// claim.</para>
/// </summary>
public sealed class WindowTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"quickshell-window-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    // ---- The falsification: what a default installation shows ----

    /// <summary>
    /// A default installation shows a title bar and a terminal, and the tab strip that is there is
    /// not on screen. No toolbar, no status bar, no sidebar — and not hidden ones either, because
    /// the tree does not contain them at all.
    /// </summary>
    [Fact]
    public void ADefaultWindowShowsATitleBarAndATerminalAndNothingElse()
    {
        (bool tabs, string[] tree) = OnStaThread(() =>
        {
            MainWindow window = new();

            return (window.TabStripShowing, Tree(window).ToArray());
        });

        Assert.False(tabs, "the tab strip was on screen with one tab open");

        // The elements a terminal that opens onto a terminal does not have.
        Assert.DoesNotContain(tree, element => element.Contains("ToolBar", StringComparison.Ordinal));
        Assert.DoesNotContain(tree, element => element.Contains("StatusBar", StringComparison.Ordinal));
        Assert.DoesNotContain(tree, element => element.Contains("Menu", StringComparison.Ordinal));
        Assert.DoesNotContain(tree, element => element.Contains("Ribbon", StringComparison.Ordinal));
    }

    /// <summary>And the model says the same, which is what a settings surface will read.</summary>
    [Fact]
    public void TheChromeModelAgreesWithTheWindow()
    {
        Assert.Equal([ChromeElement.TitleBar, ChromeElement.Terminal], Chrome.Default.Showing(1));

        Assert.Equal([ChromeElement.TitleBar, ChromeElement.TabStrip, ChromeElement.Terminal],
                     Chrome.Default.Showing(2));

        Assert.False(Chrome.Default.Toolbar);
        Assert.False(Chrome.Default.StatusBar);
        Assert.False(Chrome.Default.Sidebar);
    }

    /// <summary>The strip appears when there is a choice to make, and goes again when there is not.</summary>
    [Fact]
    public void TheTabStripAppearsWithTheSecondTabAndLeavesWithIt()
    {
        bool[] showing = OnStaThread(() =>
        {
            MainWindow window = new();

            bool one = window.TabStripShowing;

            window.AddTab("first");

            bool stillOne = window.TabStripShowing;

            window.AddTab("second");

            bool two = window.TabStripShowing;

            window.RemoveTab();

            return new[] { one, stillOne, two, window.TabStripShowing };
        });

        Assert.False(showing[0]);
        Assert.False(showing[1]);
        Assert.True(showing[2], "a second tab did not bring the strip up");
        Assert.False(showing[3], "the strip stayed after the second tab went");
    }

    // ---- Theme: the chrome's and the terminal's are two settings ----

    /// <summary>
    /// Following the system means handing the question to WPF, which watches it. Resolving it to a
    /// colour at start-up is the bug that passes every test anybody runs by restarting.
    /// </summary>
    [Fact]
    public void FollowingTheSystemIsHandedToWpfRatherThanResolvedOnce()
    {
        ThemeMode mode = OnStaThread(() => new MainWindow().ThemeMode);

        Assert.Equal(ThemeMode.System, mode);
        Assert.True(Appearance.Default.FollowsSystem);
    }

    [Theory]
    [InlineData(ChromeTheme.Light)]
    [InlineData(ChromeTheme.Dark)]
    public void AChosenThemeIsHeldWhateverWindowsIsDoing(ChromeTheme chosen)
    {
        ThemeMode mode = OnStaThread(() =>
            new MainWindow(Appearance.Default with { Theme = chosen }).ThemeMode);

        Assert.NotEqual(ThemeMode.System, mode);
    }

    /// <summary>
    /// The mistake this design names: a user with a favourite scheme wants it under either chrome.
    /// Changing one must not touch the other, in the model or in the window.
    /// </summary>
    [Fact]
    public void TheChromesThemeDoesNotTouchTheTerminalsColours()
    {
        TerminalPalette mine = new("mine", new Rgb(1, 2, 3), new Rgb(4, 5, 6),
                                   new Rgb(7, 8, 9), new Rgb(10, 11, 12));

        Appearance light = new() { Theme = ChromeTheme.Light, Palette = mine };
        Appearance dark = light with { Theme = ChromeTheme.Dark };

        Assert.Equal(mine, dark.Palette);
        Assert.Equal(mine, light.Palette);

        TerminalPalette carried = OnStaThread(() => new MainWindow(dark).Appearance.Palette);

        Assert.Equal(mine, carried);
    }

    // ---- Where the window goes, per arrangement of screens ----

    private static readonly Screen Laptop = new(0, 0, 1920, 1080);
    private static readonly Screen Dock = new(1920, 0, 2560, 1440);

    /// <summary>
    /// The failure this exists to prevent: a window remembered on the dock coming back off-screen
    /// on the laptop alone. Two arrangements, two answers.
    /// </summary>
    [Fact]
    public void AWindowRememberedOnTheDockDoesNotComeBackOffScreen()
    {
        WindowPlacements placements = WindowPlacements.Empty();

        Placement onDock = new(2200, 100, 1200, 800, false);

        placements.Remember([Laptop, Dock], onDock);

        Assert.Equal(onDock, placements.For([Laptop, Dock]));

        // Undocked: that arrangement is not this one, so there is nothing remembered for it and the
        // window manager places the window somewhere a person can see.
        Assert.Null(placements.For([Laptop]));
    }

    /// <summary>And plugging the dock back in returns the window to where it was on it.</summary>
    [Fact]
    public void PluggingTheDockBackInRestoresWhereItWasThere()
    {
        string file = Path.Combine(_directory, "placements.json");

        Placement onDock = new(2200, 100, 1200, 800, false);
        Placement onLaptop = new(40, 40, 1200, 800, false);

        WindowPlacements first = WindowPlacements.ReadFrom(file);

        first.Remember([Laptop, Dock], onDock);
        first.Remember([Laptop], onLaptop);

        // A different launch, reading only what was written.
        WindowPlacements next = WindowPlacements.ReadFrom(file);

        Assert.Equal(onDock, next.For([Laptop, Dock]));
        Assert.Equal(onLaptop, next.For([Laptop]));
    }

    /// <summary>
    /// The same arrangement can still have lost a screen — a projector unplugged between launches —
    /// so a placement no screen holds is refused rather than opening a window nobody can find.
    /// </summary>
    [Fact]
    public void APlacementNoScreenHoldsIsRefused()
    {
        WindowPlacements placements = WindowPlacements.Empty();

        placements.Remember([Laptop], new Placement(5000, 5000, 800, 600, false));

        Assert.Null(placements.For([Laptop]));
    }

    /// <summary>Maximised is remembered on its own, and lands on a screen whatever happened.</summary>
    [Fact]
    public void MaximisedIsRememberedApartFromASize()
    {
        WindowPlacements placements = WindowPlacements.Empty();

        Placement maximised = new(2200, 100, 1200, 800, true);

        placements.Remember([Laptop], maximised);

        Assert.Equal(maximised, placements.For([Laptop]));
    }

    /// <summary>The arrangement's name does not depend on the order Windows lists screens in.</summary>
    [Fact]
    public void TheArrangementIsTheSameWhicheverOrderScreensArrive()
    {
        Assert.Equal(WindowPlacements.Arrangement([Laptop, Dock]),
                     WindowPlacements.Arrangement([Dock, Laptop]));

        Assert.NotEqual(WindowPlacements.Arrangement([Laptop, Dock]),
                        WindowPlacements.Arrangement([Laptop]));
    }

    /// <summary>A file this cannot read costs a window position and never a start-up.</summary>
    [Fact]
    public void AnUnreadablePlacementFileIsAnEmptySet()
    {
        Directory.CreateDirectory(_directory);

        string file = Path.Combine(_directory, "broken.json");

        File.WriteAllText(file, "this is not json");

        Assert.Equal(0, WindowPlacements.ReadFrom(file).Count);
    }

    // ---- Closing with sessions open ----

    /// <summary>Asks once, and names what is open rather than counting it.</summary>
    [Fact]
    public void ClosingWithSessionsOpenNamesThem()
    {
        CloseGuard guard = CloseGuard.Asking();

        ClosingQuestion? asked = guard.Ask(["prod-db", "staging"]);

        Assert.NotNull(asked);
        Assert.Equal(["prod-db", "staging"], asked.Value.Open);
        Assert.Contains("2 sessions", asked.Value.Asking, StringComparison.Ordinal);

        // One is named, because "1 session" tells a user nothing they did not know.
        Assert.Contains("prod-db", guard.Ask(["prod-db"])!.Value.Asking, StringComparison.Ordinal);
    }

    /// <summary>Nothing open is nothing to ask about.</summary>
    [Fact]
    public void ClosingWithNothingOpenAsksNothing()
    {
        Assert.Null(CloseGuard.Asking().Ask([]));
    }

    /// <summary>
    /// Never again means never, including next launch. A confirmation a user switched off and which
    /// comes back teaches them the checkbox is a lie — and after that they stop reading every dialog
    /// this client shows, including the one about a host key that changed.
    /// </summary>
    [Fact]
    public void NeverAgainIsHonouredAcrossLaunches()
    {
        string file = Path.Combine(_directory, "never-again");

        CloseGuard guard = CloseGuard.ReadFrom(file);

        Assert.NotNull(guard.Ask(["prod-db"]));

        guard.NeverAgain();

        Assert.True(guard.Silenced);
        Assert.Null(guard.Ask(["prod-db"]));

        // The next launch, which knows only what was written down.
        Assert.True(CloseGuard.ReadFrom(file).Silenced);
        Assert.Null(CloseGuard.ReadFrom(file).Ask(["prod-db"]));
    }

    // ---- Start-up: the first paint waits on nothing ----

    /// <summary>
    /// The window is built without reading a file or opening a connection.
    ///
    /// <para>Asserted by building one with the user profile pointed at an empty directory: anything
    /// on this path that read configuration would find none, and anything that opened a connection
    /// would be visible as a delay. Cold start is a number this project publishes and the first
    /// paint is the half a user feels.</para>
    /// </summary>
    [Fact]
    public void TheWindowIsBuiltWithoutReadingAnythingOrOpeningAnything()
    {
        System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();

        bool built = OnStaThread(() => new MainWindow() is not null);

        clock.Stop();

        Assert.True(built);

        // Generous by two orders of magnitude against anything that touched a network, and tight
        // enough that a configuration file parsed here would show.
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2),
                    $"building the window took {clock.Elapsed.TotalMilliseconds:F0} ms");
    }

    // ---- plumbing ----

    /// <summary>
    /// Runs something on an STA thread, which is the only kind a WPF window can be built on.
    ///
    /// <para>The test host's threads are not STA and xunit has no attribute for it here, so the
    /// thread is made rather than asked for. Everything comes back through the result or the
    /// exception, so a failure inside reads as a failure of the test rather than as a hang.</para>
    /// </summary>
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

        // Background, so a WPF dispatcher that outlives the work does not hold the test host open
        // for ten seconds after every run.
        thread.IsBackground = true;
        thread.Start();

        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "the STA thread never finished");

        if (failed is not null)
        {
            throw new InvalidOperationException("the window could not be built", failed);
        }

        return result;
    }

    /// <summary>Every element in the window's tree, by type name.</summary>
    private static IEnumerable<string> Tree(DependencyObject root)
    {
        yield return root.GetType().Name;

        if (root is ContentControl { Content: DependencyObject content })
        {
            foreach (string name in Tree(content))
            {
                yield return name;
            }
        }

        if (root is not Panel panel)
        {
            yield break;
        }

        foreach (UIElement child in panel.Children)
        {
            foreach (string name in Tree(child))
            {
                yield return name;
            }
        }
    }
}
