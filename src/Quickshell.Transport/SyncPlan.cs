using System.Security.Cryptography;
using System.Text;

namespace Quickshell.Transport;

/// <summary>Which way a synchronisation goes.</summary>
public enum SyncDirection
{
    /// <summary>Local to remote, adding and replacing, never deleting.</summary>
    Upload,

    /// <summary>Remote to local, adding and replacing, never deleting.</summary>
    Download,

    /// <summary>Upward, and what the source does not have is deleted from the destination.</summary>
    Mirror,
}

/// <summary>What a comparison found about one path.</summary>
public enum SyncChange
{
    /// <summary>Both sides agree, within tolerance.</summary>
    Same,

    /// <summary>The source has it and the destination does not.</summary>
    New,

    /// <summary>Both have it and they differ.</summary>
    Changed,

    /// <summary>The destination has it and the source does not.</summary>
    OnlyOnDestination,
}

/// <summary>One path, and what the two sides say about it.</summary>
/// <param name="Path">Where it is, relative to the roots being compared.</param>
/// <param name="Change">What was found.</param>
/// <param name="Length">Its size at the source, or zero where there is none.</param>
/// <param name="Modified">When the source last changed it.</param>
/// <param name="DestinationLength">Its size at the destination, or zero where there is none.</param>
/// <param name="DestinationModified">When the destination last changed it.</param>
/// <param name="Why">What made this differ, in the words a user is shown.</param>
public readonly record struct SyncEntry(string Path, SyncChange Change, long Length,
                                        DateTimeOffset Modified, long DestinationLength,
                                        DateTimeOffset DestinationModified, string Why);

/// <summary>
/// How far apart two timestamps may be and still mean the same moment.
///
/// <para><b>Without this the feature does not work at all.</b> NTFS keeps time to a hundred
/// nanoseconds and SFTP version three carries whole seconds, so the same file copied between them
/// has two different timestamps the instant it lands. A comparison without tolerance reports every
/// file as changed, every time, and a sync that always copies everything is a sync nobody uses.</para>
/// </summary>
/// <param name="Granularity">
/// The coarser of the two filesystems' resolutions. Two seconds by default, which covers the
/// one-second floor of SFTP version three and the two-second floor of FAT with room to spare.
/// </param>
/// <param name="Skew">
/// How far the two clocks may be apart. Zero by default, because a machine whose clock is wrong is
/// a thing to fix rather than to accommodate silently — but a server nobody controls is real, so
/// this can be set.
/// </param>
public readonly record struct SyncTolerance(TimeSpan Granularity, TimeSpan Skew)
{
    /// <summary>The default: two seconds of resolution and no clock skew.</summary>
    public static SyncTolerance Default { get; } = new(TimeSpan.FromSeconds(2), TimeSpan.Zero);

    /// <summary>Whether two times are close enough to be the same moment.</summary>
    public bool Alike(DateTimeOffset one, DateTimeOffset other) =>
        (one - other).Duration() <= Granularity + Skew;
}

/// <summary>
/// Which paths a comparison leaves out.
///
/// <para><b>The syntax is the one people already know</b> — the shape <c>.gitignore</c> and
/// <c>rsync --exclude</c> use — rather than one invented here. A filter language a user has to learn
/// is a filter language they get wrong, and getting an exclusion wrong in a mirror deletes
/// something.</para>
/// </summary>
public sealed class SyncFilter
{
    private readonly List<string> _patterns;

    /// <summary>A filter excluding everything matching any of these patterns.</summary>
    /// <param name="patterns">
    /// Globs: <c>*</c> matches within one path segment, <c>**</c> across segments, <c>?</c> matches
    /// one character, and a trailing <c>/</c> means a directory and everything under it.
    /// </param>
    public SyncFilter(params string[] patterns)
    {
        ArgumentNullException.ThrowIfNull(patterns);

        _patterns = [.. patterns.Where(pattern => !string.IsNullOrWhiteSpace(pattern))];
    }

    /// <summary>A filter that excludes nothing.</summary>
    public static SyncFilter None { get; } = new();

