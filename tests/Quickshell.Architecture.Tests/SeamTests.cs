// System.IO is not in a WPF project's implicit usings, which is why this file names it.
using System.IO;
using System.Reflection;
using System.Xml.Linq;
using Quickshell.Transport;
using Xunit;

namespace Quickshell.Architecture.Tests;

/// <summary>
/// QS36's falsification, as a test rather than as a thing anybody has to remember: a protocol
/// library's types may not leave the transport assembly.
///
/// <para><b>The scan skips this file, and it has to.</b> The names it looks for are written out
/// below, so a scanner that read its own source would report itself and every run would be red for
/// the one file that exists to catch the problem.</para>
///
/// <para><b>Two of these assert about a library the client does not yet reference</b>, and that is
/// worth being honest about: with no library present they would pass against anything.
/// <see cref="TheRuleRefusesAReferenceThatFlowsOnwards"/> is what stops them being decoration — it
/// runs the same rule against project files written to break it, so the guard is known to fire
/// before there is anything for it to fire at. QS37 is when it starts having a subject.</para>
///
/// <para><b><c>prototypes</c> is outside this and deliberately.</b> QS5's probe names the library on
/// every line, because answering six questions about a library is what it was written to do. It is
/// in no solution, nothing references it, and none of its code ships — so the rule is scoped to the
/// client rather than to the repository. The first run of this test found it, which is the useful
/// half of the story: the guard works, and the boundary is now written down instead of assumed.</para>
/// </summary>
public sealed class SeamTests
{
    /// <summary>
    /// The assembly a protocol library may be named in. One, and it is the one holding the seam.
    /// </summary>
    private const string TransportProject = "Quickshell.Transport";

    /// <summary>
    /// The root namespaces of every SSH library this project has considered, whether or not it is
    /// referenced today. QS5 chose the first; the rest are here because the seam exists precisely so
    /// that choice can be revisited, and a guard that only knew the current answer would go quiet
    /// the moment somebody tried a different one.
    /// </summary>
    private static readonly string[] LibraryNamespaces =
    [
        "Renci.SshNet",
        "Rebex.Net",
        "Granados",
        "FxSsh",
        "libssh2",
    ];

    /// <summary>How those libraries spell themselves as a package.</summary>
    private static readonly string[] LibraryPackages =
    [
        "SSH.NET",
        "SSH.NET.Core",
        "Rebex.SshShell",
        "Granados",
        "FxSsh",
    ];

