using System.IO;
using System.Reflection;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Quickshell.App;
using Xunit;

namespace Quickshell.App.Tests;

/// <summary>
/// QS229's falsification, as SettingsReferenceTests is QS50's: <em>falsified when a field of the
/// session store has no heading on the page</em>.
///
/// <para>Both directions. A field added to the model with nothing written about it fails, and so
/// does a heading about a field the model no longer has.</para>
/// </summary>
public sealed partial class SessionsReferenceTests
{
    private const string Reference = "docs/SESSIONS.md";

    // \r? because a checkout may hand the page over with either line ending.
    [GeneratedRegex(@"^### `([A-Za-z][A-Za-z0-9]*)`\r?$", RegexOptions.Multiline)]
    private static partial Regex Documented { get; }

    /// <summary>Every field a node or its settings can hold is documented, and nothing else is.</summary>
    [Fact]
    public void EveryFieldOfTheStoreIsDocumentedAndEveryDocumentedFieldExists()
    {
        string[] known = [.. Fields(typeof(SessionNode)), .. Fields(typeof(SessionSettings))];

        string[] written = [.. Documented.Matches(File.ReadAllText(Page()))
                                         .Select(one => one.Groups[1].Value)];

        Assert.NotEmpty(written);
        Assert.Empty(known.Except(written, StringComparer.Ordinal));
        Assert.Empty(written.Except(known, StringComparer.Ordinal));
    }

    /// <summary>
    /// The example is a store this client reads, and it means what the page says it means: a folder's
    /// settings reach the sessions under it, a session's own win, and forwards stay with their session.
    /// </summary>
    [Fact]
    public void TheExampleIsAStoreThisClientReadsAsThePageDescribes()
    {
        string page = File.ReadAllText(Page());
        int opened = page.IndexOf("```jsonc", StringComparison.Ordinal);

        Assert.True(opened >= 0, "the reference carries no example");

        int from = page.IndexOf('\n', opened) + 1;
        string example = page[from..page.IndexOf("```", from, StringComparison.Ordinal)];
        string file = Path.Combine(Path.GetTempPath(), $"quickshell-sessions-{Guid.NewGuid():N}.json");

        try
        {
            File.WriteAllText(file, example);

            SessionTree tree = SessionTree.ReadFrom(file);
            ResolvedSession db = tree.Session("Work/db-primary")!;
            ResolvedSession nas = tree.Session("nas")!;

            Assert.Equal(("deploy", "Work"), (db.User!.Value.Value, db.User.Value.From));
            Assert.Equal("bastion.example.com", db.JumpHost!.Value.Value);
            Assert.True(db.Reconnect!.Value.Value);
            Assert.Equal(2222, db.Port!.Value.Value);
            Assert.Single(db.Forwards);

            Assert.False(nas.Reconnect!.Value.Value);
            Assert.Empty(tree.Session("Work/build-box")!.Forwards);
        }
        finally
        {
            File.Delete(file);
        }
    }

    /// <summary>The properties the store writes, which are the ones not marked as computed.</summary>
    private static IEnumerable<string> Fields(Type type) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.GetCustomAttribute<JsonIgnoreAttribute>() is null)
            .Select(property => property.Name);

    private static string Page()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Quickshell.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);

        return Path.Combine(directory.FullName, Reference.Replace('/', Path.DirectorySeparatorChar));
    }
}
