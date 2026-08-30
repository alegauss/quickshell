namespace Quickshell.Transport;

/// <summary>
/// Where a session is going: a host, a port and the account on it.
///
/// <para>Three fields and no object whose lifetime somebody else manages. A protocol library's
/// connection-info type would carry the same three and a dozen more — timeouts, algorithm lists, a
/// socket factory — and every one of those is a reason the library cannot be replaced. What this
/// client needs to say about a destination is here; what a library needs to be told is the
/// implementation's business.</para>
/// </summary>
/// <param name="Host">A hostname or an address. Not an alias: resolving one is the client's own work.</param>
/// <param name="Port">The port to reach it on.</param>
/// <param name="User">The account to authenticate as.</param>
public readonly record struct SshEndpoint(string Host, int Port, string User)
{
    /// <summary>The port every SSH server listens on until somebody decides otherwise.</summary>
    public const int DefaultPort = 22;

    /// <summary>An endpoint with the usual port, which is the overwhelming case.</summary>
    public static SshEndpoint For(string host, string user, int port = DefaultPort)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentException.ThrowIfNullOrWhiteSpace(user);
        ArgumentOutOfRangeException.ThrowIfLessThan(port, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65535);

        return new SshEndpoint(host, port, user);
    }

    /// <summary>The way a person writes it, and the way an error message should say it.</summary>
    public override string ToString() =>
        Port == DefaultPort ? $"{User}@{Host}" : $"{User}@{Host}:{Port}";
}

/// <summary>
/// The key a server presented, in the two parts a user is ever shown.
///
/// <para>A fingerprint and an algorithm name, and deliberately not the key. Everything a client does
/// with a host key — compare it against what was seen before, print it for a person to check, refuse
/// on a mismatch — needs exactly these two, and a key object would be a live thing belonging to a
/// library that this client would then have to keep alive.</para>
/// </summary>
/// <param name="Algorithm">What the key is, as a person reads it: <c>ssh-ed25519</c> and the like.</param>
/// <param name="Fingerprint">The SHA-256 digest of the key, base64 as OpenSSH prints it, without the prefix.</param>
public readonly record struct SshHostKey(string Algorithm, string Fingerprint)
{
    /// <summary>The line OpenSSH would print, which is the form a user can compare against.</summary>
    public override string ToString() => $"{Algorithm} SHA256:{Fingerprint}";
}

/// <summary>
/// What a caller decided about the key a server presented.
///
/// <para>A verdict rather than a boolean, because the three answers are not two: a key nobody has
/// seen before is a different situation from a key that has changed, and a client that folded them
/// together would either nag on every first connection or say nothing when the thing host keys exist
/// to catch actually happens.</para>
/// </summary>
public enum SshHostKeyVerdict
{
    /// <summary>Refuse the connection. The default for anything a caller did not positively allow.</summary>
    Refuse,

    /// <summary>Proceed, and do not remember it.</summary>
    Accept,

    /// <summary>Proceed, and remember it as this host's key from now on.</summary>
    AcceptAndRemember,
}

/// <summary>
/// Asked once per connection, before anything is authenticated, with the key the server presented.
///
/// <para>It is a parameter of connecting and not a property to set afterwards, and that is the whole
/// point of putting it here: a seam that let a caller connect first and check the key second would
/// be a seam that makes the check optional, and an optional host-key check is no host-key check.</para>
/// </summary>
/// <param name="endpoint">Where the connection was going.</param>
/// <param name="key">What answered.</param>
/// <param name="cancellationToken">Cancels a check that is waiting for a person.</param>
public delegate ValueTask<SshHostKeyVerdict> SshHostKeyCheck(
    SshEndpoint endpoint, SshHostKey key, CancellationToken cancellationToken);
