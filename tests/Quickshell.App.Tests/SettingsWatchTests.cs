using System.IO;
using Quickshell.App;
using Quickshell.Terminal;
using Xunit;

namespace Quickshell.App.Tests;

/// <summary>
/// The watch on the settings file, which is what makes every live-applied setting changeable.
///
/// <para><b>Against a real file and a real watcher.</b> The two things that go wrong here are both
/// about how a save actually reaches a disk — an editor renaming a temporary file over the original,
/// and one save arriving as several events — and neither is visible from a fake.</para>
/// </summary>
public sealed class SettingsWatchTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"quickshell-watch-{Guid.NewGuid():N}");

    /// <summary>Long enough for a watcher on a busy machine, short enough that a hang is a failure.</summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    /// <summary>A file saved in place is read, and what it says reaches the caller.</summary>
    [Fact]
    public void SavingTheFileIsReadWithoutAnybodyAsking()
    {
        string file = Path.Combine(_directory, "settings.json");

        Directory.CreateDirectory(_directory);
        File.WriteAllText(file, """{ "fontSize": 12 }""");

        List<Settings> seen = [];

        using SettingsWatch watch = OnFile(file, seen);

        File.WriteAllText(file, """{ "fontSize": 18 }""");

        Settings read = Until(seen, one => one.FontSize == 18);

        Assert.Equal(18d, read.FontSize);
    }

    /// <summary>
    /// A file replaced by a rename is read too, which is how most editors save.
    ///
    /// <para><b>This is the case a watcher listening only for <c>Changed</c> misses entirely.</b>
    /// Notepad, Vim and Visual Studio Code all write a temporary file and move it over the original,
    /// so the original is created rather than changed — and a client that heard nothing would look
    /// broken to almost everybody who edited its settings.</para>
    /// </summary>
    [Fact]
    public void AFileReplacedByARenameIsReadAsWell()
    {
        string file = Path.Combine(_directory, "settings.json");
        string temporary = Path.Combine(_directory, "settings.json.tmp");

        Directory.CreateDirectory(_directory);
        File.WriteAllText(file, """{ "fontSize": 12 }""");

        List<Settings> seen = [];

        using SettingsWatch watch = OnFile(file, seen);

        // What an editor does: write beside it, then move over it.
        File.WriteAllText(temporary, """{ "fontSize": 20 }""");
        File.Move(temporary, file, overwrite: true);

        Assert.Equal(20d, Until(seen, one => one.FontSize == 20).FontSize);
    }

    /// <summary>
    /// A burst of writes is one read, because a save arrives as several events.
    ///
    /// <para>Reading between them is reading a file that is half there, which this client answers by
    /// falling back to the defaults — and a user reads that as the thing they just edited having
    /// wiped their settings.</para>
    /// </summary>
    [Fact]
    public void ABurstOfWritesSettlesIntoOneRead()
    {
        string file = Path.Combine(_directory, "settings.json");

        Directory.CreateDirectory(_directory);
        File.WriteAllText(file, """{ "fontSize": 12 }""");

        List<Settings> seen = [];

        using SettingsWatch watch = OnFile(file, seen);

        for (int size = 13; size <= 22; size++)
        {
            File.WriteAllText(file, $$"""{ "fontSize": {{size}} }""");
        }

        Settings read = Until(seen, one => one.FontSize == 22);

        Assert.Equal(22d, read.FontSize);

        // Ten writes, and far fewer reads than writes — the debounce collapsing a burst is the whole
        // claim, and the exact number is the file system's business rather than this test's.
        Assert.True(watch.Reloads < 10,
                    $"ten writes in a burst caused {watch.Reloads} reads");
    }

    /// <summary>
    /// Rereading by hand works whatever the watcher is doing, which is what a chord calls.
    ///
    /// <para>A watch can fail to arm — a network share, a directory it cannot open — and a client
    /// without one still has to be usable.</para>
    /// </summary>
    [Fact]
    public void RereadingByHandWorksWithoutTheWatcher()
    {
        string file = Path.Combine(_directory, "settings.json");

        Directory.CreateDirectory(_directory);
        File.WriteAllText(file, """{ "cursor": "Bar" }""");

        List<Settings> seen = [];

        using SettingsWatch watch = OnFile(file, seen);

        Settings read = watch.Reload();

        Assert.Equal(CursorShape.Bar, read.Cursor);
        Assert.Equal(CursorShape.Bar, Assert.Single(seen).Cursor);
        Assert.Equal(1, watch.Reloads);
    }

    /// <summary>
    /// A watch on a directory that cannot be watched is still a watch, and says what went wrong.
    ///
    /// <para>A settings file this client cannot watch is not a reason for a terminal not to open.
    /// </para>
    /// </summary>
    [Fact]
    public void AWatchThatCouldNotBeArmedStillWorksByHand()
    {
        string file = Path.Combine(_directory, "no", "such", "settings.json");

        List<Settings> seen = [];

        using SettingsWatch watch = OnFile(file, seen);

        // The file is not there, which is the defaults and never an error.
        Assert.Equal(Settings.Default.FontSize, watch.Reload().FontSize);
    }

    private static SettingsWatch OnFile(string file, List<Settings> seen) =>
        SettingsWatch.On(file, read =>
        {
            lock (seen)
            {
                seen.Add(read);
            }
        });

    /// <summary>Waits for the watch to have read something that satisfies this.</summary>
    private static Settings Until(List<Settings> seen, Func<Settings, bool> wanted)
    {
        DateTime giveUp = DateTime.UtcNow + Patience;

        while (DateTime.UtcNow < giveUp)
        {
            lock (seen)
            {
                if (seen.FirstOrDefault(wanted) is { } found)
                {
                    return found;
                }
            }

            Thread.Sleep(25);
        }

        Assert.Fail($"the watch never read what this test was waiting for in {Patience.TotalSeconds:F0}s");

        return Settings.Default;
    }
}
