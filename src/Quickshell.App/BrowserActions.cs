using System.Globalization;
using System.IO;
using Quickshell.Transport;

namespace Quickshell.App;

/// <summary>What a copy is about to do, which is what its question says.</summary>
/// <param name="Entries">What was selected.</param>
/// <param name="To">The directory it goes into.</param>
/// <param name="Side">What that directory is on: this computer, or the host.</param>
public sealed record CopyQuestion(IReadOnlyList<FileItem> Entries, string To, string Side)
{
    /// <summary>The question, with the destination named in full.</summary>
    public string Asking =>
        $"Copy {BrowserActions.Entries(Entries.Count)} to {To} on {Side}?";
}

/// <summary>What a delete is about to remove, which is what its question says.</summary>
/// <param name="Entries">What was selected.</param>
/// <param name="From">The directory it is in.</param>
public sealed record DeleteQuestion(IReadOnlyList<FileItem> Entries, string From)
{
    /// <summary>How many of the entries are directories, whose contents go with them.</summary>
    public int Directories => Entries.Count(entry => entry.IsDirectory);

    /// <summary>
    /// The question: how many, and whether any of them is a directory — QS60's own wording of what a
    /// delete owes the user before it happens.
    /// </summary>
    public string Asking
    {
        get
        {
            string what = Entries.Count == 1
                ? Entries[0].Name
                : BrowserActions.Entries(Entries.Count);

            string folders = Directories switch
            {
                0 => string.Empty,
                1 when Entries.Count == 1 => " It is a directory, and everything in it goes too.",
                1 => " One of them is a directory, and everything in it goes too.",
                _ => $" {Directories.ToString(CultureInfo.InvariantCulture)} of them are directories, "
                     + "and everything in them goes too.",
            };

            return $"Delete {what} from {From}?{folders}";
        }
    }
}

/// <summary>A name being asked for: a new one, or a rename's.</summary>
/// <param name="Asking">What is being named, in a sentence.</param>
/// <param name="Initial">What the box starts with.</param>
public sealed record NameQuestion(string Asking, string Initial);

/// <summary>A mode being asked for, with what it is now.</summary>
/// <param name="Entry">What is being changed.</param>
/// <param name="Current">Its mode now, in the three octal digits a server writes, or empty.</param>
/// <param name="OnlyWritable">
/// Whether this side keeps only whether the owner may write, which this computer's does.
/// </param>
public sealed record ModeQuestion(FileItem Entry, string Current, bool OnlyWritable);

/// <summary>
/// The operations between the browser's two panes — QS60's second half.
///
/// <para><b>Every one acts on the selection of the pane with the keyboard</b>, and a copy goes to the
/// directory the other pane is showing. That is the whole grammar of a two-pane browser, and it is
/// why there is no "to where" field anywhere: the other pane is the answer.</para>
///
/// <para><b>Everything irreversible is asked about first, and nothing reversible is.</b> A delete
/// names how many entries and whether any is a directory; a copy names where it lands, because
/// sending a tree to the wrong host is the mistake a two-pane window makes easy. A rename and a new
/// directory ask only for the name they need.</para>
///
/// <para><b>It knows no window</b>, for the reason <see cref="DirectoryPane"/> gives: every question
/// is a delegate, and what changes a pane is handed to the pane's thread. A test answers the
/// questions and drains that thread; the browser shows dialogs and has a dispatcher.</para>
/// </summary>
public sealed class BrowserActions
{
    private readonly Action<Action> _post;

    /// <summary>The operations over a browser's two panes.</summary>
    /// <param name="local">This computer's pane.</param>
    /// <param name="remote">The host's pane, or null where the tab has none.</param>
    /// <param name="post">How work reaches the panes' thread, as for <see cref="DirectoryPane"/>.</param>
    public BrowserActions(DirectoryPane local, DirectoryPane? remote, Action<Action> post)
    {
        ArgumentNullException.ThrowIfNull(local);
        ArgumentNullException.ThrowIfNull(post);

        Local = local;
        Remote = remote;
        Active = local;
        _post = post;
    }

