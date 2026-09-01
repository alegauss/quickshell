using System.IO;
using System.Text.RegularExpressions;
using Quickshell.App;
using Quickshell.Terminal;
using Xunit;

namespace Quickshell.App.Tests;

/// <summary>
/// QS50's falsification, as a test rather than as a thing anybody has to remember:
/// <em>falsified when a setting exists that no reference documents</em>.
///
/// <para><b>Both directions, and the second one is the half that decays.</b> A key added without a
/// paragraph fails, which is the claim. A paragraph about a key that no longer exists fails too,
/// which is what stops the reference quietly becoming a description of an older client — the failure
/// mode every settings document in the world eventually reaches.</para>
/// </summary>
public sealed partial class SettingsReferenceTests
{
    /// <summary>Where the reference lives, and it is a file a user reads rather than a comment.</summary>
    private const string Reference = "docs/SETTINGS.md";

    /// <summary>A heading naming one key, which is how the reference addresses them.</summary>
    [GeneratedRegex(@"^### `([A-Za-z][A-Za-z0-9]*)`$", RegexOptions.Multiline)]
    private static partial Regex Documented { get; }

    /// <summary>Every setting this build reads is documented, and everything documented exists.</summary>
    [Fact]
    public void EverySettingIsDocumentedAndEveryDocumentedSettingExists()
    {
        string[] known = SettingsFile.Known;

        string[] written = [.. Documented.Matches(File.ReadAllText(Page()))
                                         .Select(one => one.Groups[1].Value)];

        Assert.NotEmpty(written);

        // A key this build reads with nothing written about it. This is the falsification.
        Assert.Empty(known.Except(written, StringComparer.Ordinal));

        // And a key the reference describes that this build has never heard of, which is the same
        // document telling a user about a client they are not running.
        Assert.Empty(written.Except(known, StringComparer.Ordinal));
    }

    /// <summary>
    /// The example in the reference is a settings file this client actually reads.
    ///
    /// <para>A reference whose example does not parse is worse than none: it is the first thing
    /// somebody copies, and they will conclude the client is broken rather than the page.</para>
    /// </summary>
    [Fact]
    public void TheExampleInTheReferenceIsAFileThisClientReads()
    {
        string page = File.ReadAllText(Page());

        int opened = page.IndexOf("```jsonc", StringComparison.Ordinal);

        Assert.True(opened >= 0, "the reference carries no example");

        int from = page.IndexOf('\n', opened) + 1;
        int to = page.IndexOf("```", from, StringComparison.Ordinal);

        string example = page[from..to];
        string file = Path.Combine(Path.GetTempPath(), $"quickshell-example-{Guid.NewGuid():N}.json");

        try
        {
            File.WriteAllText(file, example);

            Settings read = SettingsFile.ReadFrom(file);

            // Every value in the example is this build's default, so the example documents the
            // defaults rather than describing a machine somebody happened to be on.
            Assert.Equal(Settings.Default.Theme, read.Theme);
            Assert.Equal(Settings.Default.FontFamily, read.FontFamily);
            Assert.Equal(Settings.Default.FontSize, read.FontSize);
            Assert.Equal(Settings.Default.Scrollback, read.Scrollback);
            Assert.Equal(Settings.Default.Ligatures, read.Ligatures);
            Assert.Equal(Settings.Default.Cursor, read.Cursor);
            Assert.Equal(Settings.Default.CursorBlink, read.CursorBlink);
            Assert.Equal(Settings.Default.WarnOnPaste, read.WarnOnPaste);

            // And nothing in it was unrecognised, which is the other half of the same claim.
            Assert.Empty(read.Unrecognised);
        }
        finally
        {
            File.Delete(file);
        }
    }

    /// <summary>
    /// Writing the file back keeps the user's comments, their blank lines and their spacing.
    ///
    /// <para><b>The design's sentence is that the surface writes the file back preserving comments or
    /// is not worth having.</b> A user who wrote a note beside a setting and lost it the first time
    /// they changed one has learnt that the file is the client's rather than theirs, and after that
    /// they stop writing notes in it.</para>
    /// </summary>
    [Fact]
    public void WritingTheFileBackKeepsWhatTheUserPutInIt()
    {
        string file = Path.Combine(Path.GetTempPath(), $"quickshell-kept-{Guid.NewGuid():N}.json");

        string mine = """
            {
              // Twelve is too small on the dock and right on the laptop.
              "fontSize": 12,

              /* Left on until somebody convinces me about != */
              "ligatures": true,

              "somethingANewerBuildKnows": { "nested": [1, 2] }
            }
            """;

        try
        {
            File.WriteAllText(file, mine);

            Settings read = SettingsFile.ReadFrom(file);

            Assert.Equal(12d, read.FontSize);

            SettingsFile.WriteTo(file, read with { FontSize = 14, Ligatures = false });

            string after = File.ReadAllText(file);

            // The notes, word for word.
            Assert.Contains("// Twelve is too small on the dock", after, StringComparison.Ordinal);
            Assert.Contains("/* Left on until somebody convinces me", after, StringComparison.Ordinal);

            // The blank line between them, which is somebody's idea of how their file reads.
            Assert.Contains("\"fontSize\": 14,\r\n\r\n", after.ReplaceLineEndings("\r\n"),
                            StringComparison.Ordinal);

            // The values that changed, and the key this build has never heard of, untouched.
            Assert.Contains("\"ligatures\": false", after, StringComparison.Ordinal);
            Assert.Contains("\"somethingANewerBuildKnows\": { \"nested\": [1, 2] }", after,
                            StringComparison.Ordinal);

            // And it still reads back as what was written.
            Settings again = SettingsFile.ReadFrom(file);

            Assert.Equal(14d, again.FontSize);
            Assert.False(again.Ligatures);
        }
        finally
        {
            File.Delete(file);
        }
    }

    /// <summary>
    /// A setting the file does not mention is not added to it, because where it would go is the
    /// user's to decide.
    /// </summary>
    [Fact]
    public void AnUnmentionedSettingIsNotWrittenIn()
    {
        string file = Path.Combine(Path.GetTempPath(), $"quickshell-terse-{Guid.NewGuid():N}.json");

        try
        {
            File.WriteAllText(file, "{ \"fontSize\": 12 }");

            SettingsFile.WriteTo(file, SettingsFile.ReadFrom(file) with { Cursor = CursorShape.Bar });

            string after = File.ReadAllText(file);

            Assert.Equal("{ \"fontSize\": 12 }", after);
        }
        finally
        {
            File.Delete(file);
        }
    }

    /// <summary>The reference, found by the file that names the solution.</summary>
    private static string Page()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null
               && !File.Exists(Path.Combine(directory.FullName, "Quickshell.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);

        return Path.Combine(directory.FullName, Reference.Replace('/', Path.DirectorySeparatorChar));
    }
}
