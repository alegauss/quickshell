using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Quickshell.Transport;

/// <summary>What the store says about a key a server has just presented.</summary>
public enum KnownHostVerdict
{
    /// <summary>Nothing is stored for this host and this algorithm. Trust on first use decides.</summary>
    Unknown,

    /// <summary>Stored, and the same. Connect with no interaction at all.</summary>
    Matches,

    /// <summary>
    /// Stored, and different. This is what an interception looks like, and it is also what a
    /// rebuilt server looks like — and a client cannot tell those apart, which is why it stops.
    /// </summary>
    Changed,

    /// <summary>Marked <c>@revoked</c>. Not a question: this key is known and known to be bad.</summary>
    Revoked,
}

/// <summary>
/// The user's own <c>known_hosts</c>, read and written in OpenSSH's format.
///
/// <para><b>Their file, not ours.</b> A client with a private store of its own makes a user maintain
/// two, and the second one is always the one that is out of date. So this reads the file
/// <c>ssh</c> already wrote and writes lines <c>ssh</c> can read back — including the hashed
/// entries <c>HashKnownHosts</c> produces, which a client that could not read them would treat as
/// an empty store and re-ask about every host the user already trusts.</para>
///
/// <para><b>Several keys for one host is normal.</b> A server offers ed25519 and RSA and ECDSA, and
/// a user who has connected with two clients has two of them stored. So a key is looked up by host
/// <em>and</em> algorithm: a host with an ed25519 entry presenting an RSA key is
/// <see cref="KnownHostVerdict.Unknown"/> — a new key to learn — and never
/// <see cref="KnownHostVerdict.Changed"/>, which would cry interception at an ordinary Tuesday.</para>
/// </summary>
public sealed class KnownHosts
{
    /// <summary>The marker on a line naming a certificate authority rather than a host's own key.</summary>
    private const string Authority = "@cert-authority";

    /// <summary>The marker on a key that is known and known to be bad.</summary>
    private const string Revoked = "@revoked";

    /// <summary>The prefix OpenSSH puts on a hashed hostname.</summary>
    private const string Hashed = "|1|";

    private readonly List<Entry> _entries = [];

    private KnownHosts(string path, IEnumerable<Entry> entries)
    {
        Path = path;
        _entries.AddRange(entries);
    }

    /// <summary>One line of the file, parsed.</summary>
    /// <param name="Marker">Empty, <c>@cert-authority</c> or <c>@revoked</c>.</param>
    /// <param name="Patterns">The host field, as written: names, a hashed entry, or both.</param>
    /// <param name="Algorithm">The key type.</param>
    /// <param name="Key">The key blob.</param>
    private readonly record struct Entry(string Marker, string Patterns, string Algorithm, byte[] Key);

    /// <summary>Where <c>ssh</c> keeps it, which is where this looks unless told otherwise.</summary>
    public static string DefaultPath =>
        System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh", "known_hosts");

    /// <summary>The file this store reads and writes.</summary>
    public string Path { get; }

    /// <summary>How many entries were understood. A file of comments reads as an empty store.</summary>
    public int Count => _entries.Count;

    /// <summary>
    /// Reads a store, or an empty one where the file is not there.
    ///
    /// <para>A missing file is not an error: a user who has never used <c>ssh</c> on this machine
    /// has no <c>known_hosts</c>, and their first connection should ask rather than fail.</para>
    /// </summary>
    public static KnownHosts ReadFrom(string? path = null)
    {
        string file = path ?? DefaultPath;

        return !File.Exists(file)
            ? new KnownHosts(file, [])
            : new KnownHosts(file, File.ReadAllLines(file).Select(Parse).OfType<Entry>());
    }

    /// <summary>
    /// What this store says about a key.
    /// </summary>
    /// <param name="endpoint">Where the connection was going; the port matters to the lookup.</param>
    /// <param name="key">What the server presented.</param>
    /// <param name="stored">
    /// The key that was there instead, where the verdict is <see cref="KnownHostVerdict.Changed"/>.
    /// It is what a warning must show beside the new one: a user comparing two fingerprints can
    /// recognise a server they rebuilt last week, and a user shown only the new one cannot.
    /// </param>
    public KnownHostVerdict Check(SshEndpoint endpoint, SshHostKey key, out SshHostKey? stored)
    {
        stored = null;

        string host = Pattern(endpoint);
        Entry[] mine = [.. _entries.Where(entry => Covers(entry, host))];

        if (mine.Any(entry => entry.Key.AsSpan().SequenceEqual(key.Key.Span)
                              && entry.Marker == Revoked))
        {
            return KnownHostVerdict.Revoked;
        }

        Entry[] sameAlgorithm =
            [.. mine.Where(entry => entry.Marker.Length == 0
                                    && string.Equals(entry.Algorithm, key.Algorithm, StringComparison.Ordinal))];

        if (sameAlgorithm.Length == 0)
        {
            return KnownHostVerdict.Unknown;
        }

        if (sameAlgorithm.Any(entry => entry.Key.AsSpan().SequenceEqual(key.Key.Span)))
        {
            return KnownHostVerdict.Matches;
        }

        stored = new SshHostKey(sameAlgorithm[0].Algorithm, sameAlgorithm[0].Key);

        return KnownHostVerdict.Changed;
    }

