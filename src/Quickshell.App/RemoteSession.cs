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
    private PtyExit? _exit;
    private int _scrollback = -1;
    private bool _disposed;
    private TaskCompletionSource? _again;

    private RemoteSession(Func<CancellationToken, ValueTask<ISshTransport>> connect,
                          Emulator emulator, ReconnectPolicy policy, DamageSignal damage)
    {
        _connect = connect;
        _emulator = emulator;
        _policy = policy;
        Damage = damage;
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
    /// <param name="damage">
    /// The signal the pane's render loop sleeps on, which every connection's pipeline sets — the
    /// pane's and not one per connection, or a window asleep on the first would never wake for the
    /// second (QS151). One of its own where there is no pane.
    /// </param>
    /// <param name="changed">
    /// Told every change of status, from the first — which is why it is given here and not set
    /// afterwards: the first attempt begins before this returns.
    /// </param>
    public static RemoteSession Start(Func<CancellationToken, ValueTask<ISshTransport>> connect,
                                      Emulator emulator, ReconnectPolicy? policy = null,
                                      DamageSignal? damage = null, Action<SessionStatus>? changed = null)
    {
        ArgumentNullException.ThrowIfNull(connect);
        ArgumentNullException.ThrowIfNull(emulator);

        RemoteSession session = new(connect, emulator, policy ?? ReconnectPolicy.Off,
                                    damage ?? new DamageSignal())
        {
            Changed = changed,
        };

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
    /// What every connection's pipeline sets when it has changed the model: one signal for the
    /// session's life, like the model, so a reconnect wakes the same render loop the first
    /// connection did.
    /// </summary>
    public DamageSignal Damage { get; }

    /// <summary>
    /// Stops trying, now. This is the third of the three things the design says an attempt must make
    /// visible, and it is a verb because it is a thing the user does.
    /// </summary>
    public void Stop() => _stopping.CancelAsync().GetAwaiter().GetResult();

    /// <summary>
    /// Ends the connection there is now and makes a new one at once, whatever the policy says: the
    /// user asked for this one, which is not the unasked login a policy that is off protects a host
    /// from (QS129, where it is how a trace gets the handshake of a tab already open).
    /// </summary>
    /// <returns>Whether there was a live connection to end.</returns>
    public bool ConnectAgain() => Volatile.Read(ref _again)?.TrySetResult() == true;

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

    /// <summary>
    /// How many lines of history the model keeps: applied to the live pipeline and to every one a
    /// reconnect starts, so a depth set once is the session's and not one connection's (QS220).
    /// </summary>
    public void KeepScrollback(int lines)
    {
        Volatile.Write(ref _scrollback, lines);
        Volatile.Read(ref _pipeline)?.KeepScrollback(lines);
    }

    /// <summary>
    /// Told every change of <see cref="Status"/>, on the session's own thread, so a pane can say an
    /// attempt is running, when the next is due and why the last failed (QS220). Given to
    /// <see cref="Start"/>, because the first attempt begins before it returns.
    /// </summary>
    private Action<SessionStatus>? Changed { get; init; }

    /// <summary>The connection there is now, or null between connections.</summary>
    public ISshTransport? Transport => Volatile.Read(ref _transport);

    /// <summary>
    /// How the session ended for good: the shell's exit where it exited, or the reason the last
    /// connection went and no attempt brought it back.
    /// </summary>
    public Task<PtyExit> Ended => EndedAsync();

    private async Task<PtyExit> EndedAsync()
    {
        await Completed.ConfigureAwait(false);

        return _exit ?? PtyExit.Failed(Status.Reason == "stopped" ? string.Empty : Status.Reason);
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

            bool asked;

            try
            {
                (reason, worthRetrying, asked) = await LiveAsync().ConfigureAwait(false);

                attempt = 0;
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (SshException failure)
            {
                reason = failure.Message;
                asked = false;

                // An authentication failure and a refused host key will not fix themselves by being
                // asked again, and asking again is how a client locks an account out. Only the ways
                // a network fails are worth a second attempt - and a port with nothing listening on
                // it, which is what a server being restarted is for the seconds it takes to come
                // back (QS220). The attempts are bounded and said, so a host that has gone for good
                // is given up on in the same minute and a half as one that is unreachable.
                worthRetrying = failure.Kind is SshFailureKind.Unreachable or SshFailureKind.Dropped
                                                or SshFailureKind.Refused;
            }

            if (asked)
            {
                // Straight back, with no wait: the connection ended because it was asked to.
                continue;
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
    /// <returns>
    /// Why it ended, whether that ending is one worth trying again, and whether it ended because
    /// <see cref="ConnectAgain"/> asked it to.
    /// </returns>
    private async Task<(string Reason, bool WorthRetrying, bool Asked)> LiveAsync()
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
            // replacing it. And the same signal, for the same reason: the window asleep on it is the
            // one that has to wake for what this connection prints (QS151).
            SessionPipeline pipeline = SessionPipeline.Start(channel, _emulator, damage: Damage);

            if (Volatile.Read(ref _scrollback) is var depth and >= 0)
            {
                pipeline.KeepScrollback(depth);
            }

            TaskCompletionSource again = new(TaskCreationOptions.RunContinuationsAsynchronously);

            Volatile.Write(ref _pipeline, pipeline);
            Volatile.Write(ref _again, again);
            Connections++;

            Publish(new SessionStatus(SessionState.Live, 0, TimeSpan.Zero, string.Empty));

            try
            {
                // Whichever comes first. A peer that froze leaves the pipeline's read waiting on a
                // socket that will never say anything, so the transport's verdict is the only thing
                // that ends this connection — waiting for the pipeline alone is how a session sat
                // "live" on a dead host (QS38, QS111). Disposing the pipeline below is what cancels
                // that read. And the user, who can ask for a new connection (QS129).
                await Task.WhenAny(pipeline.Completed, transport.Disconnected, again.Task)
                          .WaitAsync(_stopping.Token).ConfigureAwait(false);
            }
            finally
            {
                Volatile.Write(ref _again, null);
                Volatile.Write(ref _pipeline, null);

                await pipeline.DisposeAsync().ConfigureAwait(false);
            }

            if (again.Task.IsCompleted)
            {
                return ("asked to connect again", true, true);
            }

            if (!channel.Closed.IsCompleted && transport.Disconnected.IsCompleted)
            {
                // The connection went and the shell was never told: the reason is the transport's.
                SshException? gone = await transport.Disconnected.ConfigureAwait(false);

                return gone is null
                    ? ("the connection ended", true, false)
                    : (gone.Message, gone.Kind is SshFailureKind.Dropped or SshFailureKind.Unreachable, false);
            }

            // The pipeline has ended, so the channel's close is moments away or never coming - a
            // channel whose connection is being torn down from under it may not say. Bounded and
            // stoppable, so a disposal never waits on it (QS70, found holding a test run open).
            Task said = await Task.WhenAny(channel.Closed, Task.Delay(TimeSpan.FromSeconds(5), _stopping.Token))
                                  .ConfigureAwait(false);

            _stopping.Token.ThrowIfCancellationRequested();

            if (said != channel.Closed)
            {
                return ("the connection ended", true, false);
            }

            PtyExit exit = await channel.Closed.ConfigureAwait(false);

            if (exit.IsExit)
            {
                _exit = exit;
            }

            // A program that exited said so, and a new login is not what the user asked for by
            // typing `exit`. Anything else is the link, and the link is what reconnecting is for.
            return exit.IsExit
                ? ($"the shell exited with {exit.Code}", false, false)
                : (exit.Reason.Length > 0 ? exit.Reason : "the connection ended", true, false);
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

        Changed?.Invoke(status);
    }
}
