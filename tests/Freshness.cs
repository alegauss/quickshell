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

        if (Newer(project, Path.GetDirectoryName(self.Location)!) is { } edited)
        {
            throw new InvalidOperationException(
                $"{Path.GetFileName(self.Location)} is older than {edited}, so it was not built from the "
                + "source in front of you - the last build failed or never ran. Build, read what it said, "
                + "and run again. A pass from this binary would describe code nobody compiled (QS99).");
        }
    }

    /// <summary>
    /// The first source file feeding <paramref name="project"/> that was written after the output
    /// built from it, or null where there is none or the project is not on this disk.
    ///
    /// <para><b>Each project against its own output, which is QS209.</b> The SDK builds reference
    /// assemblies, so a referenced project whose public surface did not change spares the projects
    /// above it a recompile: the test assembly keeps its old time and the fresh dependency is copied
    /// in beside it. Measured against the test assembly alone, that ordinary edit read as a failed
    /// build. So each project's sources are measured against that project's own DLL in
    /// <paramref name="output"/>, and a project with no DLL there is not judged.</para>
    /// </summary>
    /// <param name="project">The test project, the start of the closure.</param>
    /// <param name="output">The folder the test assembly and its dependencies were built into.</param>
    internal static string? Newer(string project, string output)
    {
        if (!File.Exists(project))
        {
            return null;
        }

        DateTime oldest = DateTime.MaxValue;
        string? root = null;

        foreach (string next in Closure(project))
        {
            string built = Path.Combine(output, AssemblyName(next) + ".dll");

            if (!File.Exists(built))
            {
                continue;
            }

            DateTime time = File.GetLastWriteTimeUtc(built);
            oldest = time < oldest ? time : oldest;
            root ??= Path.GetDirectoryName(next);

            foreach (string file in Sources(Path.GetDirectoryName(next)!))
            {
                if (File.GetLastWriteTimeUtc(file) > time + Slack)
                {
                    return file;
                }
            }
        }

        // The build files above the projects rebuild everything, so the oldest output is the one
        // they have to be older than.
        foreach (string build in root is null ? [] : BuildFiles(root))
        {
            if (File.GetLastWriteTimeUtc(build) > oldest + Slack)
            {
                return build;
            }
        }

        return null;
    }

    /// <summary>A project and everything it references, followed through their references, each once.</summary>
    private static HashSet<string> Closure(string project)
    {
        HashSet<string> projects = new(StringComparer.OrdinalIgnoreCase);
        Stack<string> pending = new([Path.GetFullPath(project)]);

        while (pending.Count > 0)
        {
            string next = pending.Pop();

            if (!File.Exists(next) || !projects.Add(next))
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

        return projects;
    }

    /// <summary>The project's AssemblyName where it sets one — the client's is <c>quickshell</c> — and its file name otherwise.</summary>
    private static string AssemblyName(string project) =>
        XDocument.Load(project).Descendants("AssemblyName").FirstOrDefault()?.Value is { Length: > 0 } named
            ? named
            : Path.GetFileNameWithoutExtension(project);

    /// <summary>Every input file in a project's folder, outside what the build writes.</summary>
    private static IEnumerable<string> Sources(string directory)
    {
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
    }

    /// <summary>The Directory.Build files above a folder, up to the repository's root.</summary>
    private static IEnumerable<string> BuildFiles(string directory)
    {
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
                yield break;
            }
        }
    }
}