    /// <summary>
    /// Remembers a key, appending a line <c>ssh</c> can read.
    ///
    /// <para>Appended rather than rewritten. The file is the user's and may hold entries this client
    /// did not understand — a marker from a newer OpenSSH, a comment somebody wrote — and rewriting
    /// it would be this client silently deciding what of a user's own file survives.</para>
    /// </summary>
    public void Add(SshEndpoint endpoint, SshHostKey key)
    {
        string line = $"{Pattern(endpoint)} {key.Stored}";

        _entries.Add(new Entry(string.Empty, Pattern(endpoint), key.Algorithm, key.Key.ToArray()));

        string? directory = System.IO.Path.GetDirectoryName(Path);

        if (directory is { Length: > 0 })
        {
            Directory.CreateDirectory(directory);
        }

        // A newline before it where the file does not end in one, because a file whose last line
        // has no terminator would otherwise get this one joined onto it.
        string opening = File.Exists(Path) && File.ReadAllBytes(Path) is { Length: > 0 } existing
                         && existing[^1] != (byte)'\n'
            ? "\n"
            : string.Empty;

        File.AppendAllText(Path, opening + line + "\n", new UTF8Encoding(false));
    }

    /// <summary>
    /// Forgets every key stored for a host, rewriting the file without those lines.
    ///
    /// <para>This is the deliberate act a changed key requires. It is not something a dialog does on
    /// a user's behalf while they click Continue — the whole point of refusing a changed key is that
    /// getting past it should cost more than a reflex.</para>
    /// </summary>
    /// <returns>How many lines were removed.</returns>
    public int Forget(SshEndpoint endpoint)
    {
        string host = Pattern(endpoint);

        if (!File.Exists(Path))
        {
            return 0;
        }

        string[] lines = File.ReadAllLines(Path);
        List<string> kept = [];
        int removed = 0;

        foreach (string line in lines)
        {
            if (Parse(line) is { } entry && Covers(entry, host))
            {
                removed++;
                continue;
            }

            kept.Add(line);
        }

        if (removed > 0)
        {
            File.WriteAllText(Path, string.Join("\n", kept) + (kept.Count > 0 ? "\n" : string.Empty),
                              new UTF8Encoding(false));

            _entries.RemoveAll(entry => Covers(entry, host));
        }

        return removed;
    }

    /// <summary>
    /// How <c>known_hosts</c> names a host.
    ///
    /// <para>The bracket form for anything but port 22, which is OpenSSH's own spelling and the
    /// reason a client that ignored the port would trust the key of a different service on the same
    /// machine.</para>
    /// </summary>
    private static string Pattern(SshEndpoint endpoint) =>
        endpoint.Port == SshEndpoint.DefaultPort
            ? endpoint.Host
            : string.Format(CultureInfo.InvariantCulture, "[{0}]:{1}", endpoint.Host, endpoint.Port);

    /// <summary>Whether an entry's host field names this host, hashed or in the clear.</summary>
    private static bool Covers(Entry entry, string host)
    {
        foreach (string pattern in entry.Patterns.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            if (pattern.StartsWith(Hashed, StringComparison.Ordinal))
            {
                if (MatchesHash(pattern, host))
                {
                    return true;
                }

                continue;
            }

            if (string.Equals(pattern, host, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether a hashed entry is this host: HMAC-SHA1 over the name, keyed by the entry's own salt.
    ///
    /// <para>Reading these is not optional. <c>HashKnownHosts</c> is on by default on several
    /// distributions, so a client that skipped them would look at a full file, see nothing it
    /// recognised, and ask the user to trust every host they already trust — training them to click
    /// through the one dialog that must never become a reflex.</para>
    /// </summary>
    private static bool MatchesHash(string pattern, string host)
    {
        string[] parts = pattern.Split('|', StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length != 3)
        {
            return false;
        }

        try
        {
            byte[] salt = Convert.FromBase64String(parts[1]);
            byte[] expected = Convert.FromBase64String(parts[2]);
            byte[] actual = HMACSHA1.HashData(salt, Encoding.UTF8.GetBytes(host));

            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException)
        {
            // A line this client cannot read is a line it does not act on. Treating an unparseable
            // entry as a match would be the worst possible reading of it.
            return false;
        }
    }

    /// <summary>One line, or null where it is a comment, blank, or something this cannot read.</summary>
    private static Entry? Parse(string line)
    {
        string text = line.Trim();

        if (text.Length == 0 || text.StartsWith('#'))
        {
            return null;
        }

        string marker = string.Empty;

        if (text.StartsWith(Authority, StringComparison.Ordinal)
            || text.StartsWith(Revoked, StringComparison.Ordinal))
        {
            marker = text.StartsWith(Authority, StringComparison.Ordinal) ? Authority : Revoked;
            text = text[marker.Length..].TrimStart();
        }

        string[] fields = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (fields.Length < 3)
        {
            return null;
        }

        try
        {
            return new Entry(marker, fields[0], fields[1], Convert.FromBase64String(fields[2]));
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
