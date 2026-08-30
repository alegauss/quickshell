using Renci.SshNet;

namespace Quickshell.Transport;

/// <summary>
/// One machine on the way, with its own way in.
///
/// <para><b>Credentials are per hop and never shared by default.</b> A chain that quietly reused the
/// first hop's key would be offering a bastion's credential to whatever is at the end of it, which
/// is precisely the thing a bastion exists to prevent. A caller that wants the same key on every hop
/// passes it on every hop, deliberately.</para>
/// </summary>
/// <param name="Endpoint">Where this hop is, as reached from the hop before it.</param>
/// <param name="Credentials">What to offer here, and here only.</param>
/// <param name="HostKey">
/// What to do about this machine's key. Its own, checked as any other host's would be — a chain does
/// not inherit trust from the hop that carried it.
/// </param>
/// <param name="ProxyCommand">
/// A program to run to reach this hop, instead of connecting to it. Set, it replaces the way this hop
/// is reached and not the hop itself: the host key is still this machine's and is still checked. This
/// is OpenSSH's <c>ProxyCommand</c>, and it takes precedence over the hop before it exactly as it
/// does there.
/// </param>
public readonly record struct SshHop(SshEndpoint Endpoint, IReadOnlyList<SshCredential> Credentials,
                                     SshHostKeyCheck? HostKey = null, string? ProxyCommand = null);

/// <summary>
/// A session reached through one or more bastions.
///
/// <para><b>A jump host is not a proxy setting.</b> It is a connection nested inside another:
/// authenticate to the bastion, open a channel from it to the next machine, and run an entire second
/// SSH session over that channel. The target's host key is the target's own and the bastion never
/// sees the target's traffic in the clear — which is the whole reason this is worth doing properly
/// rather than tunnelling and hoping.</para>
///
/// <para><b>A chain is the same operation repeated.</b> Three hops is one hop three times, so there
/// is one loop here and no special case for the common length. The design asks that a three-hop
/// chain use the same code path as a one-hop chain, and the way to guarantee that is for there to be
/// only one path.</para>
///
/// <para><b>Every failure names its hop.</b> A bare connection-refused with no hop named is the
/// least useful message a chain can produce, and a user with three bastions and one error has
/// nothing to act on. Each attempt is wrapped with which hop it was and how many there are.</para>
/// </summary>
public sealed class SshChain : ISshTransport
{
    private readonly List<SshClient> _bastions = [];
    private readonly List<ForwardedPortLocal> _channels = [];
    private readonly List<ProxyCommandChannel> _proxies = [];
    private readonly IReadOnlyList<SshHop> _through;

    private SshNetTransport? _last;
    private bool _disposed;

    /// <summary>
    /// A transport that reaches the last of these hops through all the ones before it.
    /// </summary>
    /// <param name="hops">
    /// Every machine, in order, ending with the one the session is on. One hop is an ordinary
    /// connection and takes the same path as three.
    /// </param>
    public SshChain(IReadOnlyList<SshHop> hops)
    {
        ArgumentNullException.ThrowIfNull(hops);

        if (hops.Count == 0)
        {
            throw new SshException(
                SshFailureKind.Unrecognised,
                "A chain with no hops in it has nowhere to connect to.",
                "This is a caller that built a route and left it empty.");
        }

        _through = hops;
    }

    /// <inheritdoc/>
    public SshEndpoint Endpoint => _through[^1].Endpoint;

    /// <inheritdoc/>
    public bool IsConnected => _last?.IsConnected == true;

    /// <inheritdoc/>
    public TimeSpan KeepAlive { get; set; } = TimeSpan.Zero;

    /// <inheritdoc/>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <inheritdoc/>
    public Task<SshException?> Disconnected =>
        _last?.Disconnected ?? Task.FromResult<SshException?>(null);

    /// <summary>How many hops there are, the target included.</summary>
    public int Hops => _through.Count;