    /// <summary>The patterns, as they were given.</summary>
    public IReadOnlyList<string> Patterns => _patterns;

    /// <summary>Whether a relative path is left out.</summary>
    public bool Excludes(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        string relative = path.Replace('\\', '/').TrimStart('/');

        return _patterns.Any(pattern => Matches(relative, pattern));
    }

    /// <summary>
    /// Whether one path matches one pattern, by the rules named on the constructor.
    /// </summary>
    internal static bool Matches(string path, string pattern)
    {
        bool directory = pattern.EndsWith('/');
        string wanted = pattern.Trim('/');

        // A pattern with no slash in it matches any segment, which is what makes "*.tmp" mean what
        // everybody expects it to mean.
        if (!wanted.Contains('/', StringComparison.Ordinal))
        {
            string[] segments = path.Split('/');

            return directory
                ? segments[..^1].Any(segment => Glob(segment, wanted))
                : segments.Any(segment => Glob(segment, wanted));
        }

        return Glob(path, wanted)
               || (directory && path.StartsWith($"{wanted}/", StringComparison.Ordinal))
               || path.StartsWith($"{wanted}/", StringComparison.Ordinal);
    }

    /// <summary>
    /// A glob against one string, where <c>**</c> crosses separators and <c>*</c> does not.
    /// </summary>
    private static bool Glob(string text, string pattern)
    {
        return Walk(text, 0, pattern, 0);

        static bool Walk(string text, int at, string pattern, int p)
        {
            while (p < pattern.Length)
            {
                if (pattern[p] == '*')
                {
                    bool crosses = p + 1 < pattern.Length && pattern[p + 1] == '*';
                    int after = p + (crosses ? 2 : 1);

                    for (int take = at; take <= text.Length; take++)
                    {
                        if (!crosses && take > at && text[take - 1] == '/')
                        {
                            break;
                        }

                        if (Walk(text, take, pattern, after))
                        {
                            return true;
                        }
                    }

                    return false;
                }

                if (at >= text.Length)
                {
                    return false;
                }

                if (pattern[p] != '?' && pattern[p] != text[at])
                {
                    return false;
                }

                at++;
                p++;
            }

            return at == text.Length;
        }
    }
}

/// <summary>
/// What synchronising two directories would do, worked out before anything is touched.
///
/// <para><b>The comparison is the part worth designing, and it is shown before anything runs.</b>
/// A sync that acts first and reports afterwards is a sync that deletes something. So this is a
/// value: it is produced, it can be looked at, and only then is it handed to
/// <see cref="ApplyAsync"/>.</para>
///
/// <para><b>A mirror deletes exactly what this plan lists and nothing else.</b> Not what a second
/// walk finds at the time of the deletion — a file somebody added to the destination between the
/// comparison and the confirmation was never shown to the user, and deleting it would be deleting
/// something they never saw. The applier works from the recorded list.</para>
///
/// <para><b>Two-way synchronisation is deliberately absent.</b> It needs a record of what changed
/// since the last run, which this client does not keep, and guessing in its absence is exactly how a
/// two-way sync loses somebody's work.</para>
/// </summary>
public sealed class SyncPlan
{
    private readonly List<SyncEntry> _entries;

    private SyncPlan(SyncDirection direction, string local, string remote,
                     List<SyncEntry> entries, bool compared)
    {
        Direction = direction;
        Local = local;
        Remote = remote;
        ComparedContents = compared;
        _entries = entries;
    }

    /// <summary>Which way it would go.</summary>
    public SyncDirection Direction { get; }

    /// <summary>The directory on this machine.</summary>
    public string Local { get; }

    /// <summary>The directory on the server.</summary>
    public string Remote { get; }

    /// <summary>Whether the contents were read and compared rather than only their size and time.</summary>
    public bool ComparedContents { get; }

    /// <summary>Everything the comparison looked at, in path order.</summary>
    public IReadOnlyList<SyncEntry> Entries => _entries;

    /// <summary>What the source has and the destination does not.</summary>
    public IReadOnlyList<SyncEntry> New =>
        [.. _entries.Where(entry => entry.Change == SyncChange.New)];