    /// <summary>This computer's pane.</summary>
    public DirectoryPane Local { get; }

    /// <summary>The host's pane, or null.</summary>
    public DirectoryPane? Remote { get; }

    /// <summary>The pane with the keyboard, whose selection every operation acts on.</summary>
    public DirectoryPane Active { get; set; }

    /// <summary>The pane a copy goes to, or null where there is only one.</summary>
    public DirectoryPane? Other => ReferenceEquals(Active, Local) ? Remote : Local;

    /// <summary>Asks before a copy. Refuses when nobody answers, as every question here does.</summary>
    public Func<CopyQuestion, bool> AskingToCopy { get; set; } = _ => false;

    /// <summary>Asks before a delete.</summary>
    public Func<DeleteQuestion, bool> AskingToDelete { get; set; } = _ => false;

    /// <summary>Asks for a name, or answers null for none.</summary>
    public Func<NameQuestion, string?> AskingForName { get; set; } = _ => null;

    /// <summary>Asks for a mode, or answers null for none.</summary>
    public Func<ModeQuestion, int?> AskingForMode { get; set; } = _ => null;

    /// <summary>
    /// Asks about a name already taken at the destination. Null leaves the queue's own answer, which
    /// is to skip: a copy nobody is watching must not be the one that overwrites.
    /// </summary>
    public CollisionCheck? OnCollision { get; set; }

    /// <summary>Whether a copy has somewhere to go and something to take.</summary>
    public bool CanCopy => Other is not null && Active.Selected.Count > 0;

    /// <summary>
    /// Copies the selection into the directory the other pane shows, over the session's own file
    /// channel, and says afterwards what landed and what did not.
    ///
    /// <para>The transfer is <see cref="TransferQueue"/>'s, so it carries everything that queue already
    /// guarantees: nothing overwritten half-written, a name that is taken asked about, a directory
    /// made before what goes in it and an empty one kept.</para>
    /// </summary>
    public async Task CopyAsync(CancellationToken cancellationToken = default)
    {
        if (Other is not { } to || Active.Selected is not { Count: > 0 } picked
            || Remote?.Side is not RemoteFiles host)
        {
            return;
        }

        DirectoryPane from = Active;
        bool up = ReferenceEquals(from, Local);

        if (!AskingToCopy(new CopyQuestion(picked, to.Path, to.Side.Title)))
        {
            return;
        }

        TransferQueue queue = new(host.Channel) { OnCollision = OnCollision };
        List<string> left = [];

        try
        {
            foreach (FileItem item in picked)
            {
                string source = from.Side.Into(from.Path, item.Name);
                string target = to.Side.Into(to.Path, item.Name);

                if (item.IsDirectory)
                {
                    TransferPlan plan = up
                        ? TransferPlan.ToCopyUp(source, target)
                        : await TransferPlan.ToCopyDownAsync(host.Channel, source, target,
                                                             cancellationToken: cancellationToken)
                                            .ConfigureAwait(false);

                    await plan.EnqueueAsync(queue, host.Channel, cancellationToken).ConfigureAwait(false);

                    left.AddRange(plan.Skipped);
                }
                else
                {
                    queue.Enqueue(up ? TransferDirection.Upload : TransferDirection.Download,
                                  up ? source : target, up ? target : source);
                }
            }

            await queue.RunAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception failed) when (failed is not OperationCanceledException)
        {
            Tell(to, $"The copy stopped: {Sentence(failed)}", refresh: true);

            return;
        }

        Tell(to, Copied(queue.Entries, left), refresh: true);
    }

