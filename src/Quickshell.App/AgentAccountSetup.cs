using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Quickshell.App;

/// <summary>What setting up a server for an agent will run as root, and what undoes it.</summary>
/// <param name="Script">The setup, a POSIX shell script, shown to the person before anything runs.</param>
/// <param name="Undo">What removes it again.</param>
public sealed record AgentSetupPlan(string Script, string Undo);

/// <summary>A setup that cannot be written as asked, in words the person can act on.</summary>
public sealed class AgentSetupRefusal(string message) : Exception(message);

/// <summary>
/// The server-side boundary (QS340): the one layer an agent on the person's machine cannot route around,
/// made easy to set up. For one server, a dedicated account with its own key, the key restricted to this
/// machine and to no forwarding or terminal, and a sudoers file naming the commands it may run as root and
/// nothing else.
///
/// <para><b>The exact script, shown first.</b> It is generated here, read by the person, and run only on
/// their word, as their own account, with their own sudo. The sudoers file is checked by
/// <c>visudo -c</c> before it is put in place, so a mistake in it never locks root out.</para>
///
/// <para><b>Arguments matched whole.</b> Each allowed command is its program's full path on the server,
/// found by <c>command -v</c>, and its arguments; one with none is written with <c>""</c>, which in
/// sudoers means none, since a bare command would allow any. An argument holding a space or any of
/// <c>, : = \ * ? [</c>, which sudoers reads as syntax or a wildcard, is refused rather than escaped
/// into something the person did not see.</para>
/// </summary>
public static partial class AgentAccountSetup
{
    /// <summary>Where the allowlist is written on the server.</summary>
    public const string Sudoers = "/etc/sudoers.d/quickshell-agent";

    [GeneratedRegex("^[a-z_][a-z0-9_-]{0,31}$")]
    private static partial Regex AccountName { get; }

    [GeneratedRegex("^[A-Za-z0-9._/@%+-]+$")]
    private static partial Regex PlainWord { get; }

    [GeneratedRegex(@"^(ssh-ed25519|ssh-rsa|ecdsa-sha2-nistp256|ecdsa-sha2-nistp384|ecdsa-sha2-nistp521) [A-Za-z0-9+/=]+( [^\r\n']*)?$")]
    private static partial Regex PublicKeyLine { get; }

    [GeneratedRegex(@"^[0-9a-fA-F.:/,]+$")]
    private static partial Regex Addresses { get; }

    /// <summary>The setup and its undo for one server.</summary>
    /// <param name="account">The agent's account, made where it is not there.</param>
    /// <param name="publicKey">The agent key's public half, one line as a <c>.pub</c> holds it.</param>
    /// <param name="from">The address or addresses the server sees this machine connect from, or null for anywhere.</param>
    /// <param name="sudo">The commands the account may run as root, each its program and arguments.</param>
    public static AgentSetupPlan Plan(string account, string publicKey, string? from, IReadOnlyList<IReadOnlyList<string>> sudo)
    {
        ArgumentNullException.ThrowIfNull(sudo);

        if (!AccountName.IsMatch(account ?? string.Empty))
        {
            throw new AgentSetupRefusal($"{account} is not an account name a server takes: lower case letters, digits, _ and -.");
        }

        string key = (publicKey ?? string.Empty).Trim();

        if (!PublicKeyLine.IsMatch(key))
        {
            throw new AgentSetupRefusal("The agent's key is not a public key line, as a .pub file holds it.");
        }

        if (from is not null && !Addresses.IsMatch(from))
        {
            throw new AgentSetupRefusal($"{from} is not an address list for from=.");
        }

        string options = from is null ? "restrict" : $"restrict,from=\"{from}\"";
        StringBuilder script = new();

        script.Append("#!/bin/sh\n")
              .Append("# Written by quickshell (QS340): an account for an agent, limited by this server.\n")
              .Append("set -eu\n")
              .Append(CultureInfo.InvariantCulture, $"account={account}\n")
              .Append("id -u \"$account\" >/dev/null 2>&1 || useradd -m -s /bin/sh \"$account\"\n")
              .Append("home=$(getent passwd \"$account\" | cut -d: -f6)\n")
              .Append("install -d -m 700 -o \"$account\" -g \"$account\" \"$home/.ssh\"\n")
              .Append(CultureInfo.InvariantCulture, $"printf '%s\\n' '{options} {key}' > \"$home/.ssh/authorized_keys\"\n")
              .Append("chown \"$account:$account\" \"$home/.ssh/authorized_keys\"\n")
              .Append("chmod 600 \"$home/.ssh/authorized_keys\"\n")
              .Append("allowed=$(mktemp)\n")
              .Append("printf '%s\\n' '# Written by quickshell (QS340): what the agent account may run as root, and nothing else.' > \"$allowed\"\n");

        foreach (IReadOnlyList<string> command in sudo)
        {
            if (command.Count == 0 || command.Any(word => !PlainWord.IsMatch(word)) || command[0].Contains('/', StringComparison.Ordinal))
            {
                throw new AgentSetupRefusal($"{string.Join(' ', command)} cannot be written into sudoers exactly: name the program without a path, "
                                            + "and use arguments with no spaces or any of , : = \\ * ? [");
            }

            string arguments = command.Count == 1 ? "\"\"" : string.Join(' ', command.Skip(1));

            script.Append(CultureInfo.InvariantCulture, $"path=$(command -v {command[0]}) || {{ echo '{command[0]} is not on this server' >&2; exit 3; }}\n")
                  .Append(CultureInfo.InvariantCulture, $"printf '%s ALL=(root) NOPASSWD: %s %s\\n' \"$account\" \"$path\" '{arguments}' >> \"$allowed\"\n");
        }

        script.Append("visudo -cf \"$allowed\" >/dev/null\n")
              .Append(CultureInfo.InvariantCulture, $"install -m 440 -o root -g root \"$allowed\" {Sudoers}\n")
              .Append("rm -f \"$allowed\"\n")
              .Append("echo quickshell-agent-ready\n");

        string undo = "#!/bin/sh\n"
                      + "# Removes what quickshell's agent setup made (QS340).\n"
                      + $"rm -f {Sudoers}\n"
                      + $"pkill -u {account} 2>/dev/null || true\n"
                      + $"userdel -r {account} 2>/dev/null || true\n"
                      + "echo quickshell-agent-removed\n";

        return new AgentSetupPlan(script.ToString(), undo);
    }
}
