using System.IO;
using System.Text.Json;
using Quickshell.App;
using Xunit;

namespace Quickshell.App.Tests;

/// <summary>
/// QS75's instrument: what a timed start writes, and that it writes it once.
///
/// <para>The figure itself is a measurement of the desk, taken by <c>tools/Quickshell.Startup</c>
/// and kept in <c>benchmarks/results/startup-h.md</c>. What is checked here is the part a harness
/// relies on: the report is whole when it appears, every milestone is its first time, and the last
/// one is the one the budget names.</para>
/// </summary>
public sealed class StartupTimelineTests
{
    /// <summary>
    /// Armed once in the process, as the client is: marks land in the order they happened, a repeated
    /// milestone keeps its first time, and a second interactive frame writes nothing more.
    /// </summary>
    [Fact]
    public void ATimedStartWritesItsMilestonesOnceInTheOrderTheyHappened()
    {
        string report = Path.Combine(Path.GetTempPath(), $"qs75-{Guid.NewGuid():N}.json");

        try
        {
            StartupTimeline.Arm(report);

            StartupTimeline.Mark("main");
            Thread.Sleep(5);
            StartupTimeline.Mark("shown");

            Dictionary<string, double> none = File.Exists(report) ? Read(report) : [];

            StartupTimeline.Mark("main");
            StartupTimeline.Interactive();

            Dictionary<string, double> first = Read(report);

            StartupTimeline.Mark("late");
            StartupTimeline.Interactive();

            Dictionary<string, double> after = Read(report);

            Assert.Empty(none);

            // The ones this test marked, in its order. A frame from another test's pane may land a
            // milestone of its own in between, which is the timeline doing its job in a shared
            // process and not a reason for this to fail.
            Assert.Equal(["main", "shown"], first.Keys.Where(key => key is "main" or "shown"));
            Assert.Equal("interactive", first.Keys.Last());
            Assert.True(first["main"] < first["shown"], "a later milestone was recorded earlier");
            Assert.True(first["main"] > 0, "a start was measured from somewhere after it began");
            Assert.Equal(first, after);
            Assert.False(StartupTimeline.Waiting, "a written report still reads as waiting");
            Assert.False(File.Exists(report + ".partial"), "the report was left half-written beside itself");
        }
        finally
        {
            File.Delete(report);
        }
    }

    private static Dictionary<string, double> Read(string report) =>
        JsonSerializer.Deserialize<Dictionary<string, double>>(File.ReadAllText(report)) ?? [];
}
