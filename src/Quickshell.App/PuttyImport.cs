using System.Globalization;
using Microsoft.Win32;

namespace Quickshell.App;

/// <summary>
/// Reading PuTTY's sessions, which live in the registry rather than in a file.
///
/// <para><b>One subkey per session under <c>HKCU\Software\SimonTatham\PuTTY\Sessions</c></b>, and
/// the PuTTY-derived clients — KiTTY, Solar-PuTTY and the rest — follow it, so reading it once reads
/// several clients. The subkey's name is the session's name with the awkward characters
/// percent-escaped, which is why it is unescaped here rather than shown raw.</para>
///
/// <para><b>The same accounting as the MobaXterm reader, deliberately.</b> A session is carried or
/// it is named as not carried, and a value this client has nowhere to put is reported beside the
/// session it came from. An import that silently drops a setting the source held is the failure both
/// readers are written against.</para>
///
/// <para>Reading only. Nothing here writes to the registry, and the preview writes nothing at
/// all.</para>
/// </summary>
public static class PuttyImport
{
    /// <summary>Where PuTTY keeps them, under the current user.</summary>
    public const string Path = @"Software\SimonTatham\PuTTY\Sessions";

    /// <summary>
    /// The values this reader turns into something. Everything else that is set gets named.
    /// </summary>
    private static readonly string[] Consumed =
        ["HostName", "PortNumber", "UserName", "Protocol", "PublicKeyFile"];

    /// <summary>
    /// Values worth naming by what they are rather than by their key, because a user reading the
    /// report should not have to know PuTTY's own vocabulary.
    /// </summary>
    private static readonly Dictionary<string, string> Named = new(StringComparer.Ordinal)
    {
        ["ProxyHost"] = "a proxy",
        ["RemoteCommand"] = "a command to run on login",
        ["PortForwardings"] = "port forwardings",
        ["X11Forward"] = "X11 forwarding, which this client does not do and will not (a non-goal)",
        ["Compression"] = "compression",
        ["ProxyLocalhost"] = "a proxy setting",
    };

    /// <summary>PuTTY's own protocol names, and whether this client speaks them.</summary>
    private static readonly Dictionary<string, string> Refused = new(StringComparer.OrdinalIgnoreCase)
    {
        ["telnet"] = "Telnet",
        ["rlogin"] = "Rlogin",
        ["raw"] = "a raw socket",
        ["serial"] = "Serial",
        ["supdup"] = "SUPDUP",
    };

    /// <summary>The sessions key, or null where this machine has no PuTTY sessions.</summary>
    public static RegistryKey? Find() => Registry.CurrentUser.OpenSubKey(Path);

    /// <summary>
    /// Reads whatever sessions are under a key and says what an import would do. Writes nothing.
    /// </summary>
    /// <param name="sessions">
    /// The key holding one subkey per session. Passed in rather than opened here so a test can hand
    /// over a key it made — a reader that could only read the real one could only be tested on a
    /// machine that happened to have PuTTY.
    /// </param>
    public static ImportPreview Preview(RegistryKey sessions)
    {
        ArgumentNullException.ThrowIfNull(sessions);

        List<ImportedSession> read = [];

        foreach (string name in sessions.GetSubKeyNames())
        {
            using RegistryKey? session = sessions.OpenSubKey(name);

            if (session is null)
            {
                continue;
            }

            read.Add(One(Unescape(name), session));
        }

        return new ImportPreview(read, $@"HKCU\{sessions.Name.Split('\\', 2).LastOrDefault()}");
    }

    /// <summary>One session, as what it would become and what it would lose.</summary>
    private static ImportedSession One(string name, RegistryKey session)
    {
        string protocol = Text(session, "Protocol");

        if (Refused.TryGetValue(protocol, out string? what))
        {
            return new ImportedSession(string.Empty, name, null,
                                       $"{what} is not something this client connects with", []);
        }

        if (protocol.Length > 0 && !string.Equals(protocol, "ssh", StringComparison.OrdinalIgnoreCase))
        {
            return new ImportedSession(string.Empty, name, null,
                                       $"the protocol is {protocol}, which this client does not speak",
                                       []);
        }

        string host = Text(session, "HostName");

        if (host.Length == 0)
        {
            // PuTTY keeps a "Default Settings" subkey that is a template rather than a session, and
            // it has no host. Naming it is better than importing an entry that connects to nothing.
            return new ImportedSession(string.Empty, name, null,
                                       "the session names no host, so it is a saved default rather "
                                       + "than somewhere to connect",
                                       []);
        }

        List<string> unmapped = [];

        foreach (string value in session.GetValueNames())
        {
            if (Consumed.Contains(value, StringComparer.Ordinal) || !Chosen(session, value))
            {
                continue;
            }

            unmapped.Add(Named.TryGetValue(value, out string? plain)
                             ? $"{plain} was set, and this client has nowhere to put it"
                             : $"{value} was set, and this import does not read it");
        }

        int port = (int)(session.GetValue("PortNumber") as int? ?? 22);

        return new ImportedSession(
            string.Empty, name,
            new SessionNode
            {
                Name = name,
                Host = host,
                Settings = new SessionSettings
                {
                    User = Empty(Text(session, "UserName")),
                    Port = port == 22 ? null : port,
                    Key = Empty(Text(session, "PublicKeyFile")),
                },
            },
            string.Empty, unmapped);
    }

    /// <summary>
    /// Whether a value holds something somebody chose rather than PuTTY's own default.
    ///
    /// <para>PuTTY writes every setting it has into every session, so "set" cannot mean "present" —
    /// that would report a hundred defaults per session and bury the two that matter. Zero and the
    /// empty string are what it writes for off and unset.</para>
    /// </summary>
    private static bool Chosen(RegistryKey session, string value) =>
        session.GetValue(value) switch
        {
            int number => number != 0,
            string text => text.Length > 0,
            _ => false,
        };

    private static string Text(RegistryKey session, string value) =>
        session.GetValue(value) as string ?? string.Empty;

    private static string? Empty(string value) => value.Length == 0 ? null : value;

    /// <summary>
    /// A subkey name back to the session name a user typed.
    ///
    /// <para>PuTTY escapes what a registry key may not hold as <c>%XX</c>, so a session called
    /// "work box" is stored as <c>work%20box</c>. Showing the escaped form would be showing a user
    /// their own session under a name they never gave it.</para>
    /// </summary>
    private static string Unescape(string name)
    {
        if (!name.Contains('%', StringComparison.Ordinal))
        {
            return name;
        }

        System.Text.StringBuilder plain = new(name.Length);

        for (int at = 0; at < name.Length; at++)
        {
            if (name[at] == '%' && at + 2 < name.Length
                && int.TryParse(name.AsSpan(at + 1, 2), NumberStyles.HexNumber,
                                CultureInfo.InvariantCulture, out int code))
            {
                plain.Append((char)code);
                at += 2;

                continue;
            }

            plain.Append(name[at]);
        }

        return plain.ToString();
    }
}
