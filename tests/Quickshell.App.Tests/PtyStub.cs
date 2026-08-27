using System.Threading.Channels;
using Quickshell.Transport;

namespace Quickshell.App.Tests;

/// <summary>
/// A channel with no process behind it, for the claims that are about the pipeline rather than about
/// the pseudo-console.
///
/// <para>QS25's own tests prove the pseudo-console against a real shell. What is being asserted here
/// is that no byte the far end sent goes missing between the far end and the model, and for that the
/// far end has to be something whose output is known exactly — which a real shell's is not, because a
/// console host renders rather than echoes.</para>
/// </summary>
internal sealed class PtyStub : IPtyChannel
{
    private readonly Channel<byte[]> _output = Channel.CreateUnbounded<byte[]>(
        new UnboundedChannelOptions { SingleReader = true });

    private readonly TaskCompletionSource<PtyExit> _closed =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private ReadOnlyMemory<byte> _pending;

    /// <summary>Everything written back towards the far end, in order.</summary>
    public List<byte[]> Written { get; } = [];

    /// <summary>How many writes arrived, whether or not their bytes were kept.</summary>
    public long Writes { get; private set; }

    /// <summary>
    /// Whether to keep the bytes of each write.
    ///
    /// <para>Off for the allocation measurement, where copying every write into a list would be the
    /// stub allocating and the test reporting it as the path's.</para>
    /// </summary>
    public bool Recording { get; set; } = true;

    /// <inheritdoc/>
    public (int Columns, int Rows) Size { get; private set; } = (80, 24);

    /// <inheritdoc/>
    public Task<PtyExit> Closed => _closed.Task;

    /// <summary>Puts one read's worth of output where the pipeline will find it.</summary>
    public void Produce(byte[] bytes) => _output.Writer.TryWrite(bytes);

    /// <summary>Says the far end has finished, which is what makes a read answer zero.</summary>
    public void Finish() => _output.Writer.TryComplete();

    /// <inheritdoc/>
    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_pending.IsEmpty)
        {
            try
            {
                _pending = await _output.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (ChannelClosedException)
            {
                _closed.TrySetResult(PtyExit.Exited(0));
                return 0;
            }
        }

        int taken = Math.Min(buffer.Length, _pending.Length);

        _pending[..taken].CopyTo(buffer);
        _pending = _pending[taken..];

        return taken;
    }

    /// <inheritdoc/>
    public ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default)
    {
        lock (Written)
        {
            Writes++;

            if (Recording)
            {
                Written.Add(bytes.ToArray());
            }
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public void Resize(int columns, int rows) => Size = (columns, rows);

    /// <inheritdoc/>
    public void Dispose() => _output.Writer.TryComplete();

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        Dispose();

        return ValueTask.CompletedTask;
    }
}