    /// <summary>What both have and differ on.</summary>
    public IReadOnlyList<SyncEntry> Changed =>
        [.. _entries.Where(entry => entry.Change == SyncChange.Changed)];

    /// <summary>What only the destination has.</summary>
    public IReadOnlyList<SyncEntry> OnlyOnDestination =>
        [.. _entries.Where(entry => entry.Change == SyncChange.OnlyOnDestination)];

    /// <summary>
    /// Exactly what a mirror would delete, and empty for any other direction.
    ///
    /// <para>This list is the promise. Whatever is in it is what the user is shown and what the
    /// applier removes; nothing is added to it afterwards.</para>
    /// </summary>
    public IReadOnlyList<string> Deletions =>
        Direction == SyncDirection.Mirror
            ? [.. OnlyOnDestination.Select(entry => entry.Path)]
            : [];

    /// <summary>How many bytes moving would cost.</summary>
    public long Bytes => New.Concat(Changed).Sum(entry => entry.Length);

    /// <summary>Whether there is anything at all to do.</summary>
    public bool IsEmpty => New.Count == 0 && Changed.Count == 0 && Deletions.Count == 0;

    /// <summary>
    /// Compares two directories without touching either.
    /// </summary>
    /// <param name="over">The channel to read the far side with.</param>
    /// <param name="local">The directory on this machine.</param>
    /// <param name="remote">The directory on the server.</param>
    /// <param name="direction">Which way a transfer would go.</param>
    /// <param name="filter">What to leave out.</param>
    /// <param name="tolerance">How far apart two timestamps may be and still agree.</param>
    /// <param name="contents">
    /// Whether to compare by reading both files rather than by size and time. Off by default: it
    /// costs a full read of both sides, which over a link is the whole transfer twice.
    /// </param>
    /// <param name="cancellationToken">Abandons the comparison.</param>
    public static async ValueTask<SyncPlan> CompareAsync(
        IFileTransferChannel over, string local, string remote,
        SyncDirection direction = SyncDirection.Upload, SyncFilter? filter = null,
        SyncTolerance? tolerance = null, bool contents = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(over);
        ArgumentException.ThrowIfNullOrWhiteSpace(local);
        ArgumentException.ThrowIfNullOrWhiteSpace(remote);

        SyncFilter leaving = filter ?? SyncFilter.None;
        SyncTolerance close = tolerance ?? SyncTolerance.Default;

        Dictionary<string, Side> here = new(StringComparer.Ordinal);
        Dictionary<string, Side> there = new(StringComparer.Ordinal);

        WalkLocal(local, string.Empty, leaving, here);

        await WalkRemote(over, remote, string.Empty, leaving, there, cancellationToken)
            .ConfigureAwait(false);

        bool up = direction != SyncDirection.Download;

        Dictionary<string, Side> source = up ? here : there;
        Dictionary<string, Side> destination = up ? there : here;

        List<SyncEntry> entries = [];

        foreach ((string path, Side mine) in source.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (!destination.TryGetValue(path, out Side yours))
            {
                entries.Add(new SyncEntry(path, SyncChange.New, mine.Length, mine.Modified, 0,
                                          default, "it is not on the destination"));

                continue;
            }

            (bool same, string why) = await AlikeAsync(over, local, remote, path, mine, yours,
                                                       close, contents, up, cancellationToken)
                .ConfigureAwait(false);

            entries.Add(new SyncEntry(path, same ? SyncChange.Same : SyncChange.Changed,
                                      mine.Length, mine.Modified, yours.Length, yours.Modified,
                                      why));
        }

        foreach ((string path, Side yours) in destination
                     .Where(pair => !source.ContainsKey(pair.Key))
                     .OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            entries.Add(new SyncEntry(path, SyncChange.OnlyOnDestination, 0, default, yours.Length,
                                      yours.Modified, "it is not on the source"));
        }

        return new SyncPlan(direction, local, remote, entries, contents);
    }

