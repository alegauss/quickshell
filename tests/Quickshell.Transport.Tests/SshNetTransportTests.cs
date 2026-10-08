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

    /// <summary>The fixture's server that exists to be paused (QS111).</summary>
    private const int FrozenPort = 2227;

    private static readonly SshEndpoint Frozen = SshEndpoint.For(Host, "probe", FrozenPort);

    private static CancellationToken Stop => TestContext.Current.CancellationToken;

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

    /// <summary>
    /// QS228: a connection that ends before the server presents a key refused no key. A listener
    /// that takes the connection and closes it at once is what a server mid-restart looks like, and
    /// calling that a refused host key made it a failure a reconnect would never try again.
    /// </summary>
    [Fact]
    public async Task AConnectionThatEndsBeforeAnyKeyIsNotAHostKeyRefusal()
    {
        using System.Net.Sockets.TcpListener listener = new(System.Net.IPAddress.Loopback, 0);

        listener.Start();

        int port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;

        // It says it is SSH and then goes, before any key exchange: the server got far enough to be
        // an SSH server and no further, which is what a restarting sshd is for an instant.
        Task closing = Task.Run(async () =>
        {
            using System.Net.Sockets.TcpClient taken = await listener.AcceptTcpClientAsync(Stop);

            await taken.GetStream().WriteAsync("SSH-2.0-OpenSSH_9.6\r\n"u8.ToArray(), Stop);
            await Task.Delay(200, Stop);
        }, Stop);

        bool asked = false;

        await using SshNetTransport transport = new() { Timeout = TimeSpan.FromSeconds(10) };

        SshException failed = await Assert.ThrowsAsync<SshException>(async () =>
            await transport.ConnectAsync(SshEndpoint.For("127.0.0.1", "anyone", port), [Key()],
                (_, _, _) =>
                {
                    asked = true;

                    return ValueTask.FromResult(SshHostKeyVerdict.Refuse);
                }, Stop));

        await closing;

        Assert.False(asked);
        Assert.NotEqual(SshFailureKind.HostKey, failed.Kind);
    }

    // ---- Failures arrive as this client's type, with a kind ----

    [Fact]
    public async Task AWrongCredentialFailsAsAuthenticationAndNotAsALibraryException()
    {
        SkipWithoutFixture();

        await using SshNetTransport transport = new();

        SshException refused = await Assert.ThrowsAsync<SshException>(async () =>
            await transport.ConnectAsync(Target, [new SshCredential.Password(Secret.From("not the password"))],
                                         SshFixture.Trusting, Stop));

        Assert.Equal(SshFailureKind.NoMethodAccepted, refused.Kind);
        Assert.Contains(Target.ToString(), refused.Message, StringComparison.Ordinal);
        Assert.Null(refused.InnerException);
        Assert.NotNull(refused.Origin);
    }

    /// <summary>
    /// A port with nothing on it is refused, and is its own kind. QS39 split this out of the coarser
    /// "unreachable": a refused port and a dropped packet send a user to two different places.
    ///
    /// <para><b>A password and not a key, because the credential is not what is under test.</b> This
    /// connection is refused before any of it is offered. Passing the fixture's key made this case
    /// need a file that is gitignored — correctly, it is a private key — so on any machine without
    /// the fixture it failed as <c>CredentialRejected</c> before reaching the socket at all, which
    /// is QS157 and is how it failed in the guest for a week.</para>
    /// </summary>
    [Fact]
    public async Task APortWithNothingBehindItFailsAsRefused()
    {
        await using SshNetTransport transport = new();

        SshException refused = await Assert.ThrowsAsync<SshException>(async () =>
            await transport.ConnectAsync(SshEndpoint.For(Host, "probe", 2), [Unused()], SshFixture.Trusting,
                                         Stop));

        Assert.Equal(SshFailureKind.Refused, refused.Kind);
    }

    /// <summary>
    /// A credential for a connection that never gets far enough to offer one.
    ///
    /// <para>It needs no file, which is the whole point: a case about the network must not depend on
    /// a key that cannot be committed and therefore cannot travel.</para>
    /// </summary>
    private static SshCredential.Password Unused() =>
        new SshCredential.Password(Secret.From("no connection here ever offers this"));

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
            await transport.ConnectAsync(Target, [new SshCredential.Agent()], SshFixture.Trusting, Stop));

        Assert.Equal(SshFailureKind.NoMethodAccepted, refused.Kind);
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

    /// <summary>
    /// QS110: the local half, taken the same way, so a slow link and a slow client can be told
    /// apart. Thirty-two megabytes of the same file shape typed by cmd through the pseudo-console,
    /// with nothing cancelled. It needs no fixture, so it runs on every desk and says which.
    ///
    /// <para><b>The time to print the file, and not the bytes read.</b> The console host renders what
    /// cmd prints instead of relaying it, and it sends fewer bytes than it was given: lines that
    /// scroll past between its frames are never sent. So the figure is thirty-two megabytes of
    /// source over the time until a marker typed after them comes back, which is what "how fast
    /// does a local file print" means, and the reads are counted only for the allocation per
    /// megabyte.</para>
    /// </summary>
    [Fact]
    public async Task TheLocalPseudoConsoleIsMeasuredBesideTheRemote()
    {
        const long Bytes = 32 * 1024 * 1024;
        string big = Big(Bytes);

        try
        {
            (double megabytesPerSecond, double kilobytesPerMegabyte) =
                await Printed(await LocalShell(), $"type \"{big}\" & echo QS110-END", "QS110-END", Bytes);

            string line = string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"local {Environment.MachineName} {DateTime.Now:yyyy-MM-dd}: {megabytesPerSecond:F1} MB/s at "
                + $"{kilobytesPerMegabyte:F0} KB allocated per MB");

            TestContext.Current.TestOutputHelper?.WriteLine(line);
            await File.WriteAllTextAsync(Path.Combine(Path.GetTempPath(), "quickshell-local-throughput.txt"),
                                         line, Stop);

            // Low on purpose: the figure is the point, and it is a fraction of the remote one because
            // the console host renders what cmd prints line by line instead of relaying it. A bound
            // that a working local session could fail would be measuring the host, not the channel.
            Assert.True(megabytesPerSecond > 0.2,
                        $"the local channel carried {megabytesPerSecond:F2} MB/s, which is not a pipe at all");
        }
        finally
        {
            File.Delete(big);
        }
    }

    // ---- Keepalive: telling a dead link from an idle one ----

    /// <summary>
    /// QS111's whole claim: a host that stops answering on a socket that stays open is reported as
    /// gone in seconds, through the same <see cref="ISshTransport.Disconnected"/> a closed socket
    /// arrives through.
    ///
    /// <para><b>Paused, not stopped.</b> A killed sshd closes its socket and that is the easy case;
    /// a paused container leaves the connection exactly as a vanished network does, open and
    /// answering nothing. The library's own keepalive kept "connected" true for minutes under this.
    /// The server is <c>frozen</c>, a container of its own, so the pause freezes nobody else's
    /// test.</para>
    /// </summary>
    [Fact]
    public async Task AFrozenPeerIsNoticedInSeconds()
    {
        SkipWithoutFixture();
        SkipUnlessListening(FrozenPort, "frozen");

        await using SshNetTransport transport = new() { KeepAlive = TimeSpan.FromMilliseconds(500) };

        await transport.ConnectAsync(Frozen, [Key()], SshFixture.Trusting, Stop);

        await using IPtyChannel channel = await transport.OpenShellAsync(80, 25, Stop);

        await Type(channel, "echo alive");
        await Until(channel, "alive");

        await Docker("pause", "qs-sshd-frozen");

        Stopwatch frozen = Stopwatch.StartNew();

        try
        {
            Task first = await Task.WhenAny(transport.Disconnected, Task.Delay(TimeSpan.FromSeconds(20), Stop));

            Assert.True(first == transport.Disconnected, "a frozen peer was still connected after twenty seconds");
        }
        finally
        {
            await Docker("unpause", "qs-sshd-frozen");
        }

        SshException? gone = await transport.Disconnected;

        Assert.NotNull(gone);
        Assert.Equal(SshFailureKind.Dropped, gone.Kind);
        Assert.False(transport.IsConnected);
        Assert.InRange(frozen.Elapsed, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10));

        // The channel hears it too, so a reader waiting on output learns why none is coming.
        Assert.True(channel.Closed.IsCompleted, "the shell was not told its peer had gone");
    }

    /// <summary>
    /// And a keepalive does not itself end a session that is merely quiet, which is the failure a
    /// too-eager one would introduce: an idle prompt is not a dead peer.
    /// </summary>
    [Fact]
    public async Task AnIdleSessionWithAKeepaliveStaysUp()
    {
        SkipWithoutFixture();

        await using SshNetTransport transport = new() { KeepAlive = TimeSpan.FromMilliseconds(500) };

        await transport.ConnectAsync(Target, [Key()], SshFixture.Trusting, Stop);

        await using IPtyChannel channel = await transport.OpenShellAsync(80, 25, Stop);

        await Type(channel, "echo one");
        await Until(channel, "one");

        // Several intervals of saying nothing at all.
        await Task.Delay(TimeSpan.FromSeconds(3), Stop);

        Assert.True(transport.IsConnected, "a keepalive ended a session that was only idle");
        Assert.False(transport.Disconnected.IsCompleted);

        await Type(channel, "echo two");

        Assert.Contains("two", await Until(channel, "two"), StringComparison.Ordinal);
    }

    // ---- plumbing ----

    private static async Task<ISshTransport> Connected()
    {
        SshNetTransport transport = new();

        await transport.ConnectAsync(Target, [Key()], SshFixture.Trusting, Stop);

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

    /// <summary>
    /// How fast a local command gets through a file: the source's size over the time until a marker
    /// printed after it comes back, with the allocation over the same span.
    /// </summary>
    private static async Task<(double Megabytes, double KilobytesPerMegabyte)> Printed(
        IPtyChannel channel, string command, string marker, long source)
    {
        await using (channel)
        {
            byte[] wanted = Encoding.ASCII.GetBytes(marker);
            Stopwatch clock = new();
            long before = 0;

            using CancellationTokenSource carrying = CancellationTokenSource.CreateLinkedTokenSource(Stop);
            carrying.CancelAfter(TimeSpan.FromSeconds(120));

            // Reading starts before anything is written, and that is the whole of why the first
            // attempt hung. The pipes are unbuffered, so a write completes only once the console
            // host reads it, and the host does not read input while it is blocked writing a screen
            // nobody is draining. A session loop is always reading; a test has to be too.
            Task<bool> reading = Task.Run(async () =>
            {
                byte[] buffer = new byte[64 * 1024];
                List<byte> tail = [];

                // The marker counts the second time: the first is the command line being echoed.
                int sightings = 0;

                while (sightings < 2)
                {
                    int got = await channel.ReadAsync(buffer, carrying.Token);

                    if (got == 0)
                    {
                        return false;
                    }

                    tail.AddRange(buffer.AsSpan(0, got));

                    int at;

                    while ((at = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(tail).IndexOf(wanted)) >= 0)
                    {
                        sightings++;
                        tail.RemoveRange(0, at + wanted.Length);
                    }

                    if (tail.Count > wanted.Length)
                    {
                        tail.RemoveRange(0, tail.Count - wanted.Length);
                    }
                }

                return true;
            }, Stop);

            // The banner and the prompt, read and thrown away by the loop above.
            await Task.Delay(1200, Stop);

            before = GC.GetTotalAllocatedBytes(precise: true);
            clock.Start();

            // cmd behind a pseudo-console needs the carriage return and ignores a bare line feed.
            await channel.WriteAsync(Encoding.ASCII.GetBytes(command + "\r\n"), Stop);

            bool seen = await reading;

            clock.Stop();

            Assert.True(seen, "the marker never came back, so the file was never finished");

            double megabytes = source / 1024.0 / 1024.0;
            long allocated = GC.GetTotalAllocatedBytes(precise: true) - before;

            return (megabytes / clock.Elapsed.TotalSeconds, allocated / megabytes / 1024.0);
        }
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
        Path.Combine(Repository.Root, "prototypes", "SshProbe", "fixture", "keys");

    /// <summary>
    /// Skips where the fixture is not running, saying how to start it. A remote test that quietly
    /// passed against nothing is worse than one that says it did not run.
    /// </summary>
    private static void SkipWithoutFixture()
    {
        bool up = SshFixture.Listening(TargetPort);

        Assert.SkipUnless(up && File.Exists(Path.Combine(FixtureKeys(), "probe_ed25519")),
            $"nothing is listening on {Host}:{TargetPort}: "
            + "run prototypes/SshProbe/fixture/up.sh to bring the servers up");
    }

    /// <summary>
    /// Skips, by name, where the fixture is older than the server a test needs: up.sh brings every
    /// one of them up, and an older run of it brought fewer.
    /// </summary>
    private static void SkipUnlessListening(int port, string service)
    {
        bool up = SshFixture.Listening(port);

        Assert.SkipUnless(up, $"the fixture's {service} server is not on {Host}:{port}: run up.sh again");
    }

    /// <summary>Runs one docker command against the fixture and insists that it worked.</summary>
    private static async Task Docker(string verb, string container)
    {
        using Process docker = Process.Start(new ProcessStartInfo("docker", [verb, container])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;

        await docker.WaitForExitAsync(CancellationToken.None);

        Assert.True(docker.ExitCode == 0, $"docker {verb} {container}: {await docker.StandardError.ReadToEndAsync(CancellationToken.None)}");
    }
}
