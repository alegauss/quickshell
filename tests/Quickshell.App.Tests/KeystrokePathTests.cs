using System.Diagnostics;
using System.Text;
using Quickshell.Terminal;
using Quickshell.Transport;
using Xunit;
using System.IO;

namespace Quickshell.App.Tests;

/// <summary>
/// The route a keystroke takes, and everything it does not touch.
///
/// <para>Output volume is the host's choice; the delay before a keystroke leaves is what a user
/// attributes to the client. So these are measurements and not design arguments.</para>
/// </summary>
public sealed class KeystrokePathTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(60);

    // ---- The falsification ----

    /// <summary>
    /// The design's own falsification: <em>falsified when echo latency under load differs measurably
    /// from echo latency at rest</em>.
    ///
    /// <para>The half of that this task owns is the half the symptom names — the keystroke reaching
    /// the host. It is timed at rest, then timed again with a shell printing a large file as fast as
    /// the pipe will carry it, which is the moment a user reaches for control-C and the moment a
    /// shared queue would deliver it last.</para>
    ///
    /// <para>The echo's own half — the parser reaching the bytes that carry it — is QS26's, and
    /// measured there.</para>
    /// </summary>
    [Fact]
    public async Task AKeystrokeLeavesAsFastUnderALargeFileAsAtRest()
    {
        string big = await Big(24 * 1024 * 1024);

        try
        {
            TimeSpan atRest = await Slowest(load: null);
            TimeSpan underLoad = await Slowest(load: big);

            // Five times the at-rest worst case, or two milliseconds, whichever is larger. A shared
            // queue would put a keystroke behind whatever the host had pending, which at two
            // megabytes in is orders of magnitude and not a factor of five.
            TimeSpan allowed = TimeSpan.FromTicks(
                Math.Max(atRest.Ticks * 5, TimeSpan.FromMilliseconds(2).Ticks));

            Assert.True(
                underLoad <= allowed,
                $"a keystroke took {underLoad.TotalMilliseconds:F3} ms under load against "
                + $"{atRest.TotalMilliseconds:F3} ms at rest, and {allowed.TotalMilliseconds:F3} ms was the bound");
        }
        finally
        {
            File.Delete(big);
        }
    }

    // ---- It shares nothing with the output path ----

    /// <summary>
    /// The symptom, directly: a keystroke must not queue behind pending output. Ten thousand reads
    /// waiting, and the write still lands now.
    /// </summary>
    [Fact]
    public async Task AKeystrokeDoesNotWaitForAQueueFullOfOutput()
    {
        PtyStub far = new();
        Emulator emulator = new(80, 24);
        await using SessionPipeline pipeline = SessionPipeline.Start(far, emulator);

        for (int read = 0; read < 10_000; read++)
        {
            far.Produce(Encoding.UTF8.GetBytes($"noise {read}\r\n"));
        }

        Stopwatch clock = Stopwatch.StartNew();

        await pipeline.TypeAsync(Encoding.UTF8.GetBytes("x"), TestContext.Current.CancellationToken);

        clock.Stop();

        Assert.True(
            clock.Elapsed < TimeSpan.FromMilliseconds(100),
            $"a keystroke took {clock.Elapsed.TotalMilliseconds:F2} ms with output pending");

        lock (far.Written)
        {
            Assert.Contains(far.Written, written => written.Length == 1 && written[0] == (byte)'x');
        }
    }

    /// <summary>
    /// Writes are not batched. Coalescing keystrokes to save a syscall trades away the one resource
    /// everything else here is being spent to protect, so three keys are three writes.
    /// </summary>
    [Fact]
    public async Task ThreeKeysAreThreeWrites()
    {
        PtyStub far = new();
        Emulator emulator = new(80, 24);
        await using SessionPipeline pipeline = SessionPipeline.Start(far, emulator);

        foreach (string key in new[] { "a", "b", "c" })
        {
            await pipeline.TypeAsync(Encoding.UTF8.GetBytes(key), TestContext.Current.CancellationToken);
        }

        lock (far.Written)
        {
            Assert.Equal(3, far.Written.Count);
            Assert.All(far.Written, written => Assert.Single(written));
        }
    }

    /// <summary>An empty write is not a write, so a key that encodes to nothing costs nothing.</summary>
    [Fact]
    public async Task AKeyThatEncodesToNothingSendsNothing()
    {
        PtyStub far = new();
        Emulator emulator = new(80, 24);
        await using SessionPipeline pipeline = SessionPipeline.Start(far, emulator);

        await pipeline.TypeAsync(ReadOnlyMemory<byte>.Empty, TestContext.Current.CancellationToken);

        lock (far.Written)
        {
            Assert.Empty(far.Written);
        }
    }

    // ---- It does not allocate ----

    /// <summary>
    /// The keystroke path allocates nothing of its own.
    ///
    /// <para>Measured rather than argued, for the same reason QS24's number was: every plausible path
    /// in this assembly looked allocation-free before it was measured at fifty-five kilobytes per
    /// megabyte. A keystroke is the smallest thing this client does and the one a user feels, so a
    /// collection pause landing on it is the worst possible place for one.</para>
    ///
    /// <para><b>Zero in Release, and one box in Debug.</b> A Debug build emits every async state
    /// machine as a class instead of a struct, so a single await allocates one whatever the method
    /// body does. Measured, a keystroke costs ninety-six bytes there against eighty for a bare write
    /// of the same shape — and the sixteen between them are the two <c>long</c> locals the timing
    /// keeps, sitting <em>inside</em> that same box rather than beside it. So what Debug can still
    /// gate is the count: one allocation per keystroke and not two, which is what the tolerance below
    /// is sized for. Release is where the claim is exact, and CI runs Release.</para>
    /// </summary>
    [Fact]
    public async Task TypingAllocatesNothingOfItsOwn()
    {
        PtyStub far = new() { Recording = false };
        Emulator emulator = new(80, 24);
        await using SessionPipeline pipeline = SessionPipeline.Start(far, emulator);

        byte[] key = Encoding.UTF8.GetBytes("x");

        // Taken once. Reading it inside the loop measures xunit's own context lookup.
        CancellationToken stopping = TestContext.Current.CancellationToken;

        // Warm-up: the first pass pays for whatever the runtime sets up once.
        for (int stroke = 0; stroke < 200; stroke++)
        {
            await pipeline.TypeAsync(key, stopping);
            await Bare(far, key, stopping);
        }

        long baselineFrom = GC.GetAllocatedBytesForCurrentThread();

        for (int stroke = 0; stroke < 1000; stroke++)
        {
            await Bare(far, key, stopping);
        }

        long baseline = GC.GetAllocatedBytesForCurrentThread() - baselineFrom;
        long typingFrom = GC.GetAllocatedBytesForCurrentThread();

        for (int stroke = 0; stroke < 1000; stroke++)
        {
            await pipeline.TypeAsync(key, stopping);
        }

        long typing = GC.GetAllocatedBytesForCurrentThread() - typingFrom;

#if DEBUG
        // Room for the box's own extra locals and nothing like room for a second box, which would
        // double the figure rather than add thirty-two bytes a call.
        long allowed = baseline + (1000 * 32);
#else
        long allowed = 0;
#endif

        Assert.True(
            typing <= allowed,
            $"a thousand keystrokes allocated {typing} bytes against {baseline} for a thousand bare "
            + $"writes of the same shape, and {allowed} was the bound");
        Assert.Equal(2400, far.Writes);
    }

    /// <summary>
    /// A write and nothing else, in the same asynchronous shape a keystroke takes. The baseline the
    /// keystroke path is measured against.
    /// </summary>
    /// <remarks>
    /// Typed as the interface on purpose, because that is how the pipeline holds its channel and the
    /// baseline has to pay for the same interface call the measured path does.
    /// </remarks>
