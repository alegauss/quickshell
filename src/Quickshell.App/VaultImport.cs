using Quickshell.Transport;

namespace Quickshell.App;

/// <summary>What importing one remembered password would do.</summary>
public enum ImportState
{
    /// <summary>Nothing is remembered for it yet, so the vault's password is saved.</summary>
    New,

    /// <summary>The same password is already remembered.</summary>
    Same,

    /// <summary>A different password is remembered, which is replaced only where the person says so.</summary>
    Differs,
}

/// <summary>One password the vault can give a session, or a credential several sessions share.</summary>
/// <param name="Label">How it is named to the person: an account, or a credential and how many sessions use it.</param>
/// <param name="Credential">The named credential it is saved under, or null for the endpoint.</param>
/// <param name="Endpoint">The account it is saved for where there is no credential.</param>
/// <param name="Password">The vault's password, held by the vault.</param>
/// <param name="State">What saving it would do.</param>
public sealed record ImportItem(string Label, string? Credential, SshEndpoint Endpoint, Secret Password, ImportState State);

/// <summary>
/// The passwords a KeePass vault holds, matched to the saved sessions (QS308): what a new machine
/// needs, since DPAPI keeps every remembered password with the account that saved it.
///
/// <para><b>Matched by what the entry says, and nothing else.</b> An entry's URL, <c>ssh://user@host:port</c>
/// or a bare <c>host[:port]</c>, and its user name where the URL names none, against each session's host,
/// port and login as it would connect; so a vault this client did not write imports the same way,
/// as long as its entries say where they go. An entry that matches no session is listed and left.</para>
///
/// <para><b>Nothing remembered is overwritten without asking.</b> A password that differs from the one
/// already saved is a conflict the person settles; one that is the same is counted and not written.</para>
/// </summary>
public sealed class VaultImport
{
    private VaultImport(IReadOnlyList<ImportItem> items, IReadOnlyList<string> unmatched, int empty)
    {
        Items = items;
        Unmatched = unmatched;
        Empty = empty;
    }

    /// <summary>Every password that would reach a session, one per account or credential.</summary>
    public IReadOnlyList<ImportItem> Items { get; }

    /// <summary>The titles of the entries that match no saved session.</summary>
    public IReadOnlyList<string> Unmatched { get; }

    /// <summary>How many matching entries carry no password at all.</summary>
    public int Empty { get; }

    /// <summary>Matches the vault's entries to the store's sessions, against what is already remembered.</summary>
    public static VaultImport Plan(SessionTree store, KdbxVault vault, SecretStore secrets)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(vault);
        ArgumentNullException.ThrowIfNull(secrets);

        Dictionary<(string Host, int Port, string User), KdbxEntry> byAccount = [];
        List<KdbxEntry> unaddressed = [];

        foreach (KdbxEntry entry in vault.Entries)
        {
            if (Account(entry) is { } account)
            {
                byAccount.TryAdd(account, entry);
            }
            else
            {
                unaddressed.Add(entry);
            }
        }

        HashSet<KdbxEntry> used = [];
        Dictionary<string, (string Label, string? Credential, SshEndpoint Endpoint, KdbxEntry Entry, int Sessions)> targets =
            new(StringComparer.OrdinalIgnoreCase);

        foreach (ResolvedSession session in store.Sessions())
        {
            string user = session.User?.Value ?? Environment.UserName;
            int port = session.Port?.Value ?? SshEndpoint.DefaultPort;

            if (!byAccount.TryGetValue((session.Host.ToLowerInvariant(), port, user), out KdbxEntry? entry))
            {
                continue;
            }

            used.Add(entry);

            string? credential = session.Credential?.Value is { Length: > 0 } named ? named : null;
            SshEndpoint endpoint = SshEndpoint.For(session.Host, user, port);
            string key = credential is null ? endpoint.ToString() : "credential/" + credential;

            if (targets.TryGetValue(key, out var known))
            {
                targets[key] = known with { Sessions = known.Sessions + 1 };
            }
            else
            {
                targets[key] = (credential is null ? $"{user}@{session.Host}:{port}" : $"the credential \"{credential}\"",
                                credential, endpoint, entry, 1);
            }
        }

        List<ImportItem> items = [];
        int empty = 0;

        foreach ((string label, string? credential, SshEndpoint endpoint, KdbxEntry entry, int sessions) in targets.Values)
        {
            if (entry.Password is not { Length: > 0 } password)
            {
                empty++;

                continue;
            }

            using Secret? saved = credential is null ? secrets.Load(endpoint) : secrets.LoadNamed(credential);

            ImportState state = saved is null
                ? ImportState.New
                : saved.Bytes.SequenceEqual(password.Bytes) ? ImportState.Same : ImportState.Differs;

            string named = credential is null || sessions == 1 ? label : $"{label} ({sessions} sessions)";

            items.Add(new ImportItem(named, credential, endpoint, password, state));
        }

        List<string> unmatched = [.. byAccount.Values.Where(entry => !used.Contains(entry)).Concat(unaddressed)
                                              .Select(entry => entry.Title.Length > 0 ? entry.Title : entry.Url)];

        return new VaultImport(items, unmatched, empty);
    }

    /// <summary>
    /// Saves every new password, and every differing one whose label is in <paramref name="replace"/>.
    /// </summary>
    /// <returns>How many were saved.</returns>
    public int Apply(SecretStore secrets, IReadOnlySet<string> replace)
    {
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(replace);

        int saved = 0;

        foreach (ImportItem item in Items)
        {
            if (item.State == ImportState.New || (item.State == ImportState.Differs && replace.Contains(item.Label)))
            {
                if (item.Credential is { } credential)
                {
                    secrets.SaveNamed(credential, item.Password);
                }
                else
                {
                    secrets.Save(item.Endpoint, item.Password);
                }

                saved++;
            }
        }

        return saved;
    }

    /// <summary>
    /// The account an entry says it is for: its URL's host and port, and the URL's user or else the
    /// entry's user name; null where the URL is another scheme, or no user can be found.
    /// </summary>
    public static (string Host, int Port, string User)? Account(KdbxEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        string url = entry.Url.Trim();
        string host;
        int port = SshEndpoint.DefaultPort;
        string user = entry.UserName.Trim();

        if (url.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase))
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || uri.Host.Length == 0)
            {
                return null;
            }

            host = uri.Host;
            port = uri.Port > 0 ? uri.Port : SshEndpoint.DefaultPort;

            if (uri.UserInfo is { Length: > 0 } info)
            {
                user = Uri.UnescapeDataString(info.Split(':')[0]);
            }
        }
        else if (url.Length > 0 && !url.Contains("://", StringComparison.Ordinal) && !url.Contains('/', StringComparison.Ordinal))
        {
            int colon = url.LastIndexOf(':');

            if (colon > 0 && int.TryParse(url[(colon + 1)..], System.Globalization.NumberStyles.None,
                                          System.Globalization.CultureInfo.InvariantCulture, out int given))
            {
                host = url[..colon];
                port = given;
            }
            else
            {
                host = url;
            }

            int at = host.LastIndexOf('@');

            if (at > 0)
            {
                user = host[..at];
                host = host[(at + 1)..];
            }
        }
        else
        {
            return null;
        }

        return user.Length == 0 ? null : (host.ToLowerInvariant(), port, user);
    }
}
