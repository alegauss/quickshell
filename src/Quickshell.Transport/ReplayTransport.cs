using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace Quickshell.Transport;

/// <summary>
/// A transport with no network under it: it answers from bytes somebody recorded, and keeps what was
/// written to it.
///
/// <para><b>This is half of what the seam is for.</b> A client whose only transport is a real one can
/// only be tested where there is a server, which means the tests that matter most — a link that
/// drops mid-frame, a server that sends a broken escape sequence, a host key that changed — are the
/// ones nobody writes. Here they are a byte array.</para>
///
/// <para><b>It refuses as readily as it answers.</b> A recording that ends is a connection that
/// dropped, and <see cref="Refusing"/> is a server that will not let anybody in. A synthetic
/// transport that could only succeed would be a fixture for the paths that already work.</para>
///
/// <para>It is in the shipping assembly rather than in a test project on purpose: it is the second
/// implementation that proves the seam is an interface and not a description of one class, and an
/// interface with one implementation has never been tested for the thing it exists to do.</para>
/// </summary>
public sealed class ReplayTransport : ISshTransport
{
    private readonly TaskCompletionSource<SshException?> _disconnected =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly List<byte> _written = [];
    private readonly byte[] _recording;
    private readonly SshException? _refusal;
    private readonly SshHostKey _key;

    private ReplayChannel? _shell;
    private bool _disposed;

    private ReplayTransport(byte[] recording, SshException? refusal, SshHostKey key)
    {
        _recording = recording;
        _refusal = refusal;
        _key = key;
    }

    /// <summary>
    /// The key this pretends the server presented, unless a caller says otherwise. A blob that is
    /// not a real key, deliberately: a synthetic transport handing out something that would verify
    /// against a real host would be a fixture nobody could tell from a connection.
    /// </summary>
    public static SshHostKey DefaultKey { get; } =
        new("ssh-ed25519", "quickshell-replay-host-key"u8.ToArray());

    /// <summary>A transport that connects and then replays these bytes as the shell's output.</summary>
    /// <param name="recording">What the far end said, in order.</param>
    /// <param name="key">The host key to present; <see cref="DefaultKey"/> when null.</param>
    public static ReplayTransport Replaying(ReadOnlySpan<byte> recording, SshHostKey? key = null) =>
        new(recording.ToArray(), null, key ?? DefaultKey);

    /// <summary>
    /// A transport that fails to connect, with the failure a caller wants to test the handling of.
    /// </summary>
    public static ReplayTransport Refusing(SshFailureKind kind, string reason) =>
        new([], new SshException(kind, reason), DefaultKey);

    /// <inheritdoc/>
    public SshEndpoint Endpoint { get; private set; }

    /// <inheritdoc/>
    public bool IsConnected { get; private set; }

    /// <inheritdoc/>
    public Task<SshException?> Disconnected => _disconnected.Task;

    /// <inheritdoc/>
    public TimeSpan KeepAlive { get; set; } = TimeSpan.Zero;

    /// <inheritdoc/>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Everything written to the shell channel, which is what a test asserts the client sent.</summary>
    public ReadOnlySpan<byte> Written => _written.ToArray();

    /// <summary>The credentials the last <see cref="ConnectAsync"/> was offered, in order.</summary>
    public IReadOnlyList<SshCredential> Offered { get; private set; } = [];

    /// <summary>What the host-key check answered, or null where it was never asked.</summary>
    public SshHostKeyVerdict? Verdict { get; private set; }

    /// <inheritdoc/>
    public async ValueTask ConnectAsync(SshEndpoint endpoint, IReadOnlyList<SshCredential> credentials,
                                        SshHostKeyCheck? hostKey = null,
                                        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (credentials.Count == 0)
        {
            // A caller with nothing to offer has not decided yet, and connecting anyway would be a
            // connection that succeeds only against a server with no authentication at all.
            throw new SshException(SshFailureKind.NoMethodAccepted,
                                   $"No credential was offered for {endpoint}.");
        }

        Endpoint = endpoint;
        Offered = [.. credentials];

        // The key is checked before anything is authenticated, which is the order that makes the
        // check worth doing: a secret offered to an unverified server has already been given away.
        if (hostKey is not null)
        {
            SshHostKeyVerdict verdict = await hostKey(endpoint, _key, cancellationToken)
                .ConfigureAwait(false);

            Verdict = verdict;

            if (verdict == SshHostKeyVerdict.Refuse)
            {
                throw new SshException(SshFailureKind.HostKey,
                                       $"the key {_key} offered by {endpoint} was refused");
            }
        }

        if (_refusal is not null)
        {
            throw _refusal;
        }

        IsConnected = true;
    }

