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
/// The key a server presented.
///
/// <para><b>Bytes, not an object.</b> QS36 kept a key object out of this on the grounds that it
/// would be a live thing belonging to a library — that still holds, and a blob is not one. QS42
/// needed the blob: <c>known_hosts</c> stores whole keys and a client that held only a fingerprint
/// could compare against its own store and never against the user's.</para>
///
/// <para>The fingerprints are computed here rather than taken from the library, so the digest a user
/// compares against what <c>ssh</c> printed is this client's own arithmetic over the same bytes.</para>
/// </summary>
/// <param name="Algorithm">What the key is, as a person reads it: <c>ssh-ed25519</c> and the like.</param>
/// <param name="Key">The key blob, exactly as the wire carried it and as <c>known_hosts</c> stores it.</param>
public readonly record struct SshHostKey(string Algorithm, ReadOnlyMemory<byte> Key)
{
    /// <summary>
    /// The SHA-256 digest, base64 without padding — the form <c>ssh</c> prints after
    /// <c>SHA256:</c> and so the only form a user can actually compare against.
    /// </summary>
    public string Fingerprint =>
        Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(Key.Span)).TrimEnd('=');

    /// <summary>
    /// The MD5 digest in colon-separated hex, which is what older tools and a great many runbooks
    /// still print. Offered beside the SHA-256 one because a user comparing against a wiki page
    /// written in 2014 has only this to compare with.
    /// </summary>
    public string LegacyFingerprint =>
        Convert.ToHexStringLower(System.Security.Cryptography.MD5.HashData(Key.Span))
               .Chunk(2)
               .Select(pair => new string(pair))
               .Aggregate((left, right) => $"{left}:{right}");

    /// <summary>How <c>known_hosts</c> spells the key itself: the algorithm and the blob in base64.</summary>
    public string Stored => $"{Algorithm} {Convert.ToBase64String(Key.Span)}";

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
