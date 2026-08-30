using System.Diagnostics;

namespace Quickshell.Transport;

/// <summary>Which way the bytes go.</summary>
public enum TransferDirection
{
    /// <summary>From this machine to the server.</summary>
    Upload,

    /// <summary>From the server to this machine.</summary>
    Download,
}

/// <summary>Where one entry has got to.</summary>
public enum TransferState
{
    /// <summary>Queued, not started.</summary>
    Waiting,

    /// <summary>Moving bytes now.</summary>
    Running,

    /// <summary>Stopped by the user, with what has been moved kept.</summary>
    Paused,

    /// <summary>Finished, all of it.</summary>
    Done,

    /// <summary>Stopped by a failure, which it carries.</summary>
    Failed,

    /// <summary>Stopped by the user for good.</summary>
    Cancelled,

    /// <summary>Left alone because something was already there and the answer was to keep it.</summary>
    Skipped,
}

/// <summary>What to do about a partial file that is already there.</summary>
public enum ResumeVerdict
{
    /// <summary>Nothing is there; start at the beginning.</summary>
    Fresh,

    /// <summary>The partial is a prefix of the source; carry on from where it ends.</summary>
    Resume,

    /// <summary>It cannot be shown to be a prefix, so it is written again from zero.</summary>
    Restart,
}

/// <summary>What the source looked like when a transfer began, which is what makes resume safe.</summary>
/// <param name="Length">Its size then.</param>
/// <param name="Modified">When it had last changed, then.</param>
public readonly record struct SourceState(long Length, DateTimeOffset Modified);

/// <summary>A verdict about a partial file, and the reason a user is owed.</summary>
/// <param name="Verdict">What will happen.</param>
/// <param name="At">The offset to continue from, which is zero unless resuming.</param>
/// <param name="Why">Why, in words, and always present when the answer is to start again.</param>
public readonly record struct ResumeDecision(ResumeVerdict Verdict, long At, string Why);

/// <summary>
/// One file in the queue, and everything a row about it shows.
///
/// <para>A failed entry stays here carrying its reason rather than vanishing. A transfer that
/// disappears when it fails is one the user cannot retry and cannot find out about.</para>
/// </summary>
public sealed class TransferEntry
{
    private readonly object _guard = new();
    private readonly Queue<(long At, long Moved)> _recent = new();

    private long _windowStart;

    internal TransferEntry(TransferDirection direction, string local, string remote)
    {
        Direction = direction;
        Local = local;
        Remote = remote;
    }

    /// <summary>Which way it goes.</summary>
    public TransferDirection Direction { get; }

    /// <summary>The path on this machine.</summary>
    public string Local { get; }

    /// <summary>The path on the server, spelled as the server spells it.</summary>
    public string Remote { get; }

    /// <summary>What is being read, as a person reads a row.</summary>
    public string From => Direction == TransferDirection.Upload ? Local : Remote;

    /// <summary>What is being written.</summary>
    public string To => Direction == TransferDirection.Upload ? Remote : Local;

    /// <summary>How big the whole file is, once that is known. Zero until then.</summary>
    public long Length { get; internal set; }

    /// <summary>How much of it has arrived.</summary>
    public long Moved { get; private set; }

    /// <summary>Where it has got to.</summary>
    public TransferState State { get; internal set; } = TransferState.Waiting;

    /// <summary>Why it failed, or why it started again rather than resuming. Empty otherwise.</summary>
    public string Why { get; internal set; } = string.Empty;

    /// <summary>What the source looked like when this last started moving bytes.</summary>
    public SourceState? Began { get; internal set; }

    /// <summary>Whether the user can ask for it again.</summary>
    public bool CanRetry => State is TransferState.Failed or TransferState.Paused;

    /// <summary>
    /// Where the bytes are written until they are all there.
    ///
    /// <para>Never the destination itself. An interruption part way through an overwrite would
    /// otherwise leave a truncated file where a complete one used to be, and the complete one is
    /// gone — which is the failure this line exists to prevent. What is at the destination stays
    /// untouched until every byte has arrived.</para>
    /// </summary>
    public string Partial => $"{To}.qs-part";

