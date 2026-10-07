using System.IO;
using System.Text.RegularExpressions;
using Quickshell.App;
using Xunit;

namespace Quickshell.App.Tests;

/// <summary>
/// QS178: the command line is one list, the reference names exactly that list, and the client acts
/// on no flag the list does not hold.
/// </summary>
public sealed partial class CommandLineReferenceTests
{
    /// <summary>A flag in the first column of one of the reference's tables.</summary>
    [GeneratedRegex(@"^\| `(--[a-z-]+)` \|([^|]*)\|([^|]*)\|", RegexOptions.Multiline)]
    private static partial Regex Row { get; }

    /// <summary>A flag spelled as a literal anywhere in the client's source.</summary>
    [GeneratedRegex(@"""(--[a-z][a-z-]*)""")]
    private static partial Regex Literal { get; }

    /// <summary>The page names every flag the client has, says what each takes, and names nothing else.</summary>
    [Fact]
    public void ThePageAndTheListAreTheSameFlags()
    {
        Dictionary<string, (string Takes, string Means)> written = Row.Matches(File.ReadAllText(Path.Combine(Repository.Root, "docs", "COMMAND-LINE.md")))
            .ToDictionary(row => row.Groups[1].Value,
                          // The page's backticks are markup and the list's sentences are plain.
                          row => (row.Groups[2].Value.Trim().Trim('`'),
                                  row.Groups[3].Value.Trim().Replace("`", string.Empty, StringComparison.Ordinal)),
                          StringComparer.Ordinal);

        Assert.Equal(CommandLine.All.Select(flag => flag.Name).Order(StringComparer.Ordinal),
                     written.Keys.Order(StringComparer.Ordinal));

        foreach (Flag flag in CommandLine.All)
        {
            Assert.Equal(flag.Takes, written[flag.Name].Takes);
            Assert.Equal(flag.Means, written[flag.Name].Means);
        }
    }

    /// <summary>
    /// The falsification: the client acts on no flag the list does not hold. Every flag-shaped
    /// literal in its source is one of the list's, so a flag added beside the list fails here.
    /// </summary>
    [Fact]
    public void TheClientSpellsNoFlagTheListDoesNotHold()
    {
        HashSet<string> listed = [.. CommandLine.All.Select(flag => flag.Name)];

        string[] stray = [.. Directory.EnumerateFiles(Path.Combine(Repository.Root, "src", "Quickshell.App"), "*.cs", SearchOption.AllDirectories)
                                      .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                                      .SelectMany(file => Literal.Matches(File.ReadAllText(file))
                                                                 .Select(found => found.Groups[1].Value))
                                      .Where(flag => !listed.Contains(flag))
                                      .Distinct(StringComparer.Ordinal)];

        Assert.Empty(stray);
    }
}
