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
    public void AnEditToAReferencedProjectAfterItsBuildIsFound()
    {
        Tree tree = Make();

        File.SetLastWriteTimeUtc(tree.Source, Built.AddMinutes(5));

        Assert.Equal(tree.Source, Freshness.Newer(tree.Tests, tree.Output));
    }

    [Fact]
    public void ASourceTreeOlderThanItsBuildIsNotStale()
    {
        Tree tree = Make();

        Assert.Null(Freshness.Newer(tree.Tests, tree.Output));
    }

    /// <summary>
    /// QS209: an implementation-only edit to a referenced project, built. The library's DLL is newer
    /// than the edit and the test project's is not, because reference assemblies spared it a
    /// recompile — and that is a fresh build, not a stale one.
    /// </summary>
    [Fact]
    public void AReferencedProjectRebuiltWithoutItsTestsIsNotStale()
    {
        Tree tree = Make();

        File.SetLastWriteTimeUtc(tree.Source, Built.AddMinutes(5));
        File.SetLastWriteTimeUtc(Path.Combine(tree.Output, "Library.dll"), Built.AddMinutes(6));

        Assert.Null(Freshness.Newer(tree.Tests, tree.Output));
    }

    [Fact]
    public void AnEditInsideBinOrObjIsTheBuildsOwnAndNotSource()
    {
        Tree tree = Make();
        string generated = Path.Combine(Path.GetDirectoryName(tree.Tests)!, "obj", "Generated.cs");

        Directory.CreateDirectory(Path.GetDirectoryName(generated)!);
        File.WriteAllText(generated, "// generated");
        File.SetLastWriteTimeUtc(generated, Built.AddMinutes(5));

        Assert.Null(Freshness.Newer(tree.Tests, tree.Output));
    }

    [Fact]
    public void AProjectThatIsNotOnThisDiskHasNothingToBeOlderThan() =>
        Assert.Null(Freshness.Newer(Path.Combine(_root, "nowhere", "Gone.csproj"), _root));

    private sealed record Tree(string Tests, string Source, string Output);

    /// <summary>
    /// A test project referencing a library, both built at <see cref="Built"/>, every source written
    /// five minutes before that.
    /// </summary>
    private Tree Make()
    {
        string library = Path.Combine(_root, "src", "Library");
        string tests = Path.Combine(_root, "tests", "Library.Tests");
        string output = Path.Combine(tests, "bin", "Debug");

        Directory.CreateDirectory(library);
        Directory.CreateDirectory(output);

        File.WriteAllText(Path.Combine(_root, "Quickshell.sln"), string.Empty);

        string testProject = Path.Combine(tests, "Library.Tests.csproj");
        string source = Path.Combine(library, "Thing.cs");

        File.WriteAllText(Path.Combine(library, "Library.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
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

        foreach (string dll in new[] { "Library.dll", "Library.Tests.dll" })
        {
            string path = Path.Combine(output, dll);

            File.WriteAllText(path, string.Empty);
            File.SetLastWriteTimeUtc(path, Built);
        }

        return new Tree(testProject, source, output);
    }
}
