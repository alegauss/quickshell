using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using Quickshell.Transport;
using Xunit;

namespace Quickshell.Transport.Tests;

/// <summary>
/// The remote channel against a real OpenSSH server. Nothing here is mocked and nothing is
/// simulated: QS5 built the fixture — two containers, four accounts, a certificate authority — and
/// this is the line that finally connects the client to it.
///
/// <para><b>Everything skips when the fixture is down</b>, named rather than silently green:
/// <c>prototypes/SshProbe/fixture/up.sh</c> brings it up. A remote test that passed with no server
/// would be asserting about nothing at all.</para>
/// </summary>
public sealed class SshNetTransportTests
{
    private const string Host = "127.0.0.1";
    private const int TargetPort = 2222;

    private static readonly SshEndpoint Target = SshEndpoint.For(Host, "probe", TargetPort);

    private static CancellationToken Stop => TestContext.Current.CancellationToken;

    /// <summary>Trusts whatever the fixture presents, which is what a test against a fixture means.</summary>
    private static ValueTask<SshHostKeyVerdict> Trusting(SshEndpoint _, SshHostKey __, CancellationToken ___) =>
        ValueTask.FromResult(SshHostKeyVerdict.Accept);

    // ---- The symptom: a remote host's bytes ----

    /// <summary>
    /// The line's whole claim: something a remote host printed arrives through the same four members
    /// a local shell arrives through.
    /// </summary>
    [Fact]
    public async Task ARemoteHostsOutputArrivesThroughTheSameChannelALocalShellDoes()
    {
        SkipWithoutFixture();

        await using ISshTransport transport = await Connected();
        await using IPtyChannel channel = await transport.OpenShellAsync(80, 25, Stop);

        Assert.Equal((80, 25), channel.Size);

        await Type(channel, "echo quickshell-was-here");

        Assert.Contains("quickshell-was-here", await Until(channel, "quickshell-was-here"),
                        StringComparison.Ordinal);
    }

    /// <summary>
    /// The server was told the window's size, and it believes it. <c>stty size</c> is the server's
    /// own answer, so this is the pty request having actually carried the geometry rather than the
    /// client remembering what it asked for.
    /// </summary>
    [Fact]
    public async Task TheServerBelievesTheGeometryTheClientAskedFor()
    {
        SkipWithoutFixture();

        await using ISshTransport transport = await Connected();
        await using IPtyChannel channel = await transport.OpenShellAsync(120, 40, Stop);

        await Type(channel, "stty size");

        Assert.Contains("40 120", await Until(channel, "40 120"), StringComparison.Ordinal);
    }

    /// <summary>
    /// And a resize reaches it too, which is what makes a full-screen program redraw when a window
    /// is dragged.
    /// </summary>
    [Fact]
    public async Task AResizeReachesTheServerAndNotJustTheClientsMemory()
    {
        SkipWithoutFixture();

        await using ISshTransport transport = await Connected();
        await using IPtyChannel channel = await transport.OpenShellAsync(80, 25, Stop);

        await Type(channel, "stty size");
        await Until(channel, "25 80");

        channel.Resize(132, 43);

        Assert.Equal((132, 43), channel.Size);

        await Type(channel, "stty size");

        Assert.Contains("43 132", await Until(channel, "43 132"), StringComparison.Ordinal);
    }

    /// <summary>
    /// A shell that exits ends the channel, and the reader finds out. This is one of the three
    /// endings the design says must be told apart — the other two are a drop and a close.
    /// </summary>
    [Fact]
    public async Task AShellThatExitsEndsTheChannel()
    {
        SkipWithoutFixture();

        await using ISshTransport transport = await Connected();
        IPtyChannel channel = await transport.OpenShellAsync(80, 25, Stop);

        await Type(channel, "exit");

        PtyExit exit = await channel.Closed.WaitAsync(TimeSpan.FromSeconds(20), Stop);

        Assert.True(exit.IsExit || exit.Reason.Length > 0,
                    "the channel ended without saying whether it was an exit or a failure");
    }

    // ---- The host key, which is checked before a secret is offered ----

