using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

using Quickshell.Transport;

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
    /// <param name="authority">
    /// Whether <paramref name="publicKey"/> is quickshell's certificate authority (QS341) rather than the
    /// agent's own key: the account then takes in only a certificate it signed for this account, which
    /// lasts minutes.
    /// </param>
    public static AgentSetupPlan Plan(string account, string publicKey, string? from, IReadOnlyList<IReadOnlyList<string>> sudo,
                                      bool authority = false)
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

        string options = (authority ? $"cert-authority,principals=\"{account}\"," : string.Empty)
                         + (from is null ? "restrict" : $"restrict,from=\"{from}\"");
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

    /// <summary>Where the script waits in the person's home for the line typed into their tab to run it.</summary>
    public const string Staged = ".quickshell-agent-setup.sh";

    /// <summary>What that line leaves in the person's home once the script ran to the end under their sudo.</summary>
    public const string Ready = ".quickshell-agent-ready";

    /// <summary>
    /// The line typed into the person's own tab: the staged script run with their sudo, where they answer its
    /// password themselves, and the mark left only where it succeeded.
    /// </summary>
    public const string Typed = "sudo sh ~/" + Staged + " && : > ~/" + Ready + "; rm -f ~/" + Staged + "\r";

    /// <summary>The address the server sees this connection come from, which the key's <c>from=</c> names.</summary>
    /// <exception cref="AgentSetupRefusal">The server did not say: its sshd sets no <c>SSH_CLIENT</c>.</exception>
    public static async Task<string> SeenFromAsync(ICommandChannel person, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(person);

        CommandResult said = await person.RunAsync("echo \"${SSH_CLIENT%% *}\"", TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
        string from = said.Output.Trim();

        return said.ExitCode == 0 && Addresses.IsMatch(from)
            ? from
            : throw new AgentSetupRefusal("The server did not say which address this machine connects from, so the key cannot be tied to it. "
                                          + "This setup needs a POSIX server running OpenSSH.");
    }

    /// <summary>Puts the script in the person's home, readable by them alone, and clears any mark an earlier run left.</summary>
    public static async Task StageAsync(ICommandChannel person, AgentSetupPlan plan, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(person);
        ArgumentNullException.ThrowIfNull(plan);

        string encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(plan.Script));
        CommandResult staged = await person.RunAsync($"umask 077; rm -f ~/{Ready}; printf '%s' '{encoded}' | base64 -d > ~/{Staged}",
                                                     TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);

        if (staged.ExitCode != 0)
        {
            throw new AgentSetupRefusal($"The script could not be put in your home on the server: {staged.Error.Trim()}");
        }
    }

    /// <summary>Whether the typed line has run the script to the end, taking the mark it left if so.</summary>
    public static async Task<bool> ReadyAsync(ICommandChannel person, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(person);

        CommandResult asked = await person.RunAsync($"test -e ~/{Ready} && rm -f ~/{Ready} && echo ready",
                                                    TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);

        return asked.Output.Trim() == "ready";
    }

    /// <summary>
    /// Where the agent's key for the session at <paramref name="path"/> is kept: in quickshell's own folder,
    /// one per session, named after it.
    /// </summary>
    public static string KeyFor(string folder, string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        string name = new([.. path.Select(letter => char.IsAsciiLetterOrDigit(letter) || letter is '-' or '.' ? letter : '_')]);

        return Path.Combine(folder, name + "_ed25519");
    }

    /// <summary>
    /// An ed25519 pair with no passphrase at <paramref name="path"/>, made by the OpenSSH that ships with
    /// Windows, where none is there yet. A key there already is kept, so setting up a server again does not
    /// lock the agent out of another set up with it.
    /// </summary>
    /// <returns>Whether a key was made, which a setup that is then abandoned removes again.</returns>
    public static bool MakeKey(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (File.Exists(path) && File.Exists(path + ".pub"))
        {
            return false;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        using Process keygen = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "OpenSSH", "ssh-keygen.exe"))
        {
            ArgumentList = { "-q", "-t", "ed25519", "-N", "", "-C", "quickshell-agent", "-f", path },
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        }) ?? throw new AgentSetupRefusal("ssh-keygen did not start.");

        string error = keygen.StandardError.ReadToEnd();

        keygen.WaitForExit();

        return File.Exists(path + ".pub") ? true : throw new AgentSetupRefusal($"ssh-keygen made no key: {error.Trim()}");
    }

    /// <summary>
    /// Removes a key <see cref="MakeKey"/> made, for a setup the person abandoned.
    /// </summary>
    public static void ForgetKey(string path)
    {
        File.Delete(path);
        File.Delete(path + ".pub");
    }

    /// <summary>The store with the agent's identity on the session at <paramref name="path"/>, everything else as it was.</summary>
    /// <exception cref="SessionStoreException">The store holds nothing at that path.</exception>
    /// <param name="store">The store as it is now.</param>
    /// <param name="path">The session set up.</param>
    /// <param name="account">The agent's account on its server.</param>
    /// <param name="key">The agent's private key.</param>
    /// <param name="certificate">Whether the server trusts quickshell's authority, so the gateway signs a certificate each time (QS341).</param>
    public static SessionTree Record(SessionTree store, string path, string account, string key, bool certificate = false)
    {
        ArgumentNullException.ThrowIfNull(store);

        SessionNode node = store.Find(path) ?? throw new SessionStoreException($"There is nothing at {path} in the session store.",
                                                                                "It may have been moved or deleted while the setup ran.");

        return store.With(path, node with
        {
            Settings = node.Settings with { AgentUser = account, AgentKey = key, AgentCertificate = certificate ? true : null },
        });
    }
}
