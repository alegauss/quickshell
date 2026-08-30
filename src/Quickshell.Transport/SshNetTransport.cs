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
    public async ValueTask ConnectAsync(SshEndpoint endpoint, IReadOnlyList<SshCredential> credentials,
                                        SshHostKeyCheck? hostKey = null,
                                        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (credentials.Count == 0)
        {
            throw new SshException(SshFailureKind.Authentication,
                                   $"no credential was offered for {endpoint}");
        }

        Endpoint = endpoint;

        AuthenticationMethod[] methods = [.. credentials.Select(credential => Method(endpoint, credential))];
        ConnectionInfo connection = new(endpoint.Host, endpoint.Port, endpoint.User, methods);
        SshClient client = new(connection);

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
            throw new SshException(SshFailureKind.Cancelled, $"the connection to {endpoint} was abandoned");
        }
        catch (Exception failure)
        {
            client.Dispose();

            throw verdict == SshHostKeyVerdict.Refuse && failure is SshConnectionException
                ? SshException.From(SshFailureKind.HostKey,
                                    $"the key offered by {endpoint} was refused", failure)
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
            throw Translate(Endpoint, failure);
        }
    }

    /// <inheritdoc/>
    public ValueTask<IFileTransferChannel> OpenFileTransferAsync(
        CancellationToken cancellationToken = default)
    {
        Live();

        // Named rather than half-built: QS41 is the line, and a file pane against a stub would show
        // an empty home directory, which is a picture of a bug rather than of missing work.
        throw new SshException(SshFailureKind.Protocol,
                               "file transfer over this connection is not implemented yet");
    }

    /// <inheritdoc/>
    public ValueTask<IForwardedChannel> OpenForwardAsync(string host, int port,
                                                         CancellationToken cancellationToken = default)
    {
        Live();

        throw new SshException(SshFailureKind.Protocol,
                               $"forwarding to {host}:{port} is not implemented yet");
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
            throw new SshException(SshFailureKind.Dropped, $"there is no connection to {Endpoint}");
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

        // Already base64 and already SHA-256; the trailing padding is dropped because OpenSSH prints
        // it without, and a fingerprint a user cannot compare against what ssh showed them is a
        // fingerprint that does not do its job.
        SshHostKey key = new(presented.HostKeyName, presented.FingerPrintSHA256.TrimEnd('='));

        return check(endpoint, key, cancellationToken).AsTask().GetAwaiter().GetResult();
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
                SshFailureKind.Authentication,
                "keys held by an agent are not supported yet"),

            _ => throw new SshException(SshFailureKind.Authentication,
                                        $"{credential.GetType().Name} is not a credential this transport knows"),
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
            throw SshException.From(SshFailureKind.Authentication,
                                    $"the key at {key.Path} could not be read", failure);
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
    /// A library exception, as this client's failure.
    ///
    /// <para>Coarse on purpose. QS39 is the line that makes a refused key read differently from a
    /// refused port, and it will do it here — the value of doing the translation at the seam now is
    /// that no assembly above ever learns to catch the other kind.</para>
    /// </summary>
    private static SshException Translate(SshEndpoint endpoint, Exception failure) => failure switch
    {
        SshAuthenticationException =>
            SshException.From(SshFailureKind.Authentication,
                              $"{endpoint} did not accept the credentials offered", failure),

        SshConnectionException =>
            SshException.From(SshFailureKind.Dropped, $"the connection to {endpoint} ended", failure),

        SshOperationTimeoutException =>
            SshException.From(SshFailureKind.Unreachable, $"{endpoint} did not answer in time", failure),

        System.Net.Sockets.SocketException =>
            SshException.From(SshFailureKind.Unreachable, $"{endpoint} could not be reached", failure),

        ProxyException =>
            SshException.From(SshFailureKind.Unreachable,
                              $"the proxy in front of {endpoint} refused", failure),

        OperationCanceledException =>
            SshException.From(SshFailureKind.Cancelled, $"the work against {endpoint} was abandoned", failure),

        _ => SshException.From(SshFailureKind.Protocol,
                               string.Format(CultureInfo.InvariantCulture,
                                             "{0} failed in a way this client does not recognise", endpoint),
                               failure),
    };
}
