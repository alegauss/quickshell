using System.Windows;
using System.Windows.Input;
using Quickshell.App;
using Quickshell.Terminal;
using Xunit;

namespace Quickshell.App.Tests;

/// <summary>
/// QS52's falsification: <em>falsified when an action exists that the palette cannot reach</em>.
///
/// <para>The claim is only keepable because the list is derived. So the test that keeps it walks the
/// window's real input bindings — the same list a user's fingers reach — and asks the palette for
/// every one of them.</para>
/// </summary>
public sealed class CommandsTests
{
    /// <summary>The one device, atlas and render loop these tabs draw with. See QS49.</summary>
    private static readonly TerminalShare Shared = new();

    // ---- The falsification ----

    /// <summary>
    /// Every chord the window binds is an action the palette offers.
    ///
    /// <para><b>This is the falsification, and it is why the base class exists.</b> A command added
    /// without a name does not compile; a command added with one appears here without anybody
    /// remembering to add it.</para>
    /// </summary>
    [Fact]
    public void EveryBoundActionIsReachableFromThePalette()
    {
        (string[] bound, string[] offered) = OnStaThread(() =>
        {
            MainWindow window = new();

            // Two tabs, so the tab-stepping commands can run and are therefore offered. With one
            // tab they are correctly absent, which the test below is about.
            window.Add(TerminalTab.Open(Settings.Default, Shared, "first"));
            window.Add(TerminalTab.Open(Settings.Default, Shared, "second"));

            string[] names = [.. window.InputBindings
                                       .OfType<KeyBinding>()
                                       .Select(one => one.Command)
                                       .OfType<INamedCommand>()
                                       .Where(one => one.CanExecute(null))
                                       .Select(one => one.Name)
                                       .Distinct(StringComparer.Ordinal)];

            return (names, window.Actions.Select(one => one.Name).ToArray());
        });

        Assert.NotEmpty(bound);

        // An action a user can press that the palette does not offer. This is the claim.
        Assert.Empty(bound.Except(offered, StringComparer.Ordinal));

        // And nothing offered that nothing can do, which is the same claim read the other way.
        Assert.Empty(offered.Except(bound, StringComparer.Ordinal));
    }

    /// <summary>
    /// Every command the window binds carries a name, including one added tomorrow.
    ///
    /// <para>A binding whose command is a bare <see cref="ICommand"/> is invisible to the palette
    /// and would fail the claim above only where its chord happened to be checked. This fails on the
    /// binding itself.</para>
    /// </summary>
    [Fact]
    public void NoBindingCarriesACommandThatCannotSayWhatItIs()
    {
        string[] nameless = OnStaThread<string[]>(() =>
            [.. new MainWindow().InputBindings
                                .OfType<KeyBinding>()
                                .Where(one => one.Command is not INamedCommand)
                                .Select(one => Chord.Naming(one.Key, one.Modifiers))]);

        Assert.Empty(nameless);
    }

    /// <summary>
    /// An action that cannot run now is not offered, and a tab that exists is.
    ///
    /// <para>"Go to tab 7" in a window with two tabs is not an action. A palette that offered it
    /// would be teaching a user that some of its entries do nothing, which is how a palette stops
    /// being read.</para>
    /// </summary>
    [Fact]
    public void OnlyTheTabsThatAreOpenAreOffered()
    {
        (string[] one, string[] three) = OnStaThread(() =>
        {
            MainWindow window = new();

            window.Add(TerminalTab.Open(Settings.Default, Shared, "alpha"));

            string[] alone = [.. window.Actions.Select(entry => entry.Name)];

            window.Add(TerminalTab.Open(Settings.Default, Shared, "beta"));
            window.Add(TerminalTab.Open(Settings.Default, Shared, "gamma"));

            return (alone, window.Actions.Select(entry => entry.Name).ToArray());
        });

        Assert.Single(one, name => name.StartsWith("Go to tab", StringComparison.Ordinal));
        Assert.Equal(3, three.Count(name => name.StartsWith("Go to tab", StringComparison.Ordinal)));

        // With one tab there is nowhere to step to, so those are not actions either.
        Assert.DoesNotContain("Next tab", one, StringComparer.Ordinal);
        Assert.Contains("Next tab", three, StringComparer.Ordinal);
    }

    /// <summary>A tab is listed by what it is called, not only by where it sits.</summary>
    [Fact]
    public void ATabIsListedByItsName()
    {
        string[] offered = OnStaThread<string[]>(() =>
        {
            MainWindow window = new();

            window.Add(TerminalTab.Open(Settings.Default, Shared, "the build box"));

            return [.. window.Actions.Select(entry => entry.Name)];
        });

        Assert.Contains(offered, name => name.Contains("the build box", StringComparison.Ordinal));
    }

    /// <summary>
    /// Every entry shows the chord that also does it, which is how chords are learnt here.
    /// </summary>
    [Fact]
    public void AnEntryCarriesTheChordThatDoesTheSameThing()
    {
        Command found = OnStaThread(() =>
            new MainWindow().Actions.Single(entry => entry.Name == "Copy the selection"));

        Assert.Equal("Ctrl+Shift+C", found.Chord);
    }

    /// <summary>
    /// A command bound to two keys is one entry.
    ///
    /// <para>Splitting side by side is bound to both <c>OemBackslash</c> and <c>Oem5</c> because the
    /// backslash sits in different places on different keyboards. Listing it twice would show the
    /// user a fact about a <see cref="Key"/> enumeration rather than about this client.</para>
    /// </summary>
    [Fact]
    public void ACommandBoundTwiceIsOneEntry()
    {
        Command[] splitting = OnStaThread<Command[]>(() =>
            [.. new MainWindow().Actions.Where(entry => entry.Name == "Split pane right")]);

        Assert.Single(splitting);
        Assert.Equal("Ctrl+Shift+\\", splitting[0].Chord);
    }

