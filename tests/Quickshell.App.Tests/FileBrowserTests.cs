using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using Quickshell.App;
using Quickshell.Transport;
using Xunit;

namespace Quickshell.App.Tests;

/// <summary>
/// QS60's browser, the half of it that lists and moves: a pane on each side, fed as it arrives.
///
/// <para><b>The falsification is the first test and it is about time, not content.</b> <em>Falsified
/// when listing fifty thousand entries blocks the pane until it completes.</em> So the listing is
/// held open part-way through by a side that stops talking, and the pane has to have something on
/// screen while it waits — which a pane that gathered the listing first could never have.</para>
///
/// <para><b>The pane's own thread is this test's</b>, and its work arrives in a queue drained by
/// hand: that is what a dispatcher is, and it is how a test can say exactly what the window would
/// have drawn at each moment.</para>
/// </summary>
public sealed class FileBrowserTests
{
    private static CancellationToken Stop => TestContext.Current.CancellationToken;

    /// <summary>
    /// Fifty thousand entries, stalled after the first hundred: the first screen is up during the
    /// stall, and the whole listing is there once it ends, without any one step holding the pane.
    /// </summary>
    [Fact]
    public async Task FiftyThousandEntriesShowTheirFirstScreenWhileTheRestAreStillComing()
    {
        TaskCompletionSource resume = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Stalling side = new(50_000, stallAfter: 100, resume.Task);
        PanePump pump = new();
        DirectoryPane pane = new(side, pump.Post);

        Task listing = pane.Start();

        // The side has sent a hundred and gone quiet. What the pane shows now is what a user would
        // be looking at while the rest is on its way.
        await side.Stalled.WaitAsync(TimeSpan.FromSeconds(10), Stop);
        await pump.DrainUntil(() => pane.Items.Count == 100, TimeSpan.FromSeconds(5));

        int during = pane.Items.Count;
        bool stillLoading = pane.Loading;

        resume.SetResult();

        await listing.WaitAsync(TimeSpan.FromSeconds(60), Stop);
        await pump.DrainUntil(() => !pane.Loading, TimeSpan.FromSeconds(10));

        Assert.Equal(100, during);
        Assert.True(stillLoading, "the pane said it had finished while the side was still sending");
        Assert.Equal(50_000, pane.Items.Count);
        Assert.Equal("50,000 entries", pane.Status);

        // No step on the pane's thread held it for long: the biggest is the last re-sort of the
        // whole listing. Generous for a loaded runner, and nowhere near "until it completes".
        Assert.True(pump.Longest < TimeSpan.FromMilliseconds(500),
                    $"one step held the pane's thread for {pump.Longest.TotalMilliseconds:F0} ms");
    }

    /// <summary>
    /// The listing is read on another thread than the one that asked for it.
    ///
    /// <para>Proved by holding the asking thread still: the side blocks inside its first read until
    /// released, and the thread that started the listing waits to hear that the read began. A pane
    /// that read where it was asked would never return from <see cref="DirectoryPane.Start"/>, and a
    /// window whose thread that was would stop repainting until the disk answered.</para>
    /// </summary>
    [Fact]
    public async Task TheListingIsReadOffTheThreadThatAskedForIt()
    {
        using ManualResetEventSlim entered = new();
        using ManualResetEventSlim release = new();

        Blocking side = new(entered, release);
        PanePump pump = new();
        DirectoryPane pane = new(side, pump.Post);

        int asking = Environment.CurrentManagedThreadId;

        Task listing = pane.Start();

        // Waited for without yielding, so the thread that asked is busy here for the whole read.
        bool began = entered.Wait(TimeSpan.FromSeconds(10), Stop);
        int readOn = side.ReadOn;

        release.Set();

        await listing.WaitAsync(TimeSpan.FromSeconds(10), Stop);
        await pump.DrainUntil(() => !pane.Loading, TimeSpan.FromSeconds(5));

        Assert.True(began, "the read never began while the asking thread was held");
        Assert.NotEqual(asking, readOn);
        Assert.Single(pane.Items);
    }