    /// <summary>
    /// Carries out exactly what this plan says.
    /// </summary>
    /// <param name="over">The channel to move the bytes with.</param>
    /// <param name="confirm">
    /// Asked before anything is deleted, with the paths this plan listed. Required for a mirror and
    /// ignored otherwise: without it, nothing is deleted at all.
    /// </param>
    /// <param name="cancellationToken">Abandons the run.</param>
    /// <returns>The queue the transfers ran in, so a caller can read what happened to each.</returns>
    public async ValueTask<TransferQueue> ApplyAsync(
        IFileTransferChannel over, Func<IReadOnlyList<string>, ValueTask<bool>>? confirm = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(over);

        TransferQueue queue = new(over)
        {
            OnCollision = (_, _) =>
                ValueTask.FromResult(new CollisionChoice(CollisionAnswer.Overwrite, true)),
        };

        bool up = Direction != SyncDirection.Download;

        foreach (SyncEntry entry in New.Concat(Changed))
        {
            string mine = Path.Combine(Local, entry.Path.Replace('/', Path.DirectorySeparatorChar));
            string theirs = $"{Remote.TrimEnd('/')}/{entry.Path}";

            if (up)
            {
                await MakeRemote(over, Parent(theirs), cancellationToken).ConfigureAwait(false);
            }
            else
            {
                string? parent = Path.GetDirectoryName(mine);

                if (parent is { Length: > 0 })
                {
                    Directory.CreateDirectory(parent);
                }
            }

            queue.Enqueue(up ? TransferDirection.Upload : TransferDirection.Download, mine, theirs);
        }

        await queue.RunAsync(cancellationToken).ConfigureAwait(false);

        await DeleteAsync(over, confirm, up, cancellationToken).ConfigureAwait(false);

        return queue;
    }

    /// <summary>
    /// Removes what the plan listed, and only that, once somebody has agreed to the list.
    /// </summary>
    private async ValueTask DeleteAsync(IFileTransferChannel over,
                                        Func<IReadOnlyList<string>, ValueTask<bool>>? confirm,
                                        bool up, CancellationToken cancellationToken)
    {
        IReadOnlyList<string> going = Deletions;

        if (going.Count == 0)
        {
            return;
        }

        // No confirmation means no deletions. A mirror run by something that cannot ask is a mirror
        // that copies and leaves the extra files alone, which is the safe half of the operation.
        if (confirm is null || !await confirm(going).ConfigureAwait(false))
        {
            return;
        }

        foreach (string path in going)
        {
            if (up)
            {
                await over.DeleteAsync($"{Remote.TrimEnd('/')}/{path}", cancellationToken)
                          .ConfigureAwait(false);
            }
            else
            {
                string mine = Path.Combine(Local, path.Replace('/', Path.DirectorySeparatorChar));

                if (File.Exists(mine))
                {
                    File.Delete(mine);
                }
            }
        }
    }

    /// <summary>
    /// Whether two files are the same, and what says so.
    ///
    /// <para>Size first, because a differing size settles it without a round trip. Then time within
    /// tolerance. Contents only where asked for, since reading both sides is the transfer twice
    /// over.</para>
    /// </summary>
    private static async ValueTask<(bool Same, string Why)> AlikeAsync(
        IFileTransferChannel over, string local, string remote, string path, Side mine, Side yours,
        SyncTolerance close, bool contents, bool up, CancellationToken cancellationToken)
    {
        if (mine.Length != yours.Length)
        {
            return (false, $"the sizes differ: {mine.Length} against {yours.Length}");
        }

        if (contents)
        {
            string localFile = Path.Combine(local, path.Replace('/', Path.DirectorySeparatorChar));
            string remoteFile = $"{remote.TrimEnd('/')}/{path}";

            byte[] one = await LocalHash(localFile, cancellationToken).ConfigureAwait(false);
            byte[] other = await RemoteHash(over, remoteFile, cancellationToken)
                .ConfigureAwait(false);

            return one.AsSpan().SequenceEqual(other)
                ? (true, "the contents match")
                : (false, "the contents differ");
        }

        if (!close.Alike(mine.Modified, yours.Modified))
        {
            return (false,
                    $"the times differ by more than {close.Granularity + close.Skew}: "
                    + $"{mine.Modified:u} against {yours.Modified:u}");
        }

        return (true, up ? "the size and time agree" : "the size and time agree");
    }

