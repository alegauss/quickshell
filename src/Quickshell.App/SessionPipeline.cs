using System.Buffers;
using System.Diagnostics;
using System.Threading.Channels;
using Quickshell.Terminal;
using Quickshell.Transport;
using System.IO;

namespace Quickshell.App;

/// <summary>What one stage of the pipeline has done, for a test or a diagnostic to read.</summary>
/// <param name="Bytes">Host bytes parsed. Compared against what was sent, this is the no-loss claim.</param>
/// <param name="Chunks">Reads parsed.</param>
/// <param name="Signals">Times the renderer was told something changed.</param>
/// <param name="Coalesced">Reads that were absorbed into somebody else's signal.</param>
/// <param name="LargestBatch">The most reads ever taken in one drain, before one signal.</param>
/// <param name="LongestWait">
/// The longest a read waited between arriving and being parsed. A maximum, so it grows with the
/// number of reads whether or not anything is getting worse — see <paramref name="TotalWait"/> for
/// the number to compare two moments with.
/// </param>
/// <param name="TotalWait">
/// Every wait added together. Two readings of this and of <paramref name="Chunks"/> give the mean
/// wait over the interval between them, which is what says whether the parser is falling behind:
/// unlike a maximum it does not climb merely because more reads were sampled.
/// </param>
/// <param name="LargestBacklog">
/// The most bytes ever sitting unparsed when a drain began. This is how far behind the parser has
/// actually fallen, in the unit that matters — where a count of reads per drain says only how the
/// reader happened to be scheduled.
/// </param>
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
    TimeSpan TotalWait,
    long LargestBacklog,
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
    private long _totalWaitTicks;
    private long _queuedBytes;
    private long _largestBacklog;
    private long _keystrokes;
    private long _longestKeystrokeTicks;
    private Task _resizing = Task.CompletedTask;
    private int _pendingColumns;
    private int _pendingRows;
    private long _told;
    private bool _disposed;

    private SessionPipeline(IPtyChannel channel, Emulator emulator, int capacity,
                            SessionRecording? recording)
    {
        _channel = channel;
        _emulator = emulator;

        Recording = recording;

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

    /// <summary>
    /// Where this session's output is being kept, or null to keep none.
    ///
    /// <para><b>Fed from the parser stage and from nowhere else</b>, which is what makes "output
    /// only" a property of the arrangement rather than a promise. <see cref="TypeAsync"/> writes
    /// straight to the channel — it shares no queue, no buffer and no lock with this side — so there
    /// is no path by which a keystroke could reach a recording, and a password typed at a prompt
    /// cannot end up in a file the user is invited to send.</para>
    /// </summary>
    public SessionRecording? Recording { get; }

    /// <summary>Set when the model has taken a new size and the far end has not been told yet.</summary>
    private DamageSignal Resized { get; } = new();

    /// <summary>
    /// How long a size must hold still before the far end is told about it.
    ///
    /// <para>A resize is a drag and fires continuously. Undebounced, dragging a window across a
    /// screen issues hundreds of window-change requests over the network and a remote editor redraws
    /// for every one of them.</para>
    /// </summary>
    public static readonly TimeSpan ResizeQuiet = TimeSpan.FromMilliseconds(80);

    /// <summary>How many times the far end has actually been told a new size.</summary>
    public long Resizes => Interlocked.Read(ref _told);

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
        TimeSpan.FromTicks(Interlocked.Read(ref _totalWaitTicks)),
        Interlocked.Read(ref _largestBacklog),
        Interlocked.Read(ref _keystrokes),
        TimeSpan.FromTicks(Interlocked.Read(ref _longestKeystrokeTicks)));

    /// <summary>
    /// Starts the reader and the parser over a channel and a model of the same size.
    /// </summary>
    /// <param name="channel">The far end.</param>
    /// <param name="emulator">The model its output is parsed into.</param>
    /// <param name="capacity">How many reads may be waiting before the reader has to wait.</param>
    /// <param name="recording">
    /// Where to keep this session's output, or null to keep none. Passed at construction and never
    /// afterwards: a recording that could be switched on mid-session is one a user could be unaware
    /// had started, and the window's own indication is set from the same decision as this.
    /// </param>
    public static SessionPipeline Start(
        IPtyChannel channel,
        Emulator emulator,
        int capacity = QueueCapacity,
        SessionRecording? recording = null)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(emulator);
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);

        SessionPipeline pipeline = new(channel, emulator, capacity, recording);

        // Two long-running loops rather than two threads: neither of them ever blocks, so a thread
        // each would be a thread each spent waiting.
        Task reading = Task.Run(pipeline.ReadLoop);
        Task parsing = Task.Run(pipeline.ParseLoop);
        Task replying = Task.Run(pipeline.ReplyLoop);

        // The resize courier is not one of the three and is deliberately not in Completed: it waits
        // on the window rather than on the host, so it has no end of its own to reach. A session that
        // waited for it would wait for a resize that is never coming.
        pipeline._resizing = Task.Run(pipeline.ResizeLoop);

        pipeline.Completed = Task.WhenAll(reading, parsing, replying);

        return pipeline;
    }

    /// <summary>
    /// The window changed size.
    ///
    /// <para><b>Three things hold a copy of it</b> — this client's grid, the channel, and the program
    /// on the far end — <b>and only the window knows it changed</b>, which makes telling the other two
    /// an obligation rather than a courtesy. The model takes it first, in order with the bytes around
    /// it; the channel is told once the drag settles.</para>
    ///
    /// <para>A size of zero is clamped and never sent, because some programs divide by it. Restoring a
    /// maximised window, a change of scaling and a move to another monitor each produce a resize, and
    /// each arrives here.</para>
    /// </summary>
    public void Resize(int columns, int rows)
    {
        Chunk resize = new(null, 0, _clock.Elapsed.Ticks, Math.Max(1, columns), Math.Max(1, rows));

        // Posted rather than applied: the model belongs to the parser stage, and a resize applied out
        // of turn would reflow text the host had not finished sending.
        if (_queue.Writer.TryWrite(resize))
        {
            return;
        }

        // A full queue means the parser is behind, and this is worth waiting for: the alternative is
        // a program left permanently wrong about its own width.
        _queue.Writer.WriteAsync(resize, _stopping.Token).AsTask().GetAwaiter().GetResult();
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
            await _resizing.ConfigureAwait(false);
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

                // Counted before the write, so a drain that begins the instant after it lands sees
                // the bytes it is about to take rather than a backlog one read short.
                Interlocked.Add(ref _queuedBytes, read);

                // Where the queue is full this waits, and the wait reaches the far end as flow
                // control. It is the one place backpressure belongs.
                await _queue.Writer
                    .WriteAsync(new Chunk(buffer, read, _clock.Elapsed.Ticks, 0, 0), _stopping.Token)
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

                // How far behind the parser is, measured before it starts catching up. Bytes and
                // not reads: a burst of small reads from a starved reader is one drain of many
                // chunks and no backlog at all, and counting them would call that a failure.
                long behind = Interlocked.Read(ref _queuedBytes);

                if (behind > Interlocked.Read(ref _largestBacklog))
                {
                    Interlocked.Exchange(ref _largestBacklog, behind);
                }

                while (_queue.Reader.TryRead(out Chunk chunk))
                {
                    if (chunk.IsResize)
                    {
                        Reshape(chunk);
                    }
                    else
                    {
                        Parse(chunk);
                    }

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

    /// <summary>
    /// Applies a size to the model, and only then lets the far end hear about it.
    ///
    /// <para>The order is the design's: the buffer reflows first, so it is consistent before anything
    /// observes it, and the channel is told afterwards — because the moment the far end knows, the
    /// program starts drawing at the new width, and a model still holding the old one would render
    /// that as damage.</para>
    /// </summary>
    private void Reshape(Chunk chunk)
    {
        _emulator.Resize(chunk.Columns, chunk.Rows);

        Volatile.Write(ref _pendingColumns, chunk.Columns);
        Volatile.Write(ref _pendingRows, chunk.Rows);

        Resized.Set();
        Damage.Set();
    }

    private void Parse(Chunk chunk)
    {
        long waited = _clock.Elapsed.Ticks - chunk.Stamp;

        if (waited > Interlocked.Read(ref _longestWaitTicks))
        {
            Interlocked.Exchange(ref _longestWaitTicks, waited);
        }

        Interlocked.Add(ref _totalWaitTicks, waited);
        Interlocked.Add(ref _queuedBytes, -chunk.Length);

        // Before the emulator, so what is kept is what arrived rather than what this client made of
        // it. A corpus that had been through a parser is a corpus that no longer contains the defect.
        Recording?.HostSent(chunk.Buffer!.AsSpan(0, chunk.Length));

        _emulator.Feed(chunk.Buffer!.AsSpan(0, chunk.Length));

        Interlocked.Add(ref _bytes, chunk.Length);
        Interlocked.Increment(ref _chunks);

        ArrayPool<byte>.Shared.Return(chunk.Buffer!);

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
    /// Tells the far end the size, once the drag has stopped moving.
    ///
    /// <para><b>Debounced, never dropped.</b> The wait collapses a drag into a handful of requests;
    /// the loop then reads whatever the latest size is rather than the one that woke it, so the size
    /// the drag ended on always arrives. A resize that ended with no notification would leave the
    /// program permanently wrong about its own width, which is worse than a hundred notifications.</para>
    /// </summary>
    private async Task ResizeLoop()
    {
        try
        {
            while (true)
            {
                await Resized.WaitAsync(_stopping.Token).ConfigureAwait(false);
                await Task.Delay(ResizeQuiet, _stopping.Token).ConfigureAwait(false);

                // The latest, not the one that woke this: everything that arrived during the wait is
                // superseded by it, and it is the one the program has to end up with.
                _channel.Resize(Volatile.Read(ref _pendingColumns), Volatile.Read(ref _pendingRows));

                Interlocked.Increment(ref _told);
            }
        }
        catch (OperationCanceledException)
        {
            // Closing.
        }
        catch (ObjectDisposedException)
        {
            // The channel went first, which is the ordinary way a session ends.
        }
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
            if (chunk.Buffer is not null)
            {
                ArrayPool<byte>.Shared.Return(chunk.Buffer);
            }
        }
    }

    /// <summary>
    /// One item for the parser stage: a read's worth of host output, or a size the window changed to.
    ///
    /// <para><b>A resize goes down the same queue as the bytes, and that is not tidiness.</b> The
    /// model is mutated by one stage and no other — which is what lets a renderer read it without a
    /// lock — so a window thread cannot resize it directly. And the order matters on its own: a
    /// resize applied out of turn would reflow text the host had not finished sending, and re-wrap
    /// the wrong content.</para>
    /// </summary>
    private readonly record struct Chunk(byte[]? Buffer, int Length, long Stamp, int Columns, int Rows)
    {
        /// <summary>Whether this is a new size rather than output.</summary>
        public bool IsResize => Buffer is null;
    }
}