    /// <summary>
    /// Files dropped onto a pane from Explorer, copied into the directory it shows — QS64.
    ///
    /// <para><b>The drop is the question.</b> A copy started from a key names where it lands before it
    /// starts, because the destination is the other pane and easy to have wrong; a drop was aimed at
    /// the pane it lands on, so it is not asked about again. A name already taken is still asked
    /// about, as every copy here does.</para>
    ///
    /// <para>Onto the host's pane it is an upload through <see cref="TransferQueue"/>, with
    /// everything that queue guarantees. Onto this computer's pane it is a copy on this disk, with the
    /// same question about a name that is taken.</para>
    /// </summary>
    /// <param name="onto">The pane the files were let go over.</param>
    /// <param name="paths">What was dropped, as this computer's paths.</param>
    /// <param name="cancellationToken">Abandons the copy.</param>
    public async Task DropAsync(DirectoryPane onto, IReadOnlyList<string> paths,
                                CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(onto);
        ArgumentNullException.ThrowIfNull(paths);

        if (paths.Count == 0 || onto.Path.Length == 0)
        {
            return;
        }

        try
        {
            if (onto.Side is RemoteFiles host)
            {
                TransferQueue queue = new(host.Channel) { OnCollision = OnCollision };
                List<string> left = [];

                foreach (string path in paths)
                {
                    string target = host.Into(onto.Path, Path.GetFileName(Path.TrimEndingDirectorySeparator(path)));

                    if (Directory.Exists(path))
                    {
                        TransferPlan plan = TransferPlan.ToCopyUp(path, target);

                        await plan.EnqueueAsync(queue, host.Channel, cancellationToken).ConfigureAwait(false);

                        left.AddRange(plan.Skipped);
                    }
                    else
                    {
                        queue.Enqueue(TransferDirection.Upload, path, target);
                    }
                }

                await queue.RunAsync(cancellationToken).ConfigureAwait(false);

                Tell(onto, Copied(queue.Entries, left), refresh: true);

                return;
            }

            (int done, int kept) = await CopyHereAsync(paths, onto.Path, new Answering(), cancellationToken)
                                             .ConfigureAwait(false);

            string said = $"Copied {done.ToString("N0", CultureInfo.InvariantCulture)} "
                          + (done == 1 ? "file" : "files");

            Tell(onto, kept > 0 ? $"{said}, {kept.ToString(CultureInfo.InvariantCulture)} left alone." : said + ".",
                 refresh: true);
        }
        catch (Exception failed) when (failed is not OperationCanceledException)
        {
            Tell(onto, $"The copy stopped: {Sentence(failed)}", refresh: true);
        }
    }

    /// <summary>
    /// Copies files and directories into a directory of this computer, asking about a name that is
    /// taken exactly as the queue would.
    /// </summary>
    /// <returns>How many files were written, and how many were left alone.</returns>
    private async Task<(int Done, int Kept)> CopyHereAsync(IEnumerable<string> sources, string into,
                                                           Answering answering,
                                                           CancellationToken cancellationToken)
    {
        int done = 0;
        int kept = 0;

        foreach (string source in sources)
        {
            string name = Path.GetFileName(Path.TrimEndingDirectorySeparator(source));

            if (Directory.Exists(source))
            {
                string target = Path.Combine(into, name);

                Directory.CreateDirectory(target);

                (int inner, int innerKept) = await CopyHereAsync(
                    Directory.EnumerateFileSystemEntries(source), target, answering, cancellationToken)
                    .ConfigureAwait(false);

                done += inner;
                kept += innerKept;

                continue;
            }

            string? destination = await Destination(source, Path.Combine(into, name), answering,
                                                    cancellationToken).ConfigureAwait(false);

            if (destination is null)
            {
                kept++;

                continue;
            }

            await Task.Run(() => File.Copy(source, destination, overwrite: true), cancellationToken)
                      .ConfigureAwait(false);

            done++;
        }

        return (done, kept);
    }

