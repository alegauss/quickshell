namespace Quickshell.Transport;

/// <summary>A directory the copy will need, named on the destination side.</summary>
/// <param name="Path">Where it goes.</param>
/// <param name="Depth">How far below the root it sits, which is the order it must be made in.</param>
public readonly record struct PlannedDirectory(string Path, int Depth);

/// <summary>A file the copy will move.</summary>
/// <param name="From">Where it is read.</param>
/// <param name="To">Where it is written.</param>
public readonly record struct PlannedFile(string From, string To);

/// <summary>A symbolic link the copy will recreate rather than follow.</summary>
/// <param name="Target">What it points at, exactly as the far side spells it.</param>
/// <param name="Link">Where the new link goes.</param>
public readonly record struct PlannedLink(string Target, string Link);

/// <summary>
/// What copying a directory turns out to be, worked out before anything is written.
///
/// <para><b>The walk is separate from the copying because it is where the decisions are.</b>
/// Whether to follow a link, whether an empty directory exists at the far end, what order things
/// are made in — these are answered here, once, and can be inspected before a single byte
/// moves.</para>
///
/// <para><b>Directories are made before their contents, and an empty one stays empty rather than
/// disappearing.</b> That reads as too obvious to write down, and it is the most commonly skipped
/// part of a recursive copy: a walk that only enumerates files silently drops every empty
/// directory in the tree.</para>
///
/// <para><b>A link is copied as a link by default.</b> Following one is how a recursive copy walks
/// into a loop, or quietly drags in an entire filesystem through a <c>/</c> somebody left in their
/// home directory. Following is available and is chosen deliberately.</para>
///
/// <para><b>Copying a link works upward and not downward, and the walk says so rather than
/// guessing.</b> A link on this machine reports what it points at, so it can be recreated on the
/// server. A link on the server cannot be asked: SSH.NET exposes no readlink, publicly or
/// otherwise, so its target is not knowable through this seam. Such a link is left out with the
/// reason attached, because a link pointing at a guess is worse than no link. QS123 carries the
/// gap.</para>
/// </summary>
public sealed class TransferPlan
{
    private readonly List<PlannedDirectory> _directories = [];
    private readonly List<PlannedFile> _files = [];
    private readonly List<PlannedLink> _links = [];
    private readonly List<string> _skipped = [];

    private TransferPlan(TransferDirection direction, string from, string to)
    {
        Direction = direction;
        From = from;
        To = to;
    }

    /// <summary>Which way the copy goes.</summary>
    public TransferDirection Direction { get; }

    /// <summary>The directory being copied.</summary>
    public string From { get; }

    /// <summary>Where it is going.</summary>
    public string To { get; }

    /// <summary>Every directory to make, shallowest first — which is the order they must be made in.</summary>
    public IReadOnlyList<PlannedDirectory> Directories =>
        [.. _directories.OrderBy(directory => directory.Depth)];

    /// <summary>Every file to move.</summary>
    public IReadOnlyList<PlannedFile> Files => _files;

    /// <summary>Every link to recreate.</summary>
    public IReadOnlyList<PlannedLink> Links => _links;

    /// <summary>What was left out, and why, in the words a user is shown.</summary>
    public IReadOnlyList<string> Skipped => _skipped;

    /// <summary>How many bytes the files come to.</summary>
    public long Bytes { get; private set; }

    /// <summary>
    /// Works out what copying a directory from the server to this machine involves.
    /// </summary>
    /// <param name="over">The channel to read the far side with.</param>
    /// <param name="from">The remote directory.</param>
    /// <param name="to">The local directory it becomes.</param>
    /// <param name="links">What to do about symbolic links.</param>
    /// <param name="cancellationToken">Abandons the walk.</param>
    public static async ValueTask<TransferPlan> ToCopyDownAsync(
        IFileTransferChannel over, string from, string to, LinkPolicy links = LinkPolicy.Copy,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(over);
        ArgumentException.ThrowIfNullOrWhiteSpace(from);
        ArgumentException.ThrowIfNullOrWhiteSpace(to);

        TransferPlan plan = new(TransferDirection.Download, from, to);

        await plan.WalkRemote(over, from, to, links, 0, cancellationToken).ConfigureAwait(false);

        return plan;
    }

    /// <summary>Works out what copying a local directory to the server involves.</summary>
    /// <param name="from">The local directory.</param>
    /// <param name="to">The remote directory it becomes.</param>
    /// <param name="links">What to do about symbolic links.</param>
    public static TransferPlan ToCopyUp(string from, string to, LinkPolicy links = LinkPolicy.Copy)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(from);
        ArgumentException.ThrowIfNullOrWhiteSpace(to);

        TransferPlan plan = new(TransferDirection.Upload, from, to);

