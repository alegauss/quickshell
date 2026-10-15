using Quickshell.Transport;

namespace Quickshell.App;

/// <summary>
/// What stopped an agent's command to ask a question nobody was there to answer (QS327), named so the
/// agent can act on it rather than read a timeout.
/// </summary>
/// <param name="Name">A stable name, <c>prompt.sudo_password</c> and the like.</param>
/// <param name="Hint">What the agent can do about it, in a sentence.</param>
public sealed record CommandPrompt(string Name, string Hint);

/// <summary>
/// Running a command so that nothing in it can wait for a person, and naming the question it would
/// have asked (QS327).
///
/// <para><b>Removed before it runs, on a POSIX server:</b> standard input is <c>/dev/null</c>, so a
/// <c>read</c> or a y/N question gets end-of-file at once; the pagers are <c>cat</c>; apt is told no
/// dialog will be answered. There is no terminal either, so a program that would open one, sudo first
/// of all, refuses at once rather than waiting. Measured against the fixture: <c>sudo true</c> ends in
/// under a second with "a terminal is required to read the password", a <c>read -p</c> gets nothing
/// and exits 1, and <c>less</c> prints its file and returns.</para>
///
/// <para><b>Named after it runs:</b> what the command said on its way out is matched against the
/// shapes those questions take, and the answer carries the name and what to do instead.</para>
/// </summary>
public static class CommandPrompts
{
    /// <summary>
    /// What goes before a POSIX command so nothing in it can wait for a person: no input, no pager, no
    /// package dialog.
    /// </summary>
    public const string PosixPreamble =
        "exec </dev/null; export PAGER=cat GIT_PAGER=cat SYSTEMD_PAGER= DEBIAN_FRONTEND=noninteractive; ";

    /// <summary>Whether a server's version string says it is Windows, whose shell is not POSIX.</summary>
    public static bool IsWindows(string? serverVersion) =>
        serverVersion?.Contains("Windows", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>The question that stopped the command, or null where nothing did.</summary>
    public static CommandPrompt? Recognise(CommandResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        string said = result.Error + "\n" + result.Output;
        string tail = Tail(result.Output) is { Length: > 0 } output ? output : Tail(result.Error);

        if (said.Contains("a terminal is required to read the password", StringComparison.Ordinal)
            || said.Contains("sudo: a password is required", StringComparison.Ordinal)
            || tail.StartsWith("[sudo] password for", StringComparison.Ordinal))
        {
            return new CommandPrompt("prompt.sudo_password",
                "sudo asked for a password and nobody can type one here. Ask the person to run it, or to allow this "
                + "command without a password in sudoers; never put a password in the command.");
        }

        if (tail.EndsWith("password:", StringComparison.OrdinalIgnoreCase)
            || tail.EndsWith("passphrase:", StringComparison.OrdinalIgnoreCase)
            || tail.Contains("password for", StringComparison.OrdinalIgnoreCase) && tail.EndsWith(':'))
        {
            return new CommandPrompt("prompt.password",
                "The command asked for a password and nobody can type one here. Ask the person, or use a way in that "
                + "needs none, such as a key.");
        }

        if (Confirms(tail))
        {
            return new CommandPrompt("prompt.confirmation",
                "The command asked to confirm and got no answer. Run it with its own yes flag, such as -y or --yes, "
                + "where the person agrees it should go ahead.");
        }

        if (tail.EndsWith("--More--", StringComparison.Ordinal) || tail.EndsWith("(END)", StringComparison.Ordinal))
        {
            return new CommandPrompt("prompt.pager",
                "The output went to a pager that waited for a key. Pipe it through cat, or pass the program its "
                + "no-pager flag, such as --no-pager.");
        }

        return null;
    }

    private static bool Confirms(string tail) =>
        tail.EndsWith("[y/N]", StringComparison.OrdinalIgnoreCase)
        || tail.EndsWith("[Y/n]", StringComparison.Ordinal)
        || tail.EndsWith("(y/n)", StringComparison.OrdinalIgnoreCase)
        || tail.EndsWith("(yes/no)", StringComparison.OrdinalIgnoreCase)
        || tail.EndsWith("[yes/no]", StringComparison.OrdinalIgnoreCase)
        || tail.EndsWith("(yes/no/[fingerprint])?", StringComparison.OrdinalIgnoreCase)
        || tail.EndsWith("Do you want to continue?", StringComparison.Ordinal);

    /// <summary>
    /// The last line a stream wrote, trimmed. A question is the last thing a program prints before it
    /// reads, and a line read from the null device ends nothing it printed, so a reply such as
    /// <c>rc=1</c> printed after it lands on the same line; that is cut at the question's mark.
    /// </summary>
    private static string Tail(string text)
    {
        string last = text.TrimEnd().Split('\n')[^1].Trim();

        foreach (string mark in (string[])["[y/N]", "[Y/n]", "(y/n)", "(yes/no)", "[yes/no]", "password:", "Password:"])
        {
            if (last.IndexOf(mark, StringComparison.Ordinal) is var at and >= 0)
            {
                return last[..(at + mark.Length)];
            }
        }

        return last;
    }
}
