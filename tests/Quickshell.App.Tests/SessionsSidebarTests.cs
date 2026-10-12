using System.IO;
using System.Windows;
using System.Windows.Controls;
using Quickshell.App;
using Xunit;

namespace Quickshell.App.Tests;

/// <summary>
/// The sessions sidebar (QS268): what it lists, worked out from the store, and what each of its
/// gestures does to the store and the window.
///
/// <para><b>The rows are asked of the model and of a real window both.</b> The model is where the
/// tree, the search and the Connected mark are decided; the window is where they reach a row, and a
/// sidebar that listed the right rows to nobody is the defect the second half is for. The design's
/// falsification — a saved session that cannot be found and connected without the chord — is
/// <see cref="ADoubleClickOrEnterConnectsTheSessionWithoutThePalette"/>.</para>
/// </summary>
public sealed class SessionsSidebarTests : IDisposable
{
    /// <summary>The one device, atlas and render loop these tabs draw with. See QS49.</summary>
    private static readonly TerminalShare Shared = new();

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"quickshell-sidebar-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    /// <summary>The store the design draws: a folder that hands down a user and a jump host, and one that does not.</summary>
    private static SessionTree Store() => SessionTree.Of(new SessionNode
    {
        Children =
        [
            new SessionNode
            {
                Name = "Work",
                Settings = new SessionSettings { User = "deploy", JumpHost = "bastion" },
                Children =
                [
                    new SessionNode { Name = "build-box", Host = "build.example.com", Tags = ["ci"] },
                    new SessionNode { Name = "db-primary", Host = "db1.example.com", Settings = new SessionSettings { Port = 2222 }, Tags = ["postgres"] },
                ],
            },
            new SessionNode
            {
                Name = "Home lab",
                Children = [new SessionNode { Name = "nas", Host = "nas.home.arpa", Settings = new SessionSettings { User = "admin" } }],
            },
            new SessionNode { Name = "pi", Host = "192.168.1.4" },
        ],
    });

    private static readonly HashSet<string> NoneFolded = new(StringComparer.OrdinalIgnoreCase);

    // ---- The model ----

    /// <summary>
    /// The tree, top to bottom, every folder open: a folder says what it hands down, a session says its
    /// own address, and nothing a folder hands down is repeated on each session under it.
    /// </summary>
    [Fact]
    public void TheTreeListsFoldersWithWhatTheyHandDownAndSessionsWithTheirOwnAddress()
    {
        IReadOnlyList<SessionRow> rows = SessionsView.Rows(Store(), string.Empty, NoneFolded, _ => false);

        Assert.Equal(
            [
                ("Work", "deploy · via bastion", 0, true),
                ("build-box", "build.example.com", 1, false),
                ("db-primary", "db1.example.com:2222", 1, false),
                ("Home lab", "", 0, true),
                ("nas", "admin@nas.home.arpa", 1, false),
                ("pi", "192.168.1.4", 0, false),
            ],
            rows.Select(row => (row.Name, row.Meta, row.Depth, row.IsFolder)));

        Assert.Equal("Work/db-primary", rows[2].Path);
    }

    /// <summary>A folded folder is listed, marked folded, and nothing under it is.</summary>
    [Fact]
    public void AFoldedFolderHidesWhatIsInIt()
    {
        HashSet<string> folded = new(StringComparer.OrdinalIgnoreCase) { "work" };

        IReadOnlyList<SessionRow> rows = SessionsView.Rows(Store(), string.Empty, folded, _ => false);

        Assert.Equal(["Work", "Home lab", "Home lab/nas", "pi"], rows.Select(row => row.Path));
        Assert.False(rows[0].Expanded);
        Assert.True(rows[1].Expanded);
    }

    /// <summary>
    /// A search lists the sessions it finds, flat and best first, by the palette's own match: part of a
    /// name, a host or a tag, each with its folder in its line.
    /// </summary>
    [Fact]
    public void ASearchIsAFlatListFoundAsOpenSessionFindsIt()
    {
        IReadOnlyList<SessionRow> byName = SessionsView.Rows(Store(), "primary", NoneFolded, _ => false);

        Assert.Equal(["Work/db-primary"], byName.Select(row => row.Path));
        Assert.Equal(("db-primary", "Work · db1.example.com:2222", 0), (byName[0].Name, byName[0].Meta, byName[0].Depth));

        Assert.Equal(["Work/db-primary"], SessionsView.Rows(Store(), "postgres", NoneFolded, _ => false).Select(row => row.Path));
        Assert.Equal(["Home lab/nas"], SessionsView.Rows(Store(), "home.arpa", NoneFolded, _ => false).Select(row => row.Path));
        Assert.Empty(SessionsView.Rows(Store(), "nothing-like-it", NoneFolded, _ => false));
    }

