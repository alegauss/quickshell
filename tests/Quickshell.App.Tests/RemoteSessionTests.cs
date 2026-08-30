using System.Text;
using Quickshell.App;
using Quickshell.Terminal;
using Quickshell.Transport;
using Xunit;

namespace Quickshell.App.Tests;

/// <summary>
/// A session that survives its connection, tested with no server anywhere — which is what
/// <see cref="ReplayTransport"/> was put in the shipping assembly for.
///
/// <para>Every drop here is deliberate and instant. Against a real network these are the tests
/// nobody writes, because arranging a ten-second outage on demand is somebody standing beside a
/// switch.</para>
/// </summary>
public sealed class RemoteSessionTests
{
    private static readonly SshEndpoint Somewhere = SshEndpoint.For("host.example", "user");

    private static readonly IReadOnlyList<SshCredential> APassword =
        [new SshCredential.Password("hunter2")];

    /// <summary>Fast enough that a test does not wait out a real backoff, and still a real schedule.</summary>
    private static readonly ReconnectPolicy Quick = new()
    {
        Enabled = true,
        First = TimeSpan.FromMilliseconds(20),
        Ceiling = TimeSpan.FromMilliseconds(80),
        MaximumAttempts = 4,
    };

    private static CancellationToken Stop => TestContext.Current.CancellationToken;

    // ---- The schedule: bounded, predictable, and it stops ----

    [Fact]
    public void TheDelayDoublesAndThenHoldsAtTheCeiling()
    {
        ReconnectPolicy policy = new()
        {
            Enabled = true,
            First = TimeSpan.FromSeconds(1),
            Ceiling = TimeSpan.FromSeconds(8),
        };

        Assert.Equal(TimeSpan.FromSeconds(1), policy.Delay(1));
        Assert.Equal(TimeSpan.FromSeconds(2), policy.Delay(2));
        Assert.Equal(TimeSpan.FromSeconds(4), policy.Delay(3));
        Assert.Equal(TimeSpan.FromSeconds(8), policy.Delay(4));

        // And it holds there rather than growing without bound, however many have failed.
        Assert.Equal(TimeSpan.FromSeconds(8), policy.Delay(5));
        Assert.Equal(TimeSpan.FromSeconds(8), policy.Delay(40));
        Assert.Equal(TimeSpan.FromSeconds(8), policy.Delay(4000));
    }

    /// <summary>A schedule that depends on how it got here is one nobody can predict from outside.</summary>
    [Fact]
    public void TheDelayIsAFunctionOfTheAttemptAndNothingElse()
    {
        ReconnectPolicy policy = ReconnectPolicy.Default;

        for (int attempt = 1; attempt < 12; attempt++)
        {
            Assert.Equal(policy.Delay(attempt), policy.Delay(attempt));
            Assert.True(policy.Delay(attempt) <= policy.Ceiling);
        }

        Assert.Throws<ArgumentOutOfRangeException>(() => policy.Delay(0));
    }

    /// <summary>Off is the default, because an unexpected new login is itself an event on some hosts.</summary>
    [Fact]
    public void ReconnectingIsOffUntilSomebodyAsksForIt()
    {
        Assert.False(ReconnectPolicy.Off.Enabled);
        Assert.True(ReconnectPolicy.Default.Enabled);
    }

    // ---- The drop, and what comes back ----

    /// <summary>
    /// The symptom, gone: a link that drops does not cost the session. The scrollback is still
    /// there, and a second connection was made without anybody asking for one.
    /// </summary>
    [Fact]
    public async Task ADroppedLinkCostsACommandAndNotTheScrollback()
    {
        List<ReplayTransport> made = [];

        await using RemoteSession session = RemoteSession.Start(
            _ => Connect(made, "before the drop\r\n"), new Emulator(80, 25), Quick);

        await Until(() => session.Status.IsLive);
        await Until(() => Screen(session).Contains("before the drop", StringComparison.Ordinal));

        made[0].Drop("the network went away");

        // A second connection, and the same model underneath it.
        await Until(() => session.Connections >= 2);
        await Until(() => session.Status.IsLive);

        Assert.Contains("before the drop", Screen(session), StringComparison.Ordinal);
        Assert.Equal(2, made.Count);

        session.Stop();
    }

