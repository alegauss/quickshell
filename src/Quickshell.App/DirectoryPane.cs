using System.ComponentModel;
using System.Globalization;
using System.IO;

namespace Quickshell.App;

/// <summary>What a pane's listing is ordered by.</summary>
public enum SortBy
{
    /// <summary>Directories first, then by name without regard to case.</summary>
    Name,

    /// <summary>Largest last, or first descending.</summary>
    Size,

    /// <summary>Oldest last, or first descending.</summary>
    Modified,

    /// <summary>The mode string, which groups what a user may and may not write.</summary>
    Permissions,
}

/// <summary>
/// One half of the file browser: a directory on one side, listed as it arrives. QS60.
///
/// <para><b>The listing is read off the window's thread and shown in pieces, and that is the
/// design's falsification.</b> <em>Falsified when listing fifty thousand entries blocks the pane
/// until it completes.</em> So the entries are pulled on the thread pool, and handed to the pane's
/// own thread in batches that start at one entry and grow: the first screen is on the glass as soon
/// as the first entry exists, and fifty thousand cost a dozen re-sorts rather than fifty thousand.
/// A side that goes quiet mid-listing — a slow link, a server reading a huge directory — has what
/// it already sent shown after a twentieth of a second rather than held until the next entry.</para>
///
/// <para><b>It knows no window.</b> What reaches its thread is whatever <c>post</c> does, which is
/// the dispatcher in the client and a queue a test drains by hand.</para>
/// </summary>
public sealed class DirectoryPane : INotifyPropertyChanged
{
    /// <summary>How long a partial batch may wait for company before it is shown anyway.</summary>
    private static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(50);

    /// <summary>The largest batch, which bounds how long one re-sort holds the pane's thread.</summary>
    private const int LargestBatch = 8192;

    private readonly Action<Action> _post;
    private readonly List<FileItem> _all = [];
    private readonly Stack<string> _back = new();
    private readonly Stack<string> _forward = new();

    private CancellationTokenSource? _listing;
    private int _generation;
    private bool _hidden;

    /// <summary>A pane on one side, whose work reaches its own thread through <paramref name="post"/>.</summary>
    /// <param name="side">Where the listing comes from.</param>
    /// <param name="post">
    /// How work is handed to the pane's thread: the dispatcher's <c>BeginInvoke</c> in a window.
    /// Required, because the listing is read on the thread pool and a pane edited from there would
    /// be a list a window draws while another thread rewrites it.
    /// </param>
    public DirectoryPane(IFileSide side, Action<Action> post)
    {
        ArgumentNullException.ThrowIfNull(side);
        ArgumentNullException.ThrowIfNull(post);

        Side = side;
        _post = post;
    }

    /// <inheritdoc/>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Where this pane's listing comes from.</summary>
    public IFileSide Side { get; }

    /// <summary>The directory on screen, empty before the first is opened.</summary>
    public string Path { get; private set; } = string.Empty;

    /// <summary>What is shown: the listing so far, filtered and in order. Replaced, never edited.</summary>
    public IReadOnlyList<FileItem> Items { get; private set; } = [];

    /// <summary>How many entries have arrived, shown or hidden.</summary>
    public int Arrived => _all.Count;

    /// <summary>Whether entries are still arriving.</summary>
    public bool Loading { get; private set; }

    /// <summary>Why the directory could not be listed, or null.</summary>
    public string? Failed { get; private set; }

    /// <summary>What the listing is ordered by.</summary>
    public SortBy Sort { get; private set; } = SortBy.Name;

    /// <summary>Whether that order is reversed.</summary>
    public bool Descending { get; private set; }

    /// <summary>Whether hidden entries are shown.</summary>
    public bool ShowHidden
    {
        get => _hidden;

        set
        {
            if (_hidden == value)
            {
                return;
            }

            _hidden = value;

            Rebuild();
            Notify();
        }
    }

    /// <summary>Whether there is somewhere to go back to.</summary>
    public bool CanGoBack => _back.Count > 0;

    /// <summary>Whether there is somewhere to go forward to.</summary>
    public bool CanGoForward => _forward.Count > 0;

    /// <summary>Whether this directory has one above it.</summary>
    public bool CanGoUp => Path.Length > 0 && Side.Parent(Path) is not null;

    /// <summary>
    /// The one line under the listing: how many, whether more are coming, or why there are none.
    ///
    /// <para>The failure is the side's own sentence with the path in front of it, because the path
    /// is the half a user can do something about and the reason is the half they need to know
    /// which thing.</para>
    /// </summary>
    public string Status
    {
        get
        {
            if (Failed is { } why)
            {
                return $"{Path} could not be listed: {why}";
            }

            string count = Count(Items.Count, "entry", "entries");
            int hidden = _all.Count - Items.Count;

            string shown = hidden > 0 ? $"{count}, {Count(hidden, "hidden", "hidden")}" : count;

            return Loading ? $"{shown} so far, still listing" : shown;
        }
    }