    /// <summary>A session a tab holds is marked Connected, wherever it is listed; a folder never is.</summary>
    [Fact]
    public void ASessionATabHoldsIsMarkedConnected()
    {
        IReadOnlyList<SessionRow> rows = SessionsView.Rows(Store(), string.Empty, NoneFolded,
                                                           path => path is "Work/db-primary" or "Work");

        Assert.Equal(["Work/db-primary"], rows.Where(row => row.Connected).Select(row => row.Path));
        Assert.True(SessionsView.Rows(Store(), "primary", NoneFolded, path => path == "Work/db-primary").Single().Connected);
    }

    // ---- The window ----

    /// <summary>A default window has the sidebar, open, and reads nothing until it is asked to.</summary>
    [Fact]
    public void ADefaultWindowHasTheSidebarOpenAndEmptyUntilTheStoreIsRead()
    {
        (bool open, int rows) = Sta.Run(() =>
        {
            MainWindow window = new() { SessionsFile = Stored() };

            return (window.SidebarShowing, window.Sidebar.Rows.Count);
        });

        Assert.True(open, "the sidebar was folded in a default window");
        Assert.Equal(0, rows);
    }

    /// <summary>Once read, the window's sidebar lists the store, and the search narrows it.</summary>
    [Fact]
    public void TheWindowListsTheStoreAndTheSearchNarrowsIt()
    {
        string file = Stored();

        (string[] all, string[] found) = Sta.Run(() =>
        {
            MainWindow window = new() { SessionsFile = file };

            window.RefreshSessions();
            string[] listed = [.. window.Sidebar.Rows.Select(row => row.Path)];

            window.SearchSessions("nas");

            return (listed, window.Sidebar.Rows.Select(row => row.Path).ToArray());
        });

        Assert.Equal(["Work", "Work/build-box", "Work/db-primary", "Home lab", "Home lab/nas", "pi"], all);
        Assert.Equal(["Home lab/nas"], found);
    }

    /// <summary>
    /// The design's falsification: a saved session is found and connected from the sidebar, through
    /// the same path <b>Open session</b> takes, with no chord pressed.
    /// </summary>
    [Fact]
    public void ADoubleClickOrEnterConnectsTheSessionWithoutThePalette()
    {
        string file = Stored();

        string[] opened = Sta.Run(() =>
        {
            List<string> asked = [];
            MainWindow window = new() { SessionsFile = file, OpensSession = asked.Add };

            window.RefreshSessions();
            Assert.True(window.Sidebar.Select("Work/db-primary"));

            window.ConnectSession(window.Sidebar.Selected!.Path);

            return asked.ToArray();
        });

        Assert.Equal(["Work/db-primary"], opened);
    }

    /// <summary>A tab opened from a saved session marks its row Connected, and closing it takes the mark away.</summary>
    [Fact]
    public void ATabOpenFromASessionMarksItsRowAndClosingItClearsTheMark()
    {
        string file = Stored();

        (string[] whileOpen, string[] afterClose) = Sta.Run(() =>
        {
            MainWindow window = new() { SessionsFile = file };

            window.RefreshSessions();

            TerminalTab tab = TerminalTab.Open(Settings.Default, Shared, "db1.example.com");

            tab.Saved = "Work/db-primary";
            window.Add(tab);

            string[] marked = [.. window.Sidebar.Rows.Where(row => row.Connected).Select(row => row.Path)];

            window.Remove(0);

            return (marked, window.Sidebar.Rows.Where(row => row.Connected).Select(row => row.Path).ToArray());
        });

        Assert.Equal(["Work/db-primary"], whileOpen);
        Assert.Empty(afterClose);
    }

    /// <summary>
    /// Folding writes <c>sessionsSidebar</c> to the settings file, and the file read back folds a
    /// window: the next window opens the way this one was left.
    /// </summary>
    [Fact]
    public void FoldingIsWrittenToTheSettingsAndTheFileFoldsAWindow()
    {
        string settings = Path.Combine(_directory, "settings.json");

        Directory.CreateDirectory(_directory);
        File.WriteAllText(settings, "{\n  \"theme\": \"Dark\"\n}\n");

        (bool folded, bool reopened) = Sta.Run(() =>
        {
            MainWindow window = new() { SettingsPath = settings };

            window.FoldSidebar(open: false);

            MainWindow next = new();

            next.Apply(SettingsFile.ReadFrom(settings));

            return (window.SidebarShowing, next.SidebarShowing);
        });

        Assert.False(folded);
        Assert.False(reopened);
        Assert.False(SettingsFile.ReadFrom(settings).SessionsSidebar);
        Assert.Contains("\"theme\": \"Dark\"", File.ReadAllText(settings), StringComparison.Ordinal);
    }

