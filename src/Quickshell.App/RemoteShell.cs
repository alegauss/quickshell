using System.IO;
using System.Text;
using Quickshell.Terminal;
using Quickshell.Transport;

namespace Quickshell.App;

/// <summary>
/// A saved session, connected: its transport, its shell and the pipeline over them (QS126).
///
/// <para><b>The machinery was all there and nothing named it.</b> Jump hosts, host-key trust,
/// agents and key files were shipped and tested against real servers and reachable from nothing a
/// user could run. This is the one place they meet: a <see cref="ResolvedSession"/> — what the
/// store says, with every inherited value resolved — becomes a connection the way OpenSSH would
/// make it, and its shell goes into the same pipeline a local one does.</para>
///
/// <para><b>Who it offers, in the order the design asks for.</b> The session's own key file where it
/// names one; otherwise the keys OpenSSH looks for by default in <c>~/.ssh</c>; and the Windows
/// agent where one is running. Then, where the window can ask, the server's own prompts — a
/// password, a one-time code — put to the person, and a password they chose to remember offered
/// last (QS218, <see cref="SignIn"/>).</para>
///
/// <para><b>Every connection passes the host-key check.</b> The caller hands in a
/// <see cref="TrustOnFirstUse"/>, so a known key is accepted silently, a changed one is refused with
/// no question, and an unknown one is asked about — by the window, which is the one that has a
/// person in front of it.</para>
/// </summary>
public sealed class RemoteShell : IShellSession
{
    private readonly RemoteSession _inner;
    private readonly Connection _connection;

    private RemoteShell(RemoteSession inner, Connection connection)
    {
        _inner = inner;
        _connection = connection;
    }

    /// <summary>
    /// The session's forwards on the connection there is now: what started, what did not, and each
    /// to stop or start again. Started afresh on every reconnect (QS220).
    /// </summary>
    public SessionForwards Forwards => _connection.Forwards ?? throw new InvalidOperationException("the session is between connections");

    /// <summary>The same, or null between connections, for a view that reads it whenever it likes (QS70).</summary>
    public SessionForwards? ForwardsNow => _connection.Forwards;

    /// <summary>The host as the session names it.</summary>
    public string Host => _connection.Host;

    /// <summary>The connection there is now, for whatever else a pane opens over it; null between connections.</summary>
    public ISshTransport? Transport => _inner.Transport;

    /// <summary>Where the session is: live, waiting to try again, or over (QS220).</summary>
    public SessionStatus Status => _inner.Status;

    /// <summary>How many times it has connected, the first included.</summary>
    public int Connections => _inner.Connections;

    /// <summary>
    /// The host's files, over a file channel of the connection there is now (QS219), or null until
    /// that channel has opened or where the server offers none. Opened in the background once each
    /// connection is up, so the browser finds it ready and nothing on screen waits for it; never a
    /// second connection (QS59), so a hardware token is touched once.
    ///
    /// <para><b>Kept with the session, not with a browser.</b> A file opened from it in a local
    /// editor (QS185) is saved back through it after the browser has closed, so it lives as long as
    /// the connection does and goes, before the connection, when the session ends.</para>
    /// </summary>
    public RemoteFiles? Files => _connection.Files;

    /// <summary>
    /// Records this session into <paramref name="log"/> from a new connection made now, so the
    /// handshake is in it (QS129). A reconnect, with what a reconnect costs: the remote shell is a
    /// new one, and the scrollback stays.
    /// </summary>
    /// <returns>Whether there was a connection to make again; between connections the next one is recorded.</returns>
    public bool TraceInto(SessionLog log)
    {
        ArgumentNullException.ThrowIfNull(log);

        _connection.RecordInto(log);

        return _inner.ConnectAgain();
    }