    /// <summary>
    /// Where a file copied here lands: its own name where that is free, and otherwise whatever the
    /// question about a taken name decides — or null, for a file left alone.
    /// </summary>
    private async Task<string?> Destination(string source, string destination, Answering answering,
                                            CancellationToken cancellationToken)
    {
        if (!File.Exists(destination))
        {
            return destination;
        }

        FileInfo from = new(source);
        FileInfo there = new(destination);

        CollisionAnswer answer = answering.ForTheRest ?? CollisionAnswer.Skip;

        if (answering.ForTheRest is null && OnCollision is not null)
        {
            CollisionChoice chosen = await OnCollision(
                new Collision(destination, from.Length, from.LastWriteTimeUtc, there.Length, there.LastWriteTimeUtc),
                cancellationToken).ConfigureAwait(false);

            answer = chosen.Answer;

            if (chosen.ForTheRest)
            {
                answering.ForTheRest = chosen.Answer;
            }
        }

        return answer switch
        {
            CollisionAnswer.Skip => null,
            CollisionAnswer.TakeNewer when from.LastWriteTimeUtc <= there.LastWriteTimeUtc => null,
            CollisionAnswer.Rename => Free(destination),
            _ => destination,
        };
    }

    /// <summary>An answer that stands for the rest of one copy, however deep the tree it walks.</summary>
    private sealed class Answering
    {
        public CollisionAnswer? ForTheRest { get; set; }
    }

    /// <summary>The first "name (2).ext" beside a taken name that is not taken itself.</summary>
    private static string Free(string taken)
    {
        string directory = Path.GetDirectoryName(taken) ?? string.Empty;
        string stem = Path.GetFileNameWithoutExtension(taken);
        string extension = Path.GetExtension(taken);

        for (int copy = 2; ; copy++)
        {
            string candidate = Path.Combine(directory,
                                            $"{stem} ({copy.ToString(CultureInfo.InvariantCulture)}){extension}");

            if (!File.Exists(candidate) && !Directory.Exists(candidate))
            {
                return candidate;
            }
        }
    }

    /// <summary>Renames the one entry selected, to a name asked for.</summary>
    public async Task RenameAsync(CancellationToken cancellationToken = default)
    {
        DirectoryPane pane = Active;

        if (pane.Selected is not [FileItem item]
            || AskingForName(new NameQuestion($"Rename {item.Name} to:", item.Name)) is not { } name
            || name.Trim() is not { Length: > 0 } wanted
            || string.Equals(wanted, item.Name, StringComparison.Ordinal))
        {
            return;
        }

        await Doing(pane, $"rename {item.Name}",
                    () => pane.Side.RenameAsync(pane.Side.Into(pane.Path, item.Name),
                                                pane.Side.Into(pane.Path, wanted), cancellationToken),
                    $"Renamed {item.Name} to {wanted}.").ConfigureAwait(false);
    }

    /// <summary>Makes a directory in the pane with the keyboard, under a name asked for.</summary>
    public async Task CreateDirectoryAsync(CancellationToken cancellationToken = default)
    {
        DirectoryPane pane = Active;

        if (pane.Path.Length == 0
            || AskingForName(new NameQuestion($"New directory in {pane.Path}:", string.Empty)) is not { } name
            || name.Trim() is not { Length: > 0 } wanted)
        {
            return;
        }

        await Doing(pane, $"make {wanted}",
                    () => pane.Side.CreateDirectoryAsync(pane.Side.Into(pane.Path, wanted), cancellationToken),
                    $"Made {wanted}.").ConfigureAwait(false);
    }

    /// <summary>
    /// Deletes the selection, having said how many entries and whether any is a directory.
    ///
    /// <para>One at a time, and the first failure stops it: a delete that pressed on past a refusal
    /// would leave the user working out which half of their selection is still there.</para>
    /// </summary>
    public async Task DeleteAsync(CancellationToken cancellationToken = default)
    {
        DirectoryPane pane = Active;

        if (pane.Selected is not { Count: > 0 } picked
            || !AskingToDelete(new DeleteQuestion(picked, pane.Path)))
        {
            return;
        }

        int gone = 0;

        foreach (FileItem item in picked)
        {
            try
            {
                await pane.Side.DeleteAsync(item, pane.Side.Into(pane.Path, item.Name), cancellationToken)
                          .ConfigureAwait(false);

                gone++;
            }
            catch (Exception failed) when (failed is not OperationCanceledException)
            {
                Tell(pane, $"Deleted {gone} of {Entries(picked.Count)}; {item.Name} could not be: "
                           + Sentence(failed), refresh: true);

                return;
            }
        }

        Tell(pane, $"Deleted {Entries(gone)}.", refresh: true);
    }