    /// <summary>Duplicating puts a copy beside the session, named for it, and selects it.</summary>
    [Fact]
    public void DuplicatingPutsANamedCopyBesideIt()
    {
        string file = Stored();

        string selected = Sta.Run(() =>
        {
            MainWindow window = new() { SessionsFile = file };

            window.RefreshSessions();
            window.DuplicateSession("Work/db-primary");
            window.DuplicateSession("Work/db-primary");

            return window.Sidebar.Selected!.Path;
        });

        SessionTree store = SessionTree.ReadFrom(file);

        Assert.Equal("Work/db-primary copy 2", selected);
        Assert.Equal(2222, store.Session("Work/db-primary copy")!.Port!.Value.Value);
        Assert.NotNull(store.Session("Work/db-primary copy 2"));
        Assert.NotNull(store.Session("Work/db-primary"));
    }

    /// <summary>
    /// Moving takes a session to another folder, where it takes on what that folder hands down, and
    /// refuses a folder that already holds its name.
    /// </summary>
    [Fact]
    public void MovingTakesASessionToAnotherFolderAndRefusesANameThatIsThere()
    {
        string file = Stored();

        // A second nas, at the top, for the refusal to be about.
        SessionTree.ReadFrom(file).With("nas", new SessionNode { Name = "nas", Host = "other.example" }).WriteTo(file);

        (string[] offered, string? moved, string? refused, List<string> said) = Sta.Run(() =>
        {
            List<string> told = [];
            MainWindow window = new() { SessionsFile = file, Tells = told.Add };

            window.RefreshSessions();

            string[] folders = [.. window.FoldersBesides("pi")];
            string? to = window.MoveSession("pi", "Work");

            return (folders, to, window.MoveSession("Home lab/nas", string.Empty), told);
        });

        SessionTree store = SessionTree.ReadFrom(file);

        Assert.Equal(["Work", "Home lab"], offered);
        Assert.Equal("Work/pi", moved);
        Assert.Null(store.Session("pi"));
        Assert.Equal("deploy", store.Session("Work/pi")!.User!.Value.Value);

        Assert.Null(refused);
        Assert.Equal("nas.home.arpa", store.Session("Home lab/nas")!.Host);
        Assert.Contains("already holds something called nas", Assert.Single(said), StringComparison.Ordinal);
    }

    /// <summary>Delete asks first; a no keeps the session and a yes takes it out of the store.</summary>
    [Fact]
    public void DeletingAsksFirstAndAYesTakesItOut()
    {
        string file = Stored();

        (bool kept, bool deleted, string[] asked) = Sta.Run(() =>
        {
            List<string> questions = [];
            bool answer = false;

            MainWindow window = new()
            {
                SessionsFile = file,
                AsksToDelete = path =>
                {
                    questions.Add(path);

                    return answer;
                },
            };

            window.RefreshSessions();

            bool first = window.DeleteSession("Home lab/nas");

            answer = true;

            return (first, window.DeleteSession("Home lab/nas"), questions.ToArray());
        });

        Assert.False(kept);
        Assert.True(deleted);
        Assert.Equal(["Home lab/nas", "Home lab/nas"], asked);
        Assert.Null(SessionTree.ReadFrom(file).Session("Home lab/nas"));
    }

    /// <summary>Editing through the dialog writes what it saved, and a new name renames the session in place.</summary>
    [Fact]
    public void EditingRenamesASessionInPlace()
    {
        string file = Stored();

        Sta.Run(() =>
        {
            MainWindow window = new()
            {
                SessionsFile = file,
                ShowsSessionDialog = dialog =>
                {
                    ((TextBox)LogicalTreeHelper.FindLogicalNode(dialog, "Name")).Text = "builder";

                    ((Button)LogicalTreeHelper.FindLogicalNode(dialog, "Save"))
                        .RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));

                    return dialog.Saved is not null;
                },
            };

            window.RefreshSessions();
            window.EditSession("Work/build-box");
        });

        Assert.Equal(["Work/builder", "Work/db-primary", "Home lab/nas", "pi"],
                     SessionTree.ReadFrom(file).Sessions().Select(each => each.Path));
    }

    /// <summary>A store that will not read is said where the sessions would be, and the window still opens.</summary>
    [Fact]
    public void AStoreThatWillNotReadIsSaidInTheSidebar()
    {
        Directory.CreateDirectory(_directory);

        string file = Path.Combine(_directory, "sessions.json");

        File.WriteAllText(file, "{ \"name\": \"\", \"children\": [ { \"name\": \"x\", \"hots\": \"typo\" } ] }");

        int rows = Sta.Run(() =>
        {
            MainWindow window = new() { SessionsFile = file };

            window.RefreshSessions();

            return window.Sidebar.Rows.Count;
        });

        Assert.Equal(0, rows);
    }

    /// <summary>The store <see cref="Store"/> describes, written to a file of this test's own.</summary>
    private string Stored()
    {
        Directory.CreateDirectory(_directory);

        string file = Path.Combine(_directory, $"sessions-{Guid.NewGuid():N}.json");

        Store().WriteTo(file);

        return file;
    }
}
