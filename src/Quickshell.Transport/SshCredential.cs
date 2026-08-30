namespace Quickshell.Transport;

/// <summary>
/// One prompt a server asked and a person has to answer, which is what keyboard-interactive is.
/// </summary>
/// <param name="prompt">The server's own words. Shown as written: it is the server that knows what it wants.</param>
/// <param name="echoed">Whether the answer may be shown as it is typed. False for anything secret.</param>
/// <param name="cancellationToken">Cancels a prompt nobody is going to answer.</param>
/// <returns>What to send back.</returns>
public delegate ValueTask<string> SshPrompt(string prompt, bool echoed, CancellationToken cancellationToken);

/// <summary>
/// How to prove who you are, in this client's vocabulary rather than a library's.
///
/// <para><b>A closed set, and each case is a thing a user chose in a settings dialogue.</b> A
/// protocol library models the same ground as authentication-method objects it constructs, owns and
/// disposes; handing one of those across the seam would mean the library is holding state on behalf
/// of a caller that cannot name its type. These are values: they can be built before there is a
/// connection, held in a profile, and read back.</para>
///
/// <para>Several may be offered in order, because a server may demand more than one — QS5 found an
/// account under <c>AuthenticationMethods publickey,keyboard-interactive</c> on the first sshd it
/// asked. So <see cref="ISshTransport.ConnectAsync"/> takes a list and not a choice.</para>
/// </summary>
public abstract record SshCredential
{
    /// <summary>Not for deriving outside this assembly: the set is closed on purpose.</summary>
    private protected SshCredential()
    {
    }

    /// <summary>A password typed by a person.</summary>
    /// <param name="Secret">The password. Held as a string because that is what the far end will be sent.</param>
    public sealed record Password(string Secret) : SshCredential;

    /// <summary>
    /// A private key on disk, optionally with a certificate signed by an authority the server trusts.
    /// </summary>
    /// <param name="Path">Where the key file is.</param>
    /// <param name="Passphrase">What unlocks it, or null where it is not encrypted.</param>
    /// <param name="CertificatePath">
    /// The signed certificate, or null for a plain key. QS5 connected to an account with no
    /// <c>authorized_keys</c> at all this way, which is the case a client that only knows about keys
    /// cannot reach.
    /// </param>
    public sealed record PrivateKey(string Path, string? Passphrase = null, string? CertificatePath = null)
        : SshCredential;

    /// <summary>
    /// A key held by an agent, which signs without the key ever being read.
    ///
    /// <para>For a key on a smart card or a hardware token this is not a convenience — it is the
    /// only route there is, because such a key cannot be extracted by anybody, including its
    /// owner.</para>
    /// </summary>
    /// <param name="Pipe">Which agent; Windows' own unless a caller names another.</param>
    /// <param name="Fingerprint">
    /// One identity of the agent's, by the fingerprint <c>ssh-add -l</c> prints, or null for all of
    /// them.
    ///
    /// <para><b>Naming one matters more than it looks.</b> A server allows a small number of
    /// authentication attempts — six by default — and a user with ten identities loaded will be cut
    /// off before the right one is reached, having never been asked anything. So where a session
    /// names its key, only that one is offered.</para>
    /// </param>
    public sealed record Agent(string Pipe = SshAgent.OpenSshPipe, string? Fingerprint = null)
        : SshCredential;

    /// <summary>
    /// Whatever the server asks, answered as it asks it. The second factor, and anything else a
    /// server invents.
    /// </summary>
    /// <param name="Answer">Called once per prompt, in the order the server sends them.</param>
    public sealed record Interactive(SshPrompt Answer) : SshCredential;
}