    /// <summary>
    /// Going somewhere else abandons the listing in flight, and nothing it sends afterwards lands in
    /// the directory now on screen.
    /// </summary>
    [Fact]
    public async Task GoingElsewhereDropsWhatTheAbandonedListingSendsLater()
    {
        TaskCompletionSource never = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Tree side = new(new Dictionary<string, FileItem[]>
        {
            ["/slow"] = [.. Enumerable.Range(0, 5).Select(n => Plain($"slow{n}"))],
            ["/fast"] = [Plain("fast")],
        })
        {
            Holding = "/slow",
            Hold = never.Task,
        };

        PanePump pump = new();
        DirectoryPane pane = new(side, pump.Post);

        _ = pane.Go("/slow");

        await pump.DrainUntil(() => pane.Items.Count > 0, TimeSpan.FromSeconds(5));

        Task fast = pane.Go("/fast");

        await fast.WaitAsync(TimeSpan.FromSeconds(10), Stop);
        await pump.DrainUntil(() => !pane.Loading, TimeSpan.FromSeconds(5));

        never.SetResult();

        // Whatever the abandoned listing still had queued is let through the pump, and must not land.
        await Task.Delay(200, Stop);
        pump.Drain();

        Assert.Equal("/fast", pane.Path);
        Assert.Equal(["fast"], pane.Items.Select(item => item.Name));
    }

    /// <summary>Back and forward walk the history, and up goes to the directory above.</summary>
    [Fact]
    public async Task BackForwardAndUpWalkWhereThePaneHasBeen()
    {
        Tree side = new(new Dictionary<string, FileItem[]>
        {
            ["/"] = [Folder("home")],
            ["/home"] = [Folder("probe")],
            ["/home/probe"] = [Plain("notes")],
        })
        {
            HomeIs = "/",
        };

        PanePump pump = new();
        DirectoryPane pane = new(side, pump.Post);

        await Settled(pane, pump, pane.Start());
        await Settled(pane, pump, pane.Open(pane.Items.Single()));
        await Settled(pane, pump, pane.Open(pane.Items.Single()));

        string deepest = pane.Path;

        await Settled(pane, pump, pane.Back());

        string afterBack = pane.Path;
        bool canForward = pane.CanGoForward;

        await Settled(pane, pump, pane.Forward());

        string afterForward = pane.Path;

        await Settled(pane, pump, pane.Up());

        Assert.Equal("/home/probe", deepest);
        Assert.Equal("/home", afterBack);
        Assert.True(canForward, "going back left nothing to go forward to");
        Assert.Equal("/home/probe", afterForward);
        Assert.Equal("/home", pane.Path);
        Assert.True(pane.CanGoBack);
    }

    /// <summary>
    /// A column orders the listing and the same column again reverses it, with directories kept
    /// above files either way.
    /// </summary>
    [Fact]
    public async Task AColumnOrdersTheListingAndDirectoriesStayOnTop()
    {
        Tree side = new(new Dictionary<string, FileItem[]>
        {
            ["/"] =
            [
                Plain("b", length: 10), Plain("a", length: 30), Folder("z"), Plain("c", length: 20),
            ],
        })
        {
            HomeIs = "/",
        };

        PanePump pump = new();
        DirectoryPane pane = new(side, pump.Post);

        await Settled(pane, pump, pane.Start());

        string[] byName = Names(pane);

        pane.SortOn(SortBy.Size);

        string[] bySize = Names(pane);

        pane.SortOn(SortBy.Size);

        string[] bySizeDown = Names(pane);

        Assert.Equal(["z", "a", "b", "c"], byName);
        Assert.Equal(["z", "b", "c", "a"], bySize);
        Assert.Equal(["z", "a", "c", "b"], bySizeDown);
        Assert.True(pane.Descending);
    }

    /// <summary>Hidden entries are counted and left out until asked for, then shown.</summary>
    [Fact]
    public async Task HiddenEntriesAreCountedAndShownOnlyWhenAskedFor()
    {
        Tree side = new(new Dictionary<string, FileItem[]>
        {
            ["/"] = [Plain("visible"), Plain(".ssh", hidden: true), Plain(".profile", hidden: true)],
        })
        {
            HomeIs = "/",
        };

        PanePump pump = new();
        DirectoryPane pane = new(side, pump.Post);

        await Settled(pane, pump, pane.Start());

        string[] shown = Names(pane);
        string said = pane.Status;

        pane.ShowHidden = true;

        Assert.Equal(["visible"], shown);
        Assert.Equal("1 entry, 2 hidden", said);
        Assert.Equal([".profile", ".ssh", "visible"], Names(pane));
        Assert.Equal("3 entries", pane.Status);
    }

