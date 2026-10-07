using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Windows;
using Quickshell.Transport;

namespace Quickshell.App;

/// <summary>A remote file changed since it was opened, and a save is about to go over it.</summary>
/// <param name="Path">The file on the server.</param>
/// <param name="Opened">When it was last modified as this client last knew it.</param>
/// <param name="Now">When it was modified as the server says now, or null where it is gone.</param>
public sealed record OverwriteQuestion(string Path, DateTimeOffset Opened, DateTimeOffset? Now)
{
    /// <summary>The question, with both times, since they are what somebody decides it by.</summary>
    public string Asking => Now is { } now
        ? $"{Path} was changed on the server at {now.ToLocalTime():yyyy-MM-dd HH:mm:ss}, after it was "
          + $"opened here at {Opened.ToLocalTime():yyyy-MM-dd HH:mm:ss}. Saving now replaces that change."
        : $"{Path} is no longer on the server. Saving now puts it back.";
}

/// <summary>What one write-back came to.</summary>
public enum SaveOutcome
{
    /// <summary>The copy holds what the server already has from this client: nothing to send.</summary>
    Unchanged,

    /// <summary>It landed on the server.</summary>
    Landed,

    /// <summary>The server's file had changed and the user kept that change.</summary>
    Refused,

    /// <summary>The editor still held the copy open for writing; it is tried again shortly.</summary>
    Busy,

    /// <summary>It could not be written, and the user has been told why.</summary>
    Failed,
}

/// <summary>
/// One remote file being edited in a local program, with every save sent back to where it came from
/// (QS185).
///
/// <para><b>Each save is uploaded to the path it came from, and never over somebody else's change
/// without asking.</b> The server's modification time and length are recorded when the file is
/// opened and after each write-back, and checked before the next one: a file another person or a
/// deploy has touched since is a question, because an upload that silently overwrote their edit is
/// the worst outcome there is.</para>
///
/// <para><b>Written beside the file and moved into place</b>, with the old one moved aside first and
/// put back if the move fails, so a link that drops halfway leaves the file as it was rather than
/// half of the new one or none at all. The mode the
/// file had is set back afterwards, which the queue does not: a configuration file that lost its
/// <c>600</c> or a script that lost its <c>x</c> because somebody fixed a typo in it would be a
/// worse change than the typo.</para>
///
/// <para><b>One write-back at a time.</b> An editor that saves twice in a second has its second
/// save wait for the first rather than race it to the same path.</para>
/// </summary>
public sealed class RemoteEdit : IAsyncDisposable
{
    /// <summary>How long a save is left to settle before it is sent: editors write a file in several steps.</summary>
    internal static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(300);

    private readonly IFileTransferChannel _channel;
    private readonly SemaphoreSlim _one = new(1, 1);
    private readonly string _folder;
    private readonly int? _mode;

    private FileSystemWatcher? _watcher;
    private Timer? _settling;
    private RemoteEntry _known;
    private byte[] _landed;

    private RemoteEdit(IFileTransferChannel channel, string remote, string folder, string local,
                       RemoteEntry known, byte[] landed)
    {
        _channel = channel;
        _folder = folder;
        _known = known;
        _landed = landed;

        Remote = remote;
        Local = local;

        string octal = BrowserActions.Octal(known.Permissions);

        _mode = octal.Length > 0 ? Convert.ToInt32(octal, 8) : null;
    }

    /// <summary>The file on the server.</summary>
    public string Remote { get; }

    /// <summary>The copy the local program edits.</summary>
    public string Local { get; }

    /// <summary>Asked before a save goes over a change made on the server since. Refuses when unset.</summary>
    public Func<OverwriteQuestion, Task<bool>> AskingToOverwrite { get; set; } = _ => Task.FromResult(false);

    /// <summary>Told each outcome worth a sentence, and whether it is a failure.</summary>
    public Action<string, bool>? Telling { get; set; }

