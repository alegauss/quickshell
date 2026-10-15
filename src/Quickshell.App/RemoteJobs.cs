using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

using Quickshell.Transport;

namespace Quickshell.App;

/// <summary>Where a job is: running, ended with its code, cancelled, or gone without saying.</summary>
/// <param name="State"><c>running</c>, <c>exited</c>, <c>cancelled</c> or <c>lost</c>.</param>
/// <param name="ExitCode">The code it ended with, where it ended by itself.</param>
public sealed record JobStatus(string State, int? ExitCode);

/// <summary>A piece of a job's output and where the next read starts.</summary>
/// <param name="Text">The output from the cursor on, up to a limit, cut at a whole character.</param>
/// <param name="Cursor">The byte offset to pass next time.</param>
/// <param name="More">Whether there was more past this piece already written.</param>
public sealed record JobOutput(string Text, long Cursor, bool More);

/// <summary>A job call refused before anything ran, in words an agent can act on.</summary>
public sealed class JobRefusal(string message) : Exception(message);

/// <summary>
/// Commands that outlive a tool call (QS328): started detached on the server, looked in on later,
/// read a piece at a time, and cancelled.
///
/// <para><b>Everything about a job is on the server,</b> in <c>~/.quickshell/jobs/&lt;id&gt;/</c>: the
/// process id, its output and its errors, and an <c>exit</c> file it writes when it ends. So a job
/// outlives the connection that started it, the gateway that started it, and the machine the agent
/// is on, and any later connection to the same account can look back in. Nothing on this side has
/// to be kept or can be lost.</para>
///
/// <para><b>Detached under <c>setsid</c></b> where the server has it and <c>nohup</c> where it does
/// not, with nothing on its input, so a dropped connection sends it no hangup. Cancelling sends TERM to
/// its whole process group, then KILL after five seconds.</para>
/// </summary>
public static partial class RemoteJobs
{
    /// <summary>The most output one read returns.</summary>
    public const int Piece = 64 * 1024;

    private const string Root = "$HOME/.quickshell/jobs";

    [GeneratedRegex("^[0-9a-f]{12}$")]
    private static partial Regex Id { get; }