    [Fact]
    public async Task TheServersRealKeyIsPresentedForChecking()
    {
        SkipWithoutFixture();

        SshHostKey? seen = null;

        await using SshNetTransport transport = new();

        await transport.ConnectAsync(Target, [Key()], (_, key, _) =>
        {
            seen = key;

            return ValueTask.FromResult(SshHostKeyVerdict.Accept);
        }, Stop);

        Assert.NotNull(seen);
        Assert.False(string.IsNullOrWhiteSpace(seen.Value.Algorithm));

        // A SHA-256 digest in base64 with the padding dropped, which is what OpenSSH prints and so
        // what a user can actually compare against.
        Assert.Equal(43, seen.Value.Fingerprint.Length);
        Assert.DoesNotContain('=', seen.Value.Fingerprint);
    }

    /// <summary>Refusing the key refuses the connection, and it is refused as a host-key failure.</summary>
    [Fact]
    public async Task ARefusedKeyRefusesTheConnection()
    {
        SkipWithoutFixture();

        await using SshNetTransport transport = new();

        SshException refused = await Assert.ThrowsAsync<SshException>(async () =>
            await transport.ConnectAsync(Target, [Key()],
                (_, _, _) => ValueTask.FromResult(SshHostKeyVerdict.Refuse), Stop));

        Assert.Equal(SshFailureKind.HostKey, refused.Kind);
        Assert.False(transport.IsConnected);
    }

    /// <summary>
    /// A caller who says nothing about the host key gets a refusal. Silence is not consent: the
    /// alternative is a client that connects to anything claiming to be the right host.
    /// </summary>
    [Fact]
    public async Task SayingNothingAboutTheHostKeyIsARefusal()
    {
        SkipWithoutFixture();

        await using SshNetTransport transport = new();

        SshException refused = await Assert.ThrowsAsync<SshException>(async () =>
            await transport.ConnectAsync(Target, [Key()], null, Stop));

        Assert.Equal(SshFailureKind.HostKey, refused.Kind);
    }

    // ---- Failures arrive as this client's type, with a kind ----

    [Fact]
    public async Task AWrongCredentialFailsAsAuthenticationAndNotAsALibraryException()
    {
        SkipWithoutFixture();

        await using SshNetTransport transport = new();

        SshException refused = await Assert.ThrowsAsync<SshException>(async () =>
            await transport.ConnectAsync(Target, [new SshCredential.Password("not the password")],
                                         Trusting, Stop));

        Assert.Equal(SshFailureKind.Authentication, refused.Kind);
        Assert.Contains(Target.ToString(), refused.Message, StringComparison.Ordinal);
        Assert.Null(refused.InnerException);
        Assert.NotNull(refused.Origin);
    }

    [Fact]
    public async Task APortWithNothingBehindItFailsAsUnreachable()
    {
        await using SshNetTransport transport = new();

        SshException refused = await Assert.ThrowsAsync<SshException>(async () =>
            await transport.ConnectAsync(SshEndpoint.For(Host, "probe", 2), [Key()], Trusting, Stop));

        Assert.Equal(SshFailureKind.Unreachable, refused.Kind);
    }

