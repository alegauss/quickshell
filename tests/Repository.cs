// Named, because a WPF project's implicit usings do not include it.
using System.IO;

namespace Quickshell.Tests;

/// <summary>
/// The repository a test was built from, found once for every test assembly (QS195).
///
/// <para><b>Thirty-three copies of this walk</b> had settled on one marker and three ways of
/// failing. A test that reads a document, a fixture's key or a golden image does it from here.</para>
///
/// <para>Linked into every test project from <c>tests/Directory.Build.props</c>, beside
/// <see cref="Freshness"/>.</para>
/// </summary>
internal static class Repository
{
    private static readonly Lazy<string> Found = new(Walk);

    /// <summary>The folder holding <c>Quickshell.sln</c>, above wherever the test runs from.</summary>
    /// <exception cref="DirectoryNotFoundException">The test is not running from inside the repository.</exception>
    public static string Root => Found.Value;

    private static string Walk()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Quickshell.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
               ?? throw new DirectoryNotFoundException(
                   $"Quickshell.sln is not above {AppContext.BaseDirectory}, so this test is not running "
                   + "from inside the repository it was built from.");
    }
}
