using System.IO;
using System.Text.Json;
using Quickshell.App;
using Xunit;

namespace Quickshell.App.Tests;

/// <summary>
/// The settings file, and the compatibility contract it signs on the first release.
///
/// <para>The line's falsification is one sentence — a key an older build does not recognise must
/// survive a save — so that is asked first and asked the way it happens: a file is written with a
/// key this build has never heard of, loaded, saved, and loaded again.</para>
/// </summary>
public sealed class SettingsFileTests : IDisposable
{
    private readonly string _here =
        Path.Combine(Path.GetTempPath(), $"quickshell-settings-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_here))
        {
            Directory.Delete(_here, recursive: true);
        }
    }

    // ---- The falsification ----

    /// <summary>
    /// A key this build does not recognise survives being loaded and saved by it.
    ///
    /// <para>The case is a user with a newer build on one machine and an older one on another,
    /// sharing a synced folder. The older build must not silently prune what the newer one wrote —
    /// which is exactly what dropping unknown keys does, and exactly the defect nobody would ever
    /// trace back to the client.</para>
    /// </summary>
    [Fact]
    public void AKeyThisBuildDoesNotRecogniseSurvivesASave()
    {
        string file = Path.Combine(_here, "settings.json");

        Written(file, """
            {
              "schema": 1,
              "theme": "Dark",
              "splitPanes": { "layout": "vertical", "ratio": 0.6 },
              "somethingFromTheFuture": [1, 2, 3]
            }
            """);

        Settings read = SettingsFile.ReadFrom(file);

        Assert.Equal(ChromeTheme.Dark, read.Theme);
        Assert.Equal(2, read.Unrecognised.Count);

        SettingsFile.WriteTo(file, read);

        // Read back off disk, so what is asserted is the file and not the object.
        using JsonDocument saved = JsonDocument.Parse(File.ReadAllText(file));

        Assert.Equal("vertical",
                     saved.RootElement.GetProperty("splitPanes").GetProperty("layout").GetString());
        Assert.Equal(0.6,
                     saved.RootElement.GetProperty("splitPanes").GetProperty("ratio").GetDouble());
        Assert.Equal(3, saved.RootElement.GetProperty("somethingFromTheFuture").GetArrayLength());

        // And what this build does know is still right, so the preservation is not a refusal to
        // write.
        Assert.Equal("Dark", saved.RootElement.GetProperty("theme").GetString());
    }

    // ---- The version, and moving forward on it ----

    /// <summary>Every file carries a schema from the first release, because it cannot gain one later.</summary>
    [Fact]
    public void EveryFileThisWritesCarriesItsSchema()
    {
        string file = Path.Combine(_here, "settings.json");

        SettingsFile.WriteTo(file, Settings.Default);

        using JsonDocument saved = JsonDocument.Parse(File.ReadAllText(file));

        Assert.Equal(Settings.Schema, saved.RootElement.GetProperty("schema").GetInt32());
    }

    /// <summary>
    /// A file written before there were versions is schema 0, is migrated forward, and its original
    /// is kept beside it first.
    /// </summary>
    [Fact]
    public void AnUnstampedFileIsMigratedForwardAndBackedUpFirst()
    {
        string file = Path.Combine(_here, "settings.json");

        Written(file, """{ "theme": "Light", "fontSize": 14 }""");

        Settings read = SettingsFile.ReadFrom(file);

        Assert.Equal(Settings.Schema, read.SchemaVersion);
        Assert.Equal(ChromeTheme.Light, read.Theme);
        Assert.Equal(14, read.FontSize);

        // The original, named for what it was, so a user who has to go back knows which file to take.
        string backup = $"{file}.v0.backup";

        Assert.True(File.Exists(backup), $"{backup} is not there");
        Assert.Contains("\"theme\": \"Light\"", File.ReadAllText(backup), StringComparison.Ordinal);
    }

    /// <summary>
    /// A second run does not back up over the first: the migration that goes wrong would otherwise
    /// overwrite the only good copy with its own damage.
    /// </summary>
    [Fact]
    public void ABackupIsNotOverwrittenByALaterRun()
    {
        string file = Path.Combine(_here, "settings.json");

        Written(file, """{ "theme": "Light" }""");

        SettingsFile.ReadFrom(file);

        string backup = $"{file}.v0.backup";
        string first = File.ReadAllText(backup);

        // Whatever happens next, the copy of what the user actually had stays as it was.
        Written(file, """{ "theme": "Dark" }""");

        SettingsFile.ReadFrom(file);

        Assert.Equal(first, File.ReadAllText(backup));
    }