        plan.WalkLocal(from, to, links, 0);

        return plan;
    }

    /// <summary>
    /// Puts the plan into a queue: directories first, then links, then the files.
    ///
    /// <para>The directories are made before anything is written into them, which is why they are
    /// not simply created as each file arrives — a file whose parent does not exist yet is a
    /// failure the user did nothing to cause.</para>
    /// </summary>
    public async ValueTask<IReadOnlyList<TransferEntry>> EnqueueAsync(
        TransferQueue into, IFileTransferChannel over, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(into);
        ArgumentNullException.ThrowIfNull(over);

        foreach (PlannedDirectory directory in Directories)
        {
            await MakeAsync(over, directory.Path, cancellationToken).ConfigureAwait(false);
        }

        foreach (PlannedLink link in _links)
        {
            if (Direction == TransferDirection.Upload)
            {
                await over.SymbolicLinkAsync(link.Target, link.Link, cancellationToken)
                          .ConfigureAwait(false);
            }
            else
            {
                // Windows will not make one without the developer-mode privilege, so the link is
                // recorded as a loss rather than failing the whole copy over a shortcut.
                _skipped.Add($"{link.Link} is a symbolic link to {link.Target}, which was not "
                             + "recreated: this filesystem does not allow it without extra rights.");
            }
        }

        List<TransferEntry> entries = [];

        foreach (PlannedFile file in _files)
        {
            entries.Add(Direction == TransferDirection.Upload
                            ? into.Enqueue(Direction, file.From, file.To)
                            : into.Enqueue(Direction, file.To, file.From));
        }

        return entries;
    }

    private async ValueTask MakeAsync(IFileTransferChannel over, string path,
                                      CancellationToken cancellationToken)
    {
        if (Direction == TransferDirection.Download)
        {
            Directory.CreateDirectory(path);

            return;
        }

        try
        {
            await over.CreateDirectoryAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (SshException)
        {
            // Already there, which is the ordinary case for the second copy into a tree.
        }
    }

    private async ValueTask WalkRemote(IFileTransferChannel over, string from, string to,
                                       LinkPolicy links, int depth,
                                       CancellationToken cancellationToken)
    {
        // Recorded before its contents, so an empty directory is still a directory at the far end.
        _directories.Add(new PlannedDirectory(to, depth));

        List<RemoteEntry> here = [];

        await foreach (RemoteEntry entry in over.ListAsync(from, cancellationToken)
                                                .ConfigureAwait(false))
        {
            if (entry.Name is not ("." or ".."))
            {
                here.Add(entry);
            }
        }

        foreach (RemoteEntry entry in here)
        {
            string below = $"{from.TrimEnd('/')}/{entry.Name}";
            string beside = Path.Combine(to, entry.Name);

            RemoteEntry what = entry;

            if (entry.Permissions.StartsWith('l'))
            {
                switch (links)
                {
                    case LinkPolicy.Skip:
                        _skipped.Add($"{below} is a symbolic link and was left out.");

                        continue;

                    case LinkPolicy.Copy:
                        _skipped.Add($"{below} is a symbolic link and was left out: what it points "
                                     + "at cannot be read over this connection, and a link to a "
                                     + "guess is worse than no link.");

                        continue;

                    default:
                        // Followed: whatever it points at, which a stat answers because a stat
                        // follows and the listing that produced this entry did not.
                        what = await over.StatAsync(below, cancellationToken).ConfigureAwait(false);

                        break;
                }
            }

            if (what.IsDirectory)
            {
                await WalkRemote(over, below, beside, links, depth + 1, cancellationToken)
                    .ConfigureAwait(false);

                continue;
            }

            _files.Add(new PlannedFile(below, beside));

            Bytes += what.Length;
        }
    }

    private void WalkLocal(string from, string to, LinkPolicy links, int depth)
    {
        _directories.Add(new PlannedDirectory(to, depth));

        foreach (FileSystemInfo entry in new DirectoryInfo(from).EnumerateFileSystemInfos())
        {
            string beside = $"{to.TrimEnd('/')}/{entry.Name}";

            if (entry.LinkTarget is { } target)
            {
                switch (links)
                {
                    case LinkPolicy.Skip:
                        _skipped.Add($"{entry.FullName} is a link and was left out.");

                        continue;

                    case LinkPolicy.Copy:
                        _links.Add(new PlannedLink(target, beside));

                        continue;

                    default:
                        break;
                }
            }

            if (entry is DirectoryInfo directory)
            {
                WalkLocal(directory.FullName, beside, links, depth + 1);

                continue;
            }

            _files.Add(new PlannedFile(entry.FullName, beside));

            Bytes += ((FileInfo)entry).Length;
        }
    }
}
