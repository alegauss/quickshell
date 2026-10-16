using System.Globalization;
using System.Text.RegularExpressions;

namespace Quickshell.App;

/// <summary>
/// One field of the session dialog, and where its value came from.
///
/// <para><b>Inherited and unset are different states, and a field that conflated them would be the
/// dialog's central lie.</b> A user looking at a session that connects as the wrong account needs to
/// see that the account came from a folder and which folder — otherwise the only way to find out is
/// to walk the tree, and that is where an evening goes.</para>
///
/// <para><b>Overriding is an act, not a side effect of the value being displayed.</b> A field shows
/// what it inherits; it takes its own value only when <see cref="Override"/> is called. A dialog that
/// copied inherited values into the node on save would turn every folder default into a hundred
/// hard-coded copies the first time anybody opened a session and pressed OK.</para>
/// </summary>
public sealed class EditableField
{
    internal EditableField(string name, string label, Source<string>? inherited)
    {
        Name = name;
        Label = label;
        Inherited = inherited;
    }

    /// <summary>The property this stands for, spelled as <see cref="SessionSettings"/> spells it.</summary>
    public string Name { get; }

    /// <summary>What the dialog calls it.</summary>
    public string Label { get; }

    /// <summary>What the folders above say, and which one said it. Null where nothing does.</summary>
    public Source<string>? Inherited { get; }

    /// <summary>What this session says instead, once somebody has said it.</summary>
    public string? Own { get; private set; }

    /// <summary>Whether this session sets the field itself.</summary>
    public bool IsOverridden { get; private set; }

    /// <summary>Whether the value in force came from somewhere above.</summary>
    public bool IsInherited => !IsOverridden && Inherited is not null;

    /// <summary>The value in force, or null where nothing sets it anywhere.</summary>
    public string? Effective => IsOverridden ? Own : Inherited?.Value;

    /// <summary>
    /// What the dialog puts beside the field, which is the answer to "why is it this?" before the
    /// question is asked.
    /// </summary>
    public string Explains =>
        IsOverridden ? "set here"
        : Inherited is { } source ? $"inherited from {source.From}"
        : "not set";

    /// <summary>Takes the field over with a value of this session's own.</summary>
    public void Override(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        Own = value;
        IsOverridden = true;
    }

    /// <summary>Gives the field back to whatever it inherits.</summary>
    public void Inherit()
    {
        Own = null;
        IsOverridden = false;
    }
}

/// <summary>How a session signs in, as the session dialog offers it (QS270).</summary>
public enum SignInMethod
{
    /// <summary>The agent and the keys OpenSSH looks for, then a password where those are refused.</summary>
    KeyOrAgent,

    /// <summary>A password, asked for or remembered under a saved credential's name.</summary>
    Password,

    /// <summary>One key file the session names.</summary>
    KeyFile,
}

/// <summary>The groups the session dialog folds its less-asked fields into (QS270).</summary>
public enum SessionGroupName
{
    /// <summary>What it is reached through.</summary>
    JumpHost,

    /// <summary>The forwards it starts.</summary>
    Tunnels,

    /// <summary>What it types once the shell is there.</summary>
    AfterLogin,

    /// <summary>The scheme, the terminal type, the scrollback and reconnecting.</summary>
    Terminal,
}

