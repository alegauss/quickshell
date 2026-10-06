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
/// agent where one is running. A password is not asked for here yet — QS126 carries the prompt.</para>
///
/// <para><b>Every connection passes the host-key check.</b> The caller hands in a
/// <see cref="TrustOnFirstUse"/>, so a known key is accepted silently, a changed one is refused with
/// no question, and an unknown one is asked about — by the window, which is the one that has a
/// person in front of it.</para>
/// </summary>
public sealed class RemoteShell : IShellSession
{
    private readonly ISshTransport _transport;
    private readonly IPtyChannel _channel;

    private RemoteShell(ISshTransport transport, IPtyChannel channel, SessionPipeline pipeline)
    {
        _transport = transport;
        _channel = channel;

        Pipeline = pipeline;
    }

    /// <inheritdoc/>
    public SessionPipeline Pipeline { get; }

    /// <summary>The transport, for whatever else a pane opens over the same connection.</summary>
    public ISshTransport Transport => _transport;

    /// <summary>Connects a saved session and opens its shell into the model.</summary>
    /// <param name="session">The session as the store resolves it.</param>
    /// <param name="trust">The host-key check every hop passes.</param>
    /// <param name="emulator">The model its output is parsed into.</param>
    /// <param name="damage">The signal the pane's render loop is asleep on.</param>
    /// <param name="columns">The grid the pane settled on.</param>
    /// <param name="rows">Its rows.</param>
    /// <param name="cancellationToken">Gives up.</param>
    /// <exception cref="SshException">The connection did not happen, and why in words.</exception>
    public static async Task<RemoteShell> OpenAsync(ResolvedSession session, TrustOnFirstUse trust,
                                                    Emulator emulator, DamageSignal damage,
                                                    int columns, int rows,
                                                    CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(trust);
        ArgumentNullException.ThrowIfNull(emulator);
        ArgumentNullException.ThrowIfNull(damage);

        SshEndpoint target = SshEndpoint.For(session.Host, session.User?.Value ?? Environment.UserName,
                                             session.Port?.Value ?? SshEndpoint.DefaultPort);

        IReadOnlyList<SshCredential> credentials = Credentials(session);

        // A keepalive that detects, not only one that keeps (QS111): a frozen host is noticed.
        TimeSpan keepAlive = TimeSpan.FromSeconds(15);

        ISshTransport transport = session.JumpHost is { } jump
            ? new SshChain([
                new SshHop(Through(jump.Value, target.User), credentials, trust.CheckAsync),
                new SshHop(target, credentials, trust.CheckAsync),
              ]) { KeepAlive = keepAlive }
            : new SshNetTransport { KeepAlive = keepAlive };

        try
        {
            await transport.ConnectAsync(target, credentials, trust.CheckAsync, cancellationToken)
                           .ConfigureAwait(false);

            IPtyChannel channel = await transport
                .OpenShellAsync(Math.Max(1, columns), Math.Max(1, rows), cancellationToken)
                .ConfigureAwait(false);

            SessionPipeline pipeline = SessionPipeline.Start(channel, emulator, damage: damage);

            // Its own and never a folder's (SessionNode.PostLogin), typed once the shell is there.
            if (session.PostLogin is { Length: > 0 } after)
            {
                await pipeline.TypeAsync(Encoding.UTF8.GetBytes(after + "\r"), cancellationToken)
                              .ConfigureAwait(false);
            }

            return new RemoteShell(transport, channel, pipeline);
        }
        catch
        {
            await transport.DisposeAsync().ConfigureAwait(false);

            throw;
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

    private static string Expand(string path) =>
        path.StartsWith('~')
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                           path[1..].TrimStart('/', '\\'))
            : path;

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        // The pipeline first, for the reason LocalSession gives; then the shell, then the connection.
        await Pipeline.DisposeAsync().ConfigureAwait(false);
        await _channel.DisposeAsync().ConfigureAwait(false);
        await _transport.DisposeAsync().ConfigureAwait(false);
    }
}