    /// <summary>The pane's first directory: the side's home.</summary>
    public Task Start() => Load(Side.Home);

    /// <summary>
    /// Goes to a directory, remembering where it was for <see cref="Back"/>.
    /// </summary>
    public Task Go(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (Path.Length > 0 && !string.Equals(path, Path, StringComparison.Ordinal))
        {
            _back.Push(Path);
            _forward.Clear();
        }

        return Load(path);
    }

    /// <summary>Opens an entry: a directory is gone into, and a file is not yet anything.</summary>
    public Task Open(FileItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        return item.IsDirectory ? Go(Side.Into(Path, item.Name)) : Task.CompletedTask;
    }

    /// <summary>Goes to the directory this one sits in.</summary>
    public Task Up() => Side.Parent(Path) is { } parent ? Go(parent) : Task.CompletedTask;

    /// <summary>Goes back to where the last <see cref="Go"/> came from.</summary>
    public Task Back()
    {
        if (!_back.TryPop(out string? was))
        {
            return Task.CompletedTask;
        }

        _forward.Push(Path);

        return Load(was);
    }

    /// <summary>Undoes a <see cref="Back"/>.</summary>
    public Task Forward()
    {
        if (!_forward.TryPop(out string? next))
        {
            return Task.CompletedTask;
        }

        _back.Push(Path);

        return Load(next);
    }

    /// <summary>Lists the same directory again.</summary>
    public Task Refresh() => Path.Length > 0 ? Load(Path) : Start();

    /// <summary>
    /// Orders the listing by a column, and by the same column again reverses it — the gesture every
    /// file list on this platform answers a click on its header with.
    /// </summary>
    public void SortOn(SortBy by)
    {
        Descending = by == Sort && !Descending;
        Sort = by;

        Rebuild();
        Notify();
    }

    /// <summary>
    /// Starts listing a directory, abandoning whatever listing was running.
    /// </summary>
    /// <returns>A task that ends when the listing has, for a caller that wants to wait.</returns>
    private Task Load(string path)
    {
        // Not waited for: the listing being abandoned notices on its own thread, and the pane's
        // thread has a new directory to show.
        if (_listing is { } abandoned)
        {
            _ = abandoned.CancelAsync();
        }

        CancellationTokenSource listing = new();

        _listing = listing;

        int generation = ++_generation;

        _all.Clear();
        Items = [];
        Failed = null;
        Loading = true;
        Path = path;

        Notify();

        return Task.Run(() => Read(path, generation, listing.Token), listing.Token)
                   .ContinueWith(_ => { }, TaskScheduler.Default);
    }