    /// <summary>
    /// Connects along the chain, each hop through the one before it.
    ///
    /// <para>The arguments are ignored in favour of the hops this was built with, because a chain's
    /// destination is the chain. They are on the interface because every transport has them, and
    /// a caller that passed a different endpoint here would be describing a route this is not.</para>
    /// </summary>
    public async ValueTask ConnectAsync(SshEndpoint endpoint, IReadOnlyList<SshCredential> credentials,
                                        SshHostKeyCheck? hostKey = null,
                                        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Each hop is reached at the address the hop before it can see. The first is reached
        // directly; every other is reached through a channel opened on its predecessor.
        SshEndpoint reachable = _through[0].Endpoint;

        for (int hop = 0; hop < _through.Count; hop++)
        {
            SshHop step = _through[hop];
            bool last = hop == _through.Count - 1;

            ProxyCommandChannel? running = null;

            try
            {
                // A proxy command replaces the way this hop is reached, so it is consulted before
                // the tunnel the hop before it opened — which is what OpenSSH does, and what a user
                // who wrote both into one config is expecting.
                if (step.ProxyCommand is { Length: > 0 } program)
                {
                    ProxyCommandChannel proxy =
                        await ProxyCommandChannel.StartAsync(program, step.Endpoint, cancellationToken)
                                                 .ConfigureAwait(false);

                    _proxies.Add(proxy);

                    running = proxy;
                    reachable = proxy.Reachable;
                }

                if (last)
                {
                    SshNetTransport session = new() { KeepAlive = KeepAlive, Timeout = Timeout };

                    await session.ConnectAsync(reachable with { User = step.Endpoint.User },
                                               step.Credentials, step.HostKey, cancellationToken)
                                 .ConfigureAwait(false);

                    _last = session;

                    return;
                }

                reachable = await Carry(step, reachable, _through[hop + 1].Endpoint, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (SshException failure)
            {
                throw Named(failure, hop, step, running);
            }
        }
    }

    /// <inheritdoc/>
    public ValueTask<IPtyChannel> OpenShellAsync(int columns, int rows,
                                                 CancellationToken cancellationToken = default) =>
        Live().OpenShellAsync(columns, rows, cancellationToken);

    /// <inheritdoc/>
    public ValueTask<IFileTransferChannel> OpenFileTransferAsync(
        CancellationToken cancellationToken = default) =>
        Live().OpenFileTransferAsync(cancellationToken);

    /// <inheritdoc/>
    public ValueTask<IForwardedChannel> OpenForwardAsync(string host, int port,
                                                         CancellationToken cancellationToken = default) =>
        Live().OpenForwardAsync(host, port, cancellationToken);

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_last is not null)
        {
            await _last.DisposeAsync().ConfigureAwait(false);
            _last = null;
        }

        // Innermost first: a bastion torn down before the session it is carrying would end that
        // session as a dropped link rather than as a close.
        for (int channel = _channels.Count - 1; channel >= 0; channel--)
        {
            _channels[channel].Stop();
            _channels[channel].Dispose();
        }

        for (int bastion = _bastions.Count - 1; bastion >= 0; bastion--)
        {
            _bastions[bastion].Dispose();
        }

        for (int proxy = _proxies.Count - 1; proxy >= 0; proxy--)
        {
            await _proxies[proxy].DisposeAsync().ConfigureAwait(false);
        }

        _channels.Clear();
        _bastions.Clear();
        _proxies.Clear();
    }

    /// <summary>
    /// Connects to one bastion and opens a way through it to the next machine.
    ///
    /// <para><b>The channel is bound to a local port, and that is the library's shape rather than
    /// this design's.</b> OpenSSH carries a jump over the client's own standard streams; SSH.NET's
    /// public surface offers a forwarded port, so the nested session connects to <c>127.0.0.1</c>
    /// and travels from there inside the bastion's channel. End to end the traffic is still the
    /// target's own encryption and the bastion still sees none of it — but the bound port is
    /// reachable by anything running as this user while the session lasts, which QS119 carries.</para>
    /// </summary>
    /// <returns>Where the next hop can now be reached.</returns>
    private async ValueTask<SshEndpoint> Carry(SshHop step, SshEndpoint reachable, SshEndpoint next,
                                               CancellationToken cancellationToken)
    {
        SshNetTransport bastion = new() { KeepAlive = KeepAlive, Timeout = Timeout };

        await bastion.ConnectAsync(reachable with { User = step.Endpoint.User }, step.Credentials,
                                   step.HostKey, cancellationToken).ConfigureAwait(false);

        SshClient client = bastion.Client
            ?? throw new SshException(
                SshFailureKind.Dropped,
                $"The connection to {step.Endpoint} did not survive being opened.",
                "There is nothing to carry the next hop through.");

        _bastions.Add(client);

        // Port zero: the operating system chooses one that is free, so two chains in one process do
        // not fight over a number somebody picked.
        ForwardedPortLocal channel = new("127.0.0.1", 0, next.Host, (uint)next.Port);

        client.AddForwardedPort(channel);
        channel.Start();

        _channels.Add(channel);

        return SshEndpoint.For("127.0.0.1", next.User, (int)channel.BoundPort);
    }

    /// <summary>
    /// The same failure, saying which hop it was.
    ///
    /// <para>The hop's real name and not the address it was reached at: a user who wrote
    /// <c>ProxyJump bastion</c> is looking for the word "bastion", and telling them 127.0.0.1
    /// refused would be telling them about this client's plumbing.</para>
    /// </summary>
    private SshException Named(SshException failure, int hop, SshHop step,
                               ProxyCommandChannel? running)
    {
        // What the proxy command printed is the only account it gives of its own failure: without
        // it, a program that could not reach anything looks from here like a link that dropped for
        // no reason at all.
        string printed = running?.Printed ?? string.Empty;

        string means = printed.Length == 0
            ? failure.Means
            : $"{failure.Means} The proxy command said: {printed}";

        return new SshException(
            failure.Kind,
            $"Hop {hop + 1} of {_through.Count}, {step.Endpoint.Host}: {failure.Message}",
            means,
            failure.Remedy,
            failure.Origin);
    }

    private SshNetTransport Live()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        return _last ?? throw new SshException(
            SshFailureKind.Dropped,
            $"The chain to {Endpoint} is not connected.",
            $"None of its {_through.Count} hops is open.");
    }
}