    /// <summary>A directory that cannot be listed says which one and why, in words a user can act on.</summary>
    [Fact]
    public async Task ADirectoryThatCannotBeListedSaysWhereAndWhy()
    {
        Tree side = new(new Dictionary<string, FileItem[]>())
        {
            HomeIs = "/root",
            Refusing = new UnauthorizedAccessException("Access to the path '/root' is denied."),
        };

        PanePump pump = new();
        DirectoryPane pane = new(side, pump.Post);

        await Settled(pane, pump, pane.Start());

        Assert.Equal("/root could not be listed: permission denied", pane.Status);
        Assert.Empty(pane.Items);
    }

    /// <summary>
    /// This machine's side, on a real directory: hidden by attribute and by a leading dot, a
    /// directory told from a file, and the way up.
    /// </summary>
    [Fact]
    public async Task ThisComputersSideListsARealDirectory()
    {
        string root = Directory.CreateTempSubdirectory("qs60-").FullName;

        try
        {
            Directory.CreateDirectory(Path.Combine(root, "folder"));
            await File.WriteAllTextAsync(Path.Combine(root, "plain.txt"), "hello", Stop);
            await File.WriteAllTextAsync(Path.Combine(root, ".dotfile"), "x", Stop);

            string marked = Path.Combine(root, "marked.txt");

            await File.WriteAllTextAsync(marked, "x", Stop);
            File.SetAttributes(marked, FileAttributes.Hidden);

            LocalFiles side = new(root);
            List<FileItem> listed = [];

            await foreach (FileItem item in side.ListAsync(root, Stop))
            {
                listed.Add(item);
            }

            Dictionary<string, FileItem> named = listed.ToDictionary(item => item.Name);

            Assert.Equal(4, listed.Count);
            Assert.True(named["folder"].IsDirectory);
            Assert.Equal(5, named["plain.txt"].Length);
            Assert.False(named["plain.txt"].IsHidden);
            Assert.True(named[".dotfile"].IsHidden);
            Assert.True(named["marked.txt"].IsHidden);
            Assert.Equal(Path.GetDirectoryName(root), side.Parent(root));
            Assert.Equal(Path.Combine(root, "folder"), side.Into(root, "folder"));
        }
        finally
        {
            foreach (string file in Directory.EnumerateFiles(root))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>The host's side joins with a slash and goes up to the root and no further.</summary>
    [Theory]
    [InlineData("/home/probe", "/home")]
    [InlineData("/home/probe/", "/home")]
    [InlineData("/home", "/")]
    [InlineData("/", null)]
    public void TheHostsSideGoesUpToTheRootAndNoFurther(string path, string? parent) =>
        Assert.Equal(parent, new RemoteFiles(new NoChannel(), "host").Parent(path));

    /// <summary>
    /// Against a real server: fifty thousand entries in a directory of the fixture, listed over the
    /// session's own file channel, with the first screen up long before the last entry arrives.
    ///
    /// <para><b>A real endpoint, because this is what the claim is about.</b> Whether SFTP hands a
    /// long directory over in pieces or all at once is a fact about a server and a library, and a
    /// side that stalls on command in a test is the argument, not the evidence.</para>
    /// </summary>
    [Fact]
    public async Task FiftyThousandEntriesOnARealServerArriveInPieces()
    {
        SshFixture.SkipWithoutIt();

        string directory = "/tmp/qs60-" + Guid.NewGuid().ToString("N");

        Assert.SkipUnless(SshFixture.Docker($"mkdir -p {directory} && cd {directory} && seq 1 50000 | xargs touch"),
                          "the fixture's container could not be asked to make the directory");

        try
        {
            await using SshNetTransport session = await SshFixture.ConnectAsync(Stop);

            await using IFileTransferChannel files = await session.OpenFileTransferAsync(Stop);

            PanePump pump = new();
            DirectoryPane pane = new(new RemoteFiles(files, "qs-sshd-target"), pump.Post);

            Stopwatch clock = Stopwatch.StartNew();
            Task listing = pane.Go(directory);

            await pump.DrainUntil(() => pane.Items.Count > 0, TimeSpan.FromSeconds(30));

            TimeSpan first = clock.Elapsed;
            int shownFirst = pane.Items.Count;

            await listing.WaitAsync(TimeSpan.FromSeconds(120), Stop);
            await pump.DrainUntil(() => !pane.Loading, TimeSpan.FromSeconds(30));

            TimeSpan all = clock.Elapsed;

            TestContext.Current.TestOutputHelper?.WriteLine(
                $"first {shownFirst} entries after {first.TotalMilliseconds:F0} ms; all 50,000 after "
                + $"{all.TotalMilliseconds:F0} ms; longest step on the pane's thread "
                + $"{pump.Longest.TotalMilliseconds:F0} ms");

            Assert.Null(pane.Failed);
            Assert.Equal(50_000, pane.Items.Count);
            Assert.True(shownFirst < 50_000,
                        $"the first screen held all {shownFirst} entries, so nothing was shown before the listing ended");
            Assert.True(first < all / 2,
                        $"the first entries were shown after {first.TotalMilliseconds:F0} ms of a {all.TotalMilliseconds:F0} ms listing");
        }
        finally
        {
            SshFixture.Docker($"rm -rf {directory}");
        }
    }

    /// <summary>
    /// The palette reaches the browser, a second ask brings back the same one, and a tab with no
    /// remote side gets a browser that says so instead of a blank half.
    /// </summary>
    [Fact]
    public void ThePaletteOpensOneBrowserThatSaysWhyItHasNoRemoteSide()
    {
        (int shown, bool same, bool noRemote, string local) = OnStaThread(() =>
        {
            int shows = 0;

            MainWindow window = new() { ShowsBrowser = _ => shows++ };

            window.Add(TerminalTab.Open(Settings.Default, Shared, "cmd.exe"));

            // Single, so an entry missing from the palette fails here and by name.
            window.Actions.Single(action => action.Name == "Browse files").Run();

            FileBrowser first = window.BrowseFiles();
            FileBrowser again = window.BrowseFiles();

            return (shows, ReferenceEquals(first, again), first.Remote is null, first.Local.Side.Title);
        });

        Assert.Equal(1, shown);
        Assert.True(same, "a second ask opened a second browser");
        Assert.True(noRemote, "a local tab's browser claimed a remote side");
        Assert.Equal("This computer", local);
    }

    /// <summary>The one device, atlas and render loop the tab in the window test would draw with.</summary>
    private static readonly TerminalShare Shared = new();

    /// <summary>Runs something on an STA thread, and shuts the dispatcher it built down after.</summary>
    private static T OnStaThread<T>(Func<T> work)
    {
        T result = default!;
        Exception? failed = null;

        Thread thread = new(() =>
        {
            try
            {
                result = work();
            }
            catch (Exception error)
            {
                failed = error;
            }
            finally
            {
                System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();

        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "the STA thread never finished");

        if (failed is not null)
        {
            throw new InvalidOperationException("the window could not be built", failed);
        }

        return result;
    }

    // ---- The pane's thread, and the sides it lists ----

    /// <summary>Waits for a navigation to finish and for everything it posted to be drawn.</summary>
    private static async Task Settled(DirectoryPane pane, PanePump pump, Task navigating)
    {
        await navigating.WaitAsync(TimeSpan.FromSeconds(10), Stop);
        await pump.DrainUntil(() => !pane.Loading, TimeSpan.FromSeconds(5));
    }

    private static string[] Names(DirectoryPane pane) => [.. pane.Items.Select(item => item.Name)];

    private static FileItem Plain(string name, long length = 1, bool hidden = false) =>
        new(name, length, false, DateTimeOffset.UnixEpoch, "-rw-r--r--", hidden);

    private static FileItem Folder(string name) =>
        new(name, 0, true, DateTimeOffset.UnixEpoch, "drwxr-xr-x", false);

    /// <summary>
    /// A side that lists and does nothing else, which is every fake here: the operations are the
    /// real sides' and are tested against a real directory and a real server.
    /// </summary>
    private abstract class ListingSide : IFileSide
    {
        public abstract string Title { get; }

        public abstract string Home { get; }

        public abstract IAsyncEnumerable<FileItem> ListAsync(string path, CancellationToken cancellationToken = default);

        public abstract string Into(string directory, string name);

        public abstract string? Parent(string path);

        public Task RenameAsync(string from, string to, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task DeleteAsync(FileItem entry, string path, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task ChangeModeAsync(string path, int mode, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    /// <summary>A side that sends so many entries, goes quiet after some, and resumes on cue.</summary>
    private sealed class Stalling(int count, int stallAfter, Task resume) : ListingSide
    {
        private readonly TaskCompletionSource _stalled = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Stalled => _stalled.Task;

        public int ReadOn { get; private set; }

        public override string Title => "stalling";

        public override string Home => "/big";

        public override async IAsyncEnumerable<FileItem> ListAsync(
            string path, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ReadOn = Environment.CurrentManagedThreadId;

            for (int entry = 0; entry < count; entry++)
            {
                if (entry == stallAfter)
                {
                    _stalled.TrySetResult();

                    await resume.WaitAsync(cancellationToken).ConfigureAwait(false);
                }

                yield return new FileItem($"entry{entry:D6}", entry, false, DateTimeOffset.UnixEpoch,
                                          "-rw-r--r--", false);
            }
        }

        public override string Into(string directory, string name) => directory + "/" + name;

        public override string? Parent(string path) => null;
    }

    /// <summary>A side whose first read blocks its thread until it is released.</summary>
    private sealed class Blocking(ManualResetEventSlim entered, ManualResetEventSlim release) : ListingSide
    {
        public int ReadOn { get; private set; }

        public override string Title => "blocking";

        public override string Home => "/";

        public override async IAsyncEnumerable<FileItem> ListAsync(
            string path, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask.ConfigureAwait(false);

            ReadOn = Environment.CurrentManagedThreadId;
            entered.Set();

            // A disk that takes its time, which is to say a thread held.
            release.Wait(cancellationToken);

            yield return Plain("only");
        }

        public override string Into(string directory, string name) => directory + name;

        public override string? Parent(string path) => null;
    }

    /// <summary>A side over a fixed tree, which can hold one directory's listing open or refuse.</summary>
    private sealed class Tree(Dictionary<string, FileItem[]> directories) : ListingSide
    {
        public string HomeIs { get; init; } = "/";

        public string? Holding { get; init; }

        public Task Hold { get; init; } = Task.CompletedTask;

        public Exception? Refusing { get; init; }

        public override string Title => "tree";

        public override string Home => HomeIs;

        public override async IAsyncEnumerable<FileItem> ListAsync(
            string path, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();

            if (Refusing is { } refused)
            {
                throw refused;
            }

            FileItem[] entries = directories[path];

            for (int entry = 0; entry < entries.Length; entry++)
            {
                if (entry == 1 && path == Holding)
                {
                    // Deliberately not cancellable: an abandoned listing that keeps sending is the
                    // case the pane has to be right about.
                    await Hold.ConfigureAwait(false);
                }

                yield return entries[entry];
            }
        }

        public override string Into(string directory, string name) =>
            directory.EndsWith('/') ? directory + name : directory + "/" + name;

        public override string? Parent(string path) => new RemoteFiles(new NoChannel(), "tree").Parent(path);
    }

    /// <summary>A file channel for the members that never touch one.</summary>
    private sealed class NoChannel : IFileTransferChannel
    {
        public int ProtocolVersion => 3;

        public string WorkingDirectory => "/";

        public IAsyncEnumerable<RemoteEntry> ListAsync(string path, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<Stream> OpenReadAsync(string path, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<Stream> OpenWriteAsync(string path, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<Stream> OpenWriteAtAsync(string path, long at, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask DeleteAsync(string path, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask RenameAsync(string from, string to, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<RemoteEntry> StatAsync(string path, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask CreateDirectoryAsync(string path, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask SymbolicLinkAsync(string target, string link, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask ChangePermissionsAsync(string path, int mode, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask SetLastWriteTimeAsync(string path, DateTimeOffset when,
                                               CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask DownloadAsync(string path, Stream into, IProgress<long>? progress = null,
                                       CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask UploadAsync(Stream from, string path, IProgress<long>? progress = null,
                                     CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
