using System.IO;
using System.Net.Sockets;
using System.Text;
using Quickshell.App;
using Quickshell.Terminal;
using Quickshell.Transport;
using Xunit;

namespace Quickshell.App.Tests;

/// <summary>
/// A saved session, connected from the store the way the window connects it (QS126), against the
/// fixture's real OpenSSH servers.
///
/// <para>Each test reads the store from a file and nothing else, so what is being shown is the path a
/// user takes after a restart: a session saved earlier, read back, connected, its shell parsed into a
/// model. The host key goes through a <see cref="TrustOnFirstUse"/> over a <c>known_hosts</c> of the
/// test's own, which is how the second connection is shown to ask nothing.</para>
/// </summary>
public sealed class RemoteShellTests : IDisposable
{
    private readonly string _here = Path.Combine(Path.GetTempPath(), $"quickshell-remote-{Guid.NewGuid():N}");

    public RemoteShellTests() => Directory.CreateDirectory(_here);

    private static CancellationToken Stop => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        if (Directory.Exists(_here))
        {
            Directory.Delete(_here, recursive: true);
        }
    }

    /// <summary>
    /// QS126's own criterion: a saved session connects after a restart. Saved to a file, read back
    /// by a store that knows nothing else, connected, and its shell answers; then again, as the next
    /// run would, and the host key it was asked about the first time is not asked about twice.
    /// </summary>
    [Fact]
    public async Task ASavedSessionConnectsAfterARestart()
    {
        SkipWithoutFixture();

        string store = Store("""
            { "Name": "", "Children": [
                { "Name": "web", "Host": "127.0.0.1", "Settings": { "User": "probe", "Port": 2222, "Key": "KEY" } }
            ] }
            """);

        int asked = 0;
        string knownHosts = Path.Combine(_here, "known_hosts");

        for (int run = 0; run < 2; run++)
        {
            TrustOnFirstUse trust = new(KnownHosts.ReadFrom(knownHosts), (_, _) =>
            {
                asked++;

                return ValueTask.FromResult(SshHostKeyVerdict.AcceptAndRemember);
            });

            ResolvedSession web = SessionTree.ReadFrom(store).Session("web")
                                  ?? throw new InvalidOperationException("the store lost its session");

            Emulator emulator = new(80, 25);

            await using RemoteShell shell = await RemoteShell.OpenAsync(web, trust, emulator, new DamageSignal(),
                                                                        80, 25, Stop);

            await shell.Pipeline.TypeAsync(Encoding.ASCII.GetBytes("hostname\r"), Stop);

            await Until(() => Screen(emulator).Contains("qs-sshd-target", StringComparison.Ordinal));
        }

        // Asked on the first run, remembered, and not asked on the second.
        Assert.Equal(1, asked);
    }

    /// <summary>
    /// A session with a jump host is reached through it: the target is a name only the container
    /// network resolves, so the shell that answers is one carried by the bastion.
    /// </summary>
    [Fact]
    public async Task ASessionWithAJumpHostIsReachedThroughIt()
    {
        SkipWithoutFixture();

        string store = Store("""
            { "Name": "", "Settings": { "User": "probe", "Key": "KEY", "JumpHost": "probe@127.0.0.1:2223" },
              "Children": [ { "Name": "inside", "Host": "qs-sshd-target" } ] }
            """);

        ResolvedSession inside = SessionTree.ReadFrom(store).Session("inside")!;
        TrustOnFirstUse trust = new(KnownHosts.ReadFrom(Path.Combine(_here, "known_hosts")),
                                    (_, _) => ValueTask.FromResult(SshHostKeyVerdict.Accept));
        Emulator emulator = new(80, 25);

        await using RemoteShell shell = await RemoteShell.OpenAsync(inside, trust, emulator, new DamageSignal(),
                                                                    80, 25, Stop);

        await shell.Pipeline.TypeAsync(Encoding.ASCII.GetBytes("hostname\r"), Stop);

        await Until(() => Screen(emulator).Contains("qs-sshd-target", StringComparison.Ordinal));
    }

    /// <summary>A jump host is written as OpenSSH writes one, and every part of it is optional but the host.</summary>
    [Theory]
    [InlineData("bastion.example", "me", "bastion.example", 22)]
    [InlineData("admin@bastion.example", "admin", "bastion.example", 22)]
    [InlineData("admin@bastion.example:2200", "admin", "bastion.example", 2200)]
    [InlineData("bastion.example:2200", "me", "bastion.example", 2200)]
    public void AJumpHostIsReadAsOpenSshWritesIt(string written, string user, string host, int port)
    {
        SshEndpoint through = RemoteShell.Through(written, "me");

        Assert.Equal((user, host, port), (through.User, through.Host, through.Port));
    }

    // ---- plumbing ----

    /// <summary>A store file, with the fixture's key where KEY is written.</summary>
    private string Store(string json)
    {
        string file = Path.Combine(_here, "sessions.json");

        File.WriteAllText(file, json.Replace("KEY", Key().Replace("\\", "\\\\", StringComparison.Ordinal),
                                             StringComparison.Ordinal));

        return file;
    }

    private static string Screen(Emulator emulator)
    {
        TerminalBuffer buffer = emulator.Buffer;
        StringBuilder text = new();
        Span<char> cell = stackalloc char[8];

        for (int row = 0; row < buffer.Rows; row++)
        {
            foreach (Cell glyph in buffer.Line((int)buffer.AbsoluteLine(row)))
            {
                text.Append(cell[..buffer.TextOf(glyph, cell)]);
            }

            text.Append('\n');
        }

        return text.ToString();
    }

    private static async Task Until(Func<bool> ready)
    {
        for (int wait = 0; wait < 150 && !ready(); wait++)
        {
            await Task.Delay(100, Stop);
        }

        Assert.True(ready(), "the remote shell never said what this was waiting for");
    }

    private static string Key() =>
        Path.Combine(RepositoryRoot(), "prototypes", "SshProbe", "fixture", "keys", "probe_ed25519");

    private static void SkipWithoutFixture()
    {
        bool up;

        try
        {
            using TcpClient probe = new();

            up = probe.ConnectAsync("127.0.0.1", 2222).Wait(TimeSpan.FromSeconds(2));
        }
        catch (Exception failure) when (failure is SocketException or AggregateException)
        {
            up = false;
        }

        Assert.SkipUnless(up && File.Exists(Key()),
            "nothing is listening on 127.0.0.1:2222: run prototypes/SshProbe/fixture/up.sh");
    }

    private static string RepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Quickshell.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Quickshell.sln was not found above the test.");
    }
}
