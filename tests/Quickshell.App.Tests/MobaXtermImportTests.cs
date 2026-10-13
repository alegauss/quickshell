using System.IO;
using Quickshell.App;
using Xunit;

namespace Quickshell.App.Tests;

/// <summary>
/// Reading the incumbent's session file, which is the whole cost of switching.
///
/// <para>The line's falsification is one sentence — an import must not silently drop a setting the
/// source carried — so that is what most of this asks. The fixture is synthetic on purpose: a real
/// MobaXterm file is somebody's estate, and a repository is not where their hostnames go. A test
/// against a real one runs where a real one is, and asserts shapes rather than contents.</para>
/// </summary>
public sealed class MobaXtermImportTests : IDisposable
{
    private readonly string _here =
        Path.Combine(Path.GetTempPath(), $"quickshell-moba-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_here))
        {
            Directory.Delete(_here, recursive: true);
        }
    }

    // ---- The falsification ----

    /// <summary>
    /// Nothing the file carried disappears without being named — including the fields this import
    /// has never identified.
    /// </summary>
    [Fact]
    public void NoSettingTheSourceCarriedIsDroppedInSilence()
    {
        // Field 5 is X11, 6 is compression, 8 a jump host, 20 a login command; 30 is one nobody has
        // worked out, and it is set. All five must come back named.
        string session = Line("with-everything", kind: 0, "10.0.0.1", "2222", "root",
                              (5, "1"), (6, "1"), (8, "bastion.example"), (20, "sudo -i"),
                              (30, "something-unidentified"));

        ImportPreview preview = MobaXtermImport.Preview(File(session));

        ImportedSession imported = Assert.Single(preview.Sessions);

        Assert.True(imported.Carried);

        string named = string.Join(" | ", imported.Unmapped);

        Assert.Contains("X11", named, StringComparison.Ordinal);
        Assert.Contains("compression", named, StringComparison.Ordinal);
        Assert.Contains("jump host", named, StringComparison.Ordinal);
        Assert.Contains("execute-on-login", named, StringComparison.Ordinal);

        // The one nobody has identified is still reported, by position. Naming only what was worked
        // out would leave the rest to vanish quietly, which is the failure this test is about.
        Assert.Contains("not identified", named, StringComparison.Ordinal);
        Assert.Contains("30", named, StringComparison.Ordinal);
    }

    /// <summary>And a session with nothing extra set reports nothing, so the above is not noise.</summary>
    [Fact]
    public void APlainSessionCarriesAcrossWithNothingToReport()
    {
        ImportPreview preview = MobaXtermImport.Preview(File(Line("plain", 0, "host.example", "22",
                                                                 "alex")));

        ImportedSession imported = Assert.Single(preview.Sessions);

        Assert.True(imported.Carried);
        Assert.Empty(imported.Unmapped);

        Assert.Equal("host.example", imported.Node!.Host);
        Assert.Equal("alex", imported.Node.Settings.User);

        // Port 22 is the default and is not written back as a difference.
        Assert.Null(imported.Node.Settings.Port);
    }

    // ---- What does not come across ----

    /// <summary>
    /// A protocol this client refuses is named as that, per session. Twelve RDP sessions should read
    /// as twelve refusals rather than as twelve absences.
    /// </summary>
    [Theory]
    [InlineData(4, "RDP")]
    [InlineData(5, "VNC")]
    [InlineData(1, "Telnet")]
    [InlineData(8, "Serial")]
    [InlineData(14, "WSL")]
    public void AProtocolThisClientDoesNotSpeakIsNamedRatherThanMissing(int kind, string what)
    {
        ImportPreview preview =
            MobaXtermImport.Preview(File(Line("elsewhere", kind, "host.example", "22", "alex")));

        ImportedSession imported = Assert.Single(preview.Sessions);

        Assert.False(imported.Carried);
        Assert.Contains(what, imported.Skipped, StringComparison.Ordinal);
        Assert.Equal(1, preview.Skipping);
    }