#pragma warning disable CA1859 // Use concrete types when possible for improved performance
    private static async ValueTask Bare(
        IPtyChannel channel,
        ReadOnlyMemory<byte> bytes,
        CancellationToken cancellationToken) =>
        await channel.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
#pragma warning restore CA1859

    // ---- What the terminal owes the host takes the same road ----

    /// <summary>
    /// A reply the terminal owes is posted rather than written on the parser's thread, and it still
    /// arrives. Discarding the write's result was the first draft, and it leaves a failure nobody
    /// observes when the host has gone.
    /// </summary>
    [Fact]
    public async Task AReplyIsPostedAndStillArrives()
    {
        PtyStub far = new();
        Emulator emulator = new(80, 24);
        await using SessionPipeline pipeline = SessionPipeline.Start(far, emulator);

        // Device attributes, five hundred times: five hundred answers, none of them lost and none of
        // them written from the thread that parsed the question.
        for (int asked = 0; asked < 500; asked++)
        {
            far.Produce([0x1B, (byte)'[', (byte)'c']);
        }

        await Until(() =>
        {
            lock (far.Written)
            {
                return far.Written.Count >= 500;
            }
        });

        lock (far.Written)
        {
            Assert.All(
                far.Written,
                written => Assert.Contains(
                    "62;22c", Encoding.ASCII.GetString(written), StringComparison.Ordinal));
        }
    }

    // ---- Helpers ----

    /// <summary>
    /// The worst of two hundred keystrokes, on a session of its own.
    ///
    /// <para>A session each, because the at-rest probe writes bytes into the shell's command line and
    /// a shell whose line already holds two hundred null bytes will not run the command that makes
    /// the load. Sharing one session made the loaded measurement measure an idle shell — which it
    /// passed, and which would have been a green run proving nothing.</para>
    /// </summary>
    private static async Task<TimeSpan> Slowest(string? load)
    {
        await using ConPtyChannel channel = await ConPtyChannel.StartAsync(
            "cmd.exe /q", 120, 30, null, TestContext.Current.CancellationToken);

        Emulator emulator = new(120, 30);
        await using SessionPipeline pipeline = SessionPipeline.Start(channel, emulator);

        // Bytes written before the shell is reading can be dropped, which QS25 is where that is
        // written down.
        await Until(() => pipeline.Work.Chunks > 0);
        await Task.Delay(700, TestContext.Current.CancellationToken);

        if (load is not null)
        {
            await pipeline.TypeAsync(Typed($"type \"{load}\""), TestContext.Current.CancellationToken);
            await Until(() => pipeline.Work.Bytes > 2 * 1024 * 1024);
        }

        long before = pipeline.Work.Bytes;
        byte[] key = [0x00];
        TimeSpan worst = TimeSpan.Zero;

        for (int stroke = 0; stroke < 200; stroke++)
        {
            Stopwatch clock = Stopwatch.StartNew();

            // A null byte: something a shell reads and does nothing about, so this measures the write
            // and not what a shell decided to do with it.
            await pipeline.TypeAsync(key, TestContext.Current.CancellationToken);

            clock.Stop();

            if (clock.Elapsed > worst)
            {
                worst = clock.Elapsed;
            }
        }

        if (load is not null)
        {
            Assert.True(
                pipeline.Work.Bytes > before,
                "the host stopped printing during the measurement, so it measured an idle session");
        }

        return worst;
    }

    private static byte[] Typed(string line) =>
        Encoding.UTF8.GetBytes(line + (char)0x0D + (char)0x0A);

    private static async Task<string> Big(int bytes)
    {
        string path = Path.Combine(Path.GetTempPath(), $"quickshell-keys-{Guid.NewGuid():N}.txt");
        string line = new('x', 100);

        await using StreamWriter writer = new(path);

        for (int written = 0; written < bytes; written += line.Length + 2)
        {
            await writer.WriteLineAsync(line);
        }

        return path;
    }

    private static async Task Until(Func<bool> what)
    {
        Stopwatch clock = Stopwatch.StartNew();

        while (clock.Elapsed < Patience)
        {
            if (what())
            {
                return;
            }

            await Task.Delay(5, TestContext.Current.CancellationToken);
        }

        Assert.Fail("the pipeline did not get where this test needed it within the patience allowed");
    }
}