    /// <summary>Sets the mode of the one entry selected, to a mode asked for.</summary>
    public async Task ChangeModeAsync(CancellationToken cancellationToken = default)
    {
        DirectoryPane pane = Active;

        if (pane.Selected is not [FileItem item])
        {
            return;
        }

        bool local = pane.Side is LocalFiles;

        if (AskingForMode(new ModeQuestion(item, Octal(item.Permissions), local)) is not { } mode)
        {
            return;
        }

        await Doing(pane, $"change {item.Name}",
                    () => pane.Side.ChangeModeAsync(pane.Side.Into(pane.Path, item.Name), mode, cancellationToken),
                    $"{item.Name} is now {Convert.ToString(mode, 8)}.").ConfigureAwait(false);
    }

    /// <summary>
    /// The three octal digits of a mode string like <c>-rwxr-x---</c>, or empty where the string
    /// is not one — which this computer's two-letter spelling is not.
    /// </summary>
    public static string Octal(string permissions)
    {
        ArgumentNullException.ThrowIfNull(permissions);

        if (permissions.Length != 10)
        {
            return string.Empty;
        }

        int mode = 0;

        for (int bit = 0; bit < 9; bit++)
        {
            mode = (mode << 1) | (permissions[bit + 1] == '-' ? 0 : 1);
        }

        return Convert.ToString(mode, 8).PadLeft(3, '0');
    }

    /// <summary>A count of entries with its noun.</summary>
    internal static string Entries(int how) =>
        how.ToString("N0", CultureInfo.InvariantCulture) + (how == 1 ? " entry" : " entries");

    /// <summary>Runs one operation on a pane, and says what came of it there.</summary>
    private async Task Doing(DirectoryPane pane, string what, Func<Task> operation, string done)
    {
        try
        {
            await operation().ConfigureAwait(false);
        }
        catch (Exception failed) when (failed is not OperationCanceledException)
        {
            Tell(pane, $"Could not {what}: {Sentence(failed)}", refresh: false);

            return;
        }

        Tell(pane, done, refresh: true);
    }

    /// <summary>Says something in a pane, on the pane's thread, and lists it again where it changed.</summary>
    private void Tell(DirectoryPane pane, string note, bool refresh) =>
        _post(() =>
        {
            pane.Say(note);

            if (refresh)
            {
                _ = pane.Refresh();
            }
        });

    /// <summary>What a finished copy says: how many files landed, and what did not and why.</summary>
    private static string Copied(IReadOnlyList<TransferEntry> entries, List<string> left)
    {
        int done = entries.Count(entry => entry.State == TransferState.Done);
        TransferEntry[] failed = [.. entries.Where(entry => entry.State == TransferState.Failed)];
        int skipped = entries.Count(entry => entry.State == TransferState.Skipped) + left.Count;

        string said = $"Copied {done.ToString("N0", CultureInfo.InvariantCulture)} "
                      + (done == 1 ? "file" : "files");

        if (skipped > 0)
        {
            said += $", {skipped.ToString(CultureInfo.InvariantCulture)} left alone";
        }

        if (failed.Length > 0)
        {
            said += $"; {failed.Length.ToString(CultureInfo.InvariantCulture)} failed, first "
                    + $"{Path.GetFileName(failed[0].To)}: {failed[0].Why}";
        }

        return said + ".";
    }

    /// <summary>Why an operation failed, in the words a user can act on.</summary>
    private static string Sentence(Exception failed) => failed switch
    {
        UnauthorizedAccessException => "permission denied",
        DirectoryNotFoundException or FileNotFoundException => "it no longer exists",
        _ => failed.Message,
    };
}
