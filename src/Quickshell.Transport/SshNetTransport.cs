using System.Diagnostics;
using System.Globalization;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace Quickshell.Transport;

/// <summary>
/// The seam's real implementation, over SSH.NET.
///
/// <para><b>This file and the two beside it are the only place in the client where that library has
/// a name.</b> QS36 settled that and <c>SeamTests</c> enforces it: everything above is written
/// against <see cref="ISshTransport"/> and <see cref="IPtyChannel"/>, so what follows is an
/// implementation rather than an integration.</para>
///
/// <para><b>The library's exceptions stop here.</b> Every call that can throw one is wrapped, and
/// what comes out is an <see cref="SshException"/> carrying words and a kind. The classification is
/// deliberately coarse — QS39 is where a refused key stops reading like a refused port — but the
/// translation happens at the seam, which is the part that cannot be added later.</para>
/// </summary>
public sealed class SshNetTransport : ISshTransport
{
    /// <summary>
    /// What this client tells a server it is.
    ///
    /// <para>A promise rather than a label: claiming <c>xterm-256color</c> commits the emulator to
    /// behaviours a program will then use, which is why QS33 ran somebody else's conformance suite
    /// before this line rather than after it. It is the same string <c>Keys.TerminalType</c> in the
    /// terminal assembly carries, and they must not drift — a terminal that claims one thing at
    /// pty-request time and another when a program asks is a terminal that gets one of the two
    /// answers acted upon.</para>
    /// </summary>
    public const string TerminalType = "xterm-256color";

    /// <summary>
    /// How much the library buffers between the network and a reader.
    ///
    /// <para>The same 64 KB QS5 measured 81–103 MB/s through. It is a buffer and not a batch: it
    /// bounds how much may be waiting, and never delays a byte that has arrived.</para>
    /// </summary>
    private const int BufferBytes = 64 * 1024;

    /// <summary>
    /// Stands where the library will not say. Written out rather than left blank, because an empty
    /// field in a log reads as "there was nothing" and here it means "nobody was told".
    /// </summary>
    private const string Unreported = "not reported by the library";

    private readonly TaskCompletionSource<SshException?> _disconnected =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// The least a keepalive waits for an answer before calling the peer gone, however short the
    /// interval: a busy server on a slow link can take a second or two over a request, and a
    /// session ended under a user for that is worse than one noticed late.
    /// </summary>
    private static readonly TimeSpan LeastPatience = TimeSpan.FromSeconds(3);

    private readonly CancellationTokenSource _stopWatching = new();

    private SshNetChannel? _shell;
    private SshClient? _client;
    private Task? _watching;
    private bool _disposed;

    /// <inheritdoc/>
    public SshEndpoint Endpoint { get; private set; }

    /// <inheritdoc/>
    /// <remarks>
    /// False once <see cref="Disconnected"/> has an answer, even where the library still holds an open
    /// socket: a peer this transport has called gone is gone, whatever the socket says (QS111).
    /// </remarks>
    public bool IsConnected => _client is { IsConnected: true } && !_disconnected.Task.IsCompleted;

    /// <inheritdoc/>
    public Task<SshException?> Disconnected => _disconnected.Task;

    /// <summary>
    /// Where this session records what happened, or null to record nothing.
    ///
    /// <para>What reaches it is the shape of the exchange and never its content — see
    /// <see cref="SessionLog"/> for why that is a property of the surface rather than a rule
    /// somebody follows.</para>
    /// </summary>
    public SessionLog? Log { get; init; }

    /// <inheritdoc/>
    public TimeSpan KeepAlive { get; set; } = TimeSpan.Zero;

    /// <inheritdoc/>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <inheritdoc/>
    public IProgress<SshSignInStep>? SignIn { get; set; }

    /// <summary>
    /// The live client, for <see cref="SshChain"/> to open a channel on.
    ///
    /// <para><b>Internal, and it is the one crack in QS36's wall.</b> A jump host is a connection
    /// carried inside another, and carrying one needs the thing that has the connection. It is
    /// visible only inside this assembly, which is where the library is allowed to have a name at
    /// all — no caller above the seam can reach it, and <c>SeamTests</c> still holds.</para>
    /// </summary>
    internal SshClient? Client => _client;

