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

        SessionNode folder = Assert.Single(tree.Children, child => child.Name == "Europe");

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
