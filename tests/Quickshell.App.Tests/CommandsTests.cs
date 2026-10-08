using System.IO;
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
        (string[] bound, string[] offered) = Sta.Run(() =>
        {
            MainWindow window = new();

            // Two tabs, so the tab-stepping commands can run and are therefore offered. With one
            // tab they are correctly absent, which the test below is about.
            window.Add(TerminalTab.Open(Settings.Default, Shared, "first"));
            window.Add(TerminalTab.Open(Settings.Default, Shared, "second"));

            // Every binding and not only the keys: an action bound to PaletteOnly has no chord and
            // is still one of the things this client does.
            string[] names = [.. window.InputBindings
                                       .OfType<InputBinding>()
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
        string[] nameless = Sta.Run<string[]>(() =>
            [.. new MainWindow().InputBindings
                                .OfType<InputBinding>()
                                .Where(one => one.Command is not INamedCommand)
                                .Select(one => Chord.Of(one) ?? one.Command.GetType().Name)]);

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
        (string[] one, string[] three) = Sta.Run(() =>
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

    /// <summary>
    /// QS160: the tab on screen moves along the strip and stays on screen, the same tab object with
    /// its session untouched; it stops at either end, and the palette offers only the moves that can
    /// happen.
    /// </summary>
    [Fact]
    public void ATabMovesAlongTheStripAndStopsAtTheEnds()
    {
        (string[] order, int active, bool same, bool pastEnd, string[] atFirst, string[] inMiddle) =
            Sta.Run<(string[], int, bool, bool, string[], string[])>(() =>
        {
            MainWindow window = new();

            window.Add(TerminalTab.Open(Settings.Default, Shared, "alpha"));
            window.Add(TerminalTab.Open(Settings.Default, Shared, "beta"));
            window.Add(TerminalTab.Open(Settings.Default, Shared, "gamma"));

            TerminalTab gamma = window.Held[2];

            window.MoveTab(-1);
            window.MoveTab(-1);

            bool further = window.MoveTab(-1);
            string[] first = [.. window.Actions.Select(entry => entry.Name)];

            window.MoveTab(1);

            return ([.. window.Held.Select(tab => tab.Host)], window.Active,
                    ReferenceEquals(window.Current, gamma), further, first,
                    [.. window.Actions.Select(entry => entry.Name)]);
        });

        Assert.Equal(["alpha", "gamma", "beta"], order);
        Assert.Equal(1, active);
        Assert.True(same, "the tab on screen is not the one that was moved");
        Assert.False(pastEnd, "a tab moved past the first place");

        Assert.DoesNotContain("Move tab left", atFirst, StringComparer.Ordinal);
        Assert.Contains("Move tab right", atFirst, StringComparer.Ordinal);
        Assert.Contains("Move tab left", inMiddle, StringComparer.Ordinal);
        Assert.Contains("Move tab right", inMiddle, StringComparer.Ordinal);
    }

    /// <summary>
    /// QS164: splitting stops at sixteen panes, however long the chord is held, and the palette stops
    /// offering it there.
    /// </summary>
    [Fact]
    public void SplittingStopsAtSixteenPanes()
    {
        (int panes, bool lastSplit, string[] offered) = Sta.Run<(int, bool, string[])>(() =>
        {
            MainWindow window = new();

            window.Add(TerminalTab.Open(Settings.Default, Shared, "alpha"));

            bool split = true;

            for (int held = 0; held < 20; held++)
            {
                split = window.SplitPane(held % 2 == 0 ? Divide.Beside : Divide.Below);
            }

            return (window.Current!.Layout.Count, split, [.. window.Actions.Select(entry => entry.Name)]);
        });

        Assert.Equal(MainWindow.MaximumPanes, panes);
        Assert.False(lastSplit);
        Assert.DoesNotContain("Split pane right", offered, StringComparer.Ordinal);
        Assert.DoesNotContain("Split pane down", offered, StringComparer.Ordinal);
    }

    /// <summary>A tab is listed by what it is called, not only by where it sits.</summary>
    [Fact]
    public void ATabIsListedByItsName()
    {
        string[] offered = Sta.Run<string[]>(() =>
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
        Command found = Sta.Run(() =>
            new MainWindow().Actions.Single(entry => entry.Name == "Copy the selection"));

        Assert.Equal("Ctrl+Shift+C", found.Chord);
    }

    /// <summary>
    /// Splitting side by side is one entry, named by the character it is bound to.
    ///
    /// <para>It was bound to both <c>OemBackslash</c> and <c>Oem5</c> because the backslash sits in
    /// different places on different keyboards; since QS171 it is one binding on the character, and
    /// the palette names it by that character on every layout.</para>
    /// </summary>
    [Fact]
    public void ACommandBoundTwiceIsOneEntry()
    {
        Command[] splitting = Sta.Run<Command[]>(() =>
            [.. new MainWindow().Actions.Where(entry => entry.Name == "Split pane right")]);

        Assert.Single(splitting);
        Assert.Equal("Ctrl+Shift+\\", splitting[0].Chord);
    }

    /// <summary>The palette is in the palette, because a list with a hole in it has to be remembered.</summary>
    [Fact]
    public void ThePaletteIsInThePalette()
    {
        Command found = Sta.Run(() =>
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
        (bool opened, string[] recent) = Sta.Run(() =>
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
        (bool opened, int remembered) = Sta.Run(() =>
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

    // ---- Opening a saved session (QS217) ----

    /// <summary>
    /// QS217's falsification: a saved session is opened from the palette, found by typing part of
    /// its host or one of its tags as well as its path, and not only by typing its path somewhere.
    /// </summary>
    [Theory]
    [InlineData("web-01", "prod/web")]
    [InlineData("db.example", "db")]
    [InlineData("payments", "prod/web")]
    public void ASavedSessionIsOpenedFromThePaletteByWhatTheUserRemembers(string typed, string opened)
    {
        (List<string> asked, string[] offered) = WithStore(window =>
        {
            List<string> seen = [];
            string[] listed = [];

            window.OpensSession = path => seen.Add(path);
            window.Choosing = (all, recent) =>
            {
                listed = [.. all.Select(one => one.Name)];

                return Commands.Matching(all, typed, recent)[0];
            };

            window.ChooseSession();

            return (seen, listed);
        });

        Assert.Equal(["db", "prod/web"], offered.Order(StringComparer.Ordinal));
        Assert.Equal([opened], asked);
    }

    /// <summary>With nothing typed, the session opened last is first, as a command run last is.</summary>
    [Fact]
    public void TheSessionOpenedLastComesFirst()
    {
        string first = WithStore(window =>
        {
            window.OpensSession = _ => { };

            window.Choosing = (all, recent) => all.Single(one => one.Name == "db");
            window.ChooseSession();

            string top = string.Empty;

            window.Choosing = (all, recent) =>
            {
                top = Commands.Matching(all, string.Empty, recent)[0].Name;

                return null;
            };
            window.ChooseSession();

            return top;
        });

        Assert.Equal("db", first);
    }

    /// <summary>Offered only by a window that can open a session; elsewhere it would do nothing.</summary>
    [Fact]
    public void OpenSessionIsOfferedOnlyWhereASessionCanBeOpened()
    {
        (bool without, bool with) = Sta.Run(() =>
        {
            MainWindow window = new();
            bool before = window.Actions.Any(one => one.Name == "Open session");

            window.OpensSession = _ => { };

            return (before, window.Actions.Any(one => one.Name == "Open session"));
        });

        Assert.False(without);
        Assert.True(with);
    }

    /// <summary>A window over a store holding two sessions, one in a folder with a tag.</summary>
    private static T WithStore<T>(Func<MainWindow, T> work)
    {
        string store = Path.Combine(Path.GetTempPath(), $"quickshell-open-{Guid.NewGuid():N}.json");

        SessionTree.Of(new SessionNode
        {
            Children =
            [
                new SessionNode
                {
                    Name = "prod",
                    Children = [new SessionNode { Name = "web", Host = "web-01.example", Tags = ["payments"] }],
                },
                new SessionNode { Name = "db", Host = "db.example" },
            ],
        }).WriteTo(store);

        try
        {
            return Sta.Run(() => work(new MainWindow { SessionsFile = store }));
        }
        finally
        {
            File.Delete(store);
        }
    }

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
}
