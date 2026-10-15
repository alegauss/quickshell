using System.IO;
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

    private readonly bool _keys;

    /// <summary>Each key file attached so far, by its full path, and the entry it went on (QS345).</summary>
    private readonly Dictionary<string, string> _attached = new(StringComparer.OrdinalIgnoreCase);

    private readonly List<string> _missing = [];

    private VaultExport(SessionTree store, SecretStore? secrets, bool keys)
    {
        _keys = keys;
        Root = Group(store, store.Root, string.Empty, DatabaseName, secrets);
    }

    /// <summary>How many key files went in as attachments (QS345).</summary>
    public int KeyFiles => _attached.Count;

    /// <summary>The key files sessions name that were not there to attach (QS345).</summary>
    public IReadOnlyList<string> MissingKeys => _missing;

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
    /// <param name="store">The saved sessions.</param>
    /// <param name="secrets">Where their passwords are.</param>
    /// <param name="keys">
    /// Whether the key files sessions name go in as attachments (QS345): each file once, on the first
    /// entry that names it, with its <c>.pub</c> beside it where there is one, and a note on the rest.
    /// </param>
    public static VaultExport Gather(SessionTree store, SecretStore? secrets, bool keys = false)
    {
        ArgumentNullException.ThrowIfNull(store);

        return new VaultExport(store, secrets, keys);
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

        string notes = credential is { Length: > 0 }
            ? $"{session.Path}. Its password is the saved credential \"{credential}\", which every session naming it shares."
            : $"{session.Path}.";

        List<KdbxAttachment> attachments = [];

        if (_keys && session.Key?.Value is { Length: > 0 } named)
        {
            string key = Path.GetFullPath(RemoteShell.Expand(named));

            if (_attached.TryGetValue(key, out string? first))
            {
                notes += $" Its key file, {Path.GetFileName(key)}, is attached to the entry {first}.";
            }
            else if (File.Exists(key))
            {
                attachments.Add(new KdbxAttachment(Path.GetFileName(key), File.ReadAllBytes(key)));

                if (File.Exists(key + ".pub"))
                {
                    attachments.Add(new KdbxAttachment(Path.GetFileName(key) + ".pub", File.ReadAllBytes(key + ".pub")));
                }

                _attached[key] = session.Path;
                notes += $" Its key file, {Path.GetFileName(key)}, is attached.";
            }
            else if (!_missing.Contains(key, StringComparer.OrdinalIgnoreCase))
            {
                _missing.Add(key);
            }
        }

        return new KdbxEntry
        {
            Title = name,
            UserName = user,
            Url = $"ssh://{user}@{session.Host}:{port}",
            Notes = notes,
            Password = password,
            Attachments = attachments,
        };
    }
}
