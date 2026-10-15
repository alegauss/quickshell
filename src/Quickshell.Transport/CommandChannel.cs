using System.Globalization;
using System.Text;

namespace Quickshell.Transport;

/// <summary>
/// What one command run on the far side came to (QS325): its exit code, and what it wrote to each
/// stream, kept apart.
/// </summary>
/// <param name="ExitCode">The exit status the server reported, or null where it reported none, as after a signal.</param>
/// <param name="Output">What it wrote to standard output, as UTF-8.</param>
/// <param name="Error">What it wrote to standard error, as UTF-8.</param>
/// <param name="TimedOut">Whether it was still running when its time ran out, and was ended.</param>
public sealed record CommandResult(int? ExitCode, string Output, string Error, bool TimedOut = false);

/// <summary>
/// A connection that can run one command over an exec channel of its own (QS325).
///
/// <para><b>Not on <see cref="ISshTransport"/>, on purpose.</b> Exactly three kinds of channel cross
/// that seam, and a command channel is a fourth, which the client's own panes never need: they open a
/// shell. So it is offered by the implementations that hold a real connection, as scp's fallback is,
/// and a caller reaching for it is choosing to depend on one of them. The agent gateway is that
/// caller.</para>
/// </summary>
public interface ICommandChannel
{
    /// <summary>
    /// What the server said it is in the version exchange, <c>SSH-2.0-OpenSSH_9.6p1 Ubuntu-3</c> and the
    /// like, or null before a connection. A Windows server says so here, which is how a caller tells a
    /// shell that is POSIX from one that is not (QS327).
    /// </summary>
    string? ServerVersion { get; }

    /// <summary>
    /// Runs <paramref name="command"/> on this connection, in a channel of its own, and waits for it.
    /// </summary>
    /// <param name="command">The command line exactly as the server's shell should read it; see <see cref="Argv.ForPosix"/>.</param>
    /// <param name="timeout">How long it may run before it is ended and reported as timed out.</param>
    /// <param name="cancellationToken">Abandons it.</param>
    ValueTask<CommandResult> RunAsync(string command, TimeSpan timeout, CancellationToken cancellationToken = default);
}

/// <summary>
/// An argument vector turned into one command line, so a word reaches the program exactly as it was
/// given: no layer of quoting between the caller and the far side to get wrong (QS325).
/// </summary>
public static class Argv
{
    /// <summary>
    /// Joins <paramref name="words"/> for a POSIX shell: a word of only safe characters goes bare, and
    /// any other is single-quoted, with a single quote inside it closed, escaped and reopened. Nothing
    /// inside single quotes is special to <c>sh</c>, so a quote, a dollar sign or a newline arrives as
    /// itself.
    /// </summary>
    /// <param name="words">The program and its arguments; at least one.</param>
    /// <param name="directory">Where to run it, or null to run it in the account's home.</param>
    public static string ForPosix(IReadOnlyList<string> words, string? directory = null)
    {
        ArgumentNullException.ThrowIfNull(words);

        if (words.Count == 0)
        {
            throw new ArgumentException("A command needs at least the program to run.", nameof(words));
        }

        StringBuilder line = new();

        if (directory is { Length: > 0 })
        {
            line.Append("cd ").Append(Word(directory)).Append(" && ");
        }

        for (int at = 0; at < words.Count; at++)
        {
            ArgumentNullException.ThrowIfNull(words[at], nameof(words));

            if (at > 0)
            {
                line.Append(' ');
            }

            line.Append(Word(words[at]));
        }

        return line.ToString();
    }

    /// <summary>One word, bare where nothing in it is special to a shell.</summary>
    public static string Word(string word)
    {
        ArgumentNullException.ThrowIfNull(word);

        return word.Length > 0 && word.All(Safe)
            ? word
            : string.Create(CultureInfo.InvariantCulture, $"'{word.Replace("'", @"'\''", StringComparison.Ordinal)}'");
    }

    private static bool Safe(char letter) =>
        letter is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9')
            or '_' or '-' or '.' or '/' or ':' or '=' or '@' or '%' or '+' or ',';
}
