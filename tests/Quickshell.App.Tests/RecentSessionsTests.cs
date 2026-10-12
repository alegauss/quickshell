using System.Globalization;
using System.IO;
using Quickshell.App;
using Xunit;

namespace Quickshell.App.Tests;

/// <summary>
/// The sessions connected last (QS279): kept in a file across restarts, carried by a rename and
/// dropped by a delete, and said as a card says when.
/// </summary>
public sealed class RecentSessionsTests : IDisposable
{
    private static readonly DateTimeOffset Noon = new(2026, 10, 12, 12, 0, 0, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 10, 12)));

    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"quickshell-recent-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private string File => Path.Combine(_directory, "recent.json");

    /// <summary>What one list wrote, another read of the file has, newest first and each path once.</summary>
    [Fact]
    public void TheListOutlivesWhatWroteIt()
    {
        RecentSessions written = RecentSessions.ReadFrom(File);

        written.Connected("Work/db", Noon);
        written.Connected("pi", Noon.AddMinutes(1));
        written.Connected("Work/db", Noon.AddMinutes(2));

        RecentSessions read = RecentSessions.ReadFrom(File);

        Assert.Equal(["Work/db", "pi"], read.Paths);
        Assert.Equal(Noon.AddMinutes(2), read.Sessions[0].At);
    }

    /// <summary>It keeps twenty, the newest.</summary>
    [Fact]
    public void ItKeepsTwenty()
    {
        RecentSessions list = RecentSessions.ReadFrom(File);

        for (int each = 0; each < 25; each++)
        {
            list.Connected(string.Create(CultureInfo.InvariantCulture, $"s{each}"), Noon.AddMinutes(each));
        }

        IReadOnlyList<string> kept = RecentSessions.ReadFrom(File).Paths;

        Assert.Equal(RecentSessions.Kept, kept.Count);
        Assert.Equal("s24", kept[0]);
        Assert.DoesNotContain("s4", kept);
    }

    /// <summary>A folder renamed carries every session under it; one deleted takes them all, and nothing outside it.</summary>
    [Fact]
    public void AFolderCarriesWhatIsUnderIt()
    {
        RecentSessions list = RecentSessions.ReadFrom(File);

        list.Connected("Work/db", Noon);
        list.Connected("Work/web", Noon.AddMinutes(1));
        list.Connected("Workshop/pi", Noon.AddMinutes(2));

        list.Moved("Work", "Office");

        Assert.Equal(["Workshop/pi", "Office/web", "Office/db"], RecentSessions.ReadFrom(File).Paths);

        list.Dropped("Office");

        Assert.Equal(["Workshop/pi"], RecentSessions.ReadFrom(File).Paths);
    }

    /// <summary>A file that will not read is an empty list, not a refusal: a lost ranking is not worth a dialog.</summary>
    [Fact]
    public void AnUnreadableFileIsAnEmptyList()
    {
        Directory.CreateDirectory(_directory);
        System.IO.File.WriteAllText(File, "{ not json");

        Assert.Empty(RecentSessions.ReadFrom(File).Sessions);
    }

    /// <summary>When, in a card's words.</summary>
    [Theory]
    [InlineData(0, false, "Today")]
    [InlineData(0, true, "Connected now")]
    [InlineData(1, false, "Yesterday")]
    [InlineData(3, false, "Last week")]
    [InlineData(7, false, "Last week")]
    public void WhenIsSaidInWords(int daysAgo, bool open, string said) =>
        Assert.Equal(said, RecentSessions.When(Noon.AddDays(-daysAgo), Noon, open));

    /// <summary>Past a week it is the date, with the year only where it is not this one.</summary>
    [Fact]
    public void PastAWeekItIsTheDate()
    {
        Assert.Equal(Noon.AddDays(-30).ToString("d MMMM", CultureInfo.CurrentCulture), RecentSessions.When(Noon.AddDays(-30), Noon, open: false));
        Assert.Equal(Noon.AddDays(-400).ToString("d MMMM yyyy", CultureInfo.CurrentCulture), RecentSessions.When(Noon.AddDays(-400), Noon, open: false));
    }
}
