using Quickshell.Terminal;
using Quickshell.Transport;

namespace Quickshell.App;

/// <summary>
/// A session that outlives the connection under it.
///
/// <para><b>What survives a drop, and what honestly cannot.</b> The <see cref="Emulator"/> is this
/// object's, not the connection's, so the scrollback, the tab and everything the user has read stay
/// exactly as they were. <b>The remote state does not and cannot.</b> A reconnect is a new
/// connection, a new shell and a new process: the working directory, the environment and anything
/// that was running are gone, and no client recovers those without the far side cooperating. So a
/// drop costs a command, not an afternoon — and this says so rather than implying otherwise by
/// staying quiet.</para>
///
/// <para><b>Three failures wear one appearance</b> and this tells them apart. The server closed the
/// session: over, and reconnecting would be a new login nobody asked for. The network went away and
/// came back: retry. The network went away and the socket is still sitting there open, which is the
/// one that would otherwise hang for as long as the operating system feels like — see
/// <see cref="ISshTransport.KeepAlive"/>, which is what makes that failure look like the second one
/// within seconds instead of within minutes.</para>
/// </summary>
public sealed class RemoteSession : IAsyncDisposable
{
    private readonly Func<CancellationToken, ValueTask<ISshTransport>> _connect;
    private readonly CancellationTokenSource _stopping = new();
    private readonly ReconnectPolicy _policy;
    private readonly Emulator _emulator;
    private readonly object _lock = new();

    private ISshTransport? _transport;
    private SessionPipeline? _pipeline;
    private SessionStatus _status = SessionStatus.Idle;
    private bool _disposed;

    private RemoteSession(Func<CancellationToken, ValueTask<ISshTransport>> connect,
                          Emulator emulator, ReconnectPolicy policy)
    {
        _connect = connect;
        _emulator = emulator;
        _policy = policy;
    }

    /// <summary>
    /// Opens a session and keeps it open for as long as the policy says to.
    /// </summary>
    /// <param name="connect">
    /// Makes and connects a transport. Called once per attempt, because a connection that has failed
    /// is not one to reuse — and taking a factory rather than a transport is what lets a test hand
    /// this a <see cref="ReplayTransport"/> and drop it on demand.
    /// </param>
    /// <param name="emulator">The model. It belongs to this session and survives every reconnect.</param>
    /// <param name="policy">When to try again; <see cref="ReconnectPolicy.Off"/> to never.</param>
    public static RemoteSession Start(Func<CancellationToken, ValueTask<ISshTransport>> connect,
                                      Emulator emulator, ReconnectPolicy? policy = null)
    {
        ArgumentNullException.ThrowIfNull(connect);
        ArgumentNullException.ThrowIfNull(emulator);

        RemoteSession session = new(connect, emulator, policy ?? ReconnectPolicy.Off);

        session.Completed = Task.Run(session.RunAsync);

        return session;
    }

    /// <summary>Where this session is, right now.</summary>
    public SessionStatus Status
    {
        get
        {
            lock (_lock)
            {
                return _status;
            }
        }
    }

    /// <summary>Completes when the session is over and no further attempt will be made.</summary>
    public Task Completed { get; private set; } = Task.CompletedTask;

    /// <summary>How many times a connection has been established, including the first.</summary>
    public int Connections { get; private set; }

    /// <summary>The model, which is this session's and not the connection's.</summary>
    public Emulator Emulator => _emulator;

    /// <summary>
    /// Stops trying, now. This is the third of the three things the design says an attempt must make
    /// visible, and it is a verb because it is a thing the user does.
    /// </summary>
    public void Stop() => _stopping.CancelAsync().GetAwaiter().GetResult();

