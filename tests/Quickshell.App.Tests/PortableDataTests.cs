using System.IO;
using Quickshell.App;
using Xunit;

namespace Quickshell.App.Tests;

/// <summary>
/// QS192: a portable copy that installs itself brings its settings and saved sessions into a profile
/// that has none, and touches a profile that has its own not at all.
/// </summary>
public sealed class PortableDataTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "qs192-" + Guid.NewGuid().ToString("N"));

    private string Portable => Path.Combine(_root, "portable", "data");

    private string Installed => Path.Combine(_root, "AppData", "quickshell");

    public PortableDataTests() => Directory.CreateDirectory(Portable);

    /// <inheritdoc/>
    public void Dispose() => Directory.Delete(_root, recursive: true);

    /// <summary>
    /// The falsification: a portable copy holding saved sessions installs into a profile with no
    /// settings folder, and the installed copy's folder has them — and none of what describes the
    /// portable copy itself.
    /// </summary>
    [Fact]
    public void SessionsComeAcrossIntoAProfileThatHasNone()
    {
        File.WriteAllText(Path.Combine(Portable, "settings.json"), """{ "fontSize": 14 }""");
        File.WriteAllText(Path.Combine(Portable, "sessions.json"), """{ "sessions": [] }""");
        Directory.CreateDirectory(Path.Combine(Portable, "logs"));
        File.WriteAllText(Path.Combine(Portable, "logs", "quickshell.log"), "the portable copy's own");

        string said = PortableData.Carry(Portable, Installed);

        Assert.Equal("""{ "sessions": [] }""", File.ReadAllText(Path.Combine(Installed, "sessions.json")));
        Assert.Equal("""{ "fontSize": 14 }""", File.ReadAllText(Path.Combine(Installed, "settings.json")));
        Assert.False(Directory.Exists(Path.Combine(Installed, "logs")), "the portable copy's logs came across");
        Assert.StartsWith("Your settings and saved sessions came with it", said, StringComparison.Ordinal);

        // Copied, not moved: the portable copy keeps working with its own.
        Assert.True(File.Exists(Path.Combine(Portable, "sessions.json")));
    }

    /// <summary>A profile with its own settings is not touched, and the sentence says where the portable ones are.</summary>
    [Fact]
    public void AProfileWithItsOwnSettingsIsLeftAlone()
    {
        File.WriteAllText(Path.Combine(Portable, "sessions.json"), "portable");
        Directory.CreateDirectory(Installed);
        File.WriteAllText(Path.Combine(Installed, "sessions.json"), "installed");

        string said = PortableData.Carry(Portable, Installed);

        Assert.Equal("installed", File.ReadAllText(Path.Combine(Installed, "sessions.json")));
        Assert.Contains("nothing was brought across", said, StringComparison.Ordinal);
        Assert.Contains(Portable, said, StringComparison.Ordinal);
    }

    /// <summary>A portable copy with nothing worth carrying makes no folder and says nothing.</summary>
    [Fact]
    public void NothingToCarryIsNothingSaid()
    {
        Assert.Equal(string.Empty, PortableData.Carry(Portable, Installed));
        Assert.False(Directory.Exists(Installed));
    }
}
