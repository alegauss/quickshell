using System.Buffers;
using System.Diagnostics;
using System.Threading.Channels;
using Quickshell.Terminal;
using Quickshell.Transport;

namespace Quickshell.App;

/// <summary>What one stage of the pipeline has done, for a test or a diagnostic to read.</summary>
/// <param name="Bytes">Host bytes parsed. Compared against what was sent, this is the no-loss claim.</param>
/// <param name="Chunks">Reads parsed.</param>
/// <param name="Signals">Times the renderer was told something changed.</param>
/// <param name="Coalesced">Reads that were absorbed into somebody else's signal.</param>
/// <param name="LargestBatch">The most reads ever taken in one drain, before one signal.</param>
/// <param name="LongestWait">The longest a read waited between arriving and being parsed.</param>
/// <param name="Keystrokes">Writes the user's side made.</param>
/// <param name="LongestKeystroke">
/// The longest one of those took to reach the host. This is the number the whole arrangement is for:
/// it has to stay put whatever the host is printing.
/// </param>
public readonly record struct PipelineWork(
    long Bytes,
    long Chunks,
    long Signals,
    long Coalesced,
    int LargestBatch,
    TimeSpan LongestWait,
    long Keystrokes,
    TimeSpan LongestKeystroke);

/// <summary>
/// Three stages and one barrier: the arrangement the rest of the architecture is built around.
///
/// <para><b>Transport</b> does nothing but read into pooled arrays and hand them to a bounded queue.
/// It takes no lock and knows no terminal concept. <b>Parser</b> drains that queue, mutates the
/// model, and raises the damage signal. A render loop is the third stage and waits on that signal;
/// where it has not moved, it issues nothing at all — which is <see cref="Render.RedrawGate"/>'s
/// job, and why this class does not draw.</para>
///
/// <para><b>The decisive property is that the parser drains the entire pending queue before it
/// signals.</b> A terminal is a state machine, so intermediate states are not frames anyone is owed:
/// a hundred megabytes printed scrolls a million lines, and the user is owed the last screen, not a
/// million screens. Parsing runs near a gigabyte a second and presenting runs at a hundred and
/// twenty hertz — coalescing is not an optimisation, it is the only way those two numbers coexist. A
/// pipeline that signalled per read would make the parser wait for the renderer, the queue would
/// grow with the size of the file, and the delay before a typed character appeared would grow with
/// it. <see cref="PipelineWork.LongestWait"/> is the number that says which of those is happening.</para>
///
/// <para><b>Bytes are never dropped and frames always may be.</b> A dropped byte corrupts a state
/// machine; a dropped frame is a picture nobody would have seen. So the queue is bounded and a full
/// queue makes the reader <em>wait</em> — backpressure goes into the transport's own flow control,
/// which is where it belongs — while the damage signal latches and loses nothing but wake-ups.</para>
///
/// <para>The model is mutated on the parser stage and nowhere else, so a render loop reads
/// <see cref="Emulator.Damage"/> without a lock. That is the hand-off, and QS22 is where it is
/// argued.</para>
/// </summary>
public sealed class SessionPipeline : IAsyncDisposable
{
    /// <summary>How much one read asks for. A pty hands over what has arrived, not this much.</summary>
    public const int ReadSize = 64 * 1024;

    /// <summary>
    /// How many reads may be waiting before the reader has to wait.
    ///
    /// <para>Deep enough that a burst does not stall the transport, shallow enough that a parser
    /// falling behind is felt as backpressure rather than as memory. Sixty-four reads is four
    /// megabytes at the size above.</para>
    /// </summary>
    public const int QueueCapacity = 64;

    private readonly IPtyChannel _channel;
    private readonly Emulator _emulator;
    private readonly Channel<Chunk> _queue;
    private readonly Channel<byte[]> _replies = Channel.CreateUnbounded<byte[]>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
    private readonly CancellationTokenSource _stopping = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private long _bytes;
    private long _chunks;
    private long _coalesced;
    private int _largestBatch;
    private long _longestWaitTicks;
    private long _keystrokes;
    private long _longestKeystrokeTicks;
    private bool _disposed;