    /// <summary>
    /// Pulls the listing on the thread pool and hands it to the pane in batches.
    ///
    /// <para><b>A batch waits for company for a twentieth of a second and no longer.</b> The side's
    /// next entry is raced against that, so a listing that pauses — which a remote one does, a
    /// server's directory read at a time — shows what it has instead of holding it until the pause
    /// ends. An entry that is already there costs no race and no allocation.</para>
    /// </summary>
    private async Task Read(string path, int generation, CancellationToken cancellationToken)
    {
        Holding held = new();
        int threshold = 1;

        try
        {
            IAsyncEnumerator<FileItem> entries = Side.ListAsync(path, cancellationToken)
                                                     .GetAsyncEnumerator(cancellationToken);

            await using (entries.ConfigureAwait(false))
            {
                while (await Next(entries, held, generation).ConfigureAwait(false))
                {
                    held.Items.Add(entries.Current);

                    // One timer per batch and not one per wait: a remote listing waits between
                    // most of its entries, and fifty thousand timers is a cost the race was meant
                    // to save.
                    held.Deadline ??= Task.Delay(Quiet, cancellationToken);

                    if (held.Items.Count >= threshold)
                    {
                        Publish(generation, held.Take(), done: false);

                        // One, then four, then sixteen: the first screen at once, and a sort per
                        // batch that stays a dozen sorts for fifty thousand entries.
                        threshold = Math.Min(threshold * 4, LargestBatch);
                    }
                }
            }

            Publish(generation, held.Take(), done: true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Somebody went somewhere else. What was being read belongs to a directory nobody is
            // looking at any more.
        }
        catch (Exception failed)
        {
            _post(() =>
            {
                if (generation != _generation)
                {
                    return;
                }

                Failed = Sentence(failed);
                Loading = false;

                Notify();
            });
        }
    }

    /// <summary>
    /// The side's next entry, with what is being held shown first if its batch's deadline passes
    /// before the entry comes. An entry that is already there costs no race and no allocation.
    /// </summary>
    private async ValueTask<bool> Next(IAsyncEnumerator<FileItem> entries, Holding held,
                                       int generation)
    {
        ValueTask<bool> next = entries.MoveNextAsync();

        if (next.IsCompletedSuccessfully)
        {
            return next.Result;
        }

        Task<bool> waiting = next.AsTask();

        if (held.Deadline is { } deadline
            && await Task.WhenAny(waiting, deadline).ConfigureAwait(false) != waiting)
        {
            Publish(generation, held.Take(), done: false);
        }

        return await waiting.ConfigureAwait(false);
    }

    /// <summary>The entries read and not yet shown, and when they are due to be shown regardless.</summary>
    private sealed class Holding
    {
        /// <summary>What has been read since the last batch went.</summary>
        public List<FileItem> Items { get; } = [];

        /// <summary>When this batch goes whether it is full or not; null while it is empty.</summary>
        public Task? Deadline { get; set; }

        /// <summary>Hands over what is held and starts a new batch.</summary>
        public FileItem[] Take()
        {
            FileItem[] taken = [.. Items];

            Items.Clear();
            Deadline = null;

            return taken;
        }
    }

    /// <summary>Hands a batch to the pane's thread, which drops it if the pane has moved on.</summary>
    private void Publish(int generation, FileItem[] batch, bool done)
    {
        _post(() =>
        {
            if (generation != _generation)
            {
                return;
            }

            _all.AddRange(batch);

            if (done)
            {
                Loading = false;
            }

            Rebuild();
            Notify();
        });
    }

    /// <summary>What is shown, from what has arrived: filtered, then ordered, into a new list.</summary>
    private void Rebuild()
    {
        List<FileItem> shown = new(_all.Count);

        foreach (FileItem item in _all)
        {
            if (_hidden || !item.IsHidden)
            {
                shown.Add(item);
            }
        }

        shown.Sort(Comparer());

        Items = shown;
    }

    /// <summary>
    /// The order a column asks for. Directories stay above files in every one of them, reversed or
    /// not, because a person looking for a folder by size or date is still looking for a folder —
    /// so reversing turns the order round inside each group and never the groups themselves.
    /// </summary>
    private Comparison<FileItem> Comparer()
    {
        int sign = Descending ? -1 : 1;

        return Sort switch
        {
            SortBy.Size => Folders((a, b) => a.Length.CompareTo(b.Length), sign),
            SortBy.Modified => Folders((a, b) => a.Modified.CompareTo(b.Modified), sign),
            SortBy.Permissions => Folders((a, b) => string.CompareOrdinal(a.Permissions, b.Permissions), sign),
            _ => Folders((_, _) => 0, sign),
        };

        static Comparison<FileItem> Folders(Comparison<FileItem> within, int sign) => (a, b) =>
        {
            if (a.IsDirectory != b.IsDirectory)
            {
                return a.IsDirectory ? -1 : 1;
            }

            int by = within(a, b);

            return sign * (by != 0 ? by : StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name));
        };
    }

    /// <summary>
    /// Why a directory could not be listed, in the words a user can act on.
    ///
    /// <para>The two a person meets every day get their own sentence, because the framework's
    /// wording for them names an API rather than a situation.</para>
    /// </summary>
    private static string Sentence(Exception failed) => failed switch
    {
        UnauthorizedAccessException => "permission denied",
        DirectoryNotFoundException or FileNotFoundException => "it does not exist",
        _ => failed.Message,
    };

    /// <summary>A count with its noun, which has a plural of its own here.</summary>
    private static string Count(int how, string one, string many) =>
        how.ToString("N0", CultureInfo.InvariantCulture) + " " + (how == 1 ? one : many);

    private void Notify()
    {
        PropertyChangedEventHandler? changed = PropertyChanged;

        if (changed is null)
        {
            return;
        }

        foreach (string property in Changes)
        {
            changed(this, new PropertyChangedEventArgs(property));
        }
    }

    /// <summary>Everything a change can move, told in one go: a pane's state changes together.</summary>
    private static readonly string[] Changes =
    [
        nameof(Path), nameof(Items), nameof(Arrived), nameof(Loading), nameof(Failed),
        nameof(Status), nameof(Sort), nameof(Descending), nameof(ShowHidden),
        nameof(CanGoBack), nameof(CanGoForward), nameof(CanGoUp),
    ];
}
