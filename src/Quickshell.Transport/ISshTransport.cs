namespace Quickshell.Transport;

/// <summary>One thing in a remote directory, in the fields a file pane shows.</summary>
/// <param name="Name">The entry's own name, not a path.</param>
/// <param name="Length">Its size in bytes; zero for a directory.</param>
/// <param name="IsDirectory">Whether it can be descended into.</param>
/// <param name="Modified">When it last changed, as the server reported it.</param>
/// <param name="Permissions">The mode as a person reads it, <c>drwxr-xr-x</c> and the like.</param>
public readonly record struct RemoteEntry(string Name, long Length, bool IsDirectory,
                                          DateTimeOffset Modified, string Permissions);

/// <summary>
/// A channel carrying a file transfer session: the second of the three things that may cross the
/// seam.
///
/// <para>Every member is spelled in <see cref="System.IO.Stream"/> and paths. That is not a
/// simplification, it is the constraint: a library's own file-handle type would be a live object
/// belonging to the library, and holding one above the seam is how a file pane ends up unable to
/// compile against a different implementation.</para>
/// </summary>
public interface IFileTransferChannel : IAsyncDisposable
{
    /// <summary>What is in a directory, streamed rather than gathered: a home directory can be long.</summary>
    IAsyncEnumerable<RemoteEntry> ListAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>Opens a remote file for reading. The caller owns and disposes the stream.</summary>
    ValueTask<Stream> OpenReadAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>Creates or truncates a remote file for writing. The caller owns and disposes the stream.</summary>
    ValueTask<Stream> OpenWriteAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>Removes a file or an empty directory.</summary>
    ValueTask DeleteAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>Moves or renames, which on a remote filesystem is the same operation.</summary>
    ValueTask RenameAsync(string from, string to, CancellationToken cancellationToken = default);
}

/// <summary>
/// A channel carrying a connection forwarded through this one: the third of the three.
///
/// <para>This is what a jump host is, and QS5 proved the library can open one — a second handshake
/// completed inside a channel of the first, to a host whose own port was never used. It is also what
/// a port forward is, and the two are the same thing seen from different ends, which is why there is
/// one type here and not two.</para>
/// </summary>
public interface IForwardedChannel : IAsyncDisposable
{
    /// <summary>Where the far end of this channel goes, as it was asked for.</summary>
    (string Host, int Port) Destination { get; }

    /// <summary>The bytes, both ways. A duplex stream and nothing protocol-shaped.</summary>
    Stream Stream { get; }
}

/// <summary>
/// The seam. Everything above this line is written against these members and has never heard of a
/// protocol library.
///
/// <para><b>Why it exists before there is an implementation.</b> A seam decided afterwards is one
/// negotiated against code that already exists, and it loses every negotiation: the library's types
/// are already in the signatures, the library's exceptions are already being caught, and moving them
/// out is a rewrite nobody schedules. Deciding it first costs one file and settles the question
/// permanently.</para>
///
/// <para><b>Exactly three things may cross it</b>, and they are the entire surface the rest of the
/// client needs: a channel that behaves as an <see cref="IPtyChannel"/>, a channel carrying a file
/// transfer session, and a channel carrying a forwarded connection. A fourth arriving is not a new
/// method, it is evidence that the seam is in the wrong place, and should be treated as that
/// question rather than answered with an addition.</para>
///
/// <para><b>What may not cross, each of which looks harmless alone.</b> Exception types — see
/// <see cref="SshException"/> for the one that does, and for why the library's is not its inner.
/// Key objects: <see cref="SshHostKey"/> is an algorithm name and a fingerprint, which is everything
/// a client does with one. Connection-info structures: <see cref="SshEndpoint"/> is three fields, and
/// timeouts and algorithm lists are the implementation's business. And anything whose lifetime the
/// library manages, which is the general case the other three are instances of.</para>
///
/// <para><b>Two futures pay for this.</b> QS5's gap analysis concluded that SSH.NET can do
/// certificates and jump hosts and cannot do agents or <c>~/.ssh/config</c>; if a later question
/// answers the other way, a second implementation over libssh2 or a wrapped OpenSSH is a new class
/// rather than a rewrite. And <see cref="ReplayTransport"/> is the other: a client that can be
/// tested against a recorded session with no server anywhere.</para>
///
/// <para>Falsified when a search for a protocol library's namespace finds a hit outside this
/// assembly — which is a test, in <c>SeamTests</c>, and not a thing anybody has to remember.</para>
/// </summary>
public interface ISshTransport : IAsyncDisposable
{
    /// <summary>Where this transport is connected, or was asked to connect.</summary>
    SshEndpoint Endpoint { get; }

