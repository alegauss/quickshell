using System.Globalization;
using System.IO;
using System.Text;

using Quickshell.Transport;

namespace Quickshell.App;

/// <summary>A file read for an agent: its numbered lines and how many it has.</summary>
/// <param name="Text">The lines asked for, each led by its number and a tab.</param>
/// <param name="Lines">How many lines the whole file has.</param>
public sealed record RemoteRead(string Text, int Lines);

/// <summary>An edit made: the unified diff of it and where the original now is.</summary>
/// <param name="Diff">The change as <c>diff -u</c> prints it.</param>
/// <param name="Backup">The original, renamed beside the file.</param>
public sealed record RemoteTextEdit(string Diff, string Backup);

/// <summary>An edit or a read refused before anything was written, in words an agent can act on.</summary>
public sealed class RemoteTextRefusal(string message) : Exception(message);

/// <summary>
/// Reading and editing a text file on a server for an agent, over the session's SFTP channel (QS326),
/// so a config change is an exact replacement a person can read as a diff and undo from a backup,
/// not a heredoc or a <c>sed -i</c> nobody saw.
///
/// <para><b>An edit is the contract an agent's own edit tool has:</b> an old string that occurs
/// exactly once and its replacement. Nothing is written when it occurs nowhere or more than once.</para>
///
/// <para><b>No moment with half a file.</b> The new text is written whole under a temporary name;
/// then the original is renamed to the backup and the new one renamed into its place. SFTP's rename
/// does not replace a file that exists, which is why it takes two, and why the backup is the original
/// itself, with its owner, mode and times as they were. The edited file gets the original's mode. If
/// the second rename fails the backup is moved back, and if even that fails the error names where the
/// original is.</para>
/// </summary>
public static class RemoteText
{
    /// <summary>The largest file read or edited whole: past it, an agent should read a range of lines.</summary>
    public const long Largest = 1024 * 1024;

    private static readonly UTF8Encoding Strict = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>The file's lines from <paramref name="from"/> to <paramref name="to"/>, numbered from one.</summary>
    public static async Task<RemoteRead> ReadAsync(IFileTransferChannel files, string path, int? from, int? to,
                                                   CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(files);

        string text = await TextAsync(files, path, cancellationToken).ConfigureAwait(false);
        string[] lines = Lines(text);
        int first = Math.Max(1, from ?? 1);
        int last = Math.Min(lines.Length, to ?? lines.Length);

        StringBuilder shown = new();

        for (int number = first; number <= last; number++)
        {
            shown.Append(number.ToString(CultureInfo.InvariantCulture)).Append('\t').Append(lines[number - 1]).Append('\n');
        }

        return new RemoteRead(shown.ToString(), lines.Length);
    }

