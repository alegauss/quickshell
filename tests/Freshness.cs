using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Xml.Linq;

namespace Quickshell.Tests;

/// <summary>
/// Refuses to run a test assembly that is older than the source it is built from (QS99).
///
/// <para><b>Why the assembly checks itself.</b> A failed build leaves the last good binary where it
/// was, and running it prints a full green summary for code that was never compiled — believed for
/// a moment twice in the session that filed this, and once more in the one that fixed it. A runner
/// script can guard only the runs that go through it; an assembly launched directly, by
/// <c>dotnet test</c> or by a person, goes through this, because it runs before the first test does.
/// </para>
///
/// <para><b>What counts as source.</b> The test project and every project it references, followed
/// through their own references, and the build files above them: C#, project files, shaders and
/// XAML. Test data is not, because changing a reference image needs no rebuild.</para>
///
/// <para>Linked into every test project from <c>tests/Directory.Build.props</c>, which also writes
/// the project's path into the assembly for this to start from. A binary with no sources beside it —
/// copied somewhere else — has nothing to be older than, and runs.</para>
/// </summary>
internal static class Freshness
{
    /// <summary>The metadata key the project's own path is written under.</summary>
    internal const string ProjectKey = "QuickshellTestProject";

    /// <summary>What a file has to be to make a rebuild necessary.</summary>
    private static readonly string[] Inputs = [".cs", ".csproj", ".props", ".targets", ".hlsl", ".xaml"];

    /// <summary>
    /// A file written in the same second as the assembly is the build's own doing — a generated
    /// source, or a file system that rounds times — and not a later edit.
    /// </summary>
    private static readonly TimeSpan Slack = TimeSpan.FromSeconds(2);

    [ModuleInitializer]
    internal static void Check()
    {
        Assembly self = typeof(Freshness).Assembly;
        string? project = self.GetCustomAttributes<AssemblyMetadataAttribute>()
                              .FirstOrDefault(each => each.Key == ProjectKey)?.Value;

        if (string.IsNullOrEmpty(project) || string.IsNullOrEmpty(self.Location))
        {
            return;
        }

        if (Newer(project, File.GetLastWriteTimeUtc(self.Location)) is { } edited)
        {
            throw new InvalidOperationException(
                $"{Path.GetFileName(self.Location)} is older than {edited}, so it was not built from the "
                + "source in front of you - the last build failed or never ran. Build, read what it said, "
                + "and run again. A pass from this binary would describe code nobody compiled (QS99).");
        }
    }

    /// <summary>
    /// The first source file feeding <paramref name="project"/> that was written after
    /// <paramref name="built"/>, or null where there is none or the project is not on this disk.
    /// </summary>
    internal static string? Newer(string project, DateTime built)
    {
        if (!File.Exists(project))
        {
            return null;
        }

        foreach (string file in Sources(project))
        {
            if (File.GetLastWriteTimeUtc(file) > built + Slack)
            {
                return file;
            }
        }

        return null;
    }

    /// <summary>Every input file of a project and of everything it references, each once.</summary>
    private static IEnumerable<string> Sources(string project)
    {
        HashSet<string> projects = new(StringComparer.OrdinalIgnoreCase);
        Stack<string> pending = new([Path.GetFullPath(project)]);

        while (pending.Count > 0)
        {
            string next = pending.Pop();

            if (!projects.Add(next) || !File.Exists(next))
            {
                continue;
            }

            string directory = Path.GetDirectoryName(next)!;

            foreach (XElement reference in XDocument.Load(next).Descendants("ProjectReference"))
            {
                if (reference.Attribute("Include")?.Value is { Length: > 0 } include)
                {
                    pending.Push(Path.GetFullPath(Path.Combine(directory, include)));
                }
            }
        }

        foreach (string next in projects)
        {
            string directory = Path.GetDirectoryName(next)!;

            foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(directory, file);

                if (relative.StartsWith("bin" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    || relative.StartsWith("obj" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    || !Inputs.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                yield return file;
            }

            // The build files above the project shape it as much as its own do.
            for (DirectoryInfo? above = new DirectoryInfo(directory).Parent; above is not null; above = above.Parent)
            {
                foreach (string build in new[] { "Directory.Build.props", "Directory.Build.targets" })
                {
                    string path = Path.Combine(above.FullName, build);

                    if (File.Exists(path))
                    {
                        yield return path;
                    }
                }

                if (File.Exists(Path.Combine(above.FullName, "Quickshell.sln")))
                {
                    break;
                }
            }
        }
    }
}