    /// <summary>Whether there is a live connection right now.</summary>
    bool IsConnected { get; }

    /// <summary>
    /// How often to ask the far end whether it is still there, or <see cref="TimeSpan.Zero"/> not to
    /// ask at all.
    ///
    /// <para><b>This is what tells a dead link from an idle one.</b> A network that goes away does
    /// not close the connection: the socket sits open and the operating system will wait a very long
    /// time before it says otherwise — long enough that a user has given up and restarted the client.
    /// A keepalive at the protocol's own level notices in seconds.</para>
    ///
    /// <para>It has a second job that is easy to miss and matters more in practice: traffic keeps a
    /// NAT mapping alive, which is what stops an idle session quietly dying after twenty minutes on
    /// a corporate link. That is not a failure anybody debugs successfully without knowing to look
    /// for it.</para>
    ///
    /// <para>Set before <see cref="ConnectAsync"/>; changing it afterwards is not defined.</para>
    /// </summary>
    TimeSpan KeepAlive { get; set; }

    /// <summary>
    /// Completes when the connection is gone, carrying why.
    ///
    /// <para>A task and not an event, for the reason <see cref="IPtyChannel.Closed"/> gives: a
    /// shutdown is a thing to await once, and an event with no subscriber at the moment it fires is
    /// how a dead session stays on screen looking alive.</para>
    /// </summary>
    Task<SshException?> Disconnected { get; }

    /// <summary>
    /// Connects, checks the host key, and authenticates — in that order, in one call.
    ///
    /// <para><b>One call and not three, deliberately.</b> Split apart, the host-key check becomes a
    /// step a caller can forget, and a caller who forgets it has written a client that connects to
    /// anything claiming to be the right host. Here there is no state in which this object is
    /// connected and unverified.</para>
    /// </summary>
    /// <param name="endpoint">Where to go.</param>
    /// <param name="credentials">
    /// What to offer, in order. More than one because a server may require more than one; an empty
    /// list is a caller that has not decided yet and is refused.
    /// </param>
    /// <param name="hostKey">
    /// What to do about the key the server presents. Null refuses every key that is not already
    /// known to the implementation, which is the safe reading of "the caller did not say".
    /// </param>
    /// <param name="cancellationToken">Abandons the attempt.</param>
    /// <exception cref="SshException">The connection did not happen, and why in words.</exception>
    ValueTask ConnectAsync(SshEndpoint endpoint, IReadOnlyList<SshCredential> credentials,
                           SshHostKeyCheck? hostKey = null,
                           CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens a shell with a pseudo-terminal on it, which arrives as the same four members a local
    /// shell arrives as.
    ///
    /// <para>This is the member the whole client was built around: the parser, the buffer, the
    /// renderer and the input map were written against <see cref="IPtyChannel"/> before there was
    /// any remote anything, so a remote shell is not a case any of them handles.</para>
    /// </summary>
    ValueTask<IPtyChannel> OpenShellAsync(int columns, int rows,
                                          CancellationToken cancellationToken = default);

    /// <summary>Opens a file transfer session over this connection.</summary>
    ValueTask<IFileTransferChannel> OpenFileTransferAsync(CancellationToken cancellationToken = default);

    /// <summary>Opens a channel to a host reachable from the far end, which is what a jump host is.</summary>
    ValueTask<IForwardedChannel> OpenForwardAsync(string host, int port,
                                                  CancellationToken cancellationToken = default);
}