    /// <summary>
    /// An agent key is refused by name. QS5 established the library has none and QS43 is the line
    /// that adds it; quietly dropping the credential would look to a user like a server rejecting
    /// their key.
    /// </summary>
    [Fact]
    public async Task AnAgentKeyIsRefusedByNameRatherThanQuietlySkipped()
    {
        await using SshNetTransport transport = new();

        SshException refused = await Assert.ThrowsAsync<SshException>(async () =>
            await transport.ConnectAsync(Target, [new SshCredential.Agent()], Trusting, Stop));

        Assert.Equal(SshFailureKind.Authentication, refused.Kind);
        Assert.Contains("agent", refused.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ---- The measurements the design asks for rather than assumes ----

    /// <summary>
    /// <c>IPtyChannel</c> requires that a write is never batched, and says that for a socket
    /// implementation this means <c>TCP_NODELAY</c> "set and verified rather than assumed". SSH.NET
    /// exposes no socket and no such option, so it cannot be set here — which leaves verifying it,
    /// and this is that.
    ///
    /// <para>A Nagle delay is not subtle: it is around forty milliseconds and it lands on exactly
    /// this operation, a single small write whose answer is another single small write. Ten
    /// round-trips over loopback with the delay would take the better part of half a second.</para>
    /// </summary>
    [Fact]
    public async Task AKeystrokeIsNotHeldBackWaitingForCompany()
    {
        SkipWithoutFixture();

        await using ISshTransport transport = await Connected();
        await using IPtyChannel channel = await transport.OpenShellAsync(80, 25, Stop);

        // A prompt on screen first: the login banner would otherwise be counted as the answer.
        await Type(channel, "stty -echo; printf ready");
        await Until(channel, "ready");

        List<double> trips = [];

        for (int trip = 0; trip < 10; trip++)
        {
            Stopwatch clock = Stopwatch.StartNew();

            await channel.WriteAsync(Encoding.ASCII.GetBytes($"printf t{trip}\n"), Stop);
            await Until(channel, $"t{trip}");

            trips.Add(clock.Elapsed.TotalMilliseconds);
        }

        double worst = trips.Max();

        // Forty milliseconds is Nagle's own number. Twenty-five leaves room for a loaded machine and
        // is still nowhere near it, so this refuses the thing it exists to refuse.
        Assert.True(worst < 25.0,
                    $"the slowest of ten round-trips took {worst:F1} ms, which is the shape of a "
                    + $"delayed write; all ten: {string.Join(", ", trips.Select(t => $"{t:F1}"))}");
    }

    /// <summary>
    /// The named risk from QS5's gap analysis, measured rather than assumed: what the library's
    /// shell stream sustains, and what it costs in allocations to carry a megabyte.
    ///
    /// <para>Both figures are compared against QS5's own, taken through the same library against the
    /// same fixture — 81–103 MB/s at 112–126 KB allocated per MB. That is the only comparison
    /// available that holds the machine and the server still; the local pseudo-console is a
    /// different producer entirely and comparing against it is QS110.</para>
    /// </summary>
    [Fact]
    public async Task WhatTheLibraryCarriesAndWhatItCostsAreMeasuredNotAssumed()
    {
        SkipWithoutFixture();

        const long Bytes = 32 * 1024 * 1024;

        (double megabytesPerSecond, double kilobytesPerMegabyte) =
            await Carried(await Remote(), "cat /srv/big.txt", Bytes);

        TestContext.Current.TestOutputHelper?.WriteLine(
            $"{megabytesPerSecond:F1} MB/s at {kilobytesPerMegabyte:F0} KB allocated per MB");

        Assert.True(megabytesPerSecond > 5.0,
                    $"the remote channel carried {megabytesPerSecond:F1} MB/s, which is not a link");

        // Five hundred against QS5's 112-126. Wide, because this is a process-wide counter and the
        // test host allocates too — but nowhere near wide enough to hide the failure it exists to
        // catch: reading through the library's DataReceived event instead of its stream measured
        // 1,326 KB per MB here, which is a whole extra copy of everything a session ever prints.
        Assert.True(kilobytesPerMegabyte < 500.0,
                    $"carrying a megabyte allocated {kilobytesPerMegabyte:F0} KB, and QS5 measured "
                    + "112-126 KB through the same library");
    }

    // ---- plumbing ----

    private static async Task<ISshTransport> Connected()
    {
        SshNetTransport transport = new();

        await transport.ConnectAsync(Target, [Key()], Trusting, Stop);

        Assert.True(transport.IsConnected);

        return transport;
    }

    private static async Task<IPtyChannel> Remote()
    {
        ISshTransport transport = await Connected();

        return await transport.OpenShellAsync(200, 50, Stop);
    }

    private static async Task<IPtyChannel> LocalShell() =>
        await ConPtyChannel.StartAsync("cmd.exe /q", 200, 50, null, Stop);

    /// <summary>
    /// A file of printable ASCII for the local side to print, matching what the fixture's
    /// <c>/srv/big.txt</c> is on the remote side.
    ///
    /// <para>A file and not a loop. <c>for /L</c> in cmd was the first thing written here and it is
    /// unusably slow — a quarter of a million iterations of <c>echo</c> took longer than the whole
    /// suite — so it was measuring cmd's interpreter rather than the channel underneath it.</para>
    /// </summary>
    private static string Big(long bytes)
    {
        string path = Path.Combine(Path.GetTempPath(), $"quickshell-{Guid.NewGuid():N}.txt");
        byte[] line = Encoding.ASCII.GetBytes(new string('x', 78) + "\r\n");

        using FileStream file = File.Create(path);

        for (long written = 0; written < bytes; written += line.Length)
        {
            file.Write(line);
        }

        return path;
    }

    private static async Task<(double Megabytes, double KilobytesPerMegabyte)> Carried(
        IPtyChannel channel, string command, long expected)
    {
        await using (channel)
        {
            // Long enough for a login banner and a prompt to have arrived and be sitting in the
            // buffer. They are not drained first, and that is deliberate: cancelling a pending read
            // to stop draining aborts the read, and on a Windows pipe that takes the pipe with it —
            // the local half of this measurement read nothing for forty-five seconds until the
            // cancelled drain came out. A few hundred bytes of banner against thirty-two megabytes
            // is not worth a cancelled read.
            await Task.Delay(1200, Stop);

            byte[] buffer = new byte[64 * 1024];

            // Process-wide and not per-thread: the library reads on threads of its own, so a
            // per-thread count measures this loop and reports the transport as allocating nothing.
            long before = GC.GetTotalAllocatedBytes(precise: true);
            Stopwatch clock = Stopwatch.StartNew();
            long read = 0;

            await Type(channel, command);

            using CancellationTokenSource carrying = CancellationTokenSource.CreateLinkedTokenSource(Stop);
            carrying.CancelAfter(TimeSpan.FromSeconds(45));

            try
            {
                while (read < expected)
                {
                    int got = await channel.ReadAsync(buffer, carrying.Token);

                    if (got == 0)
                    {
                        break;
                    }

                    read += got;
                }
            }
            catch (OperationCanceledException)
            {
            }

            clock.Stop();

            double megabytes = read / 1024.0 / 1024.0;
            long allocated = GC.GetTotalAllocatedBytes(precise: true) - before;

            TestContext.Current.TestOutputHelper?.WriteLine(
                $"{command}: {megabytes:F1} MB in {clock.Elapsed.TotalSeconds:F1} s");

            return (megabytes / Math.Max(0.001, clock.Elapsed.TotalSeconds),
                    allocated / Math.Max(1.0, megabytes) / 1024.0);
        }
    }

    private static SshCredential.PrivateKey Key() =>
        new(Path.Combine(FixtureKeys(), "probe_ed25519"));

    private static ValueTask Type(IPtyChannel channel, string line) =>
        channel.WriteAsync(Encoding.ASCII.GetBytes(line + "\n"), Stop);

    /// <summary>Reads until the wanted text has arrived, or gives up saying what did arrive.</summary>
    private static async Task<string> Until(IPtyChannel channel, string wanted)
    {
        StringBuilder seen = new();
        byte[] buffer = new byte[8 * 1024];

        using CancellationTokenSource waiting = CancellationTokenSource.CreateLinkedTokenSource(Stop);
        waiting.CancelAfter(TimeSpan.FromSeconds(20));

        try
        {
            while (!seen.ToString().Contains(wanted, StringComparison.Ordinal))
            {
                int read = await channel.ReadAsync(buffer, waiting.Token);

                if (read == 0)
                {
                    break;
                }

                seen.Append(Encoding.UTF8.GetString(buffer, 0, read));
            }
        }
        catch (OperationCanceledException)
        {
        }

        return seen.ToString();
    }

    private static string FixtureKeys() =>
        Path.Combine(RepositoryRoot(), "prototypes", "SshProbe", "fixture", "keys");

    /// <summary>
    /// Skips where the fixture is not running, saying how to start it. A remote test that quietly
    /// passed against nothing is worse than one that says it did not run.
    /// </summary>
    private static void SkipWithoutFixture()
    {
        bool up;

        try
        {
            using TcpClient probe = new();

            up = probe.ConnectAsync(Host, TargetPort).Wait(TimeSpan.FromSeconds(2));
        }
        catch (SocketException)
        {
            up = false;
        }
        catch (AggregateException)
        {
            up = false;
        }

        Assert.SkipUnless(up && File.Exists(Path.Combine(FixtureKeys(), "probe_ed25519")),
            $"nothing is listening on {Host}:{TargetPort}: "
            + "run prototypes/SshProbe/fixture/up.sh to bring the servers up");
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
