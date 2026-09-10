using System.Text;
using Quickshell.Transport;

namespace Quickshell.Transport.Tests;

/// <summary>
/// A command run on the far side through a shell, and what it printed — for the tests that have to
/// look at the server to know whether what the client did there is true.
///
/// <para><b>One helper, where there were two that had drifted apart.</b> The SCP and the
/// remote-forward tests each carried their own copy, and both had the same fault (QS138): they looked
/// for the opening marker followed by a bare newline, and a shell behind a pseudo-terminal ends its
/// lines in CRLF. So neither ever saw its marker arrive, both waited out their whole timer on every
/// command, and both then returned the right answer anyway — because the parse after the loop, and
/// only the parse, stripped the carriage returns. Sixteen minutes of every suite run went on it.</para>
///
/// <para><b>The text is read without its carriage returns before a marker is looked for</b>, in the
/// wait as well as in the answer, so the two cannot disagree again. And <b>a wait that runs out is
/// a failure</b> rather than an answer: a command that printed nothing reads exactly like one that
/// never ran, and a test that passes either way proves nothing about the server.</para>
/// </summary>
internal static class RemoteShell
{
    /// <summary>
    /// Longer than any command these tests run takes, and never waited for in full unless the shell
    /// stopped answering.
    /// </summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(25);

    /// <summary>
    /// Runs a command in a fresh shell on the far side and returns what it printed between two
    /// markers, less the line break that ends the last line.
    /// </summary>
    /// <param name="patience">How long to wait for the closing marker; a test of the wait itself
    /// gives a short one.</param>
    /// <exception cref="TimeoutException">
    /// The closing marker never came: the shell stopped answering, or ended, before the command
    /// finished. Said with what the shell did print, since that is what the reader needs next.
    /// </exception>
    public static async Task<string> RunAsync(SshNetTransport session, string command, CancellationToken stop,
                                              TimeSpan? patience = null)
    {
        ArgumentNullException.ThrowIfNull(session);

        TimeSpan waited = patience ?? Patience;

        await using IPtyChannel shell = await session.OpenShellAsync(200, 25, stop);

        string begin = $"qsB{Guid.NewGuid():N}";
        string end = $"qsE{Guid.NewGuid():N}";

        StringBuilder seen = new();
        byte[] buffer = new byte[8 * 1024];

        await shell.WriteAsync(Encoding.UTF8.GetBytes($"echo {begin}; {command}; echo {end}\n"), stop);

        using CancellationTokenSource waiting = CancellationTokenSource.CreateLinkedTokenSource(stop);
        waiting.CancelAfter(waited);

        string? printed = null;
        string ended = $"no closing marker came in {waited.TotalSeconds:0} seconds";

        try
        {
            while ((printed = Between(seen, begin, end)) is null)
            {
                int read = await shell.ReadAsync(buffer, waiting.Token);

                if (read == 0)
                {
                    ended = "the shell ended before the closing marker came";

                    break;
                }

                seen.Append(Encoding.UTF8.GetString(buffer, 0, read));
            }
        }
        catch (OperationCanceledException) when (!stop.IsCancellationRequested)
        {
            // The timer and not the test being stopped, which is the failure said below.
        }

        return printed ?? throw new TimeoutException(
            $"'{command}' on the far side: {ended}. The shell printed: {Plain(seen)}");
    }

    /// <summary>
    /// What was printed between the markers, or null while the closing one has not arrived after the
    /// opening one's own line.
    ///
    /// <para>The opening marker is looked for with the line break after it, because the terminal
    /// echoes the typed command first, and in that echo the marker is followed by a semicolon.</para>
    /// </summary>
    private static string? Between(StringBuilder seen, string begin, string end)
    {
        string all = seen.ToString().Replace("\r", string.Empty, StringComparison.Ordinal);

        int from = all.IndexOf($"{begin}\n", StringComparison.Ordinal);

        if (from < 0)
        {
            return null;
        }

        from += begin.Length + 1;

        int to = all.IndexOf(end, from, StringComparison.Ordinal);

        if (to < 0)
        {
            return null;
        }

        string between = all[from..to];

        return between.EndsWith('\n') ? between[..^1] : between;
    }

    /// <summary>The shell's output with its escape sequences' introducers made visible, for a message.</summary>
    private static string Plain(StringBuilder seen) =>
        seen.ToString().Replace("\u001b", "\\e", StringComparison.Ordinal)
                       .Replace("\r", string.Empty, StringComparison.Ordinal);
}