    /// <inheritdoc/>
    public async ValueTask TypeAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default) =>
        await _inner.TypeAsync(bytes, cancellationToken).ConfigureAwait(false);

    /// <inheritdoc/>
    public void Resize(int columns, int rows) => _inner.Resize(columns, rows);

    /// <inheritdoc/>
    public void KeepScrollback(int lines) => _inner.KeepScrollback(lines);

    /// <inheritdoc/>
    public Task<PtyExit> Ended => _inner.Ended;

    /// <summary>Connects a saved session and opens its shell into the model.</summary>
    /// <param name="session">The session as the store resolves it.</param>
    /// <param name="trust">The host-key check every hop passes.</param>
    /// <param name="emulator">The model its output is parsed into.</param>
    /// <param name="damage">The signal the pane's render loop is asleep on.</param>
    /// <param name="columns">The grid the pane settled on.</param>
    /// <param name="rows">Its rows.</param>
    /// <param name="cancellationToken">Gives up.</param>
    /// <param name="log">
    /// Where the connection records what happened — the client's own log, or a trace kept for this
    /// session alone (QS129). Null records nothing.
    /// </param>
    /// <param name="ask">
    /// Puts the target's sign-in questions to the person, or null where nobody can be asked, which
    /// offers keys and the agent alone (QS218).
    /// </param>
    /// <param name="secrets">Where remembered passwords are kept, or null to remember none.</param>
    /// <param name="recording">
    /// Where to keep what the host sends, across every reconnect, or null (QS134). Not closed here.
    /// </param>
    /// <exception cref="SshException">The first connection did not happen, and why in words.</exception>
    public static async Task<RemoteShell> OpenAsync(ResolvedSession session, TrustOnFirstUse trust,
                                                    Emulator emulator, DamageSignal damage,
                                                    int columns, int rows,
                                                    CancellationToken cancellationToken = default,
                                                    SessionLog? log = null,
                                                    Func<SignInQuestion, CancellationToken, ValueTask<SignInAnswer?>>? ask = null,
                                                    SecretStore? secrets = null,
                                                    SessionRecording? recording = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(trust);
        ArgumentNullException.ThrowIfNull(emulator);
        ArgumentNullException.ThrowIfNull(damage);

        // Said into the pane while there is nothing else in it (QS113): before a shell starts, and
        // between one connection and the next, no pipeline writes to this model, so these lines are
        // the only writer it has.
        Narration said = new(emulator, damage);
        Connection connection = new(session, trust, said, log, ask, secrets);

        said.Line(session.JumpHost is { } through
            ? $"Connecting to {connection.Target} through {through.Value}..."
            : $"Connecting to {connection.Target}...");

        ISshTransport first;

        try
        {
            first = await connection.ConnectAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Where the rest of the story is, said where the user is already looking (QS129).
            if (log is not null)
            {
                said.Line($"The log for this connection is {log.Path}");
            }

            throw;
        }

        // Off unless the session says so, which is deliberate: an unexpected new login is an event
        // on plenty of hosts, so reconnecting is something a user turns on for a host (QS38).
        ReconnectPolicy policy = session.Reconnect?.Value == true ? ReconnectPolicy.Default : ReconnectPolicy.Off;

        TaskCompletionSource<bool> live = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int handed = 0;

        int lives = 0;

        // Status is narrated where no pipeline is writing: between connections, never while live.
        void Changed(SessionStatus status)
        {
            switch (status.State)
            {
                case SessionState.Live:
                    Interlocked.Increment(ref lives);
                    live.TrySetResult(true);
                    break;

                case SessionState.Waiting:
                    said.Line(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                        $"[{session.Host}: {status.Reason}. Trying again in {status.NextIn.TotalSeconds:0} s, "
                        + $"attempt {status.Attempt + 1} of {policy.MaximumAttempts}. Close the tab to stop.]"));
                    break;

                case SessionState.Connecting when Volatile.Read(ref lives) > 0 || status.Attempt > 1:
                    said.Line($"Connecting to {connection.Target} again...");
                    break;

                case SessionState.Ended:
                    live.TrySetResult(false);
                    break;

                default:
                    break;
            }
        }

        RemoteSession inner = RemoteSession.Start(
            async token =>
            {
                // The first attempt is the connection just made; every later one is a reconnect,
                // made the same way, with its forwards and its files started again on it.
                if (Interlocked.Exchange(ref handed, 1) == 0)
                {
                    return first;
                }

                return await connection.ConnectAsync(token).ConfigureAwait(false);
            },
            emulator, policy, damage, Changed, recording);

        RemoteShell shell = new(inner, connection);

        if (!await live.Task.WaitAsync(cancellationToken).ConfigureAwait(false))
        {
            // The connection was made and the shell never came: said as the transport would have.
            PtyExit ended = await inner.Ended.ConfigureAwait(false);

            await shell.DisposeAsync().ConfigureAwait(false);

            throw new SshException(SshFailureKind.Dropped,
                                   $"The shell on {connection.Target} did not start: {TerminalLeaf.Ending(ended)}.");
        }

        // Its own and never a folder's (SessionNode.PostLogin), typed once the shell is there.
        if (session.PostLogin is { Length: > 0 } after)
        {
            await inner.TypeAsync(Encoding.UTF8.GetBytes(after + "\r"), cancellationToken).ConfigureAwait(false);
        }

        return shell;
    }

    /// <summary>
    /// How one connection to a saved session is made, every time it is made (QS220): the
    /// credentials, the sign-in questions, a password-only server asked once, and once it is up,
    /// its forwards and its file channel. A reconnect is this called again.
    /// </summary>
    private sealed class Connection
    {
        private readonly ResolvedSession _session;
        private readonly TrustOnFirstUse _trust;
        private readonly Narration _said;
        private readonly Progress _signIn;
        private SessionLog? _log;
        private int _announce;
        private readonly Func<SignInQuestion, CancellationToken, ValueTask<SignInAnswer?>>? _ask;
        private readonly SecretStore? _secrets;
        private readonly IReadOnlyList<SshCredential> _keys;

        private SessionForwards? _forwards;
        private Task<RemoteFiles?>? _files;

        public Connection(ResolvedSession session, TrustOnFirstUse trust, Narration said, SessionLog? log,
                          Func<SignInQuestion, CancellationToken, ValueTask<SignInAnswer?>>? ask, SecretStore? secrets)
        {
            _session = session;
            _trust = trust;
            _said = said;
            _signIn = new Progress(said);
            _log = log;
            _ask = ask;
            _secrets = secrets;
            _keys = Credentials(session);

            Target = SshEndpoint.For(session.Host, session.User?.Value ?? Environment.UserName,
                                     session.Port?.Value ?? SshEndpoint.DefaultPort);
        }

        public SshEndpoint Target { get; }

        public string Host => _session.Host;

        public SessionForwards? Forwards => Volatile.Read(ref _forwards);

        public RemoteFiles? Files => Volatile.Read(ref _files) is { IsCompletedSuccessfully: true } opened ? opened.Result : null;

        /// <summary>Where every connection from the next one on records what happened (QS129).</summary>
        public void RecordInto(SessionLog log)
        {
            Volatile.Write(ref _log, log);
            Volatile.Write(ref _announce, 1);
        }

        /// <summary>Connects, signs in, and starts what the session runs over the connection.</summary>
        public async ValueTask<ISshTransport> ConnectAsync(CancellationToken cancellationToken)
        {
            // What the last connection left behind goes first: its listeners would hold the ports
            // the new forwards want, and its file channel is on a connection that has gone.
            await ReleaseAsync().ConfigureAwait(false);

            // Said here, between connections, where nothing else writes to the pane (QS129).
            if (Interlocked.Exchange(ref _announce, 0) == 1 && Volatile.Read(ref _log) is { } recording)
            {
                _said.Line($"This connection is traced into {recording.Path}");
            }

            // The keys first, then what a person answers: a key that works never shows them a prompt.
            (SignIn? answering, IReadOnlyList<SshCredential> answered) = _ask is null
                ? (null, [])
                : SignIn.For(Target, _ask, _secrets);

            IReadOnlyList<SshCredential> credentials = [.. _keys, .. answered];
            ISshTransport transport = Transport(credentials);
            SshCredential.Password? typed = null;

            try
            {
                try
                {
                    await transport.ConnectAsync(Target, credentials, _trust.CheckAsync, cancellationToken)
                                   .ConfigureAwait(false);
                }
                catch (SshException refused) when (answering is not null && answering.ShouldAskForPassword(refused))
                {
                    // The password method has no prompt of its own, so a server whose only way in is
                    // a password - OpenSSH's default on Ubuntu - is asked about here, once, and
                    // connected to again with what the person typed (QS218).
                    await transport.DisposeAsync().ConfigureAwait(false);

                    _said.Line($"{Target.Host} asks for a password.");

                    typed = await answering.AskPasswordAsync(cancellationToken).ConfigureAwait(false);

                    if (typed is null)
                    {
                        throw;
                    }

                    credentials = [.. credentials, typed];
                    transport = Transport(credentials);

                    await transport.ConnectAsync(Target, credentials, _trust.CheckAsync, cancellationToken)
                                   .ConfigureAwait(false);
                }
                finally
                {
                    // The library has had the bytes it needed; the copy this client holds goes now.
                    typed?.Dispose();
                }

                // Signed in, so a password the person asked to keep is the right one to keep.
                answering?.Commit();
            }
            catch
            {
                answering?.Forget();

                await transport.DisposeAsync().ConfigureAwait(false);

                throw;
            }

            // The session's forwards, each on its own: one that cannot start is said and costs
            // nothing else, least of all the shell (QS69) - and said again on every reconnect, so a
            // forward that did not come back is not a silent one (QS220).
            SessionForwards forwards = SessionForwards.Start(transport, _session.Forwards);

            foreach (StartedForward started in forwards.Started)
            {
                _said.Line($"Forward {started.Spec} is listening on port {started.BoundPort}.");
            }

            foreach (FailedForward failed in forwards.Failed)
            {
                _said.Line($"Forward {failed.Spec} did not start: {failed.Reason} {failed.Remedy}");
            }

            Volatile.Write(ref _forwards, forwards);
            Volatile.Write(ref _files, OpenFilesAsync(transport, _session.Host));

            return transport;
        }

        /// <summary>Stops what the last connection ran: its forwards, its edits and its file channel.</summary>
        public async ValueTask ReleaseAsync()
        {
            if (Interlocked.Exchange(ref _forwards, null) is { } forwards)
            {
                await forwards.DisposeAsync().ConfigureAwait(false);
            }

            if (Interlocked.Exchange(ref _files, null) is { } opening && await opening.ConfigureAwait(false) is { } files)
            {
                await files.DisposeAsync().ConfigureAwait(false);
                await files.Channel.DisposeAsync().ConfigureAwait(false);
            }
        }

        private ISshTransport Transport(IReadOnlyList<SshCredential> offered)
        {
            // A keepalive that detects, not only one that keeps (QS111): a frozen host is noticed,
            // and noticing it is what lets a reconnect begin within seconds (QS220).
            TimeSpan keepAlive = TimeSpan.FromSeconds(15);
            SessionLog? log = Volatile.Read(ref _log);

            return _session.JumpHost is { } jump
                ? new SshChain([
                    // The jump host takes the keys alone: its questions would be asked as the target's.
                    new SshHop(Through(jump.Value, Target.User), _keys, _trust.CheckAsync),
                    new SshHop(Target, offered, _trust.CheckAsync),
                  ]) { KeepAlive = keepAlive, SignIn = _signIn, Log = log }
                : new SshNetTransport { KeepAlive = keepAlive, SignIn = _signIn, Log = log };
        }

        /// <summary>The file channel and the side over it, or null where the server will not open one.</summary>
        private static async Task<RemoteFiles?> OpenFilesAsync(ISshTransport transport, string host)
        {
            try
            {
                IFileTransferChannel channel = await transport.OpenFileTransferAsync().ConfigureAwait(false);

                return new RemoteFiles(channel, host);
            }
            catch (SshException)
            {
                // A server with no file subsystem: the browser opened over this tab says it has no
                // remote side, which is the truth, rather than this failing the shell.
                return null;
            }
        }
    }

    /// <summary>
    /// What to offer: the session's key, or OpenSSH's default ones; and the agent where one runs.
    /// </summary>
    internal static IReadOnlyList<SshCredential> Credentials(ResolvedSession session)
    {
        List<SshCredential> offered = [];

        if (session.Key?.Value is { Length: > 0 } named)
        {
            offered.Add(new SshCredential.PrivateKey(Expand(named)));
        }
        else
        {
            string keys = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");

            foreach (string usual in (string[])["id_ed25519", "id_ecdsa", "id_rsa"])
            {
                string path = Path.Combine(keys, usual);

                if (File.Exists(path))
                {
                    offered.Add(new SshCredential.PrivateKey(path));
                }
            }
        }

        if (new SshAgent().IsRunning)
        {
            offered.Add(new SshCredential.Agent());
        }

        return offered;
    }

    /// <summary>A jump host as a session names one: <c>[user@]host[:port]</c>, as OpenSSH writes it.</summary>
    /// <param name="jump">What the session's JumpHost says.</param>
    /// <param name="defaultUser">Who to be where it names nobody.</param>
    public static SshEndpoint Through(string jump, string defaultUser)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jump);

        string user = defaultUser;
        string host = jump.Trim();

        if (host.IndexOf('@', StringComparison.Ordinal) is var at and > 0)
        {
            user = host[..at];
            host = host[(at + 1)..];
        }

        int port = SshEndpoint.DefaultPort;

        if (host.LastIndexOf(':') is var colon and > 0
            && int.TryParse(host[(colon + 1)..], System.Globalization.NumberStyles.Integer,
                            System.Globalization.CultureInfo.InvariantCulture, out int given))
        {
            port = given;
            host = host[..colon];
        }

        return SshEndpoint.For(host, user, port);
    }

    /// <summary>
    /// Lines written into the pane before its shell exists, so a sign-in that waits on a person —
    /// a push to approve, a code to type — is not a blank pane that looks hung (QS113).
    /// </summary>
    private sealed class Narration(Emulator emulator, DamageSignal damage)
    {
        private readonly Lock _guard = new();

        public void Line(string text)
        {
            lock (_guard)
            {
                emulator.Feed(Encoding.UTF8.GetBytes(text.ReplaceLineEndings("\r\n").TrimEnd() + "\r\n"));
            }

            damage.Set();
        }
    }

    /// <summary>The transport's sign-in steps, said as a person reads them.</summary>
    private sealed class Progress(Narration said) : IProgress<SshSignInStep>
    {
        public void Report(SshSignInStep value)
        {
            switch (value)
            {
                case SshSignInStep.Banner banner when banner.Text.Trim().Length > 0:
                    // The server's own words, already stripped of anything a display would act on.
                    said.Line(banner.Text);
                    break;

                case SshSignInStep.Partly partly:
                    said.Line($"{partly.Endpoint.Host} accepted {partly.Accepted} and wants "
                              + $"{string.Join(" or ", partly.StillWanted)} next.");
                    break;

                default:
                    break;
            }
        }
    }

    private static string Expand(string path) =>
        path.StartsWith('~')
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                           path[1..].TrimStart('/', '\\'))
            : path;

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        // The forwards first, so no listener outlives the session that made it, and the host's
        // files, edits and then their channel, while the connection under them is still there to
        // close them on (QS219); then the session, which stops trying, ends its pipeline and closes
        // the connection.
        await _connection.ReleaseAsync().ConfigureAwait(false);
        await _inner.DisposeAsync().ConfigureAwait(false);
    }
}
