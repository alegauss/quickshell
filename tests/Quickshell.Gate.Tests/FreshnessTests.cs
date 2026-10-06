using Quickshell.Tests;
using Xunit;

namespace Quickshell.Gate.Tests;

/// <summary>
/// QS99's check, against a project tree made for the purpose: the edit it has to find is in a
/// project the test project only references, which is the edit a check of the test project's own
/// folder would miss.
/// </summary>
public sealed class FreshnessTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"quickshell-fresh-{Guid.NewGuid():N}");

    private static readonly DateTime Built = new(2026, 10, 6, 12, 0, 0, DateTimeKind.Utc);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void AnEditToAReferencedProjectAfterTheBuildIsFound()
    {
        (string tests, string edited) = Tree(editedAfterBuild: true);

        Assert.Equal(edited, Freshness.Newer(tests, Built));
    }

    [Fact]
    public void ASourceTreeOlderThanTheBuildIsNotStale()
    {
        (string tests, _) = Tree(editedAfterBuild: false);

        Assert.Null(Freshness.Newer(tests, Built));
    }

    [Fact]
    public void AnEditInsideBinOrObjIsTheBuildsOwnAndNotSource()
    {
        (string tests, _) = Tree(editedAfterBuild: false);
        string generated = Path.Combine(Path.GetDirectoryName(tests)!, "obj", "Generated.cs");

        Directory.CreateDirectory(Path.GetDirectoryName(generated)!);
        File.WriteAllText(generated, "// generated");
        File.SetLastWriteTimeUtc(generated, Built.AddMinutes(5));

        Assert.Null(Freshness.Newer(tests, Built));
    }

    [Fact]
    public void AProjectThatIsNotOnThisDiskHasNothingToBeOlderThan() =>
        Assert.Null(Freshness.Newer(Path.Combine(_root, "nowhere", "Gone.csproj"), Built));

    /// <summary>A test project referencing a library, every file written before the build but one.</summary>
    private (string Tests, string Edited) Tree(bool editedAfterBuild)
    {
        string library = Path.Combine(_root, "src", "Library");
        string tests = Path.Combine(_root, "tests", "Library.Tests");

        Directory.CreateDirectory(library);
        Directory.CreateDirectory(tests);

        File.WriteAllText(Path.Combine(_root, "Quickshell.sln"), string.Empty);

        string libraryProject = Path.Combine(library, "Library.csproj");
        string testProject = Path.Combine(tests, "Library.Tests.csproj");
        string source = Path.Combine(library, "Thing.cs");

        File.WriteAllText(libraryProject, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.WriteAllText(testProject,
            "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup>"
            + "<ProjectReference Include=\"..\\..\\src\\Library\\Library.csproj\" />"
            + "</ItemGroup></Project>");
        File.WriteAllText(source, "class Thing { }");
        File.WriteAllText(Path.Combine(tests, "ThingTests.cs"), "class ThingTests { }");

        foreach (string file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
        {
            File.SetLastWriteTimeUtc(file, Built.AddMinutes(-5));
        }

        if (editedAfterBuild)
        {
            File.SetLastWriteTimeUtc(source, Built.AddMinutes(5));
        }

        return (testProject, source);
    }
}
