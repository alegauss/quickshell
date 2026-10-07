using System.Text.RegularExpressions;
using Quickshell.Transport;
using Xunit;

namespace Quickshell.Transport.Tests;

/// <summary>
/// The members of SSH.NET this client reaches by name are there, asked with no server anywhere
/// (QS122).
///
/// <para>Every test that exercises them against a real server skips where the fixture is down, so
/// before this an upgrade that renamed one built clean on a machine without docker and broke for a
/// user. This one runs everywhere, CI and the guest included, and fails on the upgrade.</para>
/// </summary>
public sealed partial class LibraryShapeTests
{
    /// <summary>The line's falsification: a break in the reached-for members fails a run with no fixture.</summary>
    [Fact]
    public void EveryMemberReachedByNameIsThere()
    {
        IReadOnlyList<string> missing = LibraryShape.Missing();

        Assert.True(missing.Count == 0,
            $"this SSH.NET is missing what quickshell reaches by name: {string.Join(", ", missing)}");
    }

    /// <summary>
    /// And the library cannot move underneath that check: the reference is an exact version, so a
    /// restore never picks a newer one nobody chose.
    /// </summary>
    [Fact]
    public void TheLibraryIsPinnedToOneVersion()
    {
        string project = File.ReadAllText(Path.Combine(Repository.Root, "src", "Quickshell.Transport",
                                                        "Quickshell.Transport.csproj"));

        Match reference = Reference().Match(project);

        Assert.True(reference.Success, "the transport project does not reference SSH.NET");
        Assert.Matches(@"^\[[0-9.]+\]$", reference.Groups["version"].Value);
    }

    [GeneratedRegex("""<PackageReference Include="SSH\.NET" Version="(?<version>[^"]+)""")]
    private static partial Regex Reference();
}
