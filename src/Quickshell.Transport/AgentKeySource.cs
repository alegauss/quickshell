using Renci.SshNet;
using Renci.SshNet.Security;

namespace Quickshell.Transport;

/// <summary>
/// One agent-held key, offered to the library as something it can sign with but never read.
///
/// <para>This is the seam QS5 found and QS43 spends: <see cref="IPrivateKeySource"/> is public and
/// asks only for host algorithms, and <see cref="HostAlgorithm"/> asks only for a public blob and a
/// <c>Sign</c>. Neither wants the private key, which is the whole reason an agent can be put behind
/// them at all — and the reason a key on a hardware token, which cannot be read by anybody, works
/// here exactly as a key in a file does.</para>
/// </summary>
internal sealed class AgentKeySource : IPrivateKeySource
{
    /// <summary>
    /// Builds the algorithms one identity can sign under.
    ///
    /// <para>An RSA key gets three, and the order is the point: the two SHA-2 names first, because a
    /// modern server refuses the SHA-1 one outright and a client that offered it first would burn
    /// one of the server's small number of authentication attempts to be told so. Everything else
    /// signs under its own name and has nothing to choose.</para>
    /// </summary>
    public AgentKeySource(SshAgent agent, AgentIdentity identity)
    {
        Identity = identity;

        HostKeyAlgorithms = identity.Algorithm == "ssh-rsa"
            ?
            [
                new AgentHostAlgorithm("rsa-sha2-512", agent, identity, SshAgent.RsaSha512),
                new AgentHostAlgorithm("rsa-sha2-256", agent, identity, SshAgent.RsaSha256),
                new AgentHostAlgorithm("ssh-rsa", agent, identity, 0),
            ]
            : [new AgentHostAlgorithm(identity.Algorithm, agent, identity, 0)];
    }

    /// <summary>Which key of the agent's this is.</summary>
    public AgentIdentity Identity { get; }

    /// <inheritdoc/>
    public IReadOnlyCollection<HostAlgorithm> HostKeyAlgorithms { get; }
}

/// <summary>
/// A signature that happens somewhere else.
///
/// <para><see cref="Data"/> is the public blob the agent listed, so the library can say which key it
/// is offering. <see cref="Sign"/> hands the bytes to the agent and returns what comes back — which
/// is already the wire form, because the agent speaks the same protocol the connection does.</para>
/// </summary>
internal sealed class AgentHostAlgorithm(string name, SshAgent agent, AgentIdentity identity, uint flags)
    : HostAlgorithm(name)
{
    /// <inheritdoc/>
    public override byte[] Data => identity.Blob.ToArray();

    /// <inheritdoc/>
    public override byte[] Sign(byte[] data) => agent.Sign(identity.Blob.Span, data, flags);

    /// <summary>
    /// Never called on a client's own key, and refused rather than guessed at.
    ///
    /// <para>Verifying is what a server does with a client's signature and what a client does with a
    /// <em>host</em> key. This object is a client identity, so nothing verifies through it — and
    /// answering true to be helpful would be answering true to a question nobody asked correctly.</para>
    /// </summary>
    public override bool VerifySignature(byte[] data, byte[] signature) => false;
}
