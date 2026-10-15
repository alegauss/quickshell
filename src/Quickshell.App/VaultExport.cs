using Quickshell.Transport;

namespace Quickshell.App;

/// <summary>
/// The saved sessions as a KeePass vault: the file a person consults when a password is forgotten
/// (QS306), written by <see cref="KdbxWriter"/>.
///
/// <para><b>Every session, saved password or not.</b> Each becomes one entry, titled by its name,
/// with the login and <c>ssh://user@host:port</c> it would connect as and the password
/// <see cref="SecretStore"/> holds for it — under its named credential where it names one (QS245),
/// else for its endpoint. One with nothing saved goes in with an empty password, so the vault is the
/// whole list of where the person goes. The sidebar's folders become the vault's groups.</para>
/// </summary>
public sealed class VaultExport : IDisposable
{
    /// <summary>What the vault's root group, and so the database, is called.</summary>
    public const string DatabaseName = "Quickshell sessions";

    private readonly List<Secret> _held = [];

    private VaultExport(SessionTree store, SecretStore? secrets)
    {
        Root = Group(store, store.Root, string.Empty, DatabaseName, secrets);
    }

    /// <summary>The vault's root group, holding every folder and session.</summary>
    public KdbxGroup Root { get; }

    /// <summary>How many sessions went in.</summary>
    public int Sessions { get; private set; }

    /// <summary>How many of them carry a saved password.</summary>
    public int WithPassword => _held.Count;

    /// <summary>
    /// Reads every session and every saved password, which the caller writes and then disposes, so
    /// the passwords are erased as soon as the vault is written.
    /// </summary>
    public static VaultExport Gather(SessionTree store, SecretStore? secrets)
    {
        ArgumentNullException.ThrowIfNull(store);

        return new VaultExport(store, secrets);
    }

    /// <summary>Writes the vault under a key derived from its passphrase.</summary>
    public void Write(string path, KdbxKey key) => KdbxWriter.Write(path, Root, key);

    /// <summary>Erases every password read.</summary>
    public void Dispose()
    {
        foreach (Secret secret in _held)
        {
            secret.Dispose();
        }
    }

    private KdbxGroup Group(SessionTree store, SessionNode folder, string path, string name, SecretStore? secrets)
    {
        List<KdbxEntry> entries = [];
        List<KdbxGroup> groups = [];

        foreach (SessionNode child in folder.Children)
        {
            string at = path.Length == 0 ? child.Name : $"{path}/{child.Name}";

            if (!child.IsSession)
            {
                groups.Add(Group(store, child, at, child.Name, secrets));
            }
            else if (store.Session(at) is { } session)
            {
                entries.Add(Entry(child.Name, session, secrets));
            }
        }

        return new KdbxGroup { Name = name, Entries = entries, Groups = groups };
    }

    private KdbxEntry Entry(string name, ResolvedSession session, SecretStore? secrets)
    {
        string user = session.User?.Value ?? Environment.UserName;
        int port = session.Port?.Value ?? SshEndpoint.DefaultPort;
        string? credential = session.Credential?.Value;

        Secret? password = credential is { Length: > 0 }
            ? secrets?.LoadNamed(credential)
            : secrets?.Load(SshEndpoint.For(session.Host, user, port));

        if (password is not null)
        {
            _held.Add(password);
        }

        Sessions++;

        return new KdbxEntry
        {
            Title = name,
            UserName = user,
            Url = $"ssh://{user}@{session.Host}:{port}",
            Notes = credential is { Length: > 0 }
                ? $"{session.Path}. Its password is the saved credential \"{credential}\", which every session naming it shares."
                : $"{session.Path}.",
            Password = password,
        };
    }
}