    /// <inheritdoc/>
    public async ValueTask ConnectAsync(SshEndpoint endpoint, IReadOnlyList<SshCredential> credentials,
                                        SshHostKeyCheck? hostKey = null,
                                        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (credentials.Count == 0)
        {
            throw new SshException(
                SshFailureKind.NoMethodAccepted,
                $"No credential was offered for {endpoint}.",
                "A connection was asked for with nothing to identify the user by.",
                "Choose a key, a password or an agent for this host.");
        }

        Endpoint = endpoint;

        // Before anything can fail, including reading a key file: an attempt that never reached the
        // network is exactly the one a user cannot explain afterwards.
        Log?.Connecting(endpoint);

        foreach (SshCredential offered in credentials)
        {
            // What kind, never which. The credential itself has no route into the log.
            Log?.Offered(endpoint, Kind(offered));
        }

        AuthenticationMethod[] methods;

        try
        {
            methods = Offer(endpoint, credentials);
        }
        catch (SshException told)
        {
            // A key that cannot be read, or an agent holding nothing, fails before the network is
            // touched — and that is the failure a user is least able to explain afterwards, because
            // no server was involved in it and there is nothing on the far side to ask.
            Log?.Failed(endpoint, told.Kind, told.Message);

            throw;
        }

        // Every method but none is watched for a partial success, which the library reaches and
        // keeps to itself (QS113). None cannot partly succeed; it only asks what the server allows.
        IProgress<SshSignInStep>? signIn = SignIn;

        if (signIn is not null)
        {
            methods = [.. methods.Select(method => method is NoneAuthenticationMethod
                ? method
                : new Reported(method, (accepted, wanted) =>
                    signIn.Report(new SshSignInStep.Partly(endpoint, accepted, wanted))))];
        }

        ConnectionInfo connection = new(endpoint.Host, endpoint.Port, endpoint.User, methods)
        {
            Timeout = Timeout,
        };

        if (signIn is not null)
        {
            connection.AuthenticationBanner += (_, banner) =>
                signIn.Report(new SshSignInStep.Banner(endpoint, SshSignInStep.Printable(banner.BannerMessage)));
        }
        SshClient client = new(connection);

        if (KeepAlive > TimeSpan.Zero)
        {
            client.KeepAliveInterval = KeepAlive;
        }

        // The key is answered before anything is authenticated, because the library raises this
        // during the handshake. A caller who said nothing gets a refusal: a client that trusts an
        // unnamed key is a client with no host-key check, and the safe reading of silence is no.
        SshHostKeyVerdict verdict = SshHostKeyVerdict.Refuse;

        client.HostKeyReceived += (_, presented) =>
        {
            verdict = Ask(hostKey, endpoint, presented, cancellationToken);
            presented.CanTrust = verdict != SshHostKeyVerdict.Refuse;
        };

        long began = Stopwatch.GetTimestamp();

        try
        {
            await Reach(client, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            client.Dispose();

            Log?.Failed(endpoint, SshFailureKind.Cancelled, "the attempt was stopped");

            throw new SshException(
                SshFailureKind.Cancelled,
                $"The attempt to reach {endpoint} was stopped.",
                "Nothing was left half-done: the connection had not been established.");
        }
        catch (Exception failure)
        {
            client.Dispose();

            SshException told = verdict == SshHostKeyVerdict.Refuse && failure is SshConnectionException
                ? SshException.From(
                    SshFailureKind.HostKey,
                    $"The key {endpoint} presented was refused.",
                    failure,
                    "The connection was abandoned before anything was sent to that server.",
                    "Compare the fingerprint against one you trust before accepting it.")
                : Translate(endpoint, failure);

            Log?.Failed(endpoint, told.Kind, told.Message);

            // Only where the server is the thing that said no. `Refused` is a port with nothing
            // listening on it, and writing "auth-refused" for that would send a user hunting through
            // their keys for a failure that never reached an authentication exchange.
            if (told.Kind is SshFailureKind.NoMethodAccepted or SshFailureKind.CredentialRejected)
            {
                foreach (SshCredential credential in credentials)
                {
                    // Every one of them: the library offers each in turn and fails only when the
                    // last has been refused, so nothing here is being guessed at.
                    Log?.Refused(endpoint, Kind(credential));
                }
            }

            // The trace is written on the way out as well as on the way in, and this is the case it
            // exists for: an appliance that will not negotiate leaves nothing behind but what the
            // two sides offered.
            Traced(connection);

            throw told;
        }

        Log?.Connected(endpoint, Stopwatch.GetElapsedTime(began));
        Log?.Authenticated(endpoint);

        Traced(connection);

        _client = client;
        _client.ErrorOccurred += (_, error) =>
        {
            SshException dropped = Translate(endpoint, error.Exception);

            Log?.Failed(endpoint, dropped.Kind, dropped.Message);
            Log?.Disconnected(endpoint, expected: false);

            _disconnected.TrySetResult(dropped);
        };
    }

    /// <inheritdoc/>
    public ValueTask<IPtyChannel> OpenShellAsync(int columns, int rows,
                                                 CancellationToken cancellationToken = default)
    {
        SshClient client = Live();

        try
        {
            // Width and height in pixels are zero, which is what a client that measures in cells
            // says: a server that needs pixels asks the program, and a wrong number here is worse
            // than an absent one.
            ShellStream shell = client.CreateShellStream(
                TerminalType, (uint)columns, (uint)rows, 0, 0, BufferBytes);

            _shell = new SshNetChannel(shell, columns, rows);

            Log?.Channel(ChannelKind.Shell, opened: true);

            if (KeepAlive > TimeSpan.Zero && _shell.CanAskPeer)
            {
                _watching = Watch(_shell, KeepAlive, _stopWatching.Token);
            }

            return ValueTask.FromResult<IPtyChannel>(_shell);
        }
        catch (Exception failure)
        {
            // Diagnosed as a shell request rather than as a connection: by here the credentials
            // were accepted, so nothing about them is worth suggesting to the user.
            throw SshDiagnosis.Shell(Endpoint, failure);
        }
    }

    /// <inheritdoc/>
    public ValueTask<IFileTransferChannel> OpenFileTransferAsync(
        CancellationToken cancellationToken = default)
    {
        Live();

        cancellationToken.ThrowIfCancellationRequested();

        // A channel of this session and never a connection of its own: see SharedSftpSession for
        // why that takes doing, and SftpChannelTests for the server's own account of it.
        return SftpChannel.OpenAsync(_client!, Timeout, Log);
    }

    /// <summary>
    /// The best way this server will move a file: the subsystem where it offers one, and scp where
    /// it does not.
    ///
    /// <para><b>Not on <see cref="ISshTransport"/>, and that is the point.</b> Exactly three kinds
    /// of channel cross the seam, and scp needs a fourth — a command channel — which the seam does
    /// not carry and should not. So the fallback lives on the implementations that can actually run
    /// a command, and a caller reaching for it is choosing to depend on that.</para>
    /// </summary>
    /// <param name="cancellationToken">Abandons the attempt.</param>
    public async ValueTask<IFileCopy> OpenFileCopyAsync(
        CancellationToken cancellationToken = default)
    {
        Live();

        try
        {
            return new SftpFileCopy(
                await OpenFileTransferAsync(cancellationToken).ConfigureAwait(false));
        }
        catch (SshException refused) when (refused.Kind == SshFailureKind.ShellRefused)
        {
            // The subsystem is not there, which is the one case scp exists for. Any other failure
            // is a failure and is not quietly downgraded into a worse protocol. Recorded, so a log
            // says which of the two carried the files (QS130).
            Log?.Channel(ChannelKind.Command, opened: true);

            return new ScpFileCopy(new ScpChannel(_client!), refused.Message);
        }
    }

    /// <inheritdoc/>
    public ValueTask<IForwardedChannel> OpenForwardAsync(string host, int port,
                                                         CancellationToken cancellationToken = default)
    {
        Live();

        throw new SshException(
            SshFailureKind.ShellRefused,
            $"Forwarding to {host}:{port} is not implemented yet.",
            "quickshell has not built the channel, which is QS42.");
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        await _stopWatching.CancelAsync().ConfigureAwait(false);

        if (_watching is not null)
        {
            await _watching.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }

        _stopWatching.Dispose();

        if (_shell is not null)
        {
            await _shell.DisposeAsync().ConfigureAwait(false);

            Log?.Channel(ChannelKind.Shell, opened: false);
        }

        if (_client is not null)
        {
            // Told apart from the drop recorded in ErrorOccurred: a session that ended because
            // somebody closed it and one that ended on its own are the same line in a log that does
            // not distinguish them, and they are the whole question a report is asking.
            Log?.Disconnected(Endpoint, expected: true);
        }

        _client?.Dispose();
        _client = null;
        _disconnected.TrySetResult(null);
    }

    /// <summary>
    /// Connects through the library's synchronous entry point, on a thread of its own, with the
    /// token abandoning the wait rather than the connect.
    ///
    /// <para><b>Synchronous because only that one says which timeout it was</b> (QS112). Through
    /// <c>ConnectAsync</c> an address routed nowhere and a socket that accepts and says nothing both
    /// arrive as "Connection has timed out."; through <c>Connect()</c> the first is "Connection
    /// failed to establish within N milliseconds" and the second "Socket read operation has timed out
    /// after N milliseconds", and <see cref="SshDiagnosis"/> turns those into two kinds with two
    /// remedies.</para>
    ///
    /// <para><b>Cancelling still works, and costs a thread for at most <see cref="Timeout"/>.</b>
    /// <c>Connect()</c> takes no token, so a cancelled attempt returns at once while the connect
    /// carries on underneath until the caller's disposal of the client ends it or its own timeout
    /// does. The same trade <see cref="SshNetChannel.ReadAsync"/> makes for a read that cannot be
    /// cancelled; the abandoned attempt's failure is observed here so it never surfaces as an
    /// unobserved exception.</para>
    /// </summary>
    private static async Task Reach(SshClient client, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        Task connecting = Task.Run(client.Connect, CancellationToken.None);

        try
        {
            await connecting.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!connecting.IsCompleted)
        {
            _ = connecting.ContinueWith(abandoned => abandoned.Exception, CancellationToken.None,
                                        TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);

            throw;
        }
    }

    /// <summary>
    /// Asks the far end, every interval, something it has to answer, and calls the session dropped
    /// when three intervals go by without one.
    ///
    /// <para><b>The library's own keepalive keeps and does not detect, and is left running for
    /// that.</b> It sends without wanting a reply, so it holds a NAT mapping open and cannot tell a
    /// frozen host from a quiet one: a paused server stayed "connected" for minutes under it
    /// (QS111). This is the half that detects. Three missed intervals is OpenSSH's own
    /// <c>ServerAliveCountMax</c>, with a floor of <see cref="LeastPatience"/>.</para>
    ///
    /// <para>Only a question that went unanswered is a death. Any other failure of the request —
    /// the channel closed because the shell exited, the session already reported dropped — ends
    /// the watch and leaves the telling to whatever noticed first.</para>
    /// </summary>
    private async Task Watch(SshNetChannel shell, TimeSpan interval, CancellationToken stop)
    {
        TimeSpan patience = interval * 3 > LeastPatience ? interval * 3 : LeastPatience;

        while (!stop.IsCancellationRequested)
        {
            await Task.Delay(interval, stop).ConfigureAwait(false);

            // On a thread of its own: the library's request blocks until the answer or its own
            // timeout, and a frozen peer is exactly the case where neither comes soon.
            Task<bool> asked = Task.Run(shell.AskPeer, CancellationToken.None);
            Task first = await Task.WhenAny(asked, Task.Delay(patience, stop)).ConfigureAwait(false);

            stop.ThrowIfCancellationRequested();

            if (first == asked && asked.Exception?.InnerException is not SshOperationTimeoutException)
            {
                if (asked.IsFaulted)
                {
                    return;
                }

                continue;
            }

            SshException gone = new(
                SshFailureKind.Dropped,
                $"{Endpoint} stopped answering.",
                $"Nothing came back for {patience.TotalSeconds:0.#} seconds, though the connection is still open: " +
                "the host froze, or the network between went away without closing it.",
                "Reconnect when the host is reachable again.");

            Log?.Failed(Endpoint, gone.Kind, gone.Message);
            Log?.Disconnected(Endpoint, expected: false);

            _disconnected.TrySetResult(gone);
            shell.Lost(gone.Message);

            return;
        }
    }

    /// <summary>
    /// The negotiation, at trace level: the two version strings and, for each thing the two sides
    /// have to agree on, what this client offered and what was agreed.
    ///
    /// <para><b>What the server offered is not in here, and the log says so rather than leaving a
    /// blank.</b> SSH.NET keeps the peer's KEXINIT lists to itself — its <c>ConnectionInfo</c>
    /// exposes what this side supports and what was chosen, and nothing in between. Half the
    /// negotiation still diagnoses most of what this level exists for: an appliance that agrees on
    /// nothing leaves a record of everything this client was willing to speak, which is what turns
    /// "connection failed" into a list to compare against the server's config. The other half is
    /// QS128.</para>
    ///
    /// <para>Called after a failure as well as after a success, because a handshake that did not
    /// finish is the one nobody can reconstruct afterwards.</para>
    /// </summary>
    private void Traced(ConnectionInfo connection)
    {
        if (Log is null)
        {
            return;
        }

        Log.Versions(connection.ClientVersion ?? Unreported,
                     connection.ServerVersion ?? "none — the version exchange did not finish");

        Log.Negotiated("kex", Offered(connection.KeyExchangeAlgorithms), Unreported,
                       Agreed(connection.CurrentKeyExchangeAlgorithm));
        Log.Negotiated("host key", Offered(connection.HostKeyAlgorithms), Unreported,
                       Agreed(connection.CurrentHostKeyAlgorithm));
        Log.Negotiated("cipher", Offered(connection.Encryptions), Unreported,
                       Agreed(connection.CurrentServerEncryption));
        Log.Negotiated("mac", Offered(connection.HmacAlgorithms), Unreported,
                       Agreed(connection.CurrentServerHmacAlgorithm));
    }

    /// <summary>What this side was willing to speak, in the order it was willing to speak it.</summary>
    private static string Offered<T>(IDictionary<string, T> supported) =>
        supported.Count == 0 ? "none" : string.Join(',', supported.Keys);

    /// <summary>What was settled on, or the fact that nothing was.</summary>
    private static string Agreed(string? chosen) =>
        chosen is { Length: > 0 } settled ? settled : "none";

    /// <summary>
    /// What a credential is, for the log.
    ///
    /// <para>A shape and never a value: this is the only thing about a credential that any log in
    /// this client is given, and the type it returns cannot carry one.</para>
    /// </summary>
    private static CredentialKind Kind(SshCredential credential) =>
        credential switch
        {
            SshCredential.Password => CredentialKind.Password,
            SshCredential.PrivateKey => CredentialKind.PrivateKey,
            SshCredential.Agent => CredentialKind.Agent,
            _ => CredentialKind.Interactive,
        };

    /// <summary>The client, or a failure saying there is not one, rather than a null reference.</summary>
    private SshClient Live()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_client is null || !_client.IsConnected)
        {
            throw new SshException(
                SshFailureKind.Dropped,
                $"There is no connection to {Endpoint}.",
                "The session is not open, so there is nothing to open a channel on.");
        }