    /// <inheritdoc/>
    public ValueTask<IPtyChannel> OpenShellAsync(int columns, int rows,
                                                 CancellationToken cancellationToken = default)
    {
        RequireConnected();

        _shell = new ReplayChannel(_recording, _written, columns, rows);

        return ValueTask.FromResult<IPtyChannel>(_shell);
    }

    /// <inheritdoc/>
    public ValueTask<IFileTransferChannel> OpenFileTransferAsync(
        CancellationToken cancellationToken = default)
    {
        RequireConnected();

        // Named rather than returned empty: a file pane against this would silently show an empty
        // home directory, which is a picture of a bug rather than of a missing implementation.
        throw new SshException(SshFailureKind.ShellRefused,
                               "A recorded session carries no file transfer channel.");
    }

    /// <inheritdoc/>
    public ValueTask<IForwardedChannel> OpenForwardAsync(string host, int port,
                                                         CancellationToken cancellationToken = default)
    {
        RequireConnected();

        throw new SshException(SshFailureKind.ShellRefused,
                               $"A recorded session cannot reach {host}:{port}.");
    }

    /// <summary>Ends the session as a drop rather than as a close, which is what a test of QS38 wants.</summary>
    public void Drop(string reason)
    {
        _shell?.Drop(reason);
        _disconnected.TrySetResult(new SshException(SshFailureKind.Dropped, reason));
        IsConnected = false;
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        IsConnected = false;

        if (_shell is not null)
        {
            await _shell.DisposeAsync().ConfigureAwait(false);
        }

        _disconnected.TrySetResult(null);
    }

    private void RequireConnected()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!IsConnected)
        {
            throw new SshException(SshFailureKind.Dropped, "the transport is not connected");
        }
    }
}

/// <summary>
/// The recorded shell: it hands out what was captured, in whatever sizes a reader asks for, and
/// remembers what was typed at it.
///
/// <para>Reads answer immediately while the recording lasts and then wait, which is the shape a real
/// channel has: the end of a recording is not the end of a stream, because a session that has said
/// everything it is going to say is still open until somebody closes it. A reader that treated the
/// two as the same would stop reading a live idle shell.</para>
/// </summary>
internal sealed class ReplayChannel : IPtyChannel
{
    private readonly TaskCompletionSource<PtyExit> _closed =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly Channel<bool> _ended = Channel.CreateUnbounded<bool>();
    private readonly List<byte> _written;
    private readonly byte[] _recording;

    private int _read;
    private bool _disposed;

    internal ReplayChannel(byte[] recording, List<byte> written, int columns, int rows)
    {
        _recording = recording;
        _written = written;
        Size = (columns, rows);
    }

    /// <inheritdoc/>
    public (int Columns, int Rows) Size { get; private set; }

    /// <inheritdoc/>
    public Task<PtyExit> Closed => _closed.Task;

    /// <summary>How many resizes the far end was told about, which is what a test of QS32 asserts.</summary>
    public int Resizes { get; private set; }

    /// <inheritdoc/>
    public async ValueTask<int> ReadAsync(Memory<byte> buffer,
                                          CancellationToken cancellationToken = default)
    {
        if (buffer.IsEmpty)
        {
            return 0;
        }

        if (_read < _recording.Length)
        {
            int taken = Math.Min(buffer.Length, _recording.Length - _read);

            _recording.AsSpan(_read, taken).CopyTo(buffer.Span);
            _read += taken;

            return taken;
        }

        if (_disposed || _closed.Task.IsCompleted)
        {
            return 0;
        }

        // Nothing left and still open: wait to be closed rather than spinning or answering zero. A
        // zero here would tell a session loop the stream had ended, and it would stop reading a
        // channel that is merely quiet.
        await _ended.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false);

        return 0;
    }

    /// <inheritdoc/>
    public ValueTask WriteAsync(ReadOnlyMemory<byte> bytes,
                                CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _written.AddRange(bytes.Span);

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public void Resize(int columns, int rows)
    {
        Size = (columns, rows);
        Resizes++;
    }

    /// <summary>Ends the recording as a link that dropped rather than a program that exited.</summary>
    internal void Drop(string reason)
    {
        _closed.TrySetResult(PtyExit.Failed(reason));
        _ended.Writer.TryWrite(true);
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
        _closed.TrySetResult(PtyExit.Exited(0));
        _ended.Writer.TryWrite(true);
        _ended.Writer.TryComplete();
    }
}