    /// <summary>A file already at this schema is not backed up, because nothing was migrated.</summary>
    [Fact]
    public void AFileAlreadyAtThisSchemaIsLeftAlone()
    {
        string file = Path.Combine(_here, "settings.json");

        SettingsFile.WriteTo(file, Settings.Default);
        SettingsFile.ReadFrom(file);

        Assert.Empty(Directory.GetFiles(_here, "*.backup"));
    }

    // ---- Failing safely ----

    /// <summary>
    /// A file that will not parse is one somebody was editing, so it loads as the defaults and is
    /// left exactly where it is. Replacing it would destroy the only copy of what they typed.
    /// </summary>
    [Fact]
    public void SettingsThatWillNotParseAreLeftOnDisk()
    {
        string file = Path.Combine(_here, "settings.json");

        Written(file, "{ theme: Dark,,, and here I was interrupted");

        Settings read = SettingsFile.ReadFrom(file);

        Assert.Equal(Settings.Default.Theme, read.Theme);
        Assert.Contains("interrupted", File.ReadAllText(file), StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(_here, "*.backup"));
    }

    /// <summary>No file at all is a first run and not a failure.</summary>
    [Fact]
    public void NoFileIsAFirstRun()
    {
        Settings read = SettingsFile.ReadFrom(Path.Combine(_here, "nothing.json"));

        Assert.Equal(Settings.Default.Theme, read.Theme);
        Assert.Equal(Settings.Default.FontFamily, read.FontFamily);
    }

    /// <summary>A key of the wrong type is one value ignored, not a file rejected.</summary>
    [Fact]
    public void AKeyOfTheWrongTypeCostsThatKeyAndNothingElse()
    {
        string file = Path.Combine(_here, "settings.json");

        Written(file, """{ "schema": 1, "theme": 7, "fontSize": "large", "scrollback": 500 }""");

        Settings read = SettingsFile.ReadFrom(file);

        Assert.Equal(Settings.Default.Theme, read.Theme);
        Assert.Equal(Settings.Default.FontSize, read.FontSize);
        Assert.Equal(500, read.Scrollback);
    }

    // ---- Where the files are ----

    /// <summary>
    /// A marker beside the executable moves the whole layout next to it, which is what this client
    /// on a USB stick means.
    /// </summary>
    [Fact]
    public void AMarkerBesideTheExecutableMovesEverythingNextToIt()
    {
        Directory.CreateDirectory(_here);
        File.WriteAllText(Path.Combine(_here, Locations.Marker), string.Empty);

        Locations portable = Locations.Discover(_here);

        Assert.True(portable.Portable);
        Assert.Equal(Path.Combine(_here, "data"), portable.Root);

        // Every folder moves, not just the settings — a portable install that scattered logs into a
        // profile would be leaving exactly what it promised not to.
        Assert.StartsWith(portable.Root, portable.Logs, StringComparison.Ordinal);
        Assert.StartsWith(portable.Root, portable.Crashes, StringComparison.Ordinal);
        Assert.StartsWith(portable.Root, portable.Recordings, StringComparison.Ordinal);
        Assert.StartsWith(portable.Root, portable.Diagnostics, StringComparison.Ordinal);
        Assert.StartsWith(portable.Root, portable.Settings, StringComparison.Ordinal);
        Assert.StartsWith(portable.Root, portable.Windows, StringComparison.Ordinal);

        Assert.Contains("portable", portable.Means, StringComparison.Ordinal);
    }

    /// <summary>And without one it follows the platform, where a user's backup already looks.</summary>
    [Fact]
    public void WithoutAMarkerItFollowsThePlatform()
    {
        Directory.CreateDirectory(_here);

        Locations installed = Locations.Discover(_here);

        Assert.False(installed.Portable);
        Assert.Equal(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                         "quickshell"),
            installed.Root);

        Assert.Contains("AppData", installed.Means, StringComparison.Ordinal);
    }

    /// <summary>
    /// And the client can say which of the two it is using, because a user who cannot find their
    /// files cannot back them up.
    /// </summary>
    [Fact]
    public void TheClientCanSayWhereItsFilesAre()
    {
        string bundle = DiagnosticBundle.Compose(DiagnosticSources.Default(),
                                                 DateTimeOffset.UnixEpoch, "none");

        Assert.Contains("where this client keeps its files", bundle, StringComparison.Ordinal);
        Assert.Contains(Locations.Current.Root, bundle, StringComparison.Ordinal);
        Assert.Contains(Locations.Current.Means, bundle, StringComparison.Ordinal);
    }

    private static void Written(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        File.WriteAllText(path, text);
    }
}