    /// <summary>
    /// Downloads a remote file into a folder of its own, under its own name — so the program it
    /// opens in still knows it by its extension — and records what the server had.
    /// </summary>
    /// <param name="channel">The session's file channel, which this does not own.</param>
    /// <param name="remote">The file on the server.</param>
    /// <param name="root">Where the folder is made.</param>
    /// <param name="cancellationToken">Abandons the download.</param>
    public static async Task<RemoteEdit> OpenAsync(IFileTransferChannel channel, string remote, string root,
                                                   CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentException.ThrowIfNullOrWhiteSpace(remote);
        ArgumentException.ThrowIfNullOrWhiteSpace(root);

        RemoteEntry known = await channel.StatAsync(remote, cancellationToken).ConfigureAwait(false);

        string name = remote[(remote.TrimEnd('/').LastIndexOf('/') + 1)..];
        string folder = Path.Combine(root, Guid.NewGuid().ToString("N"));
        string local = Path.Combine(folder, Safe(name));

        Directory.CreateDirectory(folder);

        await using (FileStream into = new(local, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await channel.DownloadAsync(remote, into, cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        byte[] landed = SHA256.HashData(await File.ReadAllBytesAsync(local, cancellationToken).ConfigureAwait(false));

        return new RemoteEdit(channel, remote, folder, local, known, landed);
    }

    /// <summary>
    /// Starts sending each save of the copy back, a moment after it is written.
    ///
    /// <para><b>Watched by folder and matched by name</b>, because many editors save by writing a
    /// new file and renaming it over the old one, and a watch on the old file's handle would see
    /// that as the file going away.</para>
    /// </summary>
    public void Watch()
    {
        if (_watcher is not null)
        {
            return;
        }

        string name = Path.GetFileName(Local);

        _settling = new Timer(state => _ = SaveAsync());

        FileSystemWatcher watcher = new(_folder)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
        };

        void Saved(object sender, FileSystemEventArgs e)
        {
            if (string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                _settling?.Change(Settle, Timeout.InfiniteTimeSpan);
            }
        }

        watcher.Changed += Saved;
        watcher.Created += Saved;
        watcher.Renamed += Saved;
        watcher.EnableRaisingEvents = true;

        _watcher = watcher;
    }

    /// <summary>Sends the copy back, if it differs from what last landed.</summary>
    /// <param name="cancellationToken">Abandons the write-back, leaving the server's file as it was.</param>
    /// <returns>What came of it.</returns>
    public async Task<SaveOutcome> SaveAsync(CancellationToken cancellationToken = default)
    {
        await _one.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            byte[] bytes;

            try
            {
                bytes = await File.ReadAllBytesAsync(Local, cancellationToken).ConfigureAwait(false);
            }
            catch (IOException) when (File.Exists(Local))
            {
                // Still being written: the next moment's look will have it whole.
                _settling?.Change(Settle, Timeout.InfiniteTimeSpan);

                return SaveOutcome.Busy;
            }

            byte[] hash = SHA256.HashData(bytes);

            if (hash.AsSpan().SequenceEqual(_landed))
            {
                return SaveOutcome.Unchanged;
            }

            string name = Path.GetFileName(Local);

            if (await Changed(cancellationToken).ConfigureAwait(false) is { } question
                && !await AskingToOverwrite(question).ConfigureAwait(false))
            {
                Telling?.Invoke($"{name} was not saved to the server: it had changed there, and that "
                                + "change was kept. Your edit is still in the open copy.", true);

                return SaveOutcome.Refused;
            }

            await WriteBack(bytes, cancellationToken).ConfigureAwait(false);

            _landed = hash;

            Telling?.Invoke($"Saved {name} to the server.", false);

            return SaveOutcome.Landed;
        }
        catch (Exception failed) when (failed is not OperationCanceledException)
        {
            Telling?.Invoke($"{Path.GetFileName(Local)} was not saved to the server: {failed.Message} "
                            + "Your edit is still in the open copy.", true);

            return SaveOutcome.Failed;
        }
        finally
        {
            _one.Release();
        }
    }

    /// <summary>Whether the copy holds exactly what last landed on the server.</summary>
    public bool Landed
    {
        get
        {
            try
            {
                return SHA256.HashData(File.ReadAllBytes(Local)).AsSpan().SequenceEqual(_landed);
            }
            catch (IOException)
            {
                return !File.Exists(Local);
            }
        }
    }

    /// <summary>
    /// Stops watching, and removes the copy — never before what is in it has landed. A copy whose
    /// last edit did not reach the server is the user's text and stays where it is, said by path.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        _watcher?.Dispose();
        _watcher = null;

        if (_settling is { } settling)
        {
            await settling.DisposeAsync().ConfigureAwait(false);
            _settling = null;
        }

        // A write-back under way finishes first: the copy is what it is sending.
        await _one.WaitAsync().ConfigureAwait(false);
        _one.Release();

        if (Landed)
        {
            try
            {
                Directory.Delete(_folder, recursive: true);
            }
            catch (IOException)
            {
                // Still open in the editor: the system's temporary folder has it, and it matches the
                // server, so nothing is lost by leaving it.
            }
            catch (UnauthorizedAccessException)
            {
                // Held by the editor in a way that refuses a delete: the same, and left the same.
            }
        }
        else
        {
            Telling?.Invoke($"{Path.GetFileName(Local)} has edits that never reached the server; they "
                            + $"are kept at {Local}.", true);
        }
    }