    /// <summary>Sends what the user typed, or nothing where there is no connection to send it on.</summary>
    /// <returns>Whether there was a shell to take it.</returns>
    public async ValueTask<bool> TypeAsync(ReadOnlyMemory<byte> bytes,
                                           CancellationToken cancellationToken = default)
    {
        SessionPipeline? pipeline = Volatile.Read(ref _pipeline);

        if (pipeline is null)
        {
            // Refused rather than queued. Keystrokes held across a reconnect arrive at a shell that
            // is not the one the user was typing at, in an order nobody chose — which is how a
            // half-finished command runs against a fresh prompt.
            return false;
        }

        await pipeline.TypeAsync(bytes, cancellationToken).ConfigureAwait(false);

        return true;
    }

    /// <summary>Tells the model and, where there is one, the far end that the window changed size.</summary>
    public void Resize(int columns, int rows) => Volatile.Read(ref _pipeline)?.Resize(columns, rows);

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        await _stopping.CancelAsync().ConfigureAwait(false);
        await Completed.ConfigureAwait(false);

        _stopping.Dispose();
    }

    /// <summary>
    /// Connect, run until the connection ends, decide whether that ending deserves another attempt.
    /// </summary>
    private async Task RunAsync()
    {
        int attempt = 0;

        while (!_stopping.IsCancellationRequested)
        {
            attempt++;

            Publish(new SessionStatus(SessionState.Connecting, attempt, TimeSpan.Zero, string.Empty));

            string reason;
            bool worthRetrying;

            try
            {
                (reason, worthRetrying) = await LiveAsync().ConfigureAwait(false);

                attempt = 0;
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (SshException failure)
            {
                reason = failure.Message;

                // An authentication failure and a refused host key will not fix themselves by being
                // asked again, and asking again is how a client locks an account out. Only the ways
                // a network fails are worth a second attempt.
                worthRetrying = failure.Kind is SshFailureKind.Unreachable or SshFailureKind.Dropped;
            }

            if (!_policy.Enabled || !worthRetrying || attempt >= _policy.MaximumAttempts)
            {
                Publish(new SessionStatus(SessionState.Ended, attempt, TimeSpan.Zero, reason));

                return;
            }

            TimeSpan wait = _policy.Delay(attempt + 1);

            Publish(new SessionStatus(SessionState.Waiting, attempt, wait, reason));

            try
            {
                await Task.Delay(wait, _stopping.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        Publish(new SessionStatus(SessionState.Ended, 0, TimeSpan.Zero, "stopped"));
    }

    /// <summary>
    /// One connection, from its first byte to its last.
    /// </summary>
    /// <returns>Why it ended, and whether that ending is one worth trying again.</returns>
    private async Task<(string Reason, bool WorthRetrying)> LiveAsync()
    {
        ISshTransport transport = await _connect(_stopping.Token).ConfigureAwait(false);

        try
        {
            _transport = transport;

            IPtyChannel channel = await transport
                .OpenShellAsync(_emulator.Buffer.Columns, _emulator.Buffer.Rows, _stopping.Token)
                .ConfigureAwait(false);

            // The same emulator every time. That is the whole claim: the scrollback the user has
            // read is this object's, and a new connection writes onto the end of it rather than
            // replacing it.
            SessionPipeline pipeline = SessionPipeline.Start(channel, _emulator);

            Volatile.Write(ref _pipeline, pipeline);
            Connections++;

            Publish(new SessionStatus(SessionState.Live, 0, TimeSpan.Zero, string.Empty));

            try
            {
                await pipeline.Completed.WaitAsync(_stopping.Token).ConfigureAwait(false);
            }
            finally
            {
                Volatile.Write(ref _pipeline, null);

                await pipeline.DisposeAsync().ConfigureAwait(false);
            }

            PtyExit exit = await channel.Closed.ConfigureAwait(false);

            // A program that exited said so, and a new login is not what the user asked for by
            // typing `exit`. Anything else is the link, and the link is what reconnecting is for.
            return exit.IsExit
                ? ($"the shell exited with {exit.Code}", false)
                : (exit.Reason.Length > 0 ? exit.Reason : "the connection ended", true);
        }
        finally
        {
            _transport = null;

            await transport.DisposeAsync().ConfigureAwait(false);
        }
    }

    private void Publish(SessionStatus status)
    {
        lock (_lock)
        {
            _status = status;
        }
    }
}