    /// <summary>The palette is in the palette, because a list with a hole in it has to be remembered.</summary>
    [Fact]
    public void ThePaletteIsInThePalette()
    {
        Command found = OnStaThread(() =>
            new MainWindow().Actions.Single(entry => entry.Name == "Show all commands"));

        Assert.Equal("Ctrl+Shift+P", found.Chord);
    }

    // ---- Matching: what typing does ----

    /// <summary>Initials find a phrase, which is the whole reason to type rather than read.</summary>
    [Fact]
    public void InitialsFindThePhrase()
    {
        IReadOnlyList<Command> found = Commands.Matching(Sample(), "spr");

        Assert.Equal("Split pane right", found[0].Name);
    }

    /// <summary>A letter the name does not have anywhere excludes it.</summary>
    [Fact]
    public void ANameWithoutTheLettersIsNotAMatch()
    {
        Assert.Equal(-1, Commands.Score("Copy the selection", "zzz"));
        Assert.True(Commands.Score("Copy the selection", "cop") > 0);
    }

    /// <summary>Order matters: the letters have to arrive in the order they were typed.</summary>
    [Fact]
    public void TheLettersHaveToBeInOrder()
    {
        Assert.True(Commands.Score("Close tab", "ct") > 0);
        Assert.Equal(-1, Commands.Score("Close tab", "bc"));
    }

    /// <summary>Letters that landed together beat the same letters scattered.</summary>
    [Fact]
    public void ARunOfLettersBeatsTheSameLettersScattered()
    {
        Assert.True(Commands.Score("Paste", "pas") > Commands.Score("Split pane right", "pas"));
    }

    /// <summary>
    /// A name that starts with the first letter typed beats one that merely contains it.
    ///
    /// <para>Both of these are three letters landing at three word starts, so without this they
    /// score the same and the winner is whichever was bound first — which is not something a user
    /// can see or predict. Somebody typing an initial means a name that begins with it.</para>
    /// </summary>
    [Fact]
    public void ANameThatStartsWithWhatWasTypedWins()
    {
        Assert.True(Commands.Score("Show all commands", "sac")
                    > Commands.Score("Import sessions from another client", "sac"));
    }

    /// <summary>Nothing typed is everything, in the order the actions were bound.</summary>
    [Fact]
    public void AnEmptyQueryIsEverything()
    {
        IReadOnlyList<Command> sample = Sample();

        Assert.Equal(sample.Select(one => one.Name), Commands.Matching(sample, "  ").Select(one => one.Name));
    }

    /// <summary>
    /// What was run recently comes first, where nothing else separates two entries.
    ///
    /// <para>With nothing typed there is no other information to rank on, and what a user wanted a
    /// minute ago is usually what they want now.</para>
    /// </summary>
    [Fact]
    public void WhatWasRunRecentlyComesFirst()
    {
        IReadOnlyList<Command> found = Commands.Matching(Sample(), string.Empty, ["Paste", "Close tab"]);

        Assert.Equal("Paste", found[0].Name);
        Assert.Equal("Close tab", found[1].Name);
    }

    /// <summary>But never over a better match, because the user typed the better match.</summary>
    [Fact]
    public void RecencyNeverBeatsWhatWasTyped()
    {
        IReadOnlyList<Command> found = Commands.Matching(Sample(), "spr", ["Paste"]);

        Assert.Equal("Split pane right", found[0].Name);
    }

    /// <summary>Running one from the palette runs the command the chord runs, and remembers it.</summary>
    [Fact]
    public void RunningAnEntryRunsTheSameCommandTheChordDoes()
    {
        (bool opened, string[] recent) = OnStaThread(() =>
        {
            MainWindow window = new();

            bool asked = false;

            window.Opens = () => asked = true;
            window.Choosing = (all, _) => all.Single(entry => entry.Name == "New tab");

            window.ShowPalette();

            return (asked, window.Recent.ToArray());
        });

        Assert.True(opened, "the entry did not reach the command the chord reaches");
        Assert.Equal(["New tab"], recent);
    }

    /// <summary>Escaping the palette runs nothing and remembers nothing.</summary>
    [Fact]
    public void LeavingThePaletteRunsNothing()
    {
        (bool opened, int remembered) = OnStaThread(() =>
        {
            MainWindow window = new();

            bool asked = false;

            window.Opens = () => asked = true;
            window.Choosing = (_, _) => null;

            window.ShowPalette();

            return (asked, window.Recent.Count);
        });

        Assert.False(opened);
        Assert.Equal(0, remembered);
    }

    /// <summary>A handful of entries with nothing behind them, for the ranking claims.</summary>
    private static IReadOnlyList<Command> Sample() =>
    [
        new Command("Close tab", "Ctrl+Shift+W", Nothing.At.All),
        new Command("Split pane right", "Ctrl+Shift+\\", Nothing.At.All),
        new Command("Paste", "Ctrl+Shift+V", Nothing.At.All),
    ];

    /// <summary>A command that does nothing, so a ranking test needs no window.</summary>
    private sealed class Nothing : ICommand
    {
        public static Nothing At { get; } = new();

        public Nothing All => this;

        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter)
        {
        }
    }

    /// <summary>A window is a WPF object, so it is built where WPF can build one.</summary>
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
