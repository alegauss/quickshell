using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
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

    private static readonly HashSet<string> AllOpened = new(StringComparer.OrdinalIgnoreCase) { "Work", "Home lab" };

    private static readonly HashSet<string> NoneOpened = new(StringComparer.OrdinalIgnoreCase);

    // ---- The model ----

    /// <summary>
    /// The tree, top to bottom, every folder open: a folder says what it hands down, a session says its
    /// own address, and nothing a folder hands down is repeated on each session under it.
    /// </summary>
    [Fact]
    public void TheTreeListsFoldersWithWhatTheyHandDownAndSessionsWithTheirOwnAddress()
    {
        IReadOnlyList<SessionRow> rows = SessionsView.Rows(Store(), string.Empty, AllOpened, _ => false);

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
        HashSet<string> opened = new(StringComparer.OrdinalIgnoreCase) { "home lab" };

        IReadOnlyList<SessionRow> rows = SessionsView.Rows(Store(), string.Empty, opened, _ => false);

        Assert.Equal(["Work", "Home lab", "Home lab/nas", "pi"], rows.Select(row => row.Path));
        Assert.False(rows[0].Expanded);
        Assert.True(rows[1].Expanded);
    }

    /// <summary>QS304: a folder nobody opened is folded, so the tree starts as its top level.</summary>
    [Fact]
    public void EveryFolderStartsFolded()
    {
        IReadOnlyList<SessionRow> rows = SessionsView.Rows(Store(), string.Empty, NoneOpened, _ => false);

        Assert.Equal(["Work", "Home lab", "pi"], rows.Select(row => row.Path));
        Assert.All(rows.Where(row => row.IsFolder), row => Assert.False(row.Expanded));
    }

    /// <summary>
    /// A search lists the sessions it finds, flat and best first, by the palette's own match: part of a
    /// name, a host or a tag, each with its folder in its line.
    /// </summary>
    [Fact]
    public void ASearchIsAFlatListFoundAsOpenSessionFindsIt()
    {
        IReadOnlyList<SessionRow> byName = SessionsView.Rows(Store(), "primary", AllOpened, _ => false);

        Assert.Equal(["Work/db-primary"], byName.Select(row => row.Path));
        Assert.Equal(("db-primary", "Work · db1.example.com:2222", 0), (byName[0].Name, byName[0].Meta, byName[0].Depth));

        Assert.Equal(["Work/db-primary"], SessionsView.Rows(Store(), "postgres", AllOpened, _ => false).Select(row => row.Path));
        Assert.Equal(["Home lab/nas"], SessionsView.Rows(Store(), "home.arpa", AllOpened, _ => false).Select(row => row.Path));
        Assert.Empty(SessionsView.Rows(Store(), "nothing-like-it", AllOpened, _ => false));
    }

    /// <summary>A session a tab holds is marked Connected, wherever it is listed; a folder never is.</summary>
    [Fact]
    public void ASessionATabHoldsIsMarkedConnected()
    {
        IReadOnlyList<SessionRow> rows = SessionsView.Rows(Store(), string.Empty, AllOpened,
                                                           path => path is "Work/db-primary" or "Work");

        Assert.Equal(["Work/db-primary"], rows.Where(row => row.Connected).Select(row => row.Path));
        Assert.True(SessionsView.Rows(Store(), "primary", AllOpened, path => path == "Work/db-primary").Single().Connected);
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

    /// <summary>
    /// Once read, the window's sidebar lists the store with its folders folded (QS304), a folder opens
    /// on being toggled, and the search finds a session inside a folder nobody opened.
    /// </summary>
    [Fact]
    public void TheWindowListsTheStoreAndTheSearchNarrowsIt()
    {
        string file = Stored();

        (string[] first, string[] opened, string[] found) = Sta.Run(() =>
        {
            MainWindow window = new() { SessionsFile = file };

            window.RefreshSessions();
            string[] listed = [.. window.Sidebar.Rows.Select(row => row.Path)];

            window.ToggleFolder("Work");
            string[] afterOpening = [.. window.Sidebar.Rows.Select(row => row.Path)];

            window.ToggleFolder("Work");
            window.SearchSessions("nas");

            return (listed, afterOpening, window.Sidebar.Rows.Select(row => row.Path).ToArray());
        });

        Assert.Equal(["Work", "Home lab", "pi"], first);
        Assert.Equal(["Work", "Work/build-box", "Work/db-primary", "Home lab", "pi"], opened);
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
            window.ToggleFolder("Work");
            Assert.True(window.Sidebar.Select("Work/db-primary"));

            window.ConnectSession(window.Sidebar.Selected!.Path);

            return asked.ToArray();
        });

        Assert.Equal(["Work/db-primary"], opened);
    }

    /// <summary>
    /// QS309: a pane split from one opened on a saved session is handed that session to open, and a
    /// pane split from a local shell is handed none — which is the local shell it always was.
    /// </summary>
    [Fact]
    public void ASplitOfAServersPaneIsHandedThatServersSession()
    {
        string file = Stored();

        (string? fromSaved, bool fromLocal, string? newPaneWears) = Sta.Run(() =>
        {
            List<(TerminalLeaf made, TerminalLeaf? from)> handed = [];
            MainWindow window = new() { SessionsFile = file, Connects = (made, from) => handed.Add((made, from)) };

            TerminalTab remote = TerminalTab.Open(Settings.Default, Shared, "db1.example.com");

            remote.Focused.Wear(SessionTree.ReadFrom(file).Session("Work/db-primary")!, Settings.Default, file);
            window.Add(remote);
            window.SplitPane(Divide.Beside);

            window.Add(TerminalTab.Open(Settings.Default, Shared, "local"));
            window.SplitPane(Divide.Below);

            return (handed[0].from?.Worn?.Path, handed[1].from is { Worn: null }, handed[0].made.Worn?.Path);
        });

        Assert.Equal("Work/db-primary", fromSaved);
        Assert.True(fromLocal, "a split of a local pane was handed a session to open");

        // Handed, not yet worn: the program opens it, and wearing it is part of that.
        Assert.Null(newPaneWears);
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
            window.ToggleFolder("Work");

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

    /// <summary>
    /// Duplicating puts a copy beside the session, named for it, and selects it, opening the folder
    /// it is in, which starts folded (QS304).
    /// </summary>
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

        (string[] offered, string? moved, string? refused, List<string> said, string? selected) = Sta.Run(() =>
        {
            List<string> told = [];
            MainWindow window = new() { SessionsFile = file, Tells = told.Add };

            window.RefreshSessions();

            string[] folders = [.. window.FoldersBesides("pi")];
            string? to = window.MoveSession("pi", "Work");
            string? shown = window.Sidebar.Selected?.Path;

            return (folders, to, window.MoveSession("Home lab/nas", string.Empty), told, shown);
        });

        SessionTree store = SessionTree.ReadFrom(file);

        Assert.Equal(["Work", "Home lab"], offered);
        Assert.Equal("Work/pi", moved);

        // Selected in a folder that started folded, which the move opened (QS304).
        Assert.Equal("Work/pi", selected);
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

    // ---- The design's sizes (QS277) ----

    /// <summary>
    /// The open sidebar's header, search and footer at the design's sizes: a 28-pixel ghost fold, a
    /// 28-high New, a 32-high search rounded at 6, and 32-high footer buttons.
    /// </summary>
    [Fact]
    public void TheHeaderSearchAndFooterAreTheDesignsSize()
    {
        (double fold, string foldTemplate, double make, double search, double corner, double[] footer) = Sta.Run(() =>
        {
            MainWindow window = new() { SessionsFile = Stored() };
            ButtonBase folding = Find<ButtonBase>(window.Sidebar, "SessionsFold");
            Border frame = (Border)((Grid)Find<TextBox>(window.Sidebar, "SessionsSearch").Parent).Parent;

            folding.ApplyTemplate();

            return (folding.Width,
                    folding.Template.FindName("Ground", folding) is Border ground ? $"{ground.CornerRadius.TopLeft}" : "none",
                    Find<Button>(window.Sidebar, "SessionsNew").Height,
                    frame.Height,
                    frame.CornerRadius.TopLeft,
                    new[] { Find<Button>(window.Sidebar, "SessionsImport").Height, Find<Button>(window.Sidebar, "SessionsSettings").Height });
        });

        Assert.Equal(28, fold);
        Assert.Equal("6", foldTemplate);
        Assert.Equal(28, make);
        Assert.Equal(32, search);
        Assert.Equal(6, corner);
        Assert.Equal([32, 32], footer);
    }

    /// <summary>
    /// Every row is 32 high and starts 8 in plus 22 per folder; a session is drawn with the terminal
    /// glyph and never the icon fonts' Command Prompt, and an open folder's chevron is turned a quarter.
    /// </summary>
    [Fact]
    public void RowsAreTheDesignsAndASessionIsNotACommandPrompt()
    {
        (string path, double height, double left, bool terminal, bool prompt, double turn)[] rows = Sta.Run(() =>
        {
            MainWindow window = new() { SessionsFile = Stored() };

            window.RefreshSessions();
            window.ToggleFolder("Work");

            return Find<ListBox>(window.Sidebar, "SessionsList").Items.OfType<ListBoxItem>()
                .Select(item => (((SessionRow)item.Tag).Path,
                                 item.Height,
                                 item.Padding.Left,
                                 Under(item).Any(each => AutomationProperties.GetAutomationId(each) == "Terminal"),
                                 Under(item).OfType<TextBlock>().Any(each => each.Text == ""),
                                 Under(item).OfType<TextBlock>().FirstOrDefault(each => AutomationProperties.GetAutomationId(each) == "Chevron")
                                     ?.RenderTransform is RotateTransform rotated ? rotated.Angle : -1))
                .ToArray();
        });

        Assert.All(rows, row => Assert.Equal(SessionsSidebar.RowHeight, row.height));
        Assert.All(rows, row => Assert.False(row.prompt, $"{row.path} carries the Command Prompt glyph"));

        Assert.Equal((8d, false, 90d), (rows.Single(row => row.path == "Work").left, rows.Single(row => row.path == "Work").terminal,
                                       rows.Single(row => row.path == "Work").turn));
        Assert.Equal((30d, true), (rows.Single(row => row.path == "Work/db-primary").left, rows.Single(row => row.path == "Work/db-primary").terminal));
        Assert.Equal((8d, true), (rows.Single(row => row.path == "pi").left, rows.Single(row => row.path == "pi").terminal));
    }

    /// <summary>The selected row is the accent at a tenth, and the row's template fills with it rather than with an opaque block.</summary>
    [Fact]
    public void TheSelectedRowIsTheAccentAtATenth()
    {
        (double tenth, bool selects) = Sta.Run(() =>
        {
            MainWindow window = new() { SessionsFile = Stored() };

            window.RefreshSessions();
            window.ToggleFolder("Work");
            window.Sidebar.Select("Work/db-primary");

            ListBoxItem item = Find<ListBox>(window.Sidebar, "SessionsList").Items.OfType<ListBoxItem>().Single(each => each.IsSelected);
            bool usesIt = item.Template.Triggers.OfType<Trigger>().Any(trigger =>
                trigger.Property == ListBoxItem.IsSelectedProperty
                && trigger.Setters.OfType<Setter>().Any(setter => setter.Value is DynamicResourceExtension { ResourceKey: "SessionsSelectionBrush" }));

            return (((SolidColorBrush)window.Sidebar.Resources["SessionsSelectionBrush"]).Opacity, usesIt);
        });

        Assert.Equal(0.1, tenth, 3);
        Assert.True(selects, "the selected row is not filled with the selection brush");
    }

    /// <summary>
    /// The rail is the design's: Show sessions, Files on this server and Tunnels, 36 square, and nothing
    /// of the open sidebar left on it.
    /// </summary>
    [Fact]
    public void TheRailShowsSessionsFilesAndTunnels()
    {
        (string[] shown, double side, double width) = Sta.Run(() =>
        {
            MainWindow window = new() { SessionsFile = Stored() };

            window.Sidebar.Shape(open: false);

            string[] ids = ["SessionsFold", "SessionsFiles", "SessionsTunnels", "SessionsNew", "SessionsImport", "SessionsSettings", "SessionsSearch", "SessionsList"];

            return (ids.Where(id => Shown(Find<FrameworkElement>(window.Sidebar, id))).ToArray(),
                    Find<ButtonBase>(window.Sidebar, "SessionsFiles").Width,
                    window.Sidebar.Width);
        });

        Assert.Equal(["SessionsFold", "SessionsFiles", "SessionsTunnels"], shown);
        Assert.Equal(36, side);
        Assert.Equal(SessionsSidebar.RailWidth, width);
    }

    /// <summary>The element under a root with this automation id, by the logical tree, which is there before any layout.</summary>
    private static T Find<T>(DependencyObject root, string id) where T : DependencyObject =>
        Under(root).OfType<T>().First(each => AutomationProperties.GetAutomationId(each) == id);

    /// <summary>Everything under an element in the logical tree, the element first.</summary>
    private static IEnumerable<DependencyObject> Under(DependencyObject root)
    {
        yield return root;

        foreach (DependencyObject child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            foreach (DependencyObject below in Under(child))
            {
                yield return below;
            }
        }
    }

    /// <summary>Whether an element and everything above it is visible, which a window never shown cannot say for itself.</summary>
    private static bool Shown(FrameworkElement element)
    {
        for (DependencyObject? at = element; at is not null; at = LogicalTreeHelper.GetParent(at))
        {
            if (at is UIElement { Visibility: not Visibility.Visible })
            {
                return false;
            }

            if (at is SessionsSidebar)
            {
                return true;
            }
        }

        return true;
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
