using Quickshell.Gate;
using Xunit;

namespace Quickshell.Gate.Tests;

/// <summary>
/// QS200: the replay arms run on the client being archived. What is checked here is the folder the
/// arms run in — every file both carry is the client's, the harness adds only what the client lacks,
/// and the client's runtime settings are the harness's — which is what makes a setting that exists
/// only in the client's publish reach the figures.
/// </summary>
public sealed class BenchTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"quickshell-bench-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void TheArmsRunOnTheClientsBytesAndUnderItsSettings()
    {
        string client = Folder("client",
                               ("quickshell.exe", "client host"),
                               ("quickshell.runtimeconfig.json", """{"configProperties":{"System.Runtime.TieredPGO":false}}"""),
                               ("Quickshell.Terminal.dll", "shipped terminal"),
                               (Path.Combine("runtimes", "native.dll"), "shipped native"));

        string harness = Folder("harness",
                                ("Quickshell.Replay.exe", "harness host"),
                                ("Quickshell.Replay.dll", "harness"),
                                ("Quickshell.Replay.runtimeconfig.json", """{"configProperties":{}}"""),
                                ("Quickshell.Terminal.dll", "the harness's own build"),
                                (Path.Combine("runtimes", "native.dll"), "the harness's own native"));

        string bench = Path.Combine(_directory, "bench");

        string replay = Bench.Assemble(Path.Combine(client, "quickshell.exe"), harness, bench);

        Assert.Equal(Path.Combine(bench, "Quickshell.Replay.exe"), replay);
        Assert.Equal("harness host", Read(bench, "Quickshell.Replay.exe"));
        Assert.Equal("harness", Read(bench, "Quickshell.Replay.dll"));
        Assert.Equal("shipped terminal", Read(bench, "Quickshell.Terminal.dll"));
        Assert.Equal("shipped native", Read(bench, Path.Combine("runtimes", "native.dll")));
        Assert.Equal(Read(client, "quickshell.runtimeconfig.json"), Read(bench, "Quickshell.Replay.runtimeconfig.json"));
    }

    /// <summary>A file the last client carried and this one does not is not loaded as though it shipped.</summary>
    [Fact]
    public void ALeftoverFromAnEarlierClientIsGone()
    {
        string client = Folder("client", ("quickshell.exe", "host"), ("quickshell.runtimeconfig.json", "{}"));
        string harness = Folder("harness", ("Quickshell.Replay.exe", "host"));
        string bench = Folder("bench", ("Quickshell.Removed.dll", "from an earlier release"));

        Bench.Assemble(Path.Combine(client, "quickshell.exe"), harness, bench);

        Assert.False(File.Exists(Path.Combine(bench, "Quickshell.Removed.dll")));
    }

    /// <summary>Without the client's settings the arms would run under the harness's own, which is the gap itself.</summary>
    [Fact]
    public void AClientWithNoRuntimeSettingsIsRefused()
    {
        string client = Folder("client", ("quickshell.exe", "host"));
        string harness = Folder("harness", ("Quickshell.Replay.exe", "host"));

        GateRefusal refused = Assert.Throws<GateRefusal>(
            () => Bench.Assemble(Path.Combine(client, "quickshell.exe"), harness, Path.Combine(_directory, "bench")));

        Assert.Contains("runtimeconfig", refused.Message, StringComparison.Ordinal);
    }

    private string Folder(string name, params (string Path, string Text)[] files)
    {
        string folder = Path.Combine(_directory, name);

        foreach ((string path, string text) in files)
        {
            string file = Path.Combine(folder, path);

            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, text);
        }

        return folder;
    }

    private static string Read(string folder, string file) => File.ReadAllText(Path.Combine(folder, file));
}