    private static async ValueTask<byte[]> LocalHash(string path, CancellationToken cancellationToken)
    {
        await using FileStream reading = File.OpenRead(path);

        return await SHA256.HashDataAsync(reading, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<byte[]> RemoteHash(IFileTransferChannel over, string path,
                                                      CancellationToken cancellationToken)
    {
        await using Stream reading = await over.OpenReadAsync(path, cancellationToken)
                                               .ConfigureAwait(false);

        return await SHA256.HashDataAsync(reading, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask MakeRemote(IFileTransferChannel over, string path,
                                              CancellationToken cancellationToken)
    {
        if (path.Length == 0)
        {
            return;
        }

        try
        {
            await over.CreateDirectoryAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (SshException)
        {
            // Already there, which is the ordinary case.
        }
    }

    private static string Parent(string path)
    {
        int slash = path.LastIndexOf('/');

        return slash <= 0 ? string.Empty : path[..slash];
    }

    private static void WalkLocal(string root, string under, SyncFilter filter,
                                  Dictionary<string, Side> into)
    {
        if (!Directory.Exists(root))
        {
            return;
        }

        foreach (FileInfo file in new DirectoryInfo(root).EnumerateFiles())
        {
            string path = under.Length == 0 ? file.Name : $"{under}/{file.Name}";

            if (!filter.Excludes(path))
            {
                into[path] = new Side(file.Length, Second(file.LastWriteTimeUtc));
            }
        }

        foreach (DirectoryInfo directory in new DirectoryInfo(root).EnumerateDirectories())
        {
            string path = under.Length == 0 ? directory.Name : $"{under}/{directory.Name}";

            if (!filter.Excludes($"{path}/"))
            {
                WalkLocal(directory.FullName, path, filter, into);
            }
        }
    }

    private static async ValueTask WalkRemote(IFileTransferChannel over, string root, string under,
                                              SyncFilter filter, Dictionary<string, Side> into,
                                              CancellationToken cancellationToken)
    {
        List<RemoteEntry> here = [];

        try
        {
            await foreach (RemoteEntry entry in over.ListAsync(root, cancellationToken)
                                                    .ConfigureAwait(false))
            {
                if (entry.Name is not ("." or ".."))
                {
                    here.Add(entry);
                }
            }
        }
        catch (SshException)
        {
            // A destination that is not there yet is an empty one, which makes everything new.
            return;
        }

        foreach (RemoteEntry entry in here)
        {
            string path = under.Length == 0 ? entry.Name : $"{under}/{entry.Name}";

            if (entry.IsDirectory)
            {
                if (!filter.Excludes($"{path}/"))
                {
                    await WalkRemote(over, $"{root.TrimEnd('/')}/{entry.Name}", path, filter, into,
                                     cancellationToken).ConfigureAwait(false);
                }

                continue;
            }

            if (!filter.Excludes(path))
            {
                into[path] = new Side(entry.Length, Second(entry.Modified.UtcDateTime));
            }
        }
    }

    /// <summary>To the second, which is the coarser of the two resolutions in play.</summary>
    private static DateTimeOffset Second(DateTime when) =>
        new(new DateTime(when.Year, when.Month, when.Day, when.Hour, when.Minute, when.Second,
                         DateTimeKind.Utc));

    /// <summary>What one side knows about one path.</summary>
    private readonly record struct Side(long Length, DateTimeOffset Modified);

    /// <summary>The plan as a person reads it, which is what a dialog shows.</summary>
    public override string ToString()
    {
        StringBuilder said = new();

        said.Append(Direction).Append(": ")
            .Append(New.Count).Append(" new, ")
            .Append(Changed.Count).Append(" changed, ")
            .Append(Deletions.Count).Append(" to delete");

        return said.ToString();
    }
}
