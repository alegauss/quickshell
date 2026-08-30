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

    private readonly TaskCompletionSource<SshException?> _disconnected =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private SshNetChannel? _shell;
    private SshClient? _client;
    private bool _disposed;

    /// <inheritdoc/>
    public SshEndpoint Endpoint { get; private set; }

    /// <inheritdoc/>
    public bool IsConnected => _client is { IsConnected: true };

    /// <inheritdoc/>
    public Task<SshException?> Disconnected => _disconnected.Task;

    /// <inheritdoc/>
    public TimeSpan KeepAlive { get; set; } = TimeSpan.Zero;

    /// <inheritdoc/>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

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

        AuthenticationMethod[] methods = Offer(endpoint, credentials);
        ConnectionInfo connection = new(endpoint.Host, endpoint.Port, endpoint.User, methods)
        {
            Timeout = Timeout,
        };
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

        try
        {
            await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            client.Dispose();
            throw new SshException(
                SshFailureKind.Cancelled,
                $"The attempt to reach {endpoint} was stopped.",
                "Nothing was left half-done: the connection had not been established.");
        }
        catch (Exception failure)
        {
            client.Dispose();

            throw verdict == SshHostKeyVerdict.Refuse && failure is SshConnectionException
                ? SshException.From(
                    SshFailureKind.HostKey,
                    $"The key {endpoint} presented was refused.",
                    failure,
                    "The connection was abandoned before anything was sent to that server.",
                    "Compare the fingerprint against one you trust before accepting it.")
                : Translate(endpoint, failure);
        }

        _client = client;
        _client.ErrorOccurred += (_, error) =>
            _disconnected.TrySetResult(Translate(endpoint, error.Exception));
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

        // Named rather than half-built: QS41 is the line, and a file pane against a stub would show
        // an empty home directory, which is a picture of a bug rather than of missing work.
        throw new SshException(
            SshFailureKind.ShellRefused,
            "File transfer over this connection is not implemented yet.",
            "quickshell has not built the channel, which is QS41.");
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

        if (_shell is not null)
        {
            await _shell.DisposeAsync().ConfigureAwait(false);
        }

        _client?.Dispose();
        _client = null;
        _disconnected.TrySetResult(null);
    }

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

        offered.AddRange(credentials.Where(credential => credential is not SshCredential.Password)
                                    .Select(credential => Method(endpoint, credential)));

        offered.AddRange(credentials.OfType<SshCredential.Password>()
                                    .Select(credential => Method(endpoint, credential)));

        return [.. offered];
    }

    /// <summary>This client's credential, as the library's own authentication method.</summary>
    private static AuthenticationMethod Method(SshEndpoint endpoint, SshCredential credential) =>
        credential switch
        {
            SshCredential.Password password =>
                new PasswordAuthenticationMethod(endpoint.User, password.Secret),

            SshCredential.PrivateKey key =>
                new PrivateKeyAuthenticationMethod(endpoint.User, KeyFile(key)),

            SshCredential.Interactive interactive => Prompted(endpoint, interactive),

            // QS43 is the line that adds it, and QS5 established the seam for doing so is public.
            // Until then this is refused by name rather than silently skipped, because a credential
            // that is quietly dropped looks to a user like a server that rejected their key.
            SshCredential.Agent => throw new SshException(
                SshFailureKind.NoMethodAccepted,
                "Keys held by an agent are not supported yet.",
                "quickshell cannot ask an agent to sign, so this credential could not be offered.",
                "Point at the key file directly until QS43 lands."),

            _ => throw new SshException(
                SshFailureKind.NoMethodAccepted,
                $"{credential.GetType().Name} is not a credential this transport knows.",
                "This is a gap in quickshell rather than something the server refused."),
        };

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
    /// A library exception, as this client's failure. The rules live in <see cref="SshDiagnosis"/>,
    /// which is where the runs that produced them are written down.
    /// </summary>
    private static SshException Translate(SshEndpoint endpoint, Exception failure) =>
        SshDiagnosis.Translate(endpoint, failure);
}
