using System.Diagnostics;
using System.Net.Sockets;
using Quickshell.Transport;
using Xunit;

namespace Quickshell.Transport.Tests;

/// <summary>
/// QS138: the helper the server-reading tests share answers when the command has, and fails when it
/// has not — rather than waiting out its timer every time and answering either way.
/// </summary>
public sealed class RemoteShellTests
{
    private const string Host = "127.0.0.1";
    private const int Ordinary = 2222;

    private static CancellationToken Stop => TestContext.Current.CancellationToken;

    /// <summary>
    /// The line's falsification: a command that has printed its closing marker is not still being
    /// waited for. Before QS138 this took the whole twenty seconds, through a pseudo-terminal's CRLF.
    /// </summary>
    [Fact]
    public async Task ACommandThatHasAnsweredIsReadWithoutWaitingForTheTimer()
    {
        SkipWithout(Ordinary);

        await using SshNetTransport session = await Connected(Ordinary);

        Stopwatch clock = Stopwatch.StartNew();

        string printed = await RemoteShell.RunAsync(session, "printf 'one\\ntwo\\n'", Stop);

        Assert.Equal("one\ntwo", printed);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10),
                    $"an answer that had arrived was waited for {clock.Elapsed.TotalSeconds:0.0} seconds");
    }

    /// <summary>
    /// A command that has not finished when the wait runs out fails the test, rather than handing
    /// back what little it printed as though it were the answer.
    /// </summary>
    [Fact]
    public async Task ACommandStillRunningWhenTheWaitEndsIsAFailureAndNotAnAnswer()
    {
        SkipWithout(Ordinary);

        await using SshNetTransport session = await Connected(Ordinary);

        TimeoutException failed = await Assert.ThrowsAsync<TimeoutException>(() =>
            RemoteShell.RunAsync(session, "echo started; sleep 30", Stop, TimeSpan.FromSeconds(2)));

        Assert.Contains("no closing marker came in 2 seconds", failed.Message, StringComparison.Ordinal);
        Assert.Contains("sleep 30", failed.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A shell that ends before its command finishes is a failure too, and says which of the two it
    /// was: an exit is not a slow command.
    /// </summary>
    [Fact]
    public async Task AShellThatEndsBeforeTheCommandFinishesIsAFailure()
    {
        SkipWithout(Ordinary);

        await using SshNetTransport session = await Connected(Ordinary);

        TimeoutException failed = await Assert.ThrowsAsync<TimeoutException>(() =>
            RemoteShell.RunAsync(session, "exit 3", Stop, TimeSpan.FromSeconds(10)));

        Assert.Contains("'exit 3'", failed.Message, StringComparison.Ordinal);
    }

    private static async Task<SshNetTransport> Connected(int port)
    {
        SshNetTransport session = new();

        await session.ConnectAsync(SshEndpoint.For(Host, "probe", port), [Key()], Trusting, Stop);

        return session;
    }

    private static ValueTask<SshHostKeyVerdict> Trusting(SshEndpoint _, SshHostKey __,
                                                         CancellationToken ___) =>
        ValueTask.FromResult(SshHostKeyVerdict.Accept);

    private static SshCredential.PrivateKey Key() =>
        new(Path.Combine(RepositoryRoot(), "prototypes", "SshProbe", "fixture", "keys",
                         "probe_ed25519"));

    private static void SkipWithout(int port)
    {
        bool up;

        try
        {
            using TcpClient probe = new();

            up = probe.ConnectAsync(Host, port).Wait(TimeSpan.FromSeconds(2));
        }
        catch (Exception)
        {
            up = false;
        }

        Assert.SkipUnless(up, $"nothing is listening on 127.0.0.1:{port}: run prototypes/SshProbe/fixture/up.sh");
    }

    private static string RepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Quickshell.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);

        return directory.FullName;
    }
}