    /// <summary>
    /// The falsification, word for word: a search for the library's namespace finds a hit outside
    /// the transport assembly.
    /// </summary>
    [Fact]
    public void NoProtocolLibraryNamespaceAppearsOutsideTheTransport()
    {
        List<string> offenders = [];

        foreach (string file in Sources())
        {
            string text = File.ReadAllText(file);

            foreach (string library in LibraryNamespaces)
            {
                if (text.Contains(library, StringComparison.Ordinal))
                {
                    offenders.Add($"{Relative(file)} names {library}");
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            $"a protocol library is named outside {TransportProject}, which is the seam QS36 exists "
            + $"to keep: {string.Join("; ", offenders)}");
    }

    /// <summary>
    /// Enforced by project references rather than by review: the package is referenced by the
    /// transport and by nothing else, and it does not flow onwards to whatever references that.
    ///
    /// <para><c>PrivateAssets="all"</c> is the mechanism and it is not decoration. Without it a
    /// project reference carries the package's compile-time assets to every consumer, so
    /// <c>Quickshell.App</c> could name the library's types without ever asking for it — which is
    /// exactly the accident this line is about, arriving through a file nobody edited.</para>
    /// </summary>
    [Fact]
    public void TheProtocolLibraryIsReferencedByTheTransportAloneAndDoesNotFlowOnwards()
    {
        List<string> offenders = [];

        foreach (string project in Projects())
        {
            offenders.AddRange(Offences(Path.GetFileNameWithoutExtension(project),
                                        XDocument.Load(project)));
        }

        Assert.True(
            offenders.Count == 0,
            $"a protocol library package escapes {TransportProject}: {string.Join("; ", offenders)}");
    }

    /// <summary>
    /// The guard, run against project files written to break it. Without this the test above is a
    /// pass with no subject, and a pass with no subject is not evidence of anything.
    /// </summary>
    [Fact]
    public void TheRuleRefusesAReferenceThatFlowsOnwards()
    {
        // The transport, correctly: referenced, and not passed on.
        Assert.Empty(Offences(TransportProject, Project(@"<PackageReference Include=""SSH.NET"" Version=""2026.0.0"" PrivateAssets=""all"" />")));

        // The transport, carelessly: the package flows to everything that references the transport.
        Assert.Single(Offences(TransportProject, Project(@"<PackageReference Include=""SSH.NET"" Version=""2026.0.0"" />")));

        // Somebody else, at all: even sealed off, it is in the wrong assembly.
        Assert.Single(Offences("Quickshell.App", Project(@"<PackageReference Include=""SSH.NET"" Version=""2026.0.0"" PrivateAssets=""all"" />")));

        // And a package that is nothing to do with SSH is nothing to do with this rule.
        Assert.Empty(Offences("Quickshell.Render", Project(@"<PackageReference Include=""Vortice.Direct3D11"" Version=""3.8.3"" />")));
    }

    /// <summary>
    /// The stronger statement, and the one with a subject today: every type named in the seam's own
    /// signatures is either this client's or the framework's.
    ///
    /// <para>The namespace scan catches a library that arrived. This catches the seam being widened
    /// to let one in, which is the same failure a step earlier and the one a person is likelier to
    /// commit while meaning well.</para>
    /// </summary>
    [Fact]
    public void EverySeamSignatureNamesThisClientsVocabularyOrTheFrameworks()
    {
        List<string> offenders = [];

        foreach (Type seam in new[] { typeof(ISshTransport), typeof(IPtyChannel),
                                      typeof(IFileTransferChannel), typeof(IForwardedChannel) })
        {
            foreach (MethodInfo member in seam.GetMethods())
            {
                foreach (Type named in member.GetParameters().Select(p => p.ParameterType)
                                             .Append(member.ReturnType)
                                             .SelectMany(Unwrap))
                {
                    if (!IsOurs(named) && !IsFramework(named))
                    {
                        offenders.Add($"{seam.Name}.{member.Name} names {named.FullName}");
                    }
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            $"the seam names a type from neither this client nor the framework: "
            + $"{string.Join("; ", offenders)}");
    }

    /// <summary>
    /// Exactly three things cross the seam. A fourth is not a method to add, it is evidence the seam
    /// is in the wrong place — so it fails here and gets argued about rather than merged.
    /// </summary>
    [Fact]
    public void ExactlyThreeKindsOfChannelCrossTheSeam()
    {
        Type[] crossing = typeof(ISshTransport).GetMethods()
            .SelectMany(member => Unwrap(member.ReturnType))
            .Where(type => type.IsInterface && IsOurs(type))
            .Distinct()
            .Order(Comparer<Type>.Create((a, b) => string.CompareOrdinal(a.Name, b.Name)))
            .ToArray();

        Assert.Equal(
            [typeof(IFileTransferChannel), typeof(IForwardedChannel), typeof(IPtyChannel)],
            crossing);
    }

    /// <summary>
    /// The seam has a second implementation, and it is in the shipping assembly. An interface with
    /// one implementation has never been tested for the thing an interface is for.
    /// </summary>
    [Fact]
    public void TheSeamHasAnImplementationThatNeedsNoServer()
    {
        Assert.True(typeof(ISshTransport).IsAssignableFrom(typeof(ReplayTransport)));
        Assert.Equal(typeof(ISshTransport).Assembly, typeof(ReplayTransport).Assembly);
    }

    /// <summary>What this rule says about one project file, as a list of what is wrong with it.</summary>
    private static List<string> Offences(string project, XDocument document)
    {
        List<string> offences = [];

        foreach (XElement reference in document.Descendants("PackageReference"))
        {
            string package = reference.Attribute("Include")?.Value ?? string.Empty;

            if (!LibraryPackages.Contains(package, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!string.Equals(project, TransportProject, StringComparison.Ordinal))
            {
                offences.Add($"{project} references {package}");
                continue;
            }

            // An attribute or an element: MSBuild accepts both spellings and a rule that knew only
            // one would be satisfied by a file that reads correctly and behaves the other way.
            string sealedOff = reference.Attribute("PrivateAssets")?.Value
                ?? reference.Element("PrivateAssets")?.Value
                ?? string.Empty;

            if (!string.Equals(sealedOff, "all", StringComparison.OrdinalIgnoreCase))
            {
                offences.Add($"{project} references {package} without PrivateAssets=all, "
                             + "so it flows to everything above");
            }
        }

        return offences;
    }

    /// <summary>A project file with one item group in it, for the rule to be run against.</summary>
    private static XDocument Project(string reference) =>
        XDocument.Parse($"<Project><ItemGroup>{reference}</ItemGroup></Project>");

    /// <summary>The types a signature really names, with tasks, collections and by-ref unwrapped.</summary>
    private static IEnumerable<Type> Unwrap(Type type)
    {
        Type bare = type.IsByRef ? type.GetElementType()! : type;

        yield return bare;

        if (bare.IsGenericType)
        {
            foreach (Type argument in bare.GetGenericArguments().SelectMany(Unwrap))
            {
                yield return argument;
            }
        }
    }

    private static bool IsOurs(Type type) =>
        type.Namespace?.StartsWith("Quickshell", StringComparison.Ordinal) == true;

    private static bool IsFramework(Type type) =>
        type.Namespace is null
        || type.Namespace.StartsWith("System", StringComparison.Ordinal)
        || type.Namespace.StartsWith("Microsoft", StringComparison.Ordinal);

    private static IEnumerable<string> Projects() =>
        Directory.EnumerateFiles(RepositoryRoot(), "*.csproj", SearchOption.AllDirectories)
            .Where(path => !Buried(path));

    /// <summary>Every source file this rule judges, which is all of them but this one.</summary>
    private static IEnumerable<string> Sources() =>
        Directory.EnumerateFiles(RepositoryRoot(), "*.cs", SearchOption.AllDirectories)
            .Where(path => !Buried(path))
            .Where(path => !path.Contains(Path.Combine("src", TransportProject), StringComparison.Ordinal))
            .Where(path => !string.Equals(Path.GetFileName(path), "SeamTests.cs", StringComparison.Ordinal));

    /// <summary>
    /// Whether a path is outside what this rule judges: a build output, or a prototype.
    ///
    /// <para>Prototypes are excluded because they exist to hold the library at arm's length and look
    /// at it — QS5's probe answered six questions that way and wrote them down in
    /// <c>docs/measurements/ssh-net-probe.md</c>. None of it is in a solution, nothing references it,
    /// and none of it ships. The rule is about the client, and the client is <c>src</c>.</para>
    /// </summary>
    private static bool Buried(string path) =>
        path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
        || path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
        || path.Contains($"{Path.DirectorySeparatorChar}prototypes{Path.DirectorySeparatorChar}", StringComparison.Ordinal);

    private static string Relative(string path) =>
        Path.GetRelativePath(RepositoryRoot(), path);

    private static string RepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Quickshell.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory.FullName;
    }
}