    /// <summary>Starts <paramref name="argv"/> detached and returns its id at once.</summary>
    public static async Task<string> StartAsync(IGatewayConnection connection, IReadOnlyList<string> argv, string? cwd,
                                                CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        Posix(connection);

        string id = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6));

        // The job itself: the command, its streams to files, and its exit code written last, by a
        // rename, so a reader never sees half a number.
        string job = $"{Argv.ForPosix(argv, cwd)} >\"$d/out\" 2>\"$d/err\" </dev/null; "
                     + "echo $? >\"$d/exit.tmp\"; mv \"$d/exit.tmp\" \"$d/exit\"";

        string start = CommandPrompts.PosixPreamble
                       + $"d=\"{Root}/{id}\"; mkdir -p \"$d\" || exit 90; export d; "
                       + "if command -v setsid >/dev/null 2>&1; then detach=setsid; else detach=nohup; fi; "
                       + $"$detach sh -c {Argv.Word(job)} >/dev/null 2>&1 </dev/null & echo $! >\"$d/pid\"; echo started";

        CommandResult ran = await connection.RunAsync(start, TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);

        if (ran.ExitCode != 0 || !ran.Output.Contains("started", StringComparison.Ordinal))
        {
            throw new JobRefusal($"The job did not start: {ran.Error.Trim()} (exit {ran.ExitCode?.ToString(CultureInfo.InvariantCulture) ?? "none"}).");
        }

        return id;
    }

    /// <summary>Whether the job runs, and how it ended where it has.</summary>
    public static async Task<JobStatus> StatusAsync(IGatewayConnection connection, string id, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        Posix(connection);

        string asked = $"d=\"{Root}/{Checked(id)}\"; [ -d \"$d\" ] || {{ echo none; exit 0; }}; "
                       + "if [ -f \"$d/exit\" ]; then echo \"exited $(cat \"$d/exit\")\"; "
                       + "elif [ -f \"$d/cancelled\" ]; then echo cancelled; "
                       + "elif kill -0 \"$(cat \"$d/pid\" 2>/dev/null)\" 2>/dev/null; then echo running; "
                       + "else echo lost; fi";

        CommandResult ran = await connection.RunAsync(asked, TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        string[] said = ran.Output.Trim().Split(' ', 2);

        return said[0] switch
        {
            "none" => throw new JobRefusal($"There is no job {id} for this account on this server."),
            "exited" => new JobStatus("exited", int.TryParse(said.ElementAtOrDefault(1), CultureInfo.InvariantCulture, out int code) ? code : null),
            "running" or "cancelled" or "lost" => new JobStatus(said[0], null),
            _ => throw new JobRefusal($"The job's state could not be read: {ran.Error.Trim()}"),
        };
    }

    /// <summary>
    /// The job's standard output, or its errors, from <paramref name="cursor"/> on, up to
    /// <see cref="Piece"/> bytes cut back to a whole UTF-8 character.
    /// </summary>
    public static async Task<JobOutput> OutputAsync(IGatewayConnection connection, string id, string stream, long cursor,
                                                    CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        Posix(connection);

        string file = stream switch
        {
            "stdout" => "out",
            "stderr" => "err",
            _ => throw new JobRefusal("stream is stdout or stderr."),
        };

        IFileTransferChannel files = await connection.FilesAsync(cancellationToken).ConfigureAwait(false);
        string path = $"{files.WorkingDirectory.TrimEnd('/')}/.quickshell/jobs/{Checked(id)}/{file}";

        long length;

        try
        {
            length = (await files.StatAsync(path, cancellationToken).ConfigureAwait(false)).Length;
        }
        catch (SshException)
        {
            throw new JobRefusal($"There is no job {id} for this account on this server, or it wrote nothing yet.");
        }

        long from = Math.Clamp(cursor, 0, length);
        byte[] piece = new byte[(int)Math.Min(Piece, length - from)];
        int filled = 0;

        if (piece.Length > 0)
        {
            await using Stream reading = await files.OpenReadAsync(path, cancellationToken).ConfigureAwait(false);

            reading.Seek(from, SeekOrigin.Begin);

            while (filled < piece.Length
                   && await reading.ReadAsync(piece.AsMemory(filled), cancellationToken).ConfigureAwait(false) is var read and > 0)
            {
                filled += read;
            }
        }

        int whole = Whole(piece.AsSpan(0, filled));

        return new JobOutput(Encoding.UTF8.GetString(piece, 0, whole), from + whole, from + whole < length);
    }

    /// <summary>Ends the job: TERM to its process group, then KILL after five seconds.</summary>
    public static async Task<JobStatus> CancelAsync(IGatewayConnection connection, string id, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        Posix(connection);

        string cancel = $"d=\"{Root}/{Checked(id)}\"; [ -d \"$d\" ] || exit 3; p=$(cat \"$d/pid\" 2>/dev/null) || exit 4; "
                        + "[ -f \"$d/exit\" ] && exit 0; touch \"$d/cancelled\"; "
                        + "kill -TERM -- \"-$p\" 2>/dev/null || kill -TERM \"$p\" 2>/dev/null; "
                        + "for i in 1 2 3 4 5; do kill -0 \"$p\" 2>/dev/null || exit 0; sleep 1; done; "
                        + "kill -KILL -- \"-$p\" 2>/dev/null || kill -KILL \"$p\" 2>/dev/null; exit 0";

        CommandResult ran = await connection.RunAsync(cancel, TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);

        return ran.ExitCode switch
        {
            0 => await StatusAsync(connection, id, cancellationToken).ConfigureAwait(false),
            3 => throw new JobRefusal($"There is no job {id} for this account on this server."),
            _ => throw new JobRefusal($"The job could not be cancelled: {ran.Error.Trim()}"),
        };
    }

    /// <summary>An id as this file makes them, and nothing that could name another path.</summary>
    private static string Checked(string id) =>
        id is not null && Id.IsMatch(id) ? id : throw new JobRefusal($"{id} is not a job id: job_start returns one, twelve hex digits.");

    private static void Posix(IGatewayConnection connection)
    {
        if (CommandPrompts.IsWindows(connection.ServerVersion))
        {
            throw new JobRefusal("Jobs run under a POSIX shell, and this server is Windows. Use exec with a timeout instead.");
        }
    }

    /// <summary>How many of these bytes end on a whole UTF-8 character.</summary>
    private static int Whole(ReadOnlySpan<byte> bytes)
    {
        int end = bytes.Length;

        // Back over at most three continuation bytes to the lead byte, and keep it only if its
        // character is complete.
        for (int back = 1; back <= Math.Min(4, end); back++)
        {
            byte at = bytes[end - back];

            if ((at & 0xC0) == 0x80)
            {
                continue;
            }

            int needs = at < 0x80 ? 1 : at >= 0xF0 ? 4 : at >= 0xE0 ? 3 : at >= 0xC0 ? 2 : 1;

            return needs <= back ? end : end - back;
        }

        return end;
    }
}
