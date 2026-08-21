using System.Xml.Linq;
using Xunit;

namespace Quickshell.Architecture.Tests;

/// <summary>
/// The one architectural failure this project cannot recover from is a protocol library's
/// types reaching the render thread. Project references are what enforce the direction, so
/// this reads them out of the .csproj files themselves: a reference that inverts the
/// layering is a build that fails here rather than a review somebody has to remember.
/// </summary>
public sealed class LayeringTests
{
    private static readonly string[] Nothing = [];
    private static readonly string[] TerminalOnly = ["Quickshell.Terminal"];
    private static readonly string[] EveryLayer = ["Quickshell.Terminal", "Quickshell.Transport", "Quickshell.Render"];

    public static TheoryData<string, string[]> Layering => new()
    {
        // project, the projects it is allowed to reference
        { "Quickshell.Terminal", Nothing },
        { "Quickshell.Transport", Nothing },
        { "Quickshell.Render", TerminalOnly },
        { "Quickshell.App", EveryLayer },
    };

    [Theory]
    [MemberData(nameof(Layering))]
    public void ProjectReferencesOnlyWhatItsLayerAllows(string project, string[] allowed)
    {
        string[] actual = ProjectReferencesOf(project);

        string[] forbidden = actual.Except(allowed, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

        Assert.True(
            forbidden.Length == 0,
            $"{project} references {string.Join(", ", forbidden)}, which inverts the layering. " +
            $"It may reference {(allowed.Length == 0 ? "nothing" : string.Join(", ", allowed))}.");
    }

    [Fact]
    public void EveryLayerIsOnDisk()
    {
        foreach (string project in EveryLayer.Append("Quickshell.App"))
        {
            Assert.True(File.Exists(ProjectFile(project)), $"{project} is missing from the tree.");
        }
    }

    private static string[] ProjectReferencesOf(string project)
    {
        XDocument document = XDocument.Load(ProjectFile(project));

        return document.Descendants("ProjectReference")
            .Select(reference => reference.Attribute("Include")?.Value)
            .Where(include => !string.IsNullOrWhiteSpace(include))
            .Select(include => Path.GetFileNameWithoutExtension(include!.Replace('\\', Path.DirectorySeparatorChar)))
            .ToArray();
    }

    private static string ProjectFile(string project) =>
        Path.Combine(RepositoryRoot(), "src", project, project + ".csproj");

    private static string RepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Quickshell.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory!.FullName;
    }
}