    /// <summary>
    /// A PuTTY-format key is referenced where it lies and said so. Converting somebody's key without
    /// being asked is the kind of surprise that costs trust in the first ten minutes.
    /// </summary>
    [Fact]
    public void APuttyKeyIsReferencedAndNotConverted()
    {
        ImportPreview preview = MobaXtermImport.Preview(
            File(Line("keyed", 0, "host.example", "22", "alex", (14, "C:\\keys\\work.ppk"))));

        ImportedSession imported = Assert.Single(preview.Sessions);

        Assert.Equal("C:\\keys\\work.ppk", imported.Node!.Settings.Key);
        Assert.Contains(imported.Unmapped,
                        note => note.Contains("not converted", StringComparison.Ordinal));
    }

    /// <summary>
    /// QS276: a key under MobaXterm's own profile placeholder imports as the rooted path it means,
    /// and not as a path beginning with <c>_ProfileDir_</c> that is never found.
    /// </summary>
    [Fact]
    public void AKeyUnderAPlaceholderImportsAsARootedPath()
    {
        ImportPreview preview = MobaXtermImport.Preview(
            File(Line("keyed", 0, "host.example", "22", "alex", (14, "_ProfileDir_\\OneDrive\\Certs\\work.ppk"))));

        string key = Assert.Single(preview.Sessions).Node!.Settings.Key!;

        Assert.True(Path.IsPathRooted(key), key);
        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "OneDrive", "Certs", "work.ppk"),
                     key);
    }

    /// <summary>Each placeholder MobaXterm writes expands; an underscore word it does not write is left alone.</summary>
    [Theory]
    [InlineData("_ProfileDir_\\k.ppk", Environment.SpecialFolder.UserProfile)]
    [InlineData("_profiledir_\\k.ppk", Environment.SpecialFolder.UserProfile)]
    [InlineData("_MyDocuments_\\k.ppk", Environment.SpecialFolder.MyDocuments)]
    [InlineData("_AppDataDir_\\k.ppk", Environment.SpecialFolder.ApplicationData)]
    public void EachPlaceholderExpands(string written, Environment.SpecialFolder folder) =>
        Assert.Equal(Environment.GetFolderPath(folder) + "\\k.ppk", MobaXtermPaths.Expand(written));

    [Fact]
    public void TheCurrentDriveIsADriveLetter()
    {
        string drive = Path.GetPathRoot(Environment.SystemDirectory)![..1];

        Assert.Equal($"{drive}:\\keys\\k.ppk", MobaXtermPaths.Expand("_CurrentDrive_:\\keys\\k.ppk"));
        Assert.Equal($"{drive}:\\keys\\k.ppk", MobaXtermPaths.Expand("_CurrentDrive_\\keys\\k.ppk"));
    }

    [Theory]
    [InlineData("_SomethingElse_\\k.ppk")]
    [InlineData("C:\\keys\\_ProfileDir_\\k.ppk")]
    [InlineData("")]
    public void WhatIsNotAPlaceholderIsLeftAlone(string written) =>
        Assert.Equal(written, MobaXtermPaths.Expand(written));

    // ---- Folders, and writing nothing ----

    [Fact]
    public void SessionsLandInTheFoldersTheyCameFrom()
    {
        string ini = string.Join(Environment.NewLine,
        [
            "[Bookmarks]",
            "SubRep=",
            "ImgNum=41",
            Line("at-the-root", 0, "root.example", "22", "alex"),
            "[Bookmarks_1]",
            "SubRep=Production\\Europe",
            "ImgNum=41",
            Line("inside", 0, "inside.example", "22", "alex"),
        ]);

        ImportPreview preview = MobaXtermImport.Preview(File(ini, whole: true));

        SessionNode tree = preview.Tree();

        Assert.Contains(tree.Children, child => child.Name == "at-the-root");

        // Nested as MobaXterm nests it (QS296), not flattened to the last level.
        SessionNode production = Assert.Single(tree.Children, child => child.Name == "Production");
        SessionNode folder = Assert.Single(production.Children, child => child.Name == "Europe");

        Assert.Equal("inside", Assert.Single(folder.Children).Name);
    }

    /// <summary>A preview writes nothing: the folder it read from is all that is touched.</summary>
    [Fact]
    public void PreviewingWritesNothing()
    {
        string path = File(Line("plain", 0, "host.example", "22", "alex"));

        MobaXtermImport.Preview(path);

        Assert.Equal(["MobaXterm.ini"],
                     Directory.GetFiles(_here).Select(Path.GetFileName).ToArray());
    }

    /// <summary>
    /// QS181: an import told which file to read previews that file, whatever this machine has
    /// installed, and one told a file that is not there answers as a machine with none does.
    /// </summary>
    [Fact]
    public void AnImportToldWhichFileReadsThatFile()
    {
        string named = File(Line("named", 0, "named.example", "22", "alex"));
        string missing = Path.Combine(_here, "not-there.ini");

        (int namedSessions, string namedSource, string missingSource) = Sta.Run(() =>
        {
            List<ImportPreview> seen = [];
            MainWindow window = new()
            {
                Importing = preview => { seen.Add(preview); return false; },
                SessionsFile = Path.Combine(_here, "sessions.json"),
            };

            window.ImportSessions(named);
            window.ImportSessions(missing);

            return (seen[0].Sessions.Count, seen[0].Source, seen[1].Source);
        });

        Assert.Equal(1, namedSessions);
        Assert.Equal(named, namedSource);
        Assert.Equal(string.Empty, missingSource);
    }

    /// <summary>
    /// QS216's falsification: a store holding a session, imported into and agreed to, holds that
    /// session and every imported one afterwards — the import under a folder of its own, and the
    /// preview having said the session already there would stay.
    /// </summary>
    [Fact]
    public void AnImportAddsToTheStoreAndKeepsWhatWasThere()
    {
        string from = File(Line("theirs", 0, "theirs.example", "22", "alex"));
        string store = Path.Combine(_here, "sessions.json");

        SessionTree.Of(new SessionNode { Children = [new SessionNode { Name = "mine", Host = "mine.example" }] })
                   .WriteTo(store);

        ImportPreview shown = Sta.Run(() =>
        {
            ImportPreview? seen = null;
            MainWindow window = new() { Importing = preview => { seen = preview; return true; }, SessionsFile = store };

            window.ImportSessions(from);

            return seen!;
        });

        SessionTree after = SessionTree.ReadFrom(store);
        string folder = $"Imported from MobaXterm {DateTime.Now:yyyy-MM-dd}";

        Assert.Equal(1, shown.Kept);
        Assert.Equal(folder, shown.Folder);
        Assert.Equal("mine.example", after.Find("mine")?.Host);
        Assert.Equal("theirs.example", after.Find($"{folder}/theirs")?.Host);
        Assert.Equal(2, after.Sessions().Count);
    }

    /// <summary>
    /// QS294: what a session could not bring is named in the preview with the sessions that had it,
    /// and written beside each of those sessions in the store, where a later write keeps it.
    /// </summary>
    [Fact]
    public void WhatIsLeftBehindIsNamedInThePreviewAndBesideEachSession()
    {
        string from = File(string.Join(Environment.NewLine,
        [
            "[Bookmarks]", "SubRep=", "ImgNum=41",
            Line("loose", 0, "loose.example", "22", "alex", (6, "1")),
            "[Bookmarks_1]", "SubRep=Work", "ImgNum=41",
            Line("db", 0, "db.example", "22", "alex", (6, "1"), (8, "bastion.example")),
            Line("plain", 0, "plain.example", "22", "alex"),
        ]), whole: true);

        string store = Path.Combine(_here, "sessions.json");

        ImportPreview shown = Sta.Run(() =>
        {
            ImportPreview? seen = null;
            MainWindow window = new() { Importing = preview => { seen = preview; return true; }, SessionsFile = store };

            window.ImportSessions(from);

            return seen!;
        });

        // In the preview: compression once, with both sessions that had it; the jump host with its one.
        IReadOnlyList<string> left = shown.LeftBehind();

        Assert.Contains(left, line => line.StartsWith("compression", StringComparison.Ordinal) && line.EndsWith("(loose, db)", StringComparison.Ordinal));
        Assert.Contains(left, line => line.StartsWith("the jump host is carried", StringComparison.Ordinal) && line.EndsWith("(db)", StringComparison.Ordinal));
        Assert.DoesNotContain(left, line => line.Contains("plain", StringComparison.Ordinal));

        // In the store: before the session it came from, and still there after the store is written again.
        string folder = $"Imported from MobaXterm {DateTime.Now:yyyy-MM-dd}";

        SessionTree.ReadFrom(store).With("mine", new SessionNode { Name = "mine", Host = "mine.example" }).WriteTo(store);

        string text = System.IO.File.ReadAllText(store);
        int db = text.IndexOf("\"Name\": \"db\"", StringComparison.Ordinal);
        int note = text.IndexOf("// Not carried over from MobaXterm: the jump host", StringComparison.Ordinal);

        Assert.True(note >= 0 && note < db && text.LastIndexOf("\"Name\"", db - 1, StringComparison.Ordinal) < note,
                    $"the jump host was not written just before db:{Environment.NewLine}{text}");
        Assert.Equal(3, Count(text, "// Not carried over from MobaXterm:"));
        Assert.NotNull(SessionTree.ReadFrom(store).Find($"{folder}/Work/db"));
    }

    /// <summary>
    /// QS296: a jump host comes across as the session's own, and folders nested in MobaXterm stay
    /// nested — two subfolders of the same name under different parents stay two.
    /// </summary>
    [Fact]
    public void AJumpHostAndNestedFoldersComeAcross()
    {
        string from = File(string.Join(Environment.NewLine,
        [
            "[Bookmarks_1]", @"SubRep=Clients\Acme\prod", "ImgNum=41",
            Line("web", 0, "web.acme.example", "22", "alex", (8, "bastion.acme.example")),
            "[Bookmarks_2]", @"SubRep=Clients\Other\prod", "ImgNum=41",
            Line("web", 0, "web.other.example", "22", "alex"),
        ]), whole: true);

        ImportPreview preview = MobaXtermImport.Preview(from) with { Folder = "in" };
        SessionTree tree = SessionTree.Of(new SessionNode { Children = [preview.Tree()] });

        Assert.Equal("web.acme.example", tree.Find("in/Clients/Acme/prod/web")?.Host);
        Assert.Equal("web.other.example", tree.Find("in/Clients/Other/prod/web")?.Host);
        Assert.Equal("bastion.acme.example", tree.Find("in/Clients/Acme/prod/web")?.Settings.JumpHost);
        Assert.Null(tree.Find("in/Clients/Other/prod/web")?.Settings.JumpHost);
    }

    /// <summary>
    /// QS297: a jump as a real file holds it — host, port and user in fields 8, 9 and 10, and two
    /// jumps joined by __PIPE__ in each — comes across as OpenSSH's chain, in order, with users and
    /// ports, the port left out where it is 22.
    /// </summary>
    [Fact]
    public void AJumpChainComesAcrossWithItsUsersAndPorts()
    {
        string from = File(string.Join(Environment.NewLine,
        [
            "[Bookmarks]", "SubRep=", "ImgNum=41",
            Line("one", 0, "10.0.0.9", "22", "alex", (8, "10.0.0.1"), (9, "2222"), (10, "jumper")),
            Line("two", 0, "10.0.0.9", "22", "alex", (8, "10.0.0.1__PIPE__10.0.0.2"), (9, "22__PIPE__2200"), (10, "jumper__PIPE__ops")),
        ]), whole: true);

        ImportPreview preview = MobaXtermImport.Preview(from);

        Assert.Equal("jumper@10.0.0.1:2222", preview.Sessions.Single(each => each.Name == "one").Node!.Settings.JumpHost);
        Assert.Equal("jumper@10.0.0.1,ops@10.0.0.2:2200", preview.Sessions.Single(each => each.Name == "two").Node!.Settings.JumpHost);

        // Fields 9 and 10 are the jump's, read, and not reported as unidentified.
        Assert.DoesNotContain(preview.Sessions.SelectMany(each => each.Unmapped), note => note.Contains("not identified", StringComparison.Ordinal));
        Assert.Contains(preview.Sessions.Single(each => each.Name == "two").Unmapped, note => note.StartsWith("the 2 jump hosts", StringComparison.Ordinal));
    }

    /// <summary>
    /// QS296: with nothing where MobaXterm and PuTTY keep their sessions, the import offers to choose
    /// the file — a portable MobaXterm's is beside its executable — and imports the one chosen.
    /// </summary>
    [Fact]
    public void WithNothingFoundTheFileIsChosen()
    {
        Assert.SkipWhen(MobaXtermImport.Find() is not null, "this desk has a MobaXterm.ini where MobaXterm keeps one, so nothing is chosen");

        string from = File(Line("portable", 0, "portable.example", "22", "alex"));
        string store = Path.Combine(_here, "sessions.json");

        int asked = Sta.Run(() =>
        {
            int chooser = 0;
            MainWindow window = new()
            {
                SessionsFile = store,
                PuttySessions = () => null,
                ChoosesSessionFile = () => { chooser++; return from; },
                Importing = _ => true,
            };

            window.ImportSessions();

            return chooser;
        });

        Assert.Equal(1, asked);
        Assert.Equal("portable.example",
                     SessionTree.ReadFrom(store).Find($"Imported from MobaXterm {DateTime.Now:yyyy-MM-dd}/portable")?.Host);
    }

    private static int Count(string text, string what)
    {
        int count = 0;

        for (int at = text.IndexOf(what, StringComparison.Ordinal); at >= 0; at = text.IndexOf(what, at + 1, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    /// <summary>A second import the same day is a second folder, not a merge into the first.</summary>
    [Fact]
    public void ASecondImportTheSameDayGoesUnderAFolderOfItsOwn()
    {
        string from = File(Line("theirs", 0, "theirs.example", "22", "alex"));
        string store = Path.Combine(_here, "sessions.json");

        Sta.Run(() =>
        {
            MainWindow window = new() { Importing = _ => true, SessionsFile = store };

            window.ImportSessions(from);
            window.ImportSessions(from);

            return 0;
        });

        SessionTree after = SessionTree.ReadFrom(store);
        string folder = $"Imported from MobaXterm {DateTime.Now:yyyy-MM-dd}";

        Assert.NotNull(after.Find($"{folder}/theirs"));
        Assert.NotNull(after.Find($"{folder} (2)/theirs"));
        Assert.Equal(2, after.Sessions().Count);
    }

    /// <summary>
    /// A store that will not read is refused before anything is asked, and left exactly as it was:
    /// writing the import over it would be the silent loss this exists to stop.
    /// </summary>
    [Fact]
    public void AStoreThatWillNotReadIsRefusedAndLeftAlone()
    {
        string from = File(Line("theirs", 0, "theirs.example", "22", "alex"));
        string store = Path.Combine(_here, "sessions.json");

        System.IO.File.WriteAllText(store, "{ this is not json");

        bool asked = Sta.Run(() =>
        {
            bool shown = false;
            MainWindow window = new() { Importing = _ => shown = true, SessionsFile = store };

            Assert.Throws<SessionStoreException>(() => window.ImportSessions(from));

            return shown;
        });

        Assert.False(asked);
        Assert.Equal("{ this is not json", System.IO.File.ReadAllText(store));
    }

    // ---- Against a real file, where there is one ----

    /// <summary>
    /// The same reader against whatever MobaXterm this machine has, which is the only way to know
    /// the format was read right rather than read consistently with the fixture.
    ///
    /// <para>Shapes only, never contents: a real file is somebody's estate. What is asserted is that
    /// it parses, that every session either carries or says why, and that nothing is carried without
    /// a host.</para>
    /// </summary>
    [Fact]
    public void ARealFileParsesAndAccountsForEverySession()
    {
        string? real = MobaXtermImport.Find();

        Assert.SkipWhen(real is null, "no MobaXterm.ini on this machine, so there is nothing real to "
                                      + "read - the fixture tests above are what run everywhere");

        ImportPreview preview = MobaXtermImport.Preview(real!);

        Assert.NotEmpty(preview.Sessions);

        foreach (ImportedSession session in preview.Sessions)
        {
            if (session.Carried)
            {
                Assert.False(string.IsNullOrWhiteSpace(session.Node!.Host));
                Assert.Equal(string.Empty, session.Skipped);
            }
            else
            {
                Assert.False(string.IsNullOrWhiteSpace(session.Skipped));
            }
        }

        Assert.Equal(preview.Sessions.Count, preview.Carrying + preview.Skipping);
    }

    /// <summary>
    /// Block J's criterion against a real file: imported the way the window imports it, every setting
    /// a carried session could not bring is named in the preview and written above that session in
    /// the store. Counts are reported, never names — a real file is somebody's estate.
    /// </summary>
    [Fact]
    public void ARealFileImportsWithEverySettingLeftBehindNamed()
    {
        string? real = MobaXtermImport.Find();

        Assert.SkipWhen(real is null, "no MobaXterm.ini on this machine, so there is nothing real to "
                                      + "read - the fixture tests above are what run everywhere");

        string store = Path.Combine(_here, "sessions.json");

        Directory.CreateDirectory(_here);

        ImportPreview shown = Sta.Run(() =>
        {
            ImportPreview? seen = null;
            MainWindow window = new()
            {
                SessionsFile = store,
                PuttySessions = () => null,
                Importing = preview => { seen = preview; return true; },
            };

            window.ImportSessions(real);

            return seen!;
        });

        List<(ImportedSession Session, string What)> left =
            [.. shown.Sessions.Where(session => session.Carried).SelectMany(session => session.Unmapped.Select(what => (session, what)))];

        IReadOnlyList<string> listed = shown.LeftBehind();
        string text = System.IO.File.ReadAllText(store);

        // Every distinct thing left behind is a line of the preview; every instance is a comment in the store.
        foreach (string what in left.Select(each => each.What).Distinct(StringComparer.Ordinal))
        {
            Assert.Contains(listed, line => line.StartsWith(what + " (", StringComparison.Ordinal));
        }

        Assert.Equal(left.Count, Count(text, "// Not carried over from MobaXterm: "));

        // And every jump host carried is one a connection can read (QS297): no MobaXterm joiner left
        // in it, and every hop a host.
        foreach (string jump in shown.Sessions.Select(session => session.Node?.Settings.JumpHost).OfType<string>())
        {
            Assert.DoesNotContain("__", jump, StringComparison.Ordinal);
            Assert.All(RemoteShell.Hops(jump, "anyone"), hop => Assert.False(string.IsNullOrWhiteSpace(hop.Host)));
        }

        TestContext.Current.TestOutputHelper?.WriteLine(
            $"{shown.Sessions.Count} sessions, {shown.Carrying} carried, {shown.Skipping} skipped; "
            + $"{left.Count} settings left behind across {left.Select(each => each.Session).Distinct().Count()} sessions, "
            + $"{listed.Count} kinds; X11 on {left.Count(each => each.What.StartsWith("X11", StringComparison.Ordinal))}, "
            + $"a login macro on {left.Count(each => each.What.StartsWith("an execute-on-login", StringComparison.Ordinal))}, "
            + $"a jump host on {left.Count(each => each.What.StartsWith("the jump host", StringComparison.Ordinal))}");
    }

    // ---- plumbing ----

    /// <summary>One bookmark line, with the fields a test cares about set by position.</summary>
    private static string Line(string name, int kind, string host, string port, string user,
                               params (int At, string Value)[] extra)
    {
        string[] fields = new string[63];

        Array.Fill(fields, string.Empty);

        fields[0] = $"#109#{kind}";
        fields[1] = host;
        fields[2] = port;
        fields[3] = user;

        foreach ((int at, string value) in extra)
        {
            fields[at] = value;
        }

        return $"{name}={string.Join('%', fields)}";
    }

    private string File(string content, bool whole = false)
    {
        Directory.CreateDirectory(_here);

        string path = Path.Combine(_here, "MobaXterm.ini");

        System.IO.File.WriteAllText(path, whole
            ? content
            : string.Join(Environment.NewLine, ["[Bookmarks]", "SubRep=", "ImgNum=41", content]));

        return path;
    }
}