    /// <summary>
    /// The falsification the design names, and the one that is easy to get wrong by being generous:
    /// a reconnect must not claim to restore state the protocol cannot restore.
    ///
    /// <para>What is asserted is that the second connection is genuinely a second connection — its
    /// own transport, its own shell, nothing carried across — while the model that holds what the
    /// user has read is the same object throughout.</para>
    /// </summary>
    [Fact]
    public async Task AReconnectRestoresTheScrollbackAndClaimsNothingElse()
    {
        List<ReplayTransport> made = [];
        Emulator emulator = new(80, 25);

        await using RemoteSession session = RemoteSession.Start(
            _ => Connect(made, "first\r\n"), emulator, Quick);

        await Until(() => session.Status.IsLive);
        await Until(() => Screen(session).Contains("first", StringComparison.Ordinal));

        made[0].Drop("gone");

        await Until(() => session.Connections >= 2);

        // The same model, kept: this is the object the scrollback lives in.
        Assert.Same(emulator, session.Emulator);

        // And a different connection under it, with nothing carried over. The far end is new, which
        // is exactly why the working directory and anything that was running are gone — a claim this
        // session makes by having a new transport rather than by asserting anything about the shell.
        Assert.NotSame(made[0], made[1]);
        Assert.False(made[0].IsConnected);
        Assert.True(made[1].Written.IsEmpty, "the new connection was handed the old one's keystrokes");

        session.Stop();
    }

    /// <summary>
    /// A shell that exited is not a link that dropped. Reconnecting there would be a new login
    /// nobody asked for, on a host where that may be the thing being watched for.
    /// </summary>
    [Fact]
    public async Task AShellThatExitedIsNotReconnected()
    {
        List<ReplayTransport> made = [];

        await using RemoteSession session = RemoteSession.Start(
            _ => Connect(made, "bye\r\n"), new Emulator(80, 25), Quick);

        await Until(() => session.Status.IsLive);

        // Disposing the channel is a clean close carrying an exit code, which is what `exit` does.
        await made[0].DisposeAsync();

        await session.Completed.WaitAsync(TimeSpan.FromSeconds(5), Stop);

        Assert.Equal(SessionState.Ended, session.Status.State);
        Assert.Single(made);
    }

    /// <summary>
    /// A credential a server refused will not be accepted by asking again, and asking again is how a
    /// client locks an account out.
    /// </summary>
    [Fact]
    public async Task ARefusedCredentialIsNotRetried()
    {
        int attempts = 0;

        await using RemoteSession session = RemoteSession.Start(
            async token =>
            {
                attempts++;

                ReplayTransport transport =
                    ReplayTransport.Refusing(SshFailureKind.Authentication, "that key is not welcome");

                await transport.ConnectAsync(Somewhere, APassword, Trusting, token);

                return transport;
            },
            new Emulator(80, 25), Quick);

        await session.Completed.WaitAsync(TimeSpan.FromSeconds(5), Stop);

        Assert.Equal(1, attempts);
        Assert.Equal(SessionState.Ended, session.Status.State);
        Assert.Contains("not welcome", session.Status.Reason, StringComparison.Ordinal);
    }

    /// <summary>A host that is simply not there is retried, and then given up on by name.</summary>
    [Fact]
    public async Task AnUnreachableHostIsRetriedUpToTheCapAndThenGivenUpOn()
    {
        int attempts = 0;

        await using RemoteSession session = RemoteSession.Start(
            async token =>
            {
                attempts++;

                ReplayTransport transport =
                    ReplayTransport.Refusing(SshFailureKind.Unreachable, "nothing is listening");

                await transport.ConnectAsync(Somewhere, APassword, Trusting, token);

                return transport;
            },
            new Emulator(80, 25), Quick);

        await session.Completed.WaitAsync(TimeSpan.FromSeconds(10), Stop);

        Assert.Equal(Quick.MaximumAttempts, attempts);
        Assert.Equal(SessionState.Ended, session.Status.State);
        Assert.Contains("nothing is listening", session.Status.Reason, StringComparison.Ordinal);
    }