    /// <summary>How a name that was already taken was resolved, or null where it was free.</summary>
    public CollisionAnswer? Resolved { get; internal set; }

    /// <summary>Where it actually landed, which differs from <see cref="To"/> after a rename.</summary>
    public string Landed { get; internal set; } = string.Empty;

    /// <summary>
    /// Bytes a second, over a short window rather than over the whole transfer.
    ///
    /// <para>An average since the start is still reporting a fast first minute half an hour after
    /// the link went slow, which makes the estimate beside it a lie.</para>
    /// </summary>
    public double BytesPerSecond
    {
        get
        {
            lock (_guard)
            {
                if (_recent.Count < 2)
                {
                    return 0;
                }

                (long firstAt, long firstMoved) = _recent.Peek();
                (long lastAt, long lastMoved) = _recent.Last();

                long ticks = lastAt - firstAt;

                return ticks <= 0 ? 0 : (lastMoved - firstMoved) * (double)Stopwatch.Frequency / ticks;
            }
        }
    }

    /// <summary>How much longer, or null where that cannot honestly be said yet.</summary>
    public TimeSpan? Remaining
    {
        get
        {
            double rate = BytesPerSecond;

            return rate <= 0 || Length <= 0 || Moved >= Length
                ? null
                : TimeSpan.FromSeconds((Length - Moved) / rate);
        }
    }

    /// <summary>How far along, from zero to one. Zero where the size is not known.</summary>
    public double Fraction => Length <= 0 ? 0 : Math.Clamp((double)Moved / Length, 0, 1);

    internal void Reached(long moved)
    {
        lock (_guard)
        {
            Moved = moved;

            long now = Stopwatch.GetTimestamp();

            _recent.Enqueue((now, moved));

            // A five-second window: long enough that one slow block does not swing the estimate,
            // short enough that a link which has changed is reported as it is now.
            _windowStart = now - (5 * Stopwatch.Frequency);

            while (_recent.Count > 2 && _recent.Peek().At < _windowStart)
            {
                _recent.Dequeue();
            }
        }
    }

    internal void Restarted()
    {
        lock (_guard)
        {
            _recent.Clear();
            Moved = 0;
        }
    }
}

/// <summary>
/// The transfers a user has asked for, running whether or not anything is watching.
///
/// <para><b>The queue outlives the dialog that made it.</b> A user queues an hour of work and goes
/// back to the terminal; a queue that belonged to a window would stop when they closed it.</para>
///
/// <para><b>Cancel stops now.</b> Not after the current file finishes — a cancel that lets a
/// half-gigabyte file run to completion first is not a cancel, and the user pressing it usually
/// wants the bandwidth back this second.</para>
///
/// <para><b>Resume is refused unless the partial can be shown to be a prefix of the source.</b>
/// SFTP reads and writes at an offset, so continuing is easy; being sure it is safe is the whole
/// problem. Size alone proves nothing — a file rewritten to the same length resumes into a mixture
/// of two versions that no checksum was ever taken of. So this resumes only where the source is
/// unchanged since the bytes on disk were written, and where that cannot be established it starts
/// again and says why.</para>
/// </summary>
public sealed class TransferQueue
{
    private readonly IFileTransferChannel _over;
    private readonly List<TransferEntry> _entries = [];
    private readonly Dictionary<TransferEntry, CancellationTokenSource> _running = [];
    private readonly List<string> _losses = [];
    private readonly object _guard = new();

    private int _concurrent = 2;
    private CollisionAnswer? _forTheRest;

    /// <summary>A queue moving files over one channel.</summary>
    public TransferQueue(IFileTransferChannel over)
    {
        ArgumentNullException.ThrowIfNull(over);

        _over = over;
    }

    /// <summary>
    /// How many files move at once.
    ///
    /// <para>Low by default and configurable, because the right number is a property of the link
    /// and not of this client. Several at once helps over a high-latency connection and hurts on a
    /// saturated one, and a default that fills somebody's uplink is a default that gets this client
    /// blamed for the network.</para>
    /// </summary>
    public int MaximumConcurrent
    {
        get => _concurrent;
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);

