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

    /// <summary>
    /// Joins <paramref name="words"/> for a Windows server, so each reaches the program exactly as given
    /// (QS356).
    ///
    /// <para><b>Past cmd, through PowerShell, to CreateProcess.</b> Windows OpenSSH runs an exec through
    /// the account's shell, cmd unless the server says otherwise. cmd gives a single quote no meaning, and
    /// expands <c>%</c> even inside double quotes, so no quoting for it carries every word. The line is
    /// therefore <c>powershell -EncodedCommand</c>, whose base64 holds no character cmd acts on, and the
    /// script it carries starts the program itself with <see cref="System.Diagnostics.ProcessStartInfo"/>.
    /// The arguments are one string, quoted here by the rules <c>CommandLineToArgvW</c> and the C runtime
    /// read back, which are the rules .NET's own <c>ArgumentList</c> writes; Windows PowerShell's own
    /// passing to a native program drops embedded double quotes, so it is never asked to.</para>
    ///
    /// <para>The program inherits the channel's streams, so its output is its own, and its exit code is
    /// the command's. A program that cannot be started is said on standard error with exit code 127, as a
    /// POSIX shell says it.</para>
    /// </summary>
    /// <param name="words">The program and its arguments; at least one.</param>
    /// <param name="directory">Where to run it, or null to run it in the account's home.</param>
    public static string ForWindows(IReadOnlyList<string> words, string? directory = null)
    {
        ArgumentNullException.ThrowIfNull(words);

        if (words.Count == 0)
        {
            throw new ArgumentException("A command needs at least the program to run.", nameof(words));
        }

        StringBuilder arguments = new();

        for (int at = 1; at < words.Count; at++)
        {
            ArgumentNullException.ThrowIfNull(words[at], nameof(words));
            Native(arguments, words[at]);
        }

        StringBuilder script = new();

        script.Append("$ErrorActionPreference='Stop';$ProgressPreference='SilentlyContinue';try{");

        if (directory is { Length: > 0 })
        {
            script.Append("Set-Location -LiteralPath ").Append(Literal(directory)).Append(';');
        }

        script.Append("$s=New-Object System.Diagnostics.ProcessStartInfo;")
              .Append("$s.FileName=").Append(Literal(words[0])).Append(';')
              .Append("$s.Arguments=").Append(Literal(arguments.ToString())).Append(';')
              .Append("$s.UseShellExecute=$false;$s.WorkingDirectory=(Get-Location).ProviderPath;")
              .Append("$p=[System.Diagnostics.Process]::Start($s);$p.WaitForExit();exit $p.ExitCode")
              .Append("}catch{$e=$_.Exception;while($e.InnerException){$e=$e.InnerException};")
              .Append("[Console]::Error.WriteLine(").Append(Literal(words[0] + ": ")).Append("+$e.Message);exit 127}");

        return "powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand "
               + Convert.ToBase64String(Encoding.Unicode.GetBytes(script.ToString()));
    }

    /// <summary>
    /// One argument as <c>CommandLineToArgvW</c> reads it back: bare where it has no space or quote,
    /// otherwise in double quotes, a quote escaped with a backslash and every backslash before a quote, or
    /// before the closing one, doubled.
    /// </summary>
    private static void Native(StringBuilder line, string word)
    {
        if (line.Length > 0)
        {
            line.Append(' ');
        }

        if (word.Length > 0 && !word.Any(letter => char.IsWhiteSpace(letter) || letter == '"'))
        {
            line.Append(word);

            return;
        }

        line.Append('"');

        for (int at = 0; at < word.Length;)
        {
            int slashes = 0;

            while (at < word.Length && word[at] == '\\')
            {
                slashes++;
                at++;
            }

            if (at == word.Length)
            {
                line.Append('\\', slashes * 2);
            }
            else if (word[at] == '"')
            {
                line.Append('\\', (slashes * 2) + 1).Append('"');
                at++;
            }
            else
            {
                line.Append('\\', slashes).Append(word[at]);
                at++;
            }
        }

        line.Append('"');
    }

    /// <summary>A PowerShell single-quoted string, in which nothing is special but the quote, doubled.</summary>
    private static string Literal(string text) => "'" + text.Replace("'", "''", StringComparison.Ordinal) + "'";

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