    /// <summary>With the policy off, one failure is the end of it.</summary>
    [Fact]
    public async Task WithReconnectingOffADropIsTheEnd()
    {
        List<ReplayTransport> made = [];

        await using RemoteSession session = RemoteSession.Start(
            _ => Connect(made, "hello\r\n"), new Emulator(80, 25), ReconnectPolicy.Off);

        await Until(() => session.Status.IsLive);

        made[0].Drop("gone");

        await session.Completed.WaitAsync(TimeSpan.FromSeconds(5), Stop);

        Assert.Equal(SessionState.Ended, session.Status.State);
        Assert.Single(made);
    }

    /// <summary>
    /// While there is no connection, a keystroke is refused rather than queued. Held across a
    /// reconnect it would arrive at a shell the user was not typing at, in an order nobody chose.
    /// </summary>
    [Fact]
    public async Task AKeystrokeWithNoConnectionIsRefusedRatherThanHeld()
    {
        List<ReplayTransport> made = [];

        await using RemoteSession session = RemoteSession.Start(
            async token =>
            {
                // Slow enough that there is a window with no pipeline in it to type into.
                await Task.Delay(150, token);

                return await Connect(made, "up\r\n");
            },
            new Emulator(80, 25), ReconnectPolicy.Off);

        Assert.False(await session.TypeAsync(Encoding.ASCII.GetBytes("ls"), Stop));

        await Until(() => session.Status.IsLive);

        Assert.True(await session.TypeAsync(Encoding.ASCII.GetBytes("ls"), Stop));

        session.Stop();
    }

    /// <summary>Stopping ends it, whatever the policy would otherwise have done next.</summary>
    [Fact]
    public async Task StoppingEndsASessionThatWouldOtherwiseKeepTrying()
    {
        await using RemoteSession session = RemoteSession.Start(
            async token =>
            {
                ReplayTransport transport =
                    ReplayTransport.Refusing(SshFailureKind.Unreachable, "nothing is listening");

                await transport.ConnectAsync(Somewhere, APassword, Trusting, token);

                return transport;
            },
            new Emulator(80, 25),
            new ReconnectPolicy
            {
                Enabled = true,
                First = TimeSpan.FromSeconds(30),
                Ceiling = TimeSpan.FromSeconds(30),
                MaximumAttempts = 1000,
            });

        await Until(() => session.Status.State == SessionState.Waiting);

        Assert.True(session.Status.NextIn > TimeSpan.Zero, "a waiting session did not say when");
        Assert.True(session.Status.Attempt > 0, "a waiting session did not say which attempt");

        session.Stop();

        await session.Completed.WaitAsync(TimeSpan.FromSeconds(5), Stop);

        Assert.Equal(SessionState.Ended, session.Status.State);
    }

    // ---- plumbing ----

    private static ValueTask<SshHostKeyVerdict> Trusting(SshEndpoint _, SshHostKey __, CancellationToken ___) =>
        ValueTask.FromResult(SshHostKeyVerdict.Accept);

    /// <summary>A connected replay transport, remembered so a test can drop it.</summary>
    private static async ValueTask<ISshTransport> Connect(List<ReplayTransport> made, string recording)
    {
        ReplayTransport transport = ReplayTransport.Replaying(Encoding.UTF8.GetBytes(recording));

        await transport.ConnectAsync(Somewhere, APassword, Trusting, CancellationToken.None);

        made.Add(transport);

        return transport;
    }

    /// <summary>What is on the screen, as text.</summary>
    private static string Screen(RemoteSession session)
    {
        TerminalBuffer buffer = session.Emulator.Buffer;
        StringBuilder text = new();
        Span<char> cell = stackalloc char[8];

        for (int row = 0; row < buffer.Rows; row++)
        {
            foreach (Cell glyph in buffer.Line((int)buffer.AbsoluteLine(row)))
            {
                int written = buffer.TextOf(glyph, cell);

                text.Append(cell[..written]);
            }

            text.Append('\n');
        }

        return text.ToString();
    }

    private static async Task Until(Func<bool> ready)
    {
        for (int wait = 0; wait < 400 && !ready(); wait++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Assert.True(ready(), "the session never reached the state this was waiting for");
    }
}
