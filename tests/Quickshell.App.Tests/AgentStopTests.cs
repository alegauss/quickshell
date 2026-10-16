using System.IO;
using System.Text.Json.Nodes;

using Quickshell.Transport;

using Xunit;

namespace Quickshell.App.Tests;

/// <summary>
/// QS334: one act that stops every agent: jobs cancelled on every session the gateway holds, its
/// connections closed, and every call refused with the reason until the person lets agents run again.
/// </summary>
public sealed class AgentStopTests : IDisposable
{
    private readonly string _here = Path.Combine(Path.GetTempPath(), "qs-stop-" + Guid.NewGuid().ToString("N"));

    public AgentStopTests() => Directory.CreateDirectory(_here);

    private static CancellationToken Stop => TestContext.Current.CancellationToken;

    private string StopFile => Path.Combine(_here, "agents-stopped");

    public void Dispose() => Directory.Delete(_here, recursive: true);

    /// <summary>
    /// Stopped: the open connection is swept for jobs and closed, the next call is refused saying the
    /// person stopped it, and once the file is gone calls run again on a new connection.
    /// </summary>
    [Fact]
    public async Task AStopSweepsClosesAndRefusesUntilLifted()
    {
        Connection connection = new();

        await using AgentGateway gateway = new(Store, (_, _) => ValueTask.FromResult<IGatewayConnection>(connection), "1.2.3") { StopFile = StopFile };

        await gateway.AnswerAsync(Exec(1), Stop);

        await File.WriteAllTextAsync(StopFile, "now", Stop);
        await gateway.StopAsync();

        Assert.Contains(connection.Commands, command => command.Contains("/.quickshell/jobs\"/*/", StringComparison.Ordinal));
        Assert.True(connection.Disposed);

        JsonObject refused = (await gateway.AnswerAsync(Exec(2), Stop))!;

        Assert.True(refused["result"]!["isError"]!.GetValue<bool>());
        Assert.True(refused["result"]!["structuredContent"]!["stopped"]!.GetValue<bool>());
        Assert.Contains("stopped every agent", refused["result"]!["structuredContent"]!["error"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal(1, gateway.Opened);

        File.Delete(StopFile);

        JsonObject again = (await gateway.AnswerAsync(Exec(3), Stop))!;

        Assert.False(again["result"]!["isError"]!.GetValue<bool>());
        Assert.Equal(2, gateway.Opened);
    }

    /// <summary>A stop file found at a call is swept there too, for a gateway whose watcher missed it.</summary>
    [Fact]
    public async Task AStopFoundAtACallIsSweptThen()
    {
        Connection connection = new();

        await using AgentGateway gateway = new(Store, (_, _) => ValueTask.FromResult<IGatewayConnection>(connection), "1.2.3") { StopFile = StopFile };

        await gateway.AnswerAsync(Exec(1), Stop);
        await File.WriteAllTextAsync(StopFile, "now", Stop);
        await gateway.AnswerAsync(Exec(2), Stop);

        Assert.True(connection.Disposed);
    }

    /// <summary>The window's chord writes the file every gateway watches, and the palette's command removes it.</summary>
    [Fact]
    public void TheWindowStopsAndLetsAgentsRun()
    {
        List<string> said = [];

        Sta.Run(() =>
        {
            MainWindow window = new() { AgentStopFile = StopFile, Tells = said.Add };

            window.StopAgents();
            Assert.True(File.Exists(StopFile));

            window.LetAgentsRun();
            Assert.False(File.Exists(StopFile));

            return true;
        });

        Assert.StartsWith("Every agent is stopped.", said[0], StringComparison.Ordinal);
    }

    /// <summary>
    /// Against the Linux fixture: three jobs on two sessions, one stop, every one gone from the server's
    /// process list within the grace, and the next exec refused with the reason.
    /// </summary>
    [Fact]
    public async Task OnAPosixServerAStopEndsEveryJob()
    {
        SshFixture.SkipWithoutIt();

        string key = Path.Combine(Repository.Root, "prototypes", "SshProbe", "fixture", "keys", "probe_ed25519");
        SessionTree store = SessionTree.Of(new SessionNode
        {
            Children =
            [
                new SessionNode { Name = "one", Host = "127.0.0.1", Settings = new SessionSettings { User = "probe", Port = 2222, Key = key } },
                new SessionNode { Name = "two", Host = "127.0.0.1", Settings = new SessionSettings { User = "probe", Port = 2222, Key = key } },
            ],
        });
        TrustOnFirstUse trust = new(KnownHosts.ReadFrom(Path.Combine(_here, "known_hosts")), (_, _) => ValueTask.FromResult(SshHostKeyVerdict.Accept));

        await using AgentGateway gateway = new(() => store, (session, token) => AgentGateway.Connect(session, trust, token), "test") { StopFile = StopFile };

        foreach ((int id, string host, string marker) in ((int, string, string)[])[(1, "one", "301"), (2, "one", "302"), (3, "two", "303")])
        {
            JsonObject started = (await gateway.AnswerAsync(Call(id, "job_start", new JsonObject
            {
                ["host"] = host, ["argv"] = new JsonArray("sh", "-c", $"sleep {marker} & sleep {marker}; wait"),
            }), Stop))!;

            Assert.False(started["result"]!["isError"]!.GetValue<bool>(), started.ToJsonString());
        }

        Assert.Equal("3", (await Running(trust, store)).Trim());

        await File.WriteAllTextAsync(StopFile, "now", Stop);

        System.Diagnostics.Stopwatch took = System.Diagnostics.Stopwatch.StartNew();
        IReadOnlyList<string> cancelled = await gateway.StopAsync();

        Assert.Equal(3, cancelled.Count);
        Assert.True(took.Elapsed < TimeSpan.FromSeconds(15), $"the stop took {took.Elapsed.TotalSeconds:0} s");
        Assert.Equal("0", (await Running(trust, store)).Trim());

        JsonObject refused = (await gateway.AnswerAsync(Call(4, "exec", new JsonObject { ["host"] = "one", ["argv"] = new JsonArray("id") }), Stop))!;

        Assert.True(refused["result"]!["structuredContent"]!["stopped"]!.GetValue<bool>());
    }

    /// <summary>How many of the jobs' sleeps the server is still running, asked on a connection of the test's own.</summary>
    private static async Task<string> Running(TrustOnFirstUse trust, SessionTree store)
    {
        await using IGatewayConnection look = await AgentGateway.Connect(store.Session("one")!, trust, Stop);

        // The parent shells counted once each, by their own command line.
        CommandResult counted = await look.RunAsync("pgrep -u probe -f '^sh -c sleep 30[123]' | wc -l", TimeSpan.FromSeconds(10), Stop);

        return counted.Output;
    }

    private static string Exec(int id) => Call(id, "exec", new JsonObject { ["host"] = "db", ["argv"] = new JsonArray("uptime") });

    private static string Call(int id, string tool, JsonObject arguments) => new JsonObject
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id,
        ["method"] = "tools/call",
        ["params"] = new JsonObject { ["name"] = tool, ["arguments"] = arguments },
    }.ToJsonString();

    private static SessionTree Store() => SessionTree.Of(new SessionNode { Children = [new SessionNode { Name = "db", Host = "db1.example.invalid" }] });

    private sealed class Connection : IGatewayConnection
    {
        public List<string> Commands { get; } = [];

        public bool Disposed { get; private set; }

        public bool IsConnected => !Disposed;

        public string? ServerVersion => "SSH-2.0-OpenSSH_9.6";

        public ValueTask<CommandResult> RunAsync(string command, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            Commands.Add(command);

            return ValueTask.FromResult(new CommandResult(0, string.Empty, string.Empty));
        }

        public ValueTask<IFileTransferChannel> FilesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask DisposeAsync()
        {
            Disposed = true;

            return ValueTask.CompletedTask;
        }
    }
}
