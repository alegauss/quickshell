using System.IO;
using System.Reflection;
using System.Text;
using Quickshell.App;
using Xunit;

namespace Quickshell.App.Tests;

/// <summary>
/// The file a user will still have in five years.
///
/// <para>The falsification is the first test and it is written the way a user would do it: a store
/// typed out by hand, read, edited by hand again, and read again. If that does not work then nothing
/// else about the format matters, because the format's whole claim is that it belongs to the user
/// rather than to this client.</para>
/// </summary>
public sealed class SessionTreeTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"quickshell-sessions-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    /// <summary>A store as somebody would type it, comments and all.</summary>
    private const string ByHand = """
        {
          // Everything under here logs in as deploy unless it says otherwise.
          "name": "",
          "settings": { "user": "deploy", "port": 22 },
          "children": [
            {
              "name": "prod",
              "settings": { "jumpHost": "bastion.example", "credential": "prod-key" },
              "tags": [ "production" ],
              "children": [
                { "name": "web", "host": "web1.prod.example", "tags": [ "http" ] },
                { "name": "db", "host": "db1.prod.example", "settings": { "user": "postgres" } },
              ]
            },
            {
              "name": "laptop",
              "host": "127.0.0.1",
              "settings": { "port": 2222 }
            }
          ]
        }
        """;

    // ---- The falsification ----

    /// <summary>
    /// The line's own falsification: the store can be edited by hand and reloaded. Written out as
    /// text, read, changed as a person would change it, and read again.
    /// </summary>
    [Fact]
    public void TheStoreIsEditedByHandAndReloaded()
    {
        string file = Write("sessions.json", ByHand);

        SessionTree first = SessionTree.ReadFrom(file);

        Assert.Equal("web1.prod.example", first.Session("prod/web")!.Host);

        // A person opens the file and changes a host, exactly as they would.
        File.WriteAllText(file, ByHand.Replace("web1.prod.example", "web2.prod.example",
                                               StringComparison.Ordinal), new UTF8Encoding(false));

        SessionTree again = SessionTree.ReadFrom(file);

        Assert.Equal("web2.prod.example", again.Session("prod/web")!.Host);
    }

    /// <summary>
    /// Comments and trailing commas are accepted, because a hand-written file has both. A parser
    /// that refused them would make "editable by hand" a claim rather than a property.
    /// </summary>
    [Fact]
    public void CommentsAndTrailingCommasAreAccepted()
    {
        Assert.Contains("//", ByHand, StringComparison.Ordinal);
        Assert.Contains("},\n", ByHand.ReplaceLineEndings("\n"), StringComparison.Ordinal);

        SessionTree tree = SessionTree.ReadFrom(Write("commented.json", ByHand));

        Assert.Equal(3, tree.Sessions().Count);
    }

    /// <summary>What this client writes is what it reads, so a round trip through it is lossless.</summary>
    [Fact]
    public void WhatIsWrittenIsReadBack()
    {
        string file = Write("written.json", ByHand);

        SessionTree.ReadFrom(file).WriteTo(file);

        SessionTree again = SessionTree.ReadFrom(file);

        Assert.Equal(3, again.Sessions().Count);
        Assert.Equal("postgres", again.Session("prod/db")!.User!.Value.Value);
        Assert.Equal(2222, again.Session("laptop")!.Port!.Value.Value);
    }

    /// <summary>
    /// A store that is there and cannot be read is said out loud. Silently replacing it with an
    /// empty one is a user whose hundred sessions have apparently vanished to a typo.
    /// </summary>
    [Fact]
    public void AStoreThatCannotBeReadIsNotSilentlyEmpty()
    {
        string file = Write("broken.json", "{ this is not json");

        SessionStoreException failed = Assert.Throws<SessionStoreException>(() =>
            SessionTree.ReadFrom(file));

        Assert.Contains("broken.json", failed.Message, StringComparison.Ordinal);
        Assert.NotEqual(string.Empty, failed.Remedy);
    }

    /// <summary>A store that is not there is empty, which is a first run and not a failure.</summary>
    [Fact]
    public void AStoreThatIsNotThereIsEmpty()
    {
        Assert.Empty(SessionTree.ReadFrom(Path.Combine(_directory, "nothing.json")).Sessions());
    }

    // ---- Inheritance, and being told where a value came from ----

    /// <summary>
    /// A folder's settings reach its children, which is the only reason a hundred hosts are
    /// manageable.
    /// </summary>
    [Fact]
    public void AFoldersSettingsReachItsChildren()
    {
        SessionTree tree = SessionTree.ReadFrom(Write("inherit.json", ByHand));

        ResolvedSession web = tree.Session("prod/web")!;

        Assert.Equal("deploy", web.User!.Value.Value);
        Assert.Equal("bastion.example", web.JumpHost!.Value.Value);
        Assert.Equal("prod-key", web.Credential!.Value.Value);
        Assert.Equal(22, web.Port!.Value.Value);
    }

    /// <summary>And a child that sets one takes it over.</summary>
    [Fact]
    public void AChildOverridesWhatItSetsAndInheritsTheRest()
    {
        SessionTree tree = SessionTree.ReadFrom(Write("override.json", ByHand));

        ResolvedSession database = tree.Session("prod/db")!;

        Assert.Equal("postgres", database.User!.Value.Value);
        Assert.Equal("bastion.example", database.JumpHost!.Value.Value);
    }

    /// <summary>
    /// The design's own requirement: a user is told where a value came from rather than left to work
    /// it out. Somebody whose session connects as the wrong account wants the folder named.
    /// </summary>
    [Fact]
    public void EveryValueNamesTheNodeThatSetIt()
    {
        SessionTree tree = SessionTree.ReadFrom(Write("sources.json", ByHand));

        ResolvedSession web = tree.Session("prod/web")!;

        // Inherited from the root, from the folder, and set on the session itself.
        Assert.Equal("/", web.User!.Value.From);
        Assert.Equal("prod", web.JumpHost!.Value.From);

        ResolvedSession database = tree.Session("prod/db")!;

        Assert.Equal("prod/db", database.User!.Value.From);
        Assert.Equal("prod", database.JumpHost!.Value.From);

        ResolvedSession laptop = tree.Session("laptop")!;

        Assert.Equal("laptop", laptop.Port!.Value.From);
        Assert.Equal("/", laptop.User!.Value.From);
    }

    /// <summary>Tags accumulate down the tree, so a folder's label finds everything under it.</summary>
    [Fact]
    public void TagsAccumulateFromEveryFolderAbove()
    {
        SessionTree tree = SessionTree.ReadFrom(Write("tags.json", ByHand));

        Assert.Equal(["production", "http"], tree.Session("prod/web")!.Tags);
        Assert.Equal(["production"], tree.Session("prod/db")!.Tags);
        Assert.Empty(tree.Session("laptop")!.Tags);
    }

    // ---- Finding one, once the tree is too big to be how anybody finds anything ----

    [Theory]
    [InlineData("web", 1)]
    [InlineData("prod", 2)]
    [InlineData("production", 2)]
    [InlineData("127.0.0.1", 1)]
    [InlineData("http", 1)]
    [InlineData("nothing like this", 0)]
    public void SearchFindsByNameHostTagAndFolder(string query, int expected)
    {
        SessionTree tree = SessionTree.ReadFrom(Write("search.json", ByHand));

        Assert.Equal(expected, tree.Search(query).Count);
    }

    /// <summary>An empty query is everything, which is what an open palette shows before typing.</summary>
    [Fact]
    public void AnEmptyQueryIsEverything()
    {
        SessionTree tree = SessionTree.ReadFrom(Write("all.json", ByHand));

        Assert.Equal(tree.Sessions().Count, tree.Search("   ").Count);
    }

    // ---- The file is safe to commit ----

    /// <summary>
    /// No secret can be in this file, because there is nowhere in the model to put one. What is
    /// stored is the name of a credential, which <c>SecretStore</c> resolves against the user's own
    /// Credential Manager.
    /// </summary>
    [Fact]
    public void ThereIsNowhereInTheModelForASecret()
    {
        string[] fields = [.. typeof(SessionSettings).GetProperties()
                                                     .Select(property => property.Name)];

        Assert.DoesNotContain("Password", fields);
        Assert.DoesNotContain("Passphrase", fields);
        Assert.DoesNotContain("Secret", fields);

        // The one credential field is a reference, and a name is what it holds.
        Assert.Contains("Credential", fields);
        Assert.Equal(typeof(string),
                     typeof(SessionSettings).GetProperty("Credential", BindingFlags.Public
                                                                       | BindingFlags.Instance)!
                                            .PropertyType);
    }

    /// <summary>And nothing that looks like a secret reaches the file this client writes.</summary>
    [Fact]
    public void NothingSecretIsWritten()
    {
        string file = Write("committed.json", ByHand);

        SessionTree.ReadFrom(file).WriteTo(file);

        string written = File.ReadAllText(file);

        Assert.Contains("prod-key", written, StringComparison.Ordinal);
        Assert.DoesNotContain("password", written, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("passphrase", written, StringComparison.OrdinalIgnoreCase);
    }

    // ---- Opening one that is already open ----

    /// <summary>
    /// A double click on a host somebody is already logged into brings it forward. A second login is
    /// a second entry in the server's auth log, and on a jump box that may be what is being watched.
    /// </summary>
    [Fact]
    public void OpeningOneThatIsOpenFocusesIt()
    {
        OpenSessions open = new();

        Assert.Equal(Opening.Opened, open.Open("prod/web"));
        Assert.Equal(Opening.Focused, open.Open("prod/web"));
        Assert.Single(open.Paths);

        // Unless the user asked for a second, which is the only way to get one.
        Assert.Equal(Opening.Opened, open.Open("prod/web", another: true));
        Assert.Equal(2, open.Paths.Count);
    }

    /// <summary>What a closing window lists is hosts, each named once however many are open.</summary>
    [Fact]
    public void WhatIsListedOnClosingIsHostsAndNotWindows()
    {
        OpenSessions open = new();

        open.Open("prod/web");
        open.Open("prod/web", another: true);
        open.Open("prod/db");

        Assert.Equal(["prod/web", "prod/db"], open.Closing());

        open.Closed("prod/db");

        Assert.Equal(["prod/web"], open.Closing());
    }

    private string Write(string name, string content)
    {
        Directory.CreateDirectory(_directory);

        string file = Path.Combine(_directory, name);

        File.WriteAllText(file, content, new UTF8Encoding(false));

        return file;
    }
}