    /// <summary>The question to ask, where the server's file is not what this client last knew.</summary>
    private async Task<OverwriteQuestion?> Changed(CancellationToken cancellationToken)
    {
        RemoteEntry now;

        try
        {
            now = await _channel.StatAsync(Remote, cancellationToken).ConfigureAwait(false);
        }
        catch (SshException)
        {
            return new OverwriteQuestion(Remote, _known.Modified, null);
        }

        return now.Length == _known.Length && now.Modified == _known.Modified
            ? null
            : new OverwriteQuestion(Remote, _known.Modified, now.Modified);
    }

    /// <summary>Writes beside the file, moves it into place, and gives it back its mode.</summary>
    private async Task WriteBack(byte[] bytes, CancellationToken cancellationToken)
    {
        string partial = Remote + ".qs-part";

        Stream writing = await _channel.OpenWriteAsync(partial, cancellationToken).ConfigureAwait(false);

        await using (writing.ConfigureAwait(false))
        {
            await writing.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        }

        // Moved aside rather than deleted: SFTP's rename will not go over a name that exists, and a
        // link that drops between a delete and the rename would leave no file at all. Aside, the old
        // one is put back if the new one cannot be moved in.
        string aside = Remote + ".qs-old";
        bool moved;

        try
        {
            await _channel.RenameAsync(Remote, aside, cancellationToken).ConfigureAwait(false);

            moved = true;
        }
        catch (SshException)
        {
            // Gone already, which the question above has asked about.
            moved = false;
        }

        try
        {
            await _channel.RenameAsync(partial, Remote, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (moved)
        {
            await _channel.RenameAsync(aside, Remote, CancellationToken.None).ConfigureAwait(false);

            throw;
        }

        if (moved)
        {
            await _channel.DeleteAsync(aside, cancellationToken).ConfigureAwait(false);
        }

        if (_mode is { } mode)
        {
            await _channel.ChangePermissionsAsync(Remote, mode, cancellationToken).ConfigureAwait(false);
        }

        _known = await _channel.StatAsync(Remote, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>A server's name as this computer can hold it: what Windows refuses becomes an underscore.</summary>
    private static string Safe(string name)
    {
        char[] refused = Path.GetInvalidFileNameChars();
        string safe = string.Concat(name.Select(c => Array.IndexOf(refused, c) >= 0 ? '_' : c)).TrimEnd('.', ' ');

        return safe.Length > 0 ? safe : "file";
    }
}

/// <summary>
/// The remote files a session has open in local programs — kept for as long as the session, not
/// the browser, since an editor stays open after the browser that opened it is closed (QS185).
/// </summary>
public sealed class RemoteEdits : IAsyncDisposable
{
    private readonly IFileTransferChannel _channel;
    private readonly List<RemoteEdit> _open = [];
    private readonly object _guard = new();

    /// <summary>Edits over one session's file channel.</summary>
    public RemoteEdits(IFileTransferChannel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);

        _channel = channel;
    }

    /// <summary>Where the copies are made: a folder of this client's in the user's temporary directory.</summary>
    public string Root { get; init; } = Path.Combine(Path.GetTempPath(), "quickshell", "edit");

    /// <summary>
    /// How a copy is opened: in whatever Windows associates with it, unless a caller says otherwise —
    /// which is how a test edits one without an editor.
    /// </summary>
    public Action<string> Opens { get; set; } = path =>
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();

    /// <summary>Asked before a save goes over a change on the server. Shows a question unless set.</summary>
    public Func<OverwriteQuestion, Task<bool>> AskingToOverwrite { get; set; } = Overwriting;

    /// <summary>
    /// Told what each save came to, by whichever browser is open. Unset, a failure is still shown,
    /// since a save that did not land is not something to lose because the browser was closed.
    /// </summary>
    public Action<string, bool>? Telling { get; set; }

    /// <summary>The files open now.</summary>
    public IReadOnlyList<RemoteEdit> Open
    {
        get
        {
            lock (_guard)
            {
                return [.. _open];
            }
        }
    }

    /// <summary>
    /// Opens a remote file in a local program, and sends each save back. A file already open is
    /// opened again from the same copy, rather than downloaded over the edits in it.
    /// </summary>
    /// <param name="remote">The file on the server.</param>
    /// <param name="cancellationToken">Abandons the download.</param>
    /// <returns>The edit, watching its copy.</returns>
    public async Task<RemoteEdit> EditAsync(string remote, CancellationToken cancellationToken = default)
    {
        RemoteEdit? already;

        lock (_guard)
        {
            already = _open.Find(edit => string.Equals(edit.Remote, remote, StringComparison.Ordinal));
        }

        if (already is null)
        {
            RemoteEdit edit = await RemoteEdit.OpenAsync(_channel, remote, Root, cancellationToken)
                                              .ConfigureAwait(false);

            edit.AskingToOverwrite = question => AskingToOverwrite(question);
            edit.Telling = Tell;

            lock (_guard)
            {
                _open.Add(edit);
            }

            edit.Watch();
            already = edit;
        }

        Opens(already.Local);

        return already;
    }

    /// <summary>Stops every edit; the copies that landed go, and the ones that did not are said.</summary>
    public async ValueTask DisposeAsync()
    {
        foreach (RemoteEdit edit in Open)
        {
            await edit.DisposeAsync().ConfigureAwait(false);
        }

        lock (_guard)
        {
            _open.Clear();
        }
    }

    private void Tell(string note, bool failed)
    {
        if (Telling is { } telling)
        {
            telling(note, failed);
        }
        else if (failed)
        {
            _ = OnDesk(() => Choice.Ask(null, "Save to server", note, new ChoiceButton("OK", "ok")));
        }
    }

    /// <summary>The question, asked on the window's thread, with the safe answer as the way out.</summary>
    private static async Task<bool> Overwriting(OverwriteQuestion question) =>
        await OnDesk(() => Choice.Ask(null, "Save to server", question.Asking,
                                      new ChoiceButton("Replace their change", "overwrite"),
                                      new ChoiceButton("Keep theirs", "keep") { IsWayOut = true }))
            .ConfigureAwait(false) == "overwrite";

    /// <summary>Runs a question on the application's thread, or answers nothing where there is none.</summary>
    private static Task<string?> OnDesk(Func<string?> asking) =>
        Application.Current?.Dispatcher is { } dispatcher
            ? dispatcher.InvokeAsync(asking).Task
            : Task.FromResult<string?>(null);
}