            _concurrent = value;
        }
    }

    /// <summary>How big a block is asked for at a time.</summary>
    public int BlockSize { get; init; } = 64 * 1024;

    /// <summary>
    /// Asked when a destination name is already taken. Where nothing is set, the answer is to skip:
    /// a queue running with nobody to ask must not be the one that overwrites.
    /// </summary>
    public CollisionCheck? OnCollision { get; init; }

    /// <summary>Whether times are copied along with the bytes, where the destination can hold them.</summary>
    public bool PreserveTimes { get; init; } = true;

    /// <summary>
    /// What could not be carried across, said once for the whole queue rather than once per file.
    ///
    /// <para>A warning repeated four hundred times is a warning nobody reads, and the thing it was
    /// warning about happens anyway.</para>
    /// </summary>
    public IReadOnlyList<string> Losses
    {
        get
        {
            lock (_guard)
            {
                return [.. _losses];
            }
        }
    }

    /// <summary>Everything queued, in the order it was asked for.</summary>
    public IReadOnlyList<TransferEntry> Entries
    {
        get
        {
            lock (_guard)
            {
                return [.. _entries];
            }
        }
    }

    /// <summary>Every byte the queue knows it has to move.</summary>
    public long TotalBytes => Entries.Sum(entry => entry.Length);

    /// <summary>Every byte it has moved.</summary>
    public long MovedBytes => Entries.Sum(entry => entry.Moved);

    /// <summary>Adds a file to the queue without starting it.</summary>
    public TransferEntry Enqueue(TransferDirection direction, string local, string remote)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(local);
        ArgumentException.ThrowIfNullOrWhiteSpace(remote);

        TransferEntry entry = new(direction, local, remote);

        lock (_guard)
        {
            _entries.Add(entry);
        }

        return entry;
    }

    /// <summary>
    /// Decides what to do about a partial file, and says why in words a user can act on.
    ///
    /// <para>Static and separate because it is the judgement this line exists to get right, and a
    /// judgement buried in a copy loop is one nobody can test.</para>
    /// </summary>
    /// <param name="began">What the source looked like when the partial was written, if known.</param>
    /// <param name="source">What the source looks like now.</param>
    /// <param name="already">How many bytes of the destination are already there.</param>
    public static ResumeDecision Decide(SourceState? began, SourceState source, long already)
    {
        if (already <= 0)
        {
            return new ResumeDecision(ResumeVerdict.Fresh, 0, string.Empty);
        }

        if (began is not { } when)
        {
            // A partial with no record of what it was copied from. It may well be a prefix, and
            // there is no way to show it — including it being another program's file entirely.
            return new ResumeDecision(
                ResumeVerdict.Restart, 0,
                "Nothing is recorded about the source these bytes came from, so they cannot be "
                + "shown to be the beginning of it. Writing it again from the start.");
        }

        if (already > source.Length)
        {
            return new ResumeDecision(
                ResumeVerdict.Restart, 0,
                $"What is already there is longer than the source ({already} bytes against "
                + $"{source.Length}), so it is not the beginning of it. Writing it again.");
        }

        if (when.Length != source.Length || when.Modified != source.Modified)
        {
            return new ResumeDecision(
                ResumeVerdict.Restart, 0,
                "The source has changed since these bytes were copied, so continuing would join "
                + "two versions of the file. Writing it again from the start.");
        }

        return new ResumeDecision(ResumeVerdict.Resume, already, string.Empty);
    }

    /// <summary>
    /// Runs the queue until nothing is left waiting or running.
    ///
    /// <para>Returns when the work is done rather than when it is started, so a caller that wants
    /// the queue in the background starts it and keeps the task.</para>
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        List<Task> moving = [];

        while (!cancellationToken.IsCancellationRequested)
        {
            while (moving.Count < MaximumConcurrent && Next() is { } next)
            {
                moving.Add(MoveAsync(next, cancellationToken));
            }

            if (moving.Count == 0)
            {
                return;
            }

            Task finished = await Task.WhenAny(moving).ConfigureAwait(false);

            moving.Remove(finished);
        }

        await Task.WhenAll(moving).ConfigureAwait(false);
    }

    /// <summary>
    /// Stops an entry and keeps what it has moved, so it can carry on later.
    /// </summary>
    public void Pause(TransferEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        Stop(entry, TransferState.Paused);
    }

    /// <summary>Stops an entry for good. What was written stays where it is.</summary>
    public void Cancel(TransferEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        Stop(entry, TransferState.Cancelled);
    }

    /// <summary>Stops everything at once, keeping what has been moved.</summary>
    public void PauseAll()
    {
        foreach (TransferEntry entry in Entries)
        {
            Pause(entry);
        }
    }

    /// <summary>Stops everything for good.</summary>
    public void CancelAll()
    {
        foreach (TransferEntry entry in Entries)
        {
            Cancel(entry);
        }
    }

    /// <summary>Puts a paused, cancelled or failed entry back in the queue.</summary>
    public void Retry(TransferEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        // Under the same lock the run loop picks entries with: without it, an entry could be moved
        // back to waiting between Next() finding nothing and the loop deciding it is finished.
        lock (_guard)
        {
            if (entry.State is TransferState.Running or TransferState.Done)
            {
                return;
            }

            entry.Why = string.Empty;
            entry.State = TransferState.Waiting;
        }
    }

    /// <summary>Puts every stopped entry back in the queue.</summary>
    public void RetryAll()
    {
        foreach (TransferEntry entry in Entries)
        {
            Retry(entry);
        }
    }

    private TransferEntry? Next()
    {
        lock (_guard)
        {
            TransferEntry? next = _entries.Find(entry => entry.State == TransferState.Waiting);

            if (next is not null)
            {
                next.State = TransferState.Running;
            }

            return next;
        }
    }

    private void Stop(TransferEntry entry, TransferState into)
    {
        CancellationTokenSource? stopping;

        lock (_guard)
        {
            if (entry.State is TransferState.Done or TransferState.Cancelled)
            {
                return;
            }

            _running.TryGetValue(entry, out stopping);

            // Marked before the cancel, so the copy loop finds the state already decided and does
            // not race the caller into reporting a failure instead.
            entry.State = into;
        }

        stopping?.Cancel();
    }

    private async Task MoveAsync(TransferEntry entry, CancellationToken cancellationToken)
    {
        using CancellationTokenSource stopping =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        lock (_guard)
        {
            _running[entry] = stopping;
        }

        try
        {
            await CopyAsync(entry, stopping.Token).ConfigureAwait(false);

            lock (_guard)
            {
                if (entry.State == TransferState.Running)
                {
                    entry.State = TransferState.Done;
                }
            }
        }
        catch (OperationCanceledException)
        {
            lock (_guard)
            {
                // Pause and Cancel have already said which it was; anything else here is the
                // caller's own token, which stops the queue rather than failing the entry.
                if (entry.State == TransferState.Running)
                {
                    entry.State = TransferState.Paused;
                }
            }
        }
        catch (Exception failed) when (failed is SshException or IOException
                                                 or UnauthorizedAccessException)
        {
            lock (_guard)
            {
                entry.State = TransferState.Failed;
                entry.Why = failed.Message;
            }
        }
        finally
        {
            lock (_guard)
            {
                _running.Remove(entry);
            }
        }
    }

    private async Task CopyAsync(TransferEntry entry, CancellationToken cancellationToken)
    {
        SourceState source = entry.Direction == TransferDirection.Upload
            ? Local(entry.Local)
            : Remote(await _over.StatAsync(entry.Remote, cancellationToken).ConfigureAwait(false));

        entry.Length = source.Length;
        entry.Landed = entry.To;

        if (!await ResolveAsync(entry, source, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        // The partial and never the destination: what is already there survives every interruption
        // until the last byte has arrived. See TransferEntry.Partial.
        string partial = $"{entry.Landed}.qs-part";

        long already = await AlreadyThere(entry, partial, cancellationToken).ConfigureAwait(false);

        ResumeDecision decision = Decide(entry.Began, source, already);

        if (decision.Verdict == ResumeVerdict.Restart)
        {
            entry.Why = decision.Why;
        }

        entry.Began = source;
        entry.Restarted();
        entry.Reached(decision.At);

        await using (Stream reading = await OpenSource(entry, decision.At, cancellationToken)
                         .ConfigureAwait(false))
        await using (Stream writing = await OpenDestination(entry, partial, decision.At,
                                                            cancellationToken).ConfigureAwait(false))
        {
            byte[] block = new byte[BlockSize];
            long moved = decision.At;

            while (true)
            {
                // Checked here and not only inside the read: a cancel must stop the transfer now,
                // and not when the file it is on happens to finish.
                cancellationToken.ThrowIfCancellationRequested();

                int got = await reading.ReadAsync(block, cancellationToken).ConfigureAwait(false);

                if (got == 0)
                {
                    break;
                }

                await writing.WriteAsync(block.AsMemory(0, got), cancellationToken)
                             .ConfigureAwait(false);

                moved += got;

                entry.Reached(moved);
            }

            await writing.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        await LandAsync(entry, partial, source, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Answers a name that is already taken, and says whether there is still a transfer to make.
    /// </summary>
    private async ValueTask<bool> ResolveAsync(TransferEntry entry, SourceState source,
                                               CancellationToken cancellationToken)
    {
        SourceState? existing = await Existing(entry, entry.To, cancellationToken)
            .ConfigureAwait(false);

        if (existing is not { } there)
        {
            return true;
        }

        Collision what = new(entry.To, source.Length, source.Modified, there.Length, there.Modified);

        CollisionAnswer answer;
        bool settled;

        lock (_guard)
        {
            settled = _forTheRest is not null;
            answer = _forTheRest ?? CollisionAnswer.Skip;
        }

        if (!settled && OnCollision is not null)
        {
            CollisionChoice chosen = await OnCollision(what, cancellationToken)
                .ConfigureAwait(false);

            answer = chosen.Answer;

            if (chosen.ForTheRest)
            {
                lock (_guard)
                {
                    _forTheRest = chosen.Answer;
                }
            }
        }

        // Nobody to ask leaves the answer at Skip: a queue running unattended must not be the one
        // that overwrites, and the entry says what happened to it either way.
        entry.Resolved = answer;

        switch (answer)
        {
            case CollisionAnswer.Skip:
                entry.State = TransferState.Skipped;
                entry.Why = $"{entry.To} was already there and was left alone.";

                return false;

            case CollisionAnswer.TakeNewer when !what.IsNewer:
                entry.State = TransferState.Skipped;
                entry.Why = $"{entry.To} is the same age or newer, so it was left alone.";

                return false;

            case CollisionAnswer.Rename:
                entry.Landed = await FreeName(entry, cancellationToken).ConfigureAwait(false);

                return true;

            default:
                return true;
        }
    }

    /// <summary>
    /// Moves the finished bytes into place, and copies the times across where they can be held.
    ///
    /// <para>The destination is replaced only once every byte is written, so the window in which
    /// neither the old file nor a complete new one exists is one rename wide.</para>
    /// </summary>
    private async Task LandAsync(TransferEntry entry, string partial, SourceState source,
                                 CancellationToken cancellationToken)
    {
        if (entry.Direction == TransferDirection.Upload)
        {
            try
            {
                await _over.DeleteAsync(entry.Landed, cancellationToken).ConfigureAwait(false);
            }
            catch (SshException)
            {
                // Not there, which is the ordinary case.
            }

            await _over.RenameAsync(partial, entry.Landed, cancellationToken).ConfigureAwait(false);

            if (PreserveTimes)
            {
                await _over.SetLastWriteTimeAsync(entry.Landed, source.Modified, cancellationToken)
                           .ConfigureAwait(false);
            }

            Lose("A Windows file has no Unix mode, so what lands on the server takes the server's "
                 + "default permissions.");

            return;
        }

        File.Move(partial, entry.Landed, overwrite: true);

        if (PreserveTimes)
        {
            File.SetLastWriteTimeUtc(entry.Landed, source.Modified.UtcDateTime);
        }

        Lose("A Unix mode cannot be held on this filesystem, so permissions were not carried "
             + "across.");
    }

    private void Lose(string what)
    {
        lock (_guard)
        {
            if (!_losses.Contains(what, StringComparer.Ordinal))
            {
                _losses.Add(what);
            }
        }
    }

    /// <summary>A name beside the taken one that nothing is using.</summary>
    private async ValueTask<string> FreeName(TransferEntry entry, CancellationToken cancellationToken)
    {
        string directory = Folder(entry.To);
        string stem = Path.GetFileNameWithoutExtension(entry.To);
        string extension = Path.GetExtension(entry.To);

        for (int attempt = 2; attempt < 1000; attempt++)
        {
            string candidate = Join(directory, $"{stem} ({attempt}){extension}");

            if (await Existing(entry, candidate, cancellationToken).ConfigureAwait(false) is null)
            {
                return candidate;
            }
        }

        throw new SshException(SshFailureKind.Unrecognised,
                               $"No free name could be found beside {entry.To}.",
                               "A thousand names of that shape are already taken.");
    }

    private async ValueTask<long> AlreadyThere(TransferEntry entry, string partial,
                                               CancellationToken cancellationToken) =>
        await Existing(entry, partial, cancellationToken).ConfigureAwait(false) is { } there
            ? there.Length
            : 0;

    private async ValueTask<SourceState?> Existing(TransferEntry entry, string path,
                                                   CancellationToken cancellationToken)
    {
        if (entry.Direction == TransferDirection.Download)
        {
            return File.Exists(path) ? Local(path) : null;
        }

        try
        {
            return Remote(await _over.StatAsync(path, cancellationToken).ConfigureAwait(false));
        }
        catch (SshException)
        {
            return null;
        }
    }

    /// <summary>
    /// The directory part, in the separator the side it belongs to uses.
    ///
    /// <para>A remote path is not a Windows path: <c>Path.GetDirectoryName</c> would hand back a
    /// backslash, and a backslash is a legal character in a Unix file name rather than a
    /// separator.</para>
    /// </summary>
    private static string Folder(string path)
    {
        int slash = path.LastIndexOfAny(['/', '\\']);

        return slash < 0 ? string.Empty : path[..slash];
    }

    private static string Join(string directory, string name) =>
        directory.Length == 0 ? name
        : directory.Contains('\\', StringComparison.Ordinal) ? $"{directory}\\{name}"
        : $"{directory}/{name}";

    private async ValueTask<Stream> OpenSource(TransferEntry entry, long at,
                                               CancellationToken cancellationToken)
    {
        if (entry.Direction == TransferDirection.Upload)
        {
            FileStream local = new(entry.Local, FileMode.Open, FileAccess.Read, FileShare.Read);

            local.Seek(at, SeekOrigin.Begin);

            return local;
        }

        Stream remote = await _over.OpenReadAsync(entry.Remote, cancellationToken)
                                   .ConfigureAwait(false);

        remote.Seek(at, SeekOrigin.Begin);

        return remote;
    }

    private async ValueTask<Stream> OpenDestination(TransferEntry entry, string partial, long at,
                                                    CancellationToken cancellationToken)
    {
        if (entry.Direction == TransferDirection.Upload)
        {
            return at > 0
                ? await _over.OpenWriteAtAsync(partial, at, cancellationToken).ConfigureAwait(false)
                : await _over.OpenWriteAsync(partial, cancellationToken).ConfigureAwait(false);
        }

        string directory = Folder(partial);

        if (directory.Length > 0)
        {
            Directory.CreateDirectory(directory);
        }

        // Truncate on a fresh start rather than leaving a longer old partial with new bytes over
        // the front of it, which is a file that looks finished and is not.
        FileStream local = new(partial, at > 0 ? FileMode.OpenOrCreate : FileMode.Create,
                               FileAccess.Write, FileShare.None);

        local.Seek(at, SeekOrigin.Begin);

        return local;
    }

    private static SourceState Local(string path)
    {
        FileInfo file = new(path);

        return new SourceState(file.Length, Second(file.LastWriteTimeUtc));
    }

    private static SourceState Remote(RemoteEntry entry) =>
        new(entry.Length, Second(entry.Modified.UtcDateTime));

    /// <summary>
    /// A timestamp to the second, because that is the resolution SFTP version three carries.
    ///
    /// <para>Comparing finer would make every resume decide the source had changed, which is the
    /// safe direction but would mean resume never worked at all.</para>
    /// </summary>
    private static DateTimeOffset Second(DateTime when) =>
        new(new DateTime(when.Year, when.Month, when.Day, when.Hour, when.Minute, when.Second,
                         DateTimeKind.Utc));
}
