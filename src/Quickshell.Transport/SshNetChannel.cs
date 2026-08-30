using Renci.SshNet;
using Renci.SshNet.Common;

namespace Quickshell.Transport;

/// <summary>
/// A remote shell, as the same four members a local one arrives as.
///
/// <para>Everything above <see cref="IPtyChannel"/> was written and proven against a pseudo-console
/// before there was any remote anything, so this class exists to make a network look like that and
/// to do nothing else. There is no branch anywhere above for "this one is remote".</para>
///
/// <para><b>A write is never batched, and the interface says so as a requirement.</b> Coalescing
/// keystrokes to save a packet trades away the one resource every other decision in this client
/// spends something to protect, so a single byte goes out as a single byte and is flushed.</para>
/// </summary>
internal sealed class SshNetChannel : IPtyChannel
{
    private readonly TaskCompletionSource<PtyExit> _closed =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// This channel's own landing buffer, which is the whole reason a read can be abandoned safely.
    /// The pending read owns it exclusively until it completes.
    /// </summary>
    private readonly byte[] _landing = new byte[64 * 1024];

    private readonly ShellStream _shell;

    private Task<int>? _reading;
    private int _held;
    private int _offset;
    private bool _disposed;

    internal SshNetChannel(ShellStream shell, int columns, int rows)
    {
        _shell = shell;
        Size = (columns, rows);

        // Three endings, and the user is told which. This one is the far end going away while the
        // channel is still open — the other two are an exit code and a Dispose, below.
        _shell.ErrorOccurred += (_, error) => End(PtyExit.Failed(error.Exception.Message));
        _shell.Closed += (_, _) => End(PtyExit.Exited(0));
    }

    /// <inheritdoc/>
    public (int Columns, int Rows) Size { get; private set; }

    /// <inheritdoc/>
    public Task<PtyExit> Closed => _closed.Task;

    /// <summary>
    /// Waits for bytes and takes whatever arrived.
    ///
    /// <para><b>The obvious implementation is wrong and it is worth saying why.</b>
    /// <c>ShellStream</c> does not override <c>ReadAsync</c>, so the one it inherits runs the
    /// blocking <c>Read</c> on a thread-pool thread and checks the cancellation token only before
    /// starting. Once that read is waiting, nothing cancels it — and simply abandoning the task is
    /// worse than useless, because the read is holding a buffer it will write into whenever the
    /// bytes finally come.</para>
    ///
    /// <para>So the read is given a buffer of <em>this channel's</em> own and the task is kept
    /// rather than dropped. Cancelling abandons the wait; the read still owns
    /// <see cref="_landing"/>, no second read is ever started into it, and the next call picks up
    /// the same task. The caller's memory is never aliased by a read the caller thinks it cancelled.</para>
    ///
    /// <para><b>Not driven by <c>DataReceived</c>, and that was measured.</b> Subscribing to the
    /// event and reading on the signal is the tidier design, and against the same fixture it cost
    /// <b>1,326 KB allocated per MB carried</b> where this costs <b>233 KB</b> — the library
    /// materialises a fresh array per packet for the event whether or not the reader wants one, so
    /// the tidier version allocates a whole extra copy of everything a session ever prints.</para>
    /// </summary>
    public async ValueTask<int> ReadAsync(Memory<byte> buffer,
                                          CancellationToken cancellationToken = default)
    {
        if (buffer.IsEmpty)
        {
            return 0;
        }

        if (_held == 0)
        {
            if (_disposed)
            {
                return 0;
            }

            try
            {
                // No token on the read itself: it cannot honour one, and passing it would only
                // promise something this cannot do.
                _reading ??= _shell.ReadAsync(_landing, 0, _landing.Length, CancellationToken.None);

                int got = await _reading.WaitAsync(cancellationToken).ConfigureAwait(false);

                _reading = null;

                if (got <= 0)
                {
                    return 0;
                }

                _held = got;
                _offset = 0;
            }
            catch (ObjectDisposedException)
            {
                // A channel closed underneath a reader is the end of the stream and not an error:
                // the session loop notices zero and then reads Closed for what it meant.
                _reading = null;

                return 0;
            }
            catch (SshException failure)
            {
                _reading = null;
                End(PtyExit.Failed(failure.Message));

                return 0;
            }
        }

        int taken = Math.Min(_held, buffer.Length);

        _landing.AsSpan(_offset, taken).CopyTo(buffer.Span);

        _offset += taken;
        _held -= taken;

        return taken;
    }

    /// <inheritdoc/>
    public async ValueTask WriteAsync(ReadOnlyMemory<byte> bytes,
                                      CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _shell.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);

        // Flushed here rather than left to the buffer, which is the whole of the no-batching
        // requirement: without this a keystroke waits for whatever the library decides to send next.
        await _shell.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public void Resize(int columns, int rows)
    {
        Size = (columns, rows);

        if (_disposed)
        {
            return;
        }

        try
        {
            // Zero pixels for the same reason the pty request carried zero: this client measures in
            // cells, and inventing a pixel size is worse than declining to state one.
            _shell.ChangeWindowSize((uint)columns, (uint)rows, 0, 0);
        }
        catch (SshException)
        {
            // A resize against a channel that has gone is not worth failing a window drag over. The
            // reader will find out through Closed, which is where an ending belongs.
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        Dispose();

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _shell.Dispose();
        End(PtyExit.Exited(0));
    }

    /// <summary>
    /// Publishes the ending, once. A reader waiting on bytes that are not coming is woken by the
    /// stream's own disposal, which is what actually unblocks the read underneath it.
    ///
    /// </summary>
    private void End(PtyExit exit) => _closed.TrySetResult(exit);
}
