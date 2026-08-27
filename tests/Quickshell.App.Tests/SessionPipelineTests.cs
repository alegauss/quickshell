using System.Diagnostics;
using System.Text;
using System.Threading.Channels;
using Quickshell.Terminal;
using Quickshell.Transport;
using Xunit;

namespace Quickshell.App.Tests;

/// <summary>
/// Three stages and one barrier — the arrangement the rest of the architecture is built around, and
/// the numbers that say whether it is doing what it claims.
/// </summary>
public sealed class SessionPipelineTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    // ---- No byte is dropped ----

    /// <summary>
    /// The task's own criterion: <em>no byte of host output is ever dropped, only frames</em>.
    ///
    /// <para>Two thousand reads pushed in as fast as they will go, against a queue that holds
    /// sixty-four. The queue filling has to make the reader wait, not make it throw anything away —
    /// so the count that comes out is the count that went in, to the byte.</para>
    /// </summary>
    [Fact]
    public async Task EveryByteReachesTheModelHoweverFullTheQueueGets()
    {
        PtyStub far = new();
        Emulator emulator = new(80, 24);
        await using SessionPipeline pipeline = SessionPipeline.Start(far, emulator);

        long sent = 0;

        for (int read = 0; read < 2000; read++)
        {
            byte[] bytes = Encoding.UTF8.GetBytes($"line {read} of two thousand\r\n");
            sent += bytes.Length;
            far.Produce(bytes);
        }

        far.Finish();

        await Finished(pipeline);

        Assert.Equal(sent, pipeline.Work.Bytes);
    }

    /// <summary>And the last thing sent is what is on screen, which is the other half of not losing
    /// it.</summary>
    [Fact]
    public async Task TheLastThingTheHostSentIsWhatIsOnScreen()
    {
        PtyStub far = new();
        Emulator emulator = new(80, 24);
        await using SessionPipeline pipeline = SessionPipeline.Start(far, emulator);

        for (int read = 0; read < 500; read++)
        {
            far.Produce(Encoding.UTF8.GetBytes($"row {read}\r\n"));
        }

        far.Finish();

        await Finished(pipeline);

        Assert.Contains("row 499", Screen(emulator), StringComparison.Ordinal);
    }

    // ---- The barrier ----

    /// <summary>
    /// The task's own criterion: <em>the parser drains its queue fully before signalling damage</em>.
    ///
    /// <para>A thousand reads arriving in a burst are one screen by the time anyone could have looked,
    /// so the signal count has to be far below the read count. A pipeline that signalled per read
    /// would report them equal — and would be a pipeline whose parser waits for its renderer.</para>
    /// </summary>
    [Fact]
    public async Task ABurstOfReadsBecomesFarFewerSignals()
    {
        PtyStub far = new();
        Emulator emulator = new(80, 24);

        for (int read = 0; read < 1000; read++)
        {
            far.Produce(Encoding.UTF8.GetBytes($"burst {read}\r\n"));
        }

        far.Finish();

        await using SessionPipeline pipeline = SessionPipeline.Start(far, emulator);

        await Finished(pipeline);

        PipelineWork work = pipeline.Work;

        Assert.Equal(1000, work.Chunks);
        Assert.True(
            work.Signals < work.Chunks / 2,
            $"{work.Chunks} reads produced {work.Signals} signals, which is not coalescing");
        Assert.Equal(work.Chunks - work.Signals, work.Coalesced);
        Assert.True(work.LargestBatch > 1, "nothing was ever drained in a batch");
    }

    /// <summary>A quiet session signals once per read, because there is nothing to coalesce — the
    /// coalescing is a consequence of load and not a delay imposed on everything.</summary>
    [Fact]
    public async Task AQuietSessionSignalsForEveryRead()
    {
        PtyStub far = new();
        Emulator emulator = new(80, 24);
        await using SessionPipeline pipeline = SessionPipeline.Start(far, emulator);

        for (int read = 0; read < 5; read++)
        {
            long before = pipeline.Work.Signals;

            far.Produce(Encoding.UTF8.GetBytes($"typed {read}\r\n"));

            await Until(() => pipeline.Work.Signals > before);
        }

        Assert.Equal(5, pipeline.Work.Chunks);
        Assert.Equal(5, pipeline.Work.Signals);
        Assert.Equal(0, pipeline.Work.Coalesced);
    }

    // ---- The falsification ----

    /// <summary>
    /// The design's own falsification: <em>falsified when echo latency degrades measurably while a
    /// large file is printing</em>.
    ///
    /// <para>What a typed character waits for is the parser reaching the bytes that carry its echo, so
    /// the measurement is the longest a read waited between arriving and being parsed. The failure this
    /// looks for is that number <b>growing with the file</b>: a parser that waits for its renderer
    /// accumulates a backlog, and the delay grows with every megabyte. So it is read at four megabytes
    /// into one stream and again at sixteen, and four times the bytes must not mean four times the
    /// wait.</para>
    ///
    /// <para>A growth factor and not an absolute bound, because the absolute number is a maximum over
    /// a hundred thousand reads and one scheduler hiccup owns it. What is stable is whether it climbs.</para>
    ///
    /// <para><b>Against a real shell printing a real file</b>, because the thing being claimed is about
    /// a producer that does not wait for us.</para>
    /// </summary>
    [Fact]
    public async Task TheDelayBeforeAReadIsParsedDoesNotGrowWithTheFile()
    {
        string big = await Big(48 * 1024 * 1024);

        try
        {
            await using ConPtyChannel channel = await ConPtyChannel.StartAsync(
                "cmd.exe /q", 120, 30, null, TestContext.Current.CancellationToken);

            Emulator emulator = new(120, 30);
            await using SessionPipeline pipeline = SessionPipeline.Start(channel, emulator);

            // Let the shell get its prompt up: bytes written before it is reading can be dropped,
            // which QS25 is where that is written down.
            await Until(() => pipeline.Work.Chunks > 0);
            await Task.Delay(700, TestContext.Current.CancellationToken);

            await pipeline.TypeAsync(Typed($"type \"{big}\""), TestContext.Current.CancellationToken);

            await Until(() => pipeline.Work.Bytes > 4 * 1024 * 1024);
            PipelineWork early = pipeline.Work;

            await Until(() => pipeline.Work.Bytes > 16 * 1024 * 1024);
            PipelineWork late = pipeline.Work;

            Assert.True(late.Bytes > early.Bytes * 3, "the stream did not actually grow between readings");

            // Four times the bytes. Twice the worst wait is generous for noise and nowhere near the
            // proportional growth a coupled parser would show.
            TimeSpan allowed = TimeSpan.FromTicks(
                Math.Max(early.LongestWait.Ticks * 2, TimeSpan.FromMilliseconds(20).Ticks));

            Assert.True(
                late.LongestWait <= allowed,
                $"the worst wait went from {early.LongestWait.TotalMilliseconds:F2} ms at "
                + $"{early.Bytes / 1024 / 1024} MB to {late.LongestWait.TotalMilliseconds:F2} ms at "
                + $"{late.Bytes / 1024 / 1024} MB, and {allowed.TotalMilliseconds:F2} ms was the bound");

            // And the queue stayed a queue rather than becoming a backlog.
            Assert.True(
                late.LargestBatch < 4096,
                $"a single drain took {late.LargestBatch} reads, which is a backlog and not a batch");
        }
        finally
        {
            File.Delete(big);
        }
    }

    /// <summary>
    /// And the comparison that says why the barrier is there: the same reads through a consumer that
    /// presents each of them cost the same reads times a frame.
    ///
    /// <para>Nothing is asserted about the real pipeline here that the tests above do not already
    /// assert. What is asserted is the counterfactual — that a pipeline without the barrier is slower
    /// by the number of frames it insisted on drawing — because a design decision nobody can measure
    /// the alternative to is a decision nobody can defend.</para>
    /// </summary>
    [Fact]
    public async Task PresentingEveryReadWouldCostAFramePerRead()
    {
        TimeSpan frame = TimeSpan.FromMilliseconds(4);
        const int reads = 250;

        Stopwatch coalescing = Stopwatch.StartNew();
        {
            PtyStub far = new();
            Emulator emulator = new(80, 24);

            for (int read = 0; read < reads; read++)
            {
                far.Produce(Encoding.UTF8.GetBytes($"load {read}\r\n"));
            }

            far.Finish();

            await using SessionPipeline pipeline = SessionPipeline.Start(far, emulator);
            await Finished(pipeline);

            // One frame for whatever the barrier let through, which is the whole cost of drawing.
            await Task.Delay(
                TimeSpan.FromTicks(frame.Ticks * pipeline.Work.Signals),
                TestContext.Current.CancellationToken);
        }

        coalescing.Stop();

        Stopwatch coupled = Stopwatch.StartNew();
        {
            Emulator emulator = new(80, 24);

            // Unbounded on purpose: bounding it would make the writer wait for the reader, and the
            // cost being measured here is the frame per read and not the queue's own shape.
            Channel<byte[]> queue = Channel.CreateUnbounded<byte[]>();

            for (int read = 0; read < reads; read++)
            {
                await queue.Writer.WriteAsync(
                    Encoding.UTF8.GetBytes($"load {read}\r\n"), TestContext.Current.CancellationToken);
            }

            queue.Writer.Complete();

            // The defect, written out: parse one read, present it, parse the next.
            while (await queue.Reader.WaitToReadAsync(TestContext.Current.CancellationToken))
            {
                while (queue.Reader.TryRead(out byte[]? bytes))
                {
                    emulator.Feed(bytes);
                    await Task.Delay(frame, TestContext.Current.CancellationToken);
                }
            }
        }

        coupled.Stop();

        Assert.True(
            coalescing.Elapsed < coupled.Elapsed,
            $"coalescing took {coalescing.ElapsedMilliseconds} ms and presenting every read took "
            + $"{coupled.ElapsedMilliseconds} ms, which is not the difference this design is for");
    }

    // ---- What the terminal owes the host goes back ----

    /// <summary>
    /// A host asking a question gets an answer without the parser stopping to have a conversation.
    /// </summary>
    [Fact]
    public async Task AReplyTheTerminalOwesGoesBackToTheHost()
    {
        PtyStub far = new();
        Emulator emulator = new(80, 24);
        await using SessionPipeline pipeline = SessionPipeline.Start(far, emulator);

        // Device attributes: the question every program asks before it uses colour.
        far.Produce([0x1B, (byte)'[', (byte)'c']);

        await Until(() => far.Written.Count > 0);

        lock (far.Written)
        {
            Assert.Contains("62;22c", Encoding.ASCII.GetString(far.Written[0]), StringComparison.Ordinal);
        }
    }

    /// <summary>Typing goes straight to the far end and does not queue behind the parser.</summary>
    [Fact]
    public async Task TypingGoesStraightToTheFarEnd()
    {
        PtyStub far = new();
        Emulator emulator = new(80, 24);
        await using SessionPipeline pipeline = SessionPipeline.Start(far, emulator);

        // Ten thousand reads waiting, and a keystroke still arrives now.
        for (int read = 0; read < 10_000; read++)
        {
            far.Produce(Encoding.UTF8.GetBytes($"noise {read}\r\n"));
        }

        await pipeline.TypeAsync(Encoding.UTF8.GetBytes("x"), TestContext.Current.CancellationToken);

        lock (far.Written)
        {
            Assert.Contains(far.Written, written => written.Length == 1 && written[0] == (byte)'x');
        }
    }

    // ---- Closing ----

    [Fact]
    public async Task ClosingTwiceIsNotAnError()
    {
        PtyStub far = new();
        Emulator emulator = new(80, 24);
        SessionPipeline pipeline = SessionPipeline.Start(far, emulator);

        far.Finish();

        await pipeline.DisposeAsync();
        await pipeline.DisposeAsync();
    }

    [Fact]
    public async Task ClosingWhileOutputIsStillArrivingStops()
    {
        PtyStub far = new();
        Emulator emulator = new(80, 24);
        SessionPipeline pipeline = SessionPipeline.Start(far, emulator);

        for (int read = 0; read < 5000; read++)
        {
            far.Produce(Encoding.UTF8.GetBytes($"noise {read}\r\n"));
        }

        await pipeline.DisposeAsync();

        Assert.True(pipeline.Completed.IsCompleted);
    }

    // ---- Helpers ----

    /// <summary>A line as a user's keyboard would send it: carriage return then line feed.</summary>
    private static byte[] Typed(string line) =>
        Encoding.UTF8.GetBytes(line + (char)0x0D + (char)0x0A);

    /// <summary>A file big enough that printing it is a load rather than an event.</summary>
    private static async Task<string> Big(int bytes)
    {
        string path = Path.Combine(Path.GetTempPath(), $"quickshell-load-{Guid.NewGuid():N}.txt");
        string line = new('x', 100);

        await using StreamWriter writer = new(path);

        for (int written = 0; written < bytes; written += line.Length + 2)
        {
            await writer.WriteLineAsync(line);
        }

        return path;
    }

    private static async Task Finished(SessionPipeline pipeline)
    {
        Assert.Same(
            pipeline.Completed,
            await Task.WhenAny(pipeline.Completed, Task.Delay(Patience)));

        await pipeline.Completed;
    }

    /// <summary>Waits for something to become true, or fails saying it did not.</summary>
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

    private static string Screen(Emulator emulator)
    {
        StringBuilder text = new();

        for (int row = 0; row < emulator.Buffer.Rows; row++)
        {
            foreach (Cell cell in emulator.Buffer.Screen(row))
            {
                if (cell.Width != 0)
                {
                    text.Append(emulator.Buffer.TextOf(cell));
                }
            }

            text.Append('\n');
        }

        return text.ToString();
    }
}
