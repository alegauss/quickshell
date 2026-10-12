using System.IO;
using System.Windows;
using System.Windows.Controls;
using Quickshell.App;
using Xunit;

namespace Quickshell.App.Tests;

/// <summary>
/// Quick connect (QS271): an address read into the session the store would hold, and connected by
/// the path a saved session takes, saved or not.
/// </summary>
public sealed class QuickConnectTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"quickshell-quick-{Guid.NewGuid():N}");

    public QuickConnectTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    // ---- The address ----

    [Theory]
    [InlineData("db1.example.com", null, "db1.example.com", null)]
    [InlineData("deploy@db1.example.com", "deploy", "db1.example.com", null)]
    [InlineData("deploy@db1.example.com:2222", "deploy", "db1.example.com", 2222)]
    [InlineData("  web:2200 ", null, "web", 2200)]
    [InlineData("root@[fe80::1]:2222", "root", "fe80::1", 2222)]
    [InlineData("fe80::1", null, "fe80::1", null)]
    [InlineData("a@b@host", "a@b", "host", null)]
    public void AnAddressIsReadIntoItsFields(string typed, string? user, string host, int? port)
    {
        QuickAddress read = QuickAddress.Parse(typed);

        Assert.Equal(string.Empty, read.Refused);
        Assert.Equal(host, read.Session!.Host);
        Assert.Equal(host, read.Session.Name);
        Assert.Equal(user, read.Session.Settings.User);
        Assert.Equal(port, read.Session.Settings.Port);
    }

    [Theory]
    [InlineData("", "Type an address")]
    [InlineData("deploy@", "There is no host")]
    [InlineData("@host", "Nothing comes before the @")]
    [InlineData("host:99999", "A port is a number from 1 to 65535, and 99999 is not.")]
    [InlineData("host:ssh", "A port is a number from 1 to 65535, and ssh is not.")]
    [InlineData("deploy @host", "no spaces")]
    [InlineData("[fe80::1", "does not close it")]
    public void AnAddressThatDoesNotParseSaysWhy(string typed, string said)
    {
        QuickAddress read = QuickAddress.Parse(typed);

        Assert.Null(read.Session);
        Assert.Contains(said, read.Refused, StringComparison.Ordinal);
    }

    /// <summary>
    /// A host from ~/.ssh/config opens without anything being retyped, and what was typed wins over
    /// the config, as on ssh's own command line.
    /// </summary>
    [Fact]
    public void AnSshConfigHostIsResolvedAndTypedPartsWin()
    {
        SshConfig config = SshConfig.Parse("""
            Host db
                HostName db1.example.com
                User deploy
                Port 2222
                IdentityFile ~/.ssh/id_db
                ProxyJump bastion.example.com
            """);

        SessionNode db = QuickAddress.Parse("db", config).Session!;
        SessionNode root = QuickAddress.Parse("root@db:22", config).Session!;

        Assert.Equal("db", db.Name);
        Assert.Equal("db1.example.com", db.Host);
        Assert.Equal("deploy", db.Settings.User);
        Assert.Equal(2222, db.Settings.Port);
        Assert.EndsWith("id_db", db.Settings.Key, StringComparison.Ordinal);
        Assert.Equal("bastion.example.com", db.Settings.JumpHost);

        Assert.Equal("root", root.Settings.User);
        Assert.Equal(22, root.Settings.Port);
    }

    [Fact]
    public void TheSignInChoiceSetsTheKeyAsTheDialogDoes()
    {
        Assert.Equal("~/.ssh/k", QuickAddress.Parse("h", signIn: SignInMethod.KeyFile, keyFile: "~/.ssh/k").Session!.Settings.Key);
        Assert.Null(QuickAddress.Parse("h", signIn: SignInMethod.Password).Session!.Settings.Key);
        Assert.Contains("key file's path", QuickAddress.Parse("h", signIn: SignInMethod.KeyFile).Refused, StringComparison.Ordinal);
    }

    // ---- Connecting it ----

    /// <summary>Saved, it is in the store at the top level and opened from there by its name.</summary>
    [Fact]
    public void SavedItIsInTheStoreAndOpenedFromThere()
    {
        string store = Path.Combine(_directory, "sessions.json");

        (string refused, string? opened, bool unsaved) = Sta.Run(() =>
        {
            string? asked = null;
            bool other = false;
            MainWindow window = new()
            {
                SessionsFile = store,
                SshConfigFile = Path.Combine(_directory, "none"),
                OpensSession = path => asked = path,
                OpensUnsaved = _ => other = true,
            };

            return (window.ConnectAddress("deploy@db1.example.com:2222", SignInMethod.KeyOrAgent, null, save: true), asked, other);
        });

        ResolvedSession saved = SessionTree.ReadFrom(store).Session("db1.example.com")!;

        Assert.Equal(string.Empty, refused);
        Assert.Equal("db1.example.com", opened);
        Assert.False(unsaved);
        Assert.Equal("deploy", saved.User?.Value);
        Assert.Equal(2222, saved.Port?.Value);
    }

    /// <summary>
    /// QS271's falsification: not saved, the session opened is the one saving and opening it would
    /// have opened — the store's own top-level settings included — and nothing is written.
    /// </summary>
    [Fact]
    public void UnsavedItIsTheSessionSavingWouldHaveOpened()
    {
        string store = Path.Combine(_directory, "sessions.json");
        string text = """{ "Name": "", "Settings": { "Reconnect": true }, "Children": [] }""";

        File.WriteAllText(store, text);

        ResolvedSession? opened = Sta.Run(() =>
        {
            ResolvedSession? got = null;
            MainWindow window = new()
            {
                SessionsFile = store,
                SshConfigFile = Path.Combine(_directory, "none"),
                OpensUnsaved = session => got = session,
            };

            window.ConnectAddress("deploy@db1.example.com:2222", SignInMethod.KeyOrAgent, null, save: false);

            return got;
        });

        ResolvedSession wouldBe = SessionTree.ReadFrom(store)
                                             .With("db1.example.com", QuickAddress.Parse("deploy@db1.example.com:2222").Session!)
                                             .Session("db1.example.com")!;

        Assert.NotNull(opened);
        Assert.Equal(wouldBe.Host, opened.Host);
        Assert.Equal(wouldBe.User, opened.User);
        Assert.Equal(wouldBe.Port, opened.Port);
        Assert.Equal(true, opened.Reconnect?.Value);
        Assert.Equal(text, File.ReadAllText(store));
    }

    /// <summary>A name another session already has gets a number; the same session saved again is just opened.</summary>
    [Fact]
    public void ANameTakenByAnotherSessionGetsANumber()
    {
        string store = Path.Combine(_directory, "sessions.json");

        File.WriteAllText(store, """{ "Name": "", "Children": [ { "Name": "web", "Host": "web", "Settings": { "User": "someone" } } ] }""");

        List<string> opened = Sta.Run(() =>
        {
            List<string> asked = [];
            MainWindow window = new()
            {
                SessionsFile = store,
                SshConfigFile = Path.Combine(_directory, "none"),
                OpensSession = asked.Add,
            };

            window.ConnectAddress("me@web", SignInMethod.KeyOrAgent, null, save: true);
            window.ConnectAddress("me@web", SignInMethod.KeyOrAgent, null, save: true);

            return asked;
        });

        Assert.Equal(["web 2", "web 2"], opened);
        Assert.Equal(2, SessionTree.ReadFrom(store).Sessions().Count);
        Assert.Equal("someone", SessionTree.ReadFrom(store).Session("web")!.User?.Value);
    }

    /// <summary>The palette offers it, and a bad address says why under the box and connects nothing.</summary>
    [Fact]
    public void ThePaletteOffersItAndABadAddressSaysWhy()
    {
        (bool listed, string refused, bool connected) = Sta.Run(() =>
        {
            QuickConnectWindow? shown = null;
            bool any = false;
            MainWindow window = new()
            {
                SessionsFile = Path.Combine(_directory, "sessions.json"),
                SshConfigFile = Path.Combine(_directory, "none"),
                ShowsQuickConnect = made => shown = made,
                OpensSession = _ => any = true,
                OpensUnsaved = _ => any = true,
            };

            window.Actions.Single(action => action.Name == "Connect to an address").Run();

            shown!.Quick.Address = "deploy@";
            shown.Quick.Go();

            return (true, shown.Quick.Refused, any);
        });

        Assert.True(listed);
        Assert.StartsWith("There is no host", refused, StringComparison.Ordinal);
        Assert.False(connected);
    }
}
