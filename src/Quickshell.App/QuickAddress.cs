using System.Globalization;

namespace Quickshell.App;

/// <summary>
/// An address typed into quick connect, as the session it stands for (QS271), or why it is not one.
/// </summary>
/// <param name="Session">The session the address makes, or null where it does not parse.</param>
/// <param name="Refused">Why it does not, in the words shown under the box; empty where it parses.</param>
public sealed record QuickAddress(SessionNode? Session, string Refused)
{
    /// <summary>
    /// Reads <c>host</c>, <c>user@host</c>, <c>user@host:port</c> or <c>[v6-address]:port</c> into the
    /// session the store would hold for it.
    ///
    /// <para><b>An ssh_config host is resolved as OpenSSH would.</b> The config's blocks for the name
    /// apply — HostName, User, Port, the first IdentityFile and ProxyJump — and what was typed wins
    /// over what the config says, as it does on <c>ssh</c>'s own command line. So a host from
    /// <c>~/.ssh/config</c> opens without anything being retyped.</para>
    ///
    /// <para><b>The same session the store would hold</b>, with the address's parts in their own
    /// fields and named after the host as typed, so connecting it and saving it then opening it are
    /// one connection.</para>
    /// </summary>
    /// <param name="text">What was typed.</param>
    /// <param name="config">The user's ssh_config, or null to read none.</param>
    /// <param name="signIn">How it signs in.</param>
    /// <param name="keyFile">The key file, where <paramref name="signIn"/> is <see cref="SignInMethod.KeyFile"/>.</param>
    public static QuickAddress Parse(string? text, SshConfig? config = null,
                                     SignInMethod signIn = SignInMethod.KeyOrAgent, string? keyFile = null)
    {
        string typed = text?.Trim() ?? string.Empty;

        if (typed.Length == 0)
        {
            return No("Type an address: a host, user@host or user@host:port.");
        }

        if (typed.Any(char.IsWhiteSpace))
        {
            return No($"An address has no spaces in it, and \"{typed}\" does. Type the host alone, or user@host.");
        }

        string? user = null;
        string rest = typed;
        int at = typed.LastIndexOf('@');

        if (at >= 0)
        {
            user = typed[..at];
            rest = typed[(at + 1)..];

            if (user.Length == 0)
            {
                return No("Nothing comes before the @: type the account, as in deploy@host, or leave the @ out.");
            }
        }

        string host = rest;
        int? port = null;

        if (rest.StartsWith('['))
        {
            // An IPv6 address with a port is bracketed, as ssh and every URL write it.
            int close = rest.IndexOf(']', StringComparison.Ordinal);

            if (close < 0)
            {
                return No($"\"{rest}\" opens a bracket and does not close it.");
            }

            host = rest[1..close];

            string after = rest[(close + 1)..];

            if (after.Length > 0)
            {
                if (!after.StartsWith(':'))
                {
                    return No($"After the bracketed address comes :port and nothing else, and \"{after}\" is not that.");
                }

                if (Port(after[1..]) is not { } given)
                {
                    return No($"A port is a number from 1 to 65535, and {after[1..]} is not.");
                }

                port = given;
            }
        }
        else if (rest.Count(each => each == ':') == 1)
        {
            int colon = rest.IndexOf(':', StringComparison.Ordinal);

            host = rest[..colon];

            if (Port(rest[(colon + 1)..]) is not { } given)
            {
                return No($"A port is a number from 1 to 65535, and {rest[(colon + 1)..]} is not.");
            }

            port = given;
        }

        // More than one colon and no brackets is an IPv6 address on the usual port.
        if (host.Length == 0)
        {
            return No("There is no host in that: type the machine's name or address after the @.");
        }

        if (signIn == SignInMethod.KeyFile && string.IsNullOrWhiteSpace(keyFile))
        {
            return No("Sign in with a key file needs the key file's path.");
        }

        SshConfigHost? known = config?.Resolve(host);

        SessionNode session = new()
        {
            Name = host,
            Host = known?.Target ?? host,
            Settings = new SessionSettings
            {
                User = user ?? known?.User,
                Port = port ?? known?.Port,
                Key = signIn switch
                {
                    SignInMethod.KeyFile => keyFile!.Trim(),
                    SignInMethod.KeyOrAgent => known is { IdentityFiles.Count: > 0 } ? known.IdentityFiles[0] : null,
                    _ => null,
                },
                JumpHost = known?.ProxyJump is { Length: > 0 } jump && !string.Equals(jump, "none", StringComparison.OrdinalIgnoreCase)
                    ? jump
                    : null,
            },
        };

        return new QuickAddress(session, string.Empty);
    }

    private static QuickAddress No(string why) => new(null, why);

    private static int? Port(string text) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int port) && port is >= 1 and <= 65535
            ? port
            : null;
}
