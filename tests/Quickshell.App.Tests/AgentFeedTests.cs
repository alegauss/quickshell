using System.IO;
using System.Text;
using System.Text.Json.Nodes;

using Xunit;

namespace Quickshell.App.Tests;

/// <summary>QS335: each gateway call written as terminal output, and the file read back as it grows.</summary>
public sealed class AgentFeedTests : IDisposable
{
    private readonly string _here = Path.Combine(Path.GetTempPath(), "qs-feed-" + Guid.NewGuid().ToString("N"));

    private static CancellationToken Stop => TestContext.Current.CancellationToken;

    public void Dispose() => Directory.Delete(_here, recursive: true);

    /// <summary>A call's prompt line, its output and its end, with a secret in the command hidden as the agent's answers hide it.</summary>
    [Fact]
    public void ACallIsWrittenAsAPromptItsOutputAndItsEnd()
    {
        AgentFeed feed = AgentFeed.Start(_here, "run-1");

        feed.Begin("exec", new JsonObject { ["host"] = "Work/db", ["argv"] = new JsonArray("sh", "-c", "TOKEN=ghp_0123456789abcdefghijABCDEFGHIJ0123456789 ./deploy") });
        feed.End(new JsonObject { ["exitCode"] = 0, ["stdout"] = "deployed\n", ["stderr"] = "warning: slow\n" }, failed: false);
        feed.Begin("exec", new JsonObject { ["host"] = "nowhere", ["argv"] = new JsonArray("id") });
        feed.End(new JsonObject { ["error"] = "There is no saved session nowhere." }, failed: true);

        string text = File.ReadAllText(feed.File);

        Assert.Contains("]0;agent run-1", text, StringComparison.Ordinal);
        Assert.Contains("Work/db", text, StringComparison.Ordinal);
        Assert.Contains("deployed\r\n", text, StringComparison.Ordinal);
        Assert.Contains("warning: slow\r\n", text, StringComparison.Ordinal);
        Assert.Contains("[exit 0]", text, StringComparison.Ordinal);
        Assert.Contains("refused: There is no saved session nowhere.", text, StringComparison.Ordinal);
        Assert.DoesNotContain("ghp_0123456789", text, StringComparison.Ordinal);
    }

    /// <summary>The tab's channel reads what is there, waits, and reads what is appended after it opened.</summary>
    [Fact]
    public async Task TheChannelTailsTheFileAsItGrows()
    {
        Directory.CreateDirectory(_here);

        string path = Path.Combine(_here, "feed.vt");

        await File.WriteAllTextAsync(path, "first\r\n", Stop);

        await using FeedChannel channel = new(path, 80, 24);
        byte[] buffer = new byte[256];

        int read = await channel.ReadAsync(buffer, Stop);

        Assert.Equal("first\r\n", Encoding.UTF8.GetString(buffer, 0, read));

        Task<int> waiting = channel.ReadAsync(buffer, Stop).AsTask();

        await Task.Delay(300, Stop);
        Assert.False(waiting.IsCompleted);

        await File.AppendAllTextAsync(path, "second\r\n", Stop);

        Assert.Equal("second\r\n", Encoding.UTF8.GetString(buffer, 0, await waiting.WaitAsync(TimeSpan.FromSeconds(5), Stop)));

        // Keys go nowhere: the tab is read-only.
        await channel.WriteAsync("rm -rf /\r"u8.ToArray(), Stop);
        Assert.Equal("first\r\nsecond\r\n", await File.ReadAllTextAsync(path, Stop));
    }
}