    private SessionPipeline(IPtyChannel channel, Emulator emulator, int capacity)
    {
        _channel = channel;
        _emulator = emulator;

        // Wait, not drop. This one option is the whole no-lost-bytes property.
        _queue = Channel.CreateBounded<Chunk>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true,
        });
    }

    /// <summary>What a render loop waits on.</summary>
    public DamageSignal Damage { get; } = new();

    /// <summary>Completes when the host has closed and everything it sent has been parsed.</summary>
    public Task Completed { get; private set; } = Task.CompletedTask;

    /// <summary>What the stages have done so far.</summary>
    public PipelineWork Work => new(
        Interlocked.Read(ref _bytes),
        Interlocked.Read(ref _chunks),
        Damage.Sets,
        Interlocked.Read(ref _coalesced),
        Volatile.Read(ref _largestBatch),
        TimeSpan.FromTicks(Interlocked.Read(ref _longestWaitTicks)),
        Interlocked.Read(ref _keystrokes),
        TimeSpan.FromTicks(Interlocked.Read(ref _longestKeystrokeTicks)));

    /// <summary>Starts the reader and the parser over a channel and a model of the same size.</summary>
    public static SessionPipeline Start(
        IPtyChannel channel,
        Emulator emulator,
        int capacity = QueueCapacity)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(emulator);
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);

        SessionPipeline pipeline = new(channel, emulator, capacity);

        // Two long-running loops rather than two threads: neither of them ever blocks, so a thread
        // each would be a thread each spent waiting.
        Task reading = Task.Run(pipeline.ReadLoop);
        Task parsing = Task.Run(pipeline.ParseLoop);
        Task replying = Task.Run(pipeline.ReplyLoop);

        pipeline.Completed = Task.WhenAll(reading, parsing, replying);

        return pipeline;
    }

    /// <summary>
    /// Sends what the user typed, by the shortest route in this codebase.
    ///
    /// <para><b>It shares nothing with the output path.</b> It does not enter the queue the reader
    /// fills, does not wait for the parser, does not touch the model, does not wait for a frame and
    /// does not allocate. Output volume is the host's choice; the delay before a keystroke leaves is
    /// what a user attributes to the client — and during a <c>find /</c> the client is at its busiest
    /// and the user is most likely to be reaching for control-C, which is exactly when a shared queue
    /// would deliver that keypress last.</para>
    ///
    /// <para>Timed on the way through, because a design argument is not evidence:
    /// <see cref="PipelineWork.LongestKeystroke"/> is the number.</para>
    /// </summary>
    public async ValueTask TypeAsync(
        ReadOnlyMemory<byte> bytes,
        CancellationToken cancellationToken = default)
    {
        if (bytes.IsEmpty)
        {
            // A key that encodes to nothing is not a keystroke. Sending it would be a syscall for a
            // modifier the user pressed on its own.
            return;
        }

        long started = _clock.Elapsed.Ticks;

        await _channel.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);

        long took = _clock.Elapsed.Ticks - started;

        Interlocked.Increment(ref _keystrokes);

        if (took > Interlocked.Read(ref _longestKeystrokeTicks))
        {
            Interlocked.Exchange(ref _longestKeystrokeTicks, took);
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        await _stopping.CancelAsync().ConfigureAwait(false);
        _queue.Writer.TryComplete();
        _replies.Writer.TryComplete();

        try
        {
            await Completed.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Stopping is how this ends.
        }

        _stopping.Dispose();
    }

    /// <summary>
    /// The transport stage. It reads and it hands over, and it does nothing a terminal would
    /// recognise — which is what lets the same loop sit above an SSH channel later.
    /// </summary>
    private async Task ReadLoop()
    {
        try
        {
            while (!_stopping.IsCancellationRequested)
            {
                byte[] buffer = ArrayPool<byte>.Shared.Rent(ReadSize);
                int read;

                try
                {
                    read = await _channel.ReadAsync(buffer, _stopping.Token).ConfigureAwait(false);
                }
                catch
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                    throw;
                }

                if (read == 0)
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                    break;
                }

                // Where the queue is full this waits, and the wait reaches the far end as flow
                // control. It is the one place backpressure belongs.
                await _queue.Writer
                    .WriteAsync(new Chunk(buffer, read, _clock.Elapsed.Ticks), _stopping.Token)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Closing.
        }
        catch (IOException)
        {
            // The far end went, which is the ordinary way a session ends.
        }
        finally
        {
            _queue.Writer.TryComplete();
        }
    }

    /// <summary>
    /// The parser stage, and the barrier.
    ///
    /// <para>The inner loop is what matters: it takes everything already waiting before it lets the
    /// renderer know anything at all. Signalling inside that loop is the defect this whole task is
    /// about, and it would not look like a defect — it would look like a slow terminal.</para>
    /// </summary>
    private async Task ParseLoop()
    {
        try
        {
            while (await _queue.Reader.WaitToReadAsync(_stopping.Token).ConfigureAwait(false))
            {
                int drained = 0;

                while (_queue.Reader.TryRead(out Chunk chunk))
                {
                    Parse(chunk);
                    drained++;
                }

                if (drained == 0)
                {
                    continue;
                }

                // Everything that had arrived is now in the model. One signal for all of it: the
                // reads after the first were screens nobody would have seen.
                Interlocked.Add(ref _coalesced, drained - 1);
                Note(drained);
                Damage.Set();
            }
        }
        catch (OperationCanceledException)
        {
            // Closing.
        }
        finally
        {
            Drain();

            // Nothing more will be posted, so the courier may go home. Without this the pipeline
            // never reports itself finished after the host closes, because one of its three loops is
            // still waiting for a reply that cannot arrive.
            _replies.Writer.TryComplete();
        }
    }

    private void Parse(Chunk chunk)
    {
        long waited = _clock.Elapsed.Ticks - chunk.Stamp;

        if (waited > Interlocked.Read(ref _longestWaitTicks))
        {
            Interlocked.Exchange(ref _longestWaitTicks, waited);
        }

        _emulator.Feed(chunk.Buffer.AsSpan(0, chunk.Length));

        Interlocked.Add(ref _bytes, chunk.Length);
        Interlocked.Increment(ref _chunks);

        ArrayPool<byte>.Shared.Return(chunk.Buffer);

        Answer();
    }

    /// <summary>
    /// What the terminal owes the host, taken off the parser and handed to a courier.
    ///
    /// <para>Read here because the parser stage is the only one that may look at the model, and
    /// posted rather than written because a parser that stopped to talk to the host would be a
    /// parser waiting on I/O — which is the whole thing the barrier above exists to avoid.</para>
    ///
    /// <para><b>Posted and not fire-and-forget.</b> Writing here and discarding the result was the
    /// first draft: it leaves an unobserved failure where the host has gone, and a discarded
    /// <c>ValueTask</c> is a value whose backing may already have been recycled by the time anything
    /// looks at it. A queue costs one allocation per reply, which is per question a host asks and
    /// not per byte it sends.</para>
    /// </summary>
    private void Answer()
    {
        if (_emulator.Reply.IsEmpty)
        {
            return;
        }

        byte[] reply = _emulator.Reply.ToArray();
        _emulator.ClearReply();

        _replies.Writer.TryWrite(reply);
    }

    /// <summary>
    /// The courier: everything the terminal owes the host, written in order and awaited properly.
    ///
    /// <para>Not a fourth stage. It carries no state and makes no decision — it exists so that the
    /// parser can put a reply down and keep going.</para>
    /// </summary>
    private async Task ReplyLoop()
    {
        try
        {
            while (await _replies.Reader.WaitToReadAsync(_stopping.Token).ConfigureAwait(false))
            {
                while (_replies.Reader.TryRead(out byte[]? reply))
                {
                    await _channel.WriteAsync(reply, _stopping.Token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Closing.
        }
        catch (IOException)
        {
            // The host has gone and no longer wants an answer, which is not a failure of ours.
        }
    }

    private void Note(int drained)
    {
        if (drained > Volatile.Read(ref _largestBatch))
        {
            Volatile.Write(ref _largestBatch, drained);
        }
    }

    /// <summary>Gives back the arrays of anything still queued when the session ended.</summary>
    private void Drain()
    {
        while (_queue.Reader.TryRead(out Chunk chunk))
        {
            ArrayPool<byte>.Shared.Return(chunk.Buffer);
        }
    }

    /// <summary>One read's worth of host output, the pooled array holding it, and when it arrived.</summary>
    private readonly record struct Chunk(byte[] Buffer, int Length, long Stamp);
}