        return _client;
    }

    /// <summary>
    /// Runs the caller's host-key check inside the library's synchronous event.
    ///
    /// <para>Blocking is not a shortcut here, it is the only correct thing: the handshake is
    /// suspended at this instant and the verdict decides whether it continues. Returning early and
    /// answering later would mean the connection proceeded while the key was still a question.</para>
    /// </summary>
    private static SshHostKeyVerdict Ask(SshHostKeyCheck? check, SshEndpoint endpoint,
                                         HostKeyEventArgs presented, CancellationToken cancellationToken)
    {
        if (check is null)
        {
            return SshHostKeyVerdict.Refuse;
        }

        // The blob rather than the library's fingerprint string: known_hosts stores whole keys, and
        // this client computes its own digests from the same bytes the store holds.
        SshHostKey key = new(presented.HostKeyName, presented.HostKey);

        return check(endpoint, key, cancellationToken).AsTask().GetAwaiter().GetResult();
    }

    /// <summary>
    /// What to offer the server, in the order the design asks for.
    ///
    /// <para><b><c>none</c> goes first, as the protocol intends.</b> A server answers it with the
    /// list of methods it will actually accept, and that list is the most useful thing there is to
    /// tell a user who cannot get in — it is what turns "authentication failed" into "the server
    /// accepts publickey, keyboard-interactive". A server that accepts <c>none</c> outright has
    /// decided to let anyone in, which is its decision to have made.</para>
    ///
    /// <para><b>A password goes last.</b> Everything else is offered before it, so a key that would
    /// have worked is tried before a user is asked to type a secret. The relative order of
    /// everything else is the caller's, because past that point it is the server's policy that
    /// decides — see the tests, which offer two methods the wrong way round and watch the server
    /// run them its own way.</para>
    /// </summary>
    private static AuthenticationMethod[] Offer(SshEndpoint endpoint,
                                                IReadOnlyList<SshCredential> credentials)
    {
        List<AuthenticationMethod> offered = [new NoneAuthenticationMethod(endpoint.User)];

        // Agent keys before file keys. An agent key needs no prompt and a file key may, so trying
        // the agent first is the difference between a user typing a passphrase and not.
        offered.AddRange(credentials.OfType<SshCredential.Agent>()
                                    .Select(credential => Method(endpoint, credential)));

        offered.AddRange(credentials.Where(credential => credential is not SshCredential.Password
                                                         and not SshCredential.Agent)
                                    .Select(credential => Method(endpoint, credential)));

        offered.AddRange(credentials.OfType<SshCredential.Password>()
                                    .Select(credential => Method(endpoint, credential)));

        return [.. offered];
    }

    /// <summary>This client's credential, as the library's own authentication method.</summary>
    private static AuthenticationMethod Method(SshEndpoint endpoint, SshCredential credential) =>
        credential switch
        {
            // ToUnprotectedArray, and the comment QS44 asks for beside every one of them: the
            // library's only password constructor takes an array it keeps, so this is a copy in the
            // ordinary heap that nothing here can erase. The alternative is the string overload,
            // which is worse in every respect.
            SshCredential.Password password =>
                new PasswordAuthenticationMethod(endpoint.User, password.Secret.ToUnprotectedArray()),

            SshCredential.PrivateKey key =>
                new PrivateKeyAuthenticationMethod(endpoint.User, KeyFile(key)),

            SshCredential.Interactive interactive => Prompted(endpoint, interactive),

            SshCredential.Agent held => FromAgent(endpoint, held),

            _ => throw new SshException(
                SshFailureKind.NoMethodAccepted,
                $"{credential.GetType().Name} is not a credential this transport knows.",
                "This is a gap in quickshell rather than something the server refused."),
        };

    /// <summary>
    /// The agent's identities, as something the library can offer.
    ///
    /// <para>An agent that is not running, or is running and holding nothing, is refused by name.
    /// Silently offering nothing would reach the server as "this client had no credentials", and a
    /// user whose agent had quietly stopped would be told their key was rejected.</para>
    /// </summary>
    private static PrivateKeyAuthenticationMethod FromAgent(SshEndpoint endpoint,
                                                            SshCredential.Agent held)
    {
        SshAgent agent = new(held.Pipe);
        IReadOnlyList<AgentIdentity> identities = agent.Identities();

        if (held.Fingerprint is { Length: > 0 } wanted)
        {
            identities = [.. identities.Where(identity =>
                string.Equals(identity.Fingerprint, wanted.Replace("SHA256:", string.Empty,
                                                                   StringComparison.Ordinal),
                              StringComparison.Ordinal))];
        }

        if (identities.Count == 0)
        {
            throw new SshException(
                SshFailureKind.NoMethodAccepted,
                held.Fingerprint is null
                    ? "The agent is holding no keys."
                    : $"The agent is not holding {held.Fingerprint}.",
                $"Nothing on the {held.Pipe} pipe could be offered to {endpoint}.",
                "Load the key with ssh-add, or point this client at a key file instead.");
        }

        return new PrivateKeyAuthenticationMethod(
            endpoint.User,
            [.. identities.Select(identity => new AgentKeySource(agent, identity))]);
    }

    private static PrivateKeyFile KeyFile(SshCredential.PrivateKey key)
    {
        try
        {
            return key.CertificatePath is null
                ? new PrivateKeyFile(key.Path, key.Passphrase)
                : new PrivateKeyFile(key.Path, key.Passphrase, key.CertificatePath);
        }
        catch (Exception failure)
        {
            throw SshException.From(
                SshFailureKind.CredentialRejected,
                $"The key at {key.Path} could not be read.",
                failure,
                "The file is missing, is not a private key, or the passphrase is wrong.",
                "Check the path and the passphrase.");
        }
    }

    /// <summary>Keyboard-interactive, with the server's own prompts handed to the caller's handler.</summary>
    private static KeyboardInteractiveAuthenticationMethod Prompted(
        SshEndpoint endpoint, SshCredential.Interactive interactive)
    {
        KeyboardInteractiveAuthenticationMethod method = new(endpoint.User);

        method.AuthenticationPrompt += (_, asked) =>
        {
            foreach (AuthenticationPrompt prompt in asked.Prompts)
            {
                prompt.Response = interactive.Answer(prompt.Request, prompt.IsEchoed, CancellationToken.None)
                    .AsTask().GetAwaiter().GetResult();
            }
        };

        return method;
    }

    /// <summary>
    /// One of the library's authentication methods, run as it is, with a partial success reported
    /// on the way out.
    ///
    /// <para><b>A wrapper because the library has nowhere else to say it.</b> It reaches
    /// <c>PartialSuccess</c> — a key accepted, a second factor still wanted — and goes straight on
    /// to the next method without raising anything. Its <c>Authenticate</c> is public and virtual
    /// for exactly this kind of composition, so the method is still the library's own and none of
    /// the protocol is written here.</para>
    /// </summary>
    private sealed class Reported : AuthenticationMethod
    {
        private readonly AuthenticationMethod _inner;
        private readonly Action<string, IReadOnlyList<string>> _partly;

        internal Reported(AuthenticationMethod inner, Action<string, IReadOnlyList<string>> partly)
            : base(inner.Username)
        {
            _inner = inner;
            _partly = partly;
        }

        public override string Name => _inner.Name;

        public override AuthenticationResult Authenticate(Session session)
        {
            AuthenticationResult result = _inner.Authenticate(session);

            // The library reads what is allowed next from the method that just ran, so the wrapper
            // has to say it too or the second factor is never tried.
            AllowedAuthentications = _inner.AllowedAuthentications;

            if (result == AuthenticationResult.PartialSuccess)
            {
                _partly(Name, AllowedAuthentications ?? []);
            }

            return result;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// A library exception, as this client's failure. The rules live in <see cref="SshDiagnosis"/>,
    /// which is where the runs that produced them are written down.
    /// </summary>
    private static SshException Translate(SshEndpoint endpoint, Exception failure) =>
        SshDiagnosis.Translate(endpoint, failure);
}
