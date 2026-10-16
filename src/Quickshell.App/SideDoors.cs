using System.IO;
using System.Text.RegularExpressions;

namespace Quickshell.App;

/// <summary>
/// The ways an agent's own shell reaches a server around the gateway (QS332): <c>ssh</c>, <c>scp</c>,
/// <c>sftp</c>, PuTTY's tools, rsync and git over SSH, PowerShell remoting over SSH, and a Python
/// one-liner with an SSH library. Matched on the command line, and told which gateway tool does the job.
///
/// <para><b>This is steering, not a wall.</b> It reads a command before it runs, so it catches the habit
/// and the accident; a script written to a file first and run second walks past it, as does any
/// program it does not name. The wall is below the shell, in the agent's sandbox and on the server,
/// and the design document says which measure is which.</para>
/// </summary>
public static partial class SideDoors
{
    /// <summary>A command word that is itself a way to a server.</summary>
    private static readonly HashSet<string> Clients = new(StringComparer.OrdinalIgnoreCase)
    {
        "ssh", "scp", "sftp", "slogin", "plink", "pscp", "psftp", "putty", "kitty", "mosh", "autossh", "sshpass",
    };

    /// <summary>Programs that run another command given as their arguments.</summary>
    private static readonly HashSet<string> Runners = new(StringComparer.OrdinalIgnoreCase)
    {
        "xargs", "watch", "parallel", "find", "flock", "strace", "script", "unbuffer", "bash", "sh", "zsh", "pwsh", "powershell", "cmd",
    };

    /// <summary>Where one command ends and the next begins: separators, pipes, and the openings of a substitution.</summary>
    [GeneratedRegex(@"\|\||&&|[;|&\n]|\$\(|`|\(|\)")]
    private static partial Regex Separators { get; }

    /// <summary>A word, quoted or not.</summary>
    [GeneratedRegex("\"[^\"]*\"|'[^']*'|\\S+")]
    private static partial Regex Words { get; }

    [GeneratedRegex(@"(?i)\b(paramiko|asyncssh|fabric|pexpect\.pxssh|ssh2-python|Renci\.SshNet)\b")]
    private static partial Regex Libraries { get; }

    [GeneratedRegex(@"(?i)(^|\s)(git@[^\s:]+:|ssh://)")]
    private static partial Regex GitOverSsh { get; }

    /// <summary>
    /// Why <paramref name="command"/> reaches a server around the gateway, naming the tool to use
    /// instead; or null where nothing in it does.
    /// </summary>
    public static string? Check(string command)
    {
        ArgumentNullException.ThrowIfNull(command);

        foreach (string part in Separators.Split(command))
        {
            string[] words = [.. Words.Matches(part).Select(word => word.Value.Trim('"', '\''))];
            int at = 0;

            // Past what only prefixes a command: an assignment, sudo, env, nohup, timeout and its number,
            // PowerShell's call operator.
            while (at < words.Length && (words[at].Contains('=', StringComparison.Ordinal) && !words[at].StartsWith('-')
                                         || words[at] is "sudo" or "env" or "nohup" or "exec" or "command" or "time" or "nice" or "&"
                                         || words[at] == "timeout" || (at > 0 && words[at - 1] == "timeout")))
            {
                at++;
            }

            if (at >= words.Length)
            {
                continue;
            }

            string program = Path.GetFileNameWithoutExtension(words[at].Replace('\\', '/').Split('/')[^1]);
            string rest = string.Join(' ', words.Skip(at + 1));

            // A program that runs the command it is given, so the client may be any word after it.
            if (Runners.Contains(program) && words.Skip(at + 1).FirstOrDefault(word => Clients.Contains(Path.GetFileNameWithoutExtension(word))) is { } carried)
            {
                program = Path.GetFileNameWithoutExtension(carried);
            }

            if (Clients.Contains(program))
            {
                return program.ToLowerInvariant() switch
                {
                    "scp" or "pscp" or "sftp" or "psftp" =>
                        $"{program} copies files over SSH around quickshell. Use the gateway's read_file and edit_file, or exec, "
                        + "on a session hosts_list names.",
                    _ => $"{program} opens an SSH connection around quickshell, where no rule applies. Use the gateway's exec "
                         + "with argv on a session hosts_list names, or job_start for a long command.",
                };
            }

            if (program.Equals("rsync", StringComparison.OrdinalIgnoreCase) && (rest.Contains("ssh", StringComparison.OrdinalIgnoreCase)
                                                                                   || Regex.IsMatch(rest, @"(^|\s)[^\s/]+:[^\s]*")))
            {
                return "rsync to a host goes over SSH around quickshell. Use the gateway's exec, or read_file and edit_file.";
            }

            if (program.Equals("git", StringComparison.OrdinalIgnoreCase) && GitOverSsh.IsMatch(rest))
            {
                return "git with an SSH address connects around quickshell. Use an HTTPS remote, or run git on the server "
                       + "through the gateway's exec.";
            }

            if ((program.Equals("Enter-PSSession", StringComparison.OrdinalIgnoreCase)
                 || program.Equals("New-PSSession", StringComparison.OrdinalIgnoreCase)
                 || program.Equals("Invoke-Command", StringComparison.OrdinalIgnoreCase))
                && rest.Contains("-HostName", StringComparison.OrdinalIgnoreCase))
            {
                return $"{program} -HostName is PowerShell remoting over SSH, around quickshell. Use the gateway's exec.";
            }

            if (program.StartsWith("python", StringComparison.OrdinalIgnoreCase) && Libraries.Match(part) is { Success: true } library)
            {
                return $"A script using {library.Value} connects over SSH around quickshell. Use the gateway's exec.";
            }
        }

        return null;
    }
}