/// <summary>
/// The session dialog's model: what it asks, what it assumes, and what it refuses.
///
/// <para><b>It asks for a host and nothing else.</b> Every other field is inherited, defaulted, or
/// left unset, and a dialog demanding twelve answers for a machine on the local network is one that
/// makes simple work feel heavy — an impression formed once and not revised. What the store could
/// have inherited, this never requires.</para>
///
/// <para><b>Everything the dialog decides is here rather than in the window.</b> The window arranges
/// these fields; it does not know what a field means, when one is inherited, or which commands are
/// refused. That separation is what lets the rules be tested against the rules rather than against a
/// visual tree.</para>
/// </summary>
public sealed class SessionEditor
{
    /// <summary>
    /// Commands that carry a password, matched by the three shapes that actually appear.
    ///
    /// <para>Each is a real way people put a secret in a post-login command, and each is refused with
    /// the credential store named — because a user who is told "no" and not told "instead" will find
    /// a worse way. This is not a filter that catches everything: a determined user can always type
    /// a secret. It catches the three spellings somebody reaches for first.</para>
    /// </summary>
    private static readonly (Regex Pattern, string What)[] Secrets =
    [
        (new Regex(@"\bsshpass\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
         "sshpass puts the password on a command line every process on the machine can read"),

        (new Regex(@"\bsudo\s+(-\w+\s+)*-\w*S\b",
                   RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
         "sudo -S reads the password from what this would type"),

        // No word boundary in front: the spelling that actually turns up is an environment variable
        // like DB_PASSWORD, where the underscore means there is no boundary to anchor to.
        (new Regex(@"pass(word|wd)?\s*[:=]\s*\S",
                   RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
         "this looks like a password written into the command"),
    ];

    private readonly SessionTree _tree;
    private readonly List<EditableField> _fields = [];
    private SignInMethod _signIn;

    private SessionEditor(SessionTree tree, string path, SessionNode? existing)
    {
        _tree = tree;
        Path = path;
        IsNew = existing is null;

        Name = existing?.Name ?? string.Empty;
        Host = existing?.Host ?? string.Empty;
        PostLogin = existing?.PostLogin;
        Tags = [.. existing?.Tags ?? []];
        Forwards = existing?.Forwards ?? [];

        IReadOnlyDictionary<string, Source<string>> inherited = tree.Inherits(path);
        SessionSettings own = existing?.Settings ?? new SessionSettings();

        Add(nameof(SessionSettings.User), "User", own.User);
        Add(nameof(SessionSettings.Port), "Port", Text(own.Port));
        Add(nameof(SessionSettings.Key), "Key file", own.Key);
        Add(nameof(SessionSettings.JumpHost), "Jump host", own.JumpHost);
        Add(nameof(SessionSettings.Credential), "Saved credential", own.Credential);
        Add(nameof(SessionSettings.Scheme), "Colour scheme", own.Scheme);
        Add(nameof(SessionSettings.TerminalType), "Terminal type", own.TerminalType);
        Add(nameof(SessionSettings.Scrollback), "Scrollback", Text(own.Scrollback));

        // Spelled as the store's inheritance reports it, so what a folder hands down reads the same
        // as what this session says (QS229). Until it was here, a dialog save dropped a Reconnect
        // somebody had set by hand, because only the fields shown were written back.
        Add(nameof(SessionSettings.Reconnect), "Reconnect after a drop",
            own.Reconnect is { } again ? (again ? "true" : "false") : null);

        // What the fields already say, read the way a connection reads them: a key file wins, then a
        // remembered password, then whatever the agent and ~/.ssh offer (QS270).
        _signIn = Field(nameof(SessionSettings.Key)).Effective is { Length: > 0 } ? SignInMethod.KeyFile
            : Field(nameof(SessionSettings.Credential)).Effective is { Length: > 0 } ? SignInMethod.Password
            : SignInMethod.KeyOrAgent;

        void Add(string name, string label, string? mine)
        {
            EditableField field = new(name, label,
                                      inherited.TryGetValue(name, out Source<string> from)
                                          ? from
                                          : null);

            if (mine is not null)
            {
                field.Override(mine);
            }

            _fields.Add(field);
        }
    }

    /// <summary>Where in the tree this session is, or will be.</summary>
    public string Path { get; }

    /// <summary>Whether this is a session being created rather than one being changed.</summary>
    public bool IsNew { get; }

    /// <summary>What it is called. Defaulted from the host where the user leaves it alone.</summary>
    public string Name { get; set; }

    /// <summary>The machine. The one thing this dialog cannot supply for the user.</summary>
    public string Host { get; set; }

    /// <summary>
    /// What to type after login, which this session either has or has not. It is never taken from a
    /// folder — see <see cref="SessionNode.PostLogin"/>.
    /// </summary>
    public string? PostLogin { get; set; }

    /// <summary>Labels a search can find it by.</summary>
    public IList<string> Tags { get; }

    /// <summary>The forwards this session starts with, which the dialog lists and the file edits (QS69).</summary>
    public IReadOnlyList<Quickshell.Transport.ForwardSpec> Forwards { get; }

    /// <summary>The folder it is in, or will be, as a path; empty at the top level.</summary>
    public string Folder => Path.LastIndexOf('/') is var slash and > 0 ? Path[..slash] : string.Empty;

    /// <summary>Every folder in the store, the top level first as the empty path, which a new session can go in.</summary>
    public IReadOnlyList<string> Folders
    {
        get
        {
            List<string> folders = [string.Empty];

            Collect(_tree.Root, string.Empty);

            return folders;

            void Collect(SessionNode node, string above)
            {
                foreach (SessionNode child in node.Children.Where(child => !child.IsSession))
                {
                    string at = above.Length == 0 ? child.Name : $"{above}/{child.Name}";

                    folders.Add(at);
                    Collect(child, at);
                }
            }
        }
    }

    /// <summary>
    /// The same new session in another folder (QS270): what was typed carried over, and what it
    /// inherits read again from there, since a folder is what hands values down.
    /// </summary>
    /// <exception cref="InvalidOperationException">The session is saved already; moving one is the sidebar's.</exception>
    public SessionEditor InFolder(string folder)
    {
        if (!IsNew)
        {
            throw new InvalidOperationException("A saved session is moved from the sidebar, not from its dialog.");
        }

        string leaf = Path[(Path.LastIndexOf('/') + 1)..];

        SessionEditor moved = Creating(_tree, folder, leaf.Length > 0 ? leaf : "New session");

        moved.Name = Name;
        moved.Host = Host;
        moved.PostLogin = PostLogin;

        foreach (string tag in Tags)
        {
            moved.Tags.Add(tag);
        }

        foreach (EditableField field in _fields.Where(field => field.IsOverridden))
        {
            moved.Field(field.Name).Override(field.Own!);
        }

        moved._signIn = _signIn;

        return moved;
    }

    /// <summary>
    /// How this session signs in (QS270): the agent and the usual keys, a password, or one key file.
    ///
    /// <para><b>A choice over the two fields the store already has</b>, Key and Credential, and
    /// nothing written of its own. Choosing one gives the other back to what it inherits, so the
    /// file holds what a hand edit making the same choice would.</para>
    /// </summary>
    public SignInMethod SignIn
    {
        get => _signIn;

        set
        {
            _signIn = value;

            if (value != SignInMethod.KeyFile)
            {
                Field(nameof(SessionSettings.Key)).Inherit();
            }

            if (value != SignInMethod.Password)
            {
                Field(nameof(SessionSettings.Credential)).Inherit();
            }
        }
    }

    /// <summary>What the chosen way of signing in does, in a sentence under the choice.</summary>
    public string SignInSays => _signIn switch
    {
        SignInMethod.Password =>
            "Asks for the password when it connects. Name a saved credential to have Windows remember it.",
        SignInMethod.KeyFile => "Signs in with this one key file, and offers nothing else.",
        _ => "Tries the Windows SSH agent, then your keys in ~/.ssh. A password is asked for only if those are refused.",
    };

    /// <summary>What a group of the dialog holds, in the one line its header shows while it is shut.</summary>
    public string Summary(SessionGroupName group) => group switch
    {
        SessionGroupName.JumpHost => Said(Field(nameof(SessionSettings.JumpHost)), "None, a direct connection"),
        SessionGroupName.Tunnels => Forwards.Count switch
        {
            0 => "None",
            1 => Forwards[0].ToString(),
            _ => string.Create(CultureInfo.InvariantCulture, $"{Forwards.Count} forwards"),
        },
        SessionGroupName.AfterLogin => string.IsNullOrWhiteSpace(PostLogin) ? "Nothing typed" : PostLogin.Trim(),
        _ => string.Join(", ",
                         Field(nameof(SessionSettings.Scheme)).Effective is { Length: > 0 } scheme ? scheme : "Default scheme",
                         Toggle(Field(nameof(SessionSettings.Reconnect)).Effective) == true
                             ? "reconnects after a drop"
                             : "no reconnect"),
    };

    /// <summary>A field's value in a summary: what it is and, where a folder set it, which one.</summary>
    private static string Said(EditableField field, string none) =>
        field.Effective is not { Length: > 0 } value ? none
        : field.IsInherited ? $"{value}, from {field.Inherited!.Value.From}"
        : value;

    /// <summary>Every field the dialog can show, in the order it shows them.</summary>
    public IReadOnlyList<EditableField> Fields => _fields;

    /// <summary>
    /// What the dialog says about a post-login command before it will accept one, which is what it
    /// does rather than what it is. Empty where there is no command.
    /// </summary>
    public string PostLoginWarning =>
        string.IsNullOrWhiteSpace(PostLogin)
            ? string.Empty
            : "This is sent to the shell after login exactly as though you had typed it.";

    /// <summary>
    /// Everything wrong with the dialog as it stands, in the words the user is shown. Empty means it
    /// can be saved.
    /// </summary>
    public IReadOnlyList<string> Complaints
    {
        get
        {
            List<string> wrong = [];

            if (string.IsNullOrWhiteSpace(Host))
            {
                wrong.Add("A session needs a host to connect to.");
            }

            // A new session named like one already in its folder would replace it on save, and
            // nobody asked for that from a dialog titled "New session".
            if (IsNew && (Name.Length > 0 ? Name : Host.Trim()) is { Length: > 0 } called
                && _tree.Find(Under(Path, called)) is not null)
            {
                wrong.Add($"There is already something called {called} here. Give this one another name.");
            }

            if (Field(nameof(SessionSettings.Port)) is { IsOverridden: true, Own: { } port }
                && !IsPort(port))
            {
                wrong.Add($"A port is a number from 1 to 65535, and {port} is not.");
            }

            if (Field(nameof(SessionSettings.Reconnect)) is { IsOverridden: true, Own: { Length: > 0 } again }
                && Toggle(again) is null)
            {
                wrong.Add($"Reconnect after a drop is true or false, and {again} is neither. Leave it empty to "
                          + "take what the folder says.");
            }

            // The choice and the fields agree, or a save would not do what the dialog showed (QS270).
            if (SignIn == SignInMethod.KeyFile && Field(nameof(SessionSettings.Key)).Effective is not { Length: > 0 })
            {
                wrong.Add("Sign in with a key file needs the key file's path.");
            }

            if (SignIn != SignInMethod.KeyFile && Field(nameof(SessionSettings.Key)) is { IsInherited: true } handed)
            {
                wrong.Add($"{handed.Inherited!.Value.From} hands down the key file {handed.Inherited.Value.Value}, "
                          + "so this session signs in with it. Choose Key file, or remove it from that folder.");
            }

            if (PostLogin is { Length: > 0 } command)
            {
                foreach ((Regex pattern, string what) in Secrets)
                {
                    if (pattern.IsMatch(command))
                    {
                        wrong.Add($"This command will not be saved: {what}. Save the password as a "
                                  + "credential instead and name it in Saved credential, where it is "
                                  + "kept by Windows and never written to this file.");

                        break;
                    }
                }
            }

            return wrong;
        }
    }

    /// <summary>Whether saving would succeed.</summary>
    public bool CanSave => Complaints.Count == 0;

    /// <summary>A dialog over a session that is already in the tree.</summary>
    /// <exception cref="SessionStoreException">There is no session at that path.</exception>
    public static SessionEditor Editing(SessionTree tree, string path)
    {
        ArgumentNullException.ThrowIfNull(tree);

        SessionNode node = tree.Find(path)
            ?? throw new SessionStoreException($"There is nothing at {path} to edit.",
                                               "The path names a node this store does not have.");

        return new SessionEditor(tree, path, node);
    }

    /// <summary>
    /// A dialog over a session that does not exist yet, under a folder that may.
    ///
    /// <para>It inherits from that folder immediately, before anything has been typed, because what
    /// a new session will inherit is exactly what the user needs to see in order to know what they
    /// do <em>not</em> have to fill in.</para>
    /// </summary>
    public static SessionEditor Creating(SessionTree tree, string folder, string name)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        string path = string.IsNullOrWhiteSpace(folder) || folder == "/"
            ? name
            : $"{folder.Trim('/')}/{name}";

        return new SessionEditor(tree, path, null) { Name = name };
    }

    /// <summary>The field by the name <see cref="SessionSettings"/> gives it.</summary>
    public EditableField Field(string name) =>
        _fields.First(field => string.Equals(field.Name, name, StringComparison.Ordinal));

    /// <summary>
    /// The tree with this session in it.
    ///
    /// <para>Only overridden fields are written. What was inherited stays inherited, so a folder's
    /// default remains one value that can be changed once rather than a hundred copies.</para>
    /// </summary>
    /// <exception cref="SessionStoreException">The dialog is not in a state that can be saved.</exception>
    public SessionTree Save()
    {
        if (!CanSave)
        {
            throw new SessionStoreException("This session cannot be saved as it stands.",
                                            string.Join(" ", Complaints));
        }

        SessionNode existing = _tree.Find(Path) ?? new SessionNode();

        SessionNode saved = existing with
        {
            Name = Name.Length > 0 ? Name : Host,
            Host = Host.Trim(),
            Tags = [.. Tags],
            PostLogin = string.IsNullOrWhiteSpace(PostLogin) ? null : PostLogin,
            Settings = new SessionSettings
            {
                User = Mine(nameof(SessionSettings.User)),
                Port = Number(nameof(SessionSettings.Port)),
                Key = Mine(nameof(SessionSettings.Key)),
                JumpHost = Mine(nameof(SessionSettings.JumpHost)),
                Scheme = Mine(nameof(SessionSettings.Scheme)),
                Credential = Mine(nameof(SessionSettings.Credential)),
                // Not in the dialog: one font per window (QS135, QS245). A size a hand edit put in
                // the file is kept as it was, so a save through the dialog loses nothing.
                FontSize = existing.Settings.FontSize,
                TerminalType = Mine(nameof(SessionSettings.TerminalType)),
                Scrollback = Number(nameof(SessionSettings.Scrollback)),
                Reconnect = Toggle(Mine(nameof(SessionSettings.Reconnect))),
                // Not in the dialog: the agent's identity is the server-side setup's (QS340), and a
                // save through the dialog keeps it as it was rather than dropping it.
                AgentUser = existing.Settings.AgentUser,
                AgentKey = existing.Settings.AgentKey,
                AgentCertificate = existing.Settings.AgentCertificate,
            },
        };

        // A new session goes where its final name says, not where the name it was opened with did:
        // the dialog opens on a placeholder, and a session saved as "web" under a path ending in
        // "New session" is one that path lookups by name never find again (QS121).
        SavedAt = IsNew ? Under(Path, saved.Name) : Path;

        return _tree.With(SavedAt, saved);

        string? Mine(string name) => Field(name) is { IsOverridden: true, Own: { Length: > 0 } own }
            ? own
            : null;

        int? Number(string name) =>
            Mine(name) is { } text
            && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
                ? value
                : null;
    }

    /// <summary>Where the last <see cref="Save"/> put the session, or <see cref="Path"/> before one.</summary>
    public string SavedAt { get; private set; } = string.Empty;

    /// <summary>The same folder as <paramref name="path"/>, ending in <paramref name="name"/> instead.</summary>
    private static string Under(string path, string name)
    {
        int slash = path.LastIndexOf('/');

        return slash < 0 ? name : $"{path[..slash]}/{name}";
    }

    private static bool IsPort(string text) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int port)
        && port is >= 1 and <= 65535;

    /// <summary>A yes or a no as somebody types one, or null where it is neither.</summary>
    private static bool? Toggle(string? text) => text?.Trim().ToLowerInvariant() switch
    {
        "true" or "yes" or "on" => true,
        "false" or "no" or "off" => false,
        _ => null,
    };

    private static string? Text<T>(T? value) where T : struct, IFormattable =>
        value?.ToString(null, CultureInfo.InvariantCulture);
}