    /// <summary>
    /// Replaces the one occurrence of <paramref name="old"/> in the file with <paramref name="replacement"/>.
    /// </summary>
    /// <param name="files">The session's file channel.</param>
    /// <param name="path">The file, absolute.</param>
    /// <param name="old">The exact text to replace, which must occur once.</param>
    /// <param name="replacement">What replaces it; empty deletes it.</param>
    /// <param name="now">When, which names the backup.</param>
    /// <param name="cancellationToken">Abandons it before the original is moved.</param>
    public static async Task<RemoteTextEdit> EditAsync(IFileTransferChannel files, string path, string old, string replacement,
                                                   DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentException.ThrowIfNullOrEmpty(old);
        ArgumentNullException.ThrowIfNull(replacement);

        RemoteEntry was = await files.StatAsync(path, cancellationToken).ConfigureAwait(false);
        string text = await TextAsync(files, path, cancellationToken).ConfigureAwait(false);

        int at = text.IndexOf(old, StringComparison.Ordinal);

        if (at < 0)
        {
            throw new RemoteTextRefusal($"The old text is not in {path}, so nothing was changed. Read the file again and copy it exactly.");
        }

        int count = Occurrences(text, old);

        if (count > 1)
        {
            throw new RemoteTextRefusal($"The old text occurs {count} times in {path}, so nothing was changed. "
                                        + "Include more of the lines around it, so it names one place.");
        }

        string edited = string.Concat(text.AsSpan(0, at), replacement, text.AsSpan(at + old.Length));
        string stamp = now.ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        string backup = $"{path}.qs-bak-{stamp}";
        string written = $"{path}.qs-new-{Guid.NewGuid():N}";

        try
        {
            using (MemoryStream content = new(Strict.GetBytes(edited)))
            {
                await files.UploadAsync(content, written, cancellationToken: cancellationToken).ConfigureAwait(false);
            }

            if (BrowserActions.Octal(was.Permissions) is { Length: > 0 } mode)
            {
                await files.ChangePermissionsAsync(written, Convert.ToInt32(mode, 8), cancellationToken).ConfigureAwait(false);
            }

            await files.RenameAsync(path, backup, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await Forget(files, written).ConfigureAwait(false);

            throw;
        }

        try
        {
            await files.RenameAsync(written, path, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception failed) when (failed is SshException or IOException)
        {
            try
            {
                await files.RenameAsync(backup, path, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception stuck) when (stuck is SshException or IOException)
            {
                throw new RemoteTextRefusal($"The edit of {path} could not be put in place, and the original could not be moved "
                                            + $"back: it is at {backup}, and the edited text at {written}.");
            }

            await Forget(files, written).ConfigureAwait(false);

            throw;
        }

        return new RemoteTextEdit(Diff(path, Lines(text), Lines(edited)), backup);
    }

    /// <summary>
    /// The diff an edit would make, without making it, so the person approving it sees exactly what will
    /// change (QS337). Refused as the edit itself would be.
    /// </summary>
    public static async Task<string> PreviewAsync(IFileTransferChannel files, string path, string old, string replacement,
                                                  CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentException.ThrowIfNullOrEmpty(old);
        ArgumentNullException.ThrowIfNull(replacement);

        string text = await TextAsync(files, path, cancellationToken).ConfigureAwait(false);
        int at = text.IndexOf(old, StringComparison.Ordinal);

        if (at < 0)
        {
            throw new RemoteTextRefusal($"The old text is not in {path}, so nothing was changed. Read the file again and copy it exactly.");
        }

        if (Occurrences(text, old) is var count and > 1)
        {
            throw new RemoteTextRefusal($"The old text occurs {count} times in {path}, so nothing was changed. "
                                        + "Include more of the lines around it, so it names one place.");
        }

        string edited = string.Concat(text.AsSpan(0, at), replacement, text.AsSpan(at + old.Length));

        return Diff(path, Lines(text), Lines(edited));
    }

    /// <summary>
    /// The change as one hunk of <c>diff -u</c>, with three lines of context: an edit is one
    /// replacement, so everything that differs lies between the common start and the common end.
    /// </summary>
    public static string Diff(string path, string[] before, string[] after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);

        int start = 0;

        while (start < before.Length && start < after.Length && before[start] == after[start])
        {
            start++;
        }

        int tail = 0;

        while (tail < before.Length - start && tail < after.Length - start
               && before[before.Length - 1 - tail] == after[after.Length - 1 - tail])
        {
            tail++;
        }

        if (start == before.Length && start == after.Length)
        {
            return string.Empty;
        }

        const int context = 3;
        int from = Math.Max(0, start - context);
        int beforeEnd = Math.Min(before.Length, before.Length - tail + context);
        int afterEnd = Math.Min(after.Length, after.Length - tail + context);

        StringBuilder diff = new();

        diff.Append("--- ").Append(path).Append('\n')
            .Append("+++ ").Append(path).Append('\n')
            .Append(string.Create(CultureInfo.InvariantCulture,
                $"@@ -{from + 1},{beforeEnd - from} +{from + 1},{afterEnd - from} @@\n"));

        for (int at = from; at < start; at++)
        {
            diff.Append(' ').Append(before[at]).Append('\n');
        }

        for (int at = start; at < before.Length - tail; at++)
        {
            diff.Append('-').Append(before[at]).Append('\n');
        }

        for (int at = start; at < after.Length - tail; at++)
        {
            diff.Append('+').Append(after[at]).Append('\n');
        }

        for (int at = before.Length - tail; at < beforeEnd; at++)
        {
            diff.Append(' ').Append(before[at]).Append('\n');
        }

        return diff.ToString();
    }

    /// <summary>The file as text, refusing one too large, not UTF-8, or holding a NUL, which is binary.</summary>
    private static async Task<string> TextAsync(IFileTransferChannel files, string path, CancellationToken cancellationToken)
    {
        RemoteEntry entry = await files.StatAsync(path, cancellationToken).ConfigureAwait(false);

        if (entry.IsDirectory)
        {
            throw new RemoteTextRefusal($"{path} is a directory.");
        }

        if (entry.Length > Largest)
        {
            throw new RemoteTextRefusal(string.Create(CultureInfo.InvariantCulture,
                $"{path} is {entry.Length} bytes, more than the {Largest} read whole. Read it with exec, a range at a time."));
        }

        using MemoryStream into = new();

        await files.DownloadAsync(path, into, cancellationToken: cancellationToken).ConfigureAwait(false);

        byte[] bytes = into.ToArray();

        if (Array.IndexOf(bytes, (byte)0) >= 0)
        {
            throw new RemoteTextRefusal($"{path} holds a NUL byte, so it is binary and not read as text.");
        }

        try
        {
            return Strict.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            throw new RemoteTextRefusal($"{path} is not UTF-8 text, so it is not read or edited here.");
        }
    }

    /// <summary>The lines, a final newline ending the last one rather than starting another.</summary>
    private static string[] Lines(string text)
    {
        string[] lines = text.Split('\n');

        return text.EndsWith('\n') ? lines[..^1] : lines;
    }

    private static int Occurrences(string text, string old)
    {
        int count = 0;

        for (int at = text.IndexOf(old, StringComparison.Ordinal); at >= 0; at = text.IndexOf(old, at + 1, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    private static async Task Forget(IFileTransferChannel files, string path)
    {
        try
        {
            await files.DeleteAsync(path, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception left) when (left is SshException or IOException)
        {
            // A temporary file left behind is named in nothing and harms nothing; the failure that
            // brought us here is the one worth saying.
        }
    }
}
