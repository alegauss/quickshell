using System.Net.Sockets;
using System.Text;
using Quickshell.Transport;
using Xunit;

namespace Quickshell.Transport.Tests;

/// <summary>
/// Comparing two trees, and the one thing a mirror must never do.
///
/// <para>The comparison is tested against a real server, because the reason a sync feature is
/// useless in practice is timestamp resolution — and a fake channel would report whatever
/// resolution the fake was written with.</para>
/// </summary>
public sealed class SyncPlanTests : IDisposable
{
    private const string Host = "127.0.0.1";
    private const int Port = 2222;

    private readonly string _here =
        Path.Combine(Path.GetTempPath(), $"quickshell-sync-{Guid.NewGuid():N}");

    private static CancellationToken Stop => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        if (Directory.Exists(_here))
        {
            Directory.Delete(_here, recursive: true);
        }
    }

    // ---- The falsification ----

    /// <summary>
    /// The line's own falsification: a mirror deletes nothing the user was not shown.
    ///
    /// <para>The sharp case is a file that appears on the destination <em>after</em> the comparison
    /// and before the confirmation. It was never in the list the user approved, so it must survive —
    /// and a mirror that re-walks the destination at deletion time would remove it. That is the
    /// implementation this test exists to refuse.</para>
    /// </summary>
    [Fact]
    public async Task AMirrorDeletesNothingItDidNotShowFirst()
    {
        SkipWithoutFixture();

        await using SshNetTransport session = new();

        await session.ConnectAsync(SshEndpoint.For(Host, "probe", Port), [Key()], Trusting, Stop);

        await using IFileTransferChannel files = await session.OpenFileTransferAsync(Stop);

        string there = $"/tmp/qs-{Guid.NewGuid():N}";

        await files.CreateDirectoryAsync(there, Stop);

        await Write("keep.txt", "kept");

        try
        {
            // The destination has one file the source does not.
            await Put(files, $"{there}/stale.txt", "stale");

            SyncPlan plan = await SyncPlan.CompareAsync(files, Mine(), there,
                                                        SyncDirection.Mirror,
                                                        cancellationToken: Stop);

            Assert.Equal(["stale.txt"], plan.Deletions);

            // Somebody else writes to the destination after the user saw the list.
            await Put(files, $"{there}/arrived-later.txt", "not in the list");

            List<string> shown = [];

            await plan.ApplyAsync(files, going =>
            {
                shown.AddRange(going);

                return ValueTask.FromResult(true);
            }, Stop);

            // What was shown is what went.
            Assert.Equal(["stale.txt"], shown);

            List<string> left = await Names(files, there);

            Assert.Contains("keep.txt", left);
            Assert.DoesNotContain("stale.txt", left);

            // And the file that arrived after the list was made is still there.
            Assert.Contains("arrived-later.txt", left);
        }
        finally
        {
            await Remove(files, there);
        }
    }

    /// <summary>
    /// Nothing is deleted where there is nobody to confirm it, which is the safe half of a mirror.
    /// </summary>
    [Fact]
    public async Task AMirrorWithNobodyToAskDeletesNothing()
    {
        SkipWithoutFixture();

        await using SshNetTransport session = new();

        await session.ConnectAsync(SshEndpoint.For(Host, "probe", Port), [Key()], Trusting, Stop);

        await using IFileTransferChannel files = await session.OpenFileTransferAsync(Stop);

        string there = $"/tmp/qs-{Guid.NewGuid():N}";

        await files.CreateDirectoryAsync(there, Stop);

        await Write("keep.txt", "kept");

        try
        {
            await Put(files, $"{there}/stale.txt", "stale");

            SyncPlan plan = await SyncPlan.CompareAsync(files, Mine(), there, SyncDirection.Mirror,
                                                        cancellationToken: Stop);

            await plan.ApplyAsync(files, confirm: null, Stop);

            List<string> left = await Names(files, there);

            // Copied, and nothing removed.
            Assert.Contains("keep.txt", left);
            Assert.Contains("stale.txt", left);
        }
        finally
        {
            await Remove(files, there);
        }
    }

    /// <summary>And a refusal is a refusal: the copies still happen, the deletions do not.</summary>
    [Fact]
    public async Task DecliningTheDeletionsStillCopies()
    {
        SkipWithoutFixture();

        await using SshNetTransport session = new();

        await session.ConnectAsync(SshEndpoint.For(Host, "probe", Port), [Key()], Trusting, Stop);

        await using IFileTransferChannel files = await session.OpenFileTransferAsync(Stop);

        string there = $"/tmp/qs-{Guid.NewGuid():N}";

        await files.CreateDirectoryAsync(there, Stop);

        await Write("new.txt", "brought over");

        try
        {
            await Put(files, $"{there}/stale.txt", "stale");

            SyncPlan plan = await SyncPlan.CompareAsync(files, Mine(), there, SyncDirection.Mirror,
                                                        cancellationToken: Stop);

            await plan.ApplyAsync(files, _ => ValueTask.FromResult(false), Stop);

            List<string> left = await Names(files, there);

            Assert.Contains("new.txt", left);
            Assert.Contains("stale.txt", left);
        }
        finally
        {
            await Remove(files, there);
        }
    }

    /// <summary>Upload and download never delete, whatever the destination has extra.</summary>
    [Theory]
    [InlineData(SyncDirection.Upload)]
    [InlineData(SyncDirection.Download)]
    public async Task OnlyAMirrorEverDeletes(SyncDirection direction)
    {
        SkipWithoutFixture();

        await using SshNetTransport session = new();

        await session.ConnectAsync(SshEndpoint.For(Host, "probe", Port), [Key()], Trusting, Stop);

        await using IFileTransferChannel files = await session.OpenFileTransferAsync(Stop);

        string there = $"/tmp/qs-{Guid.NewGuid():N}";

        await files.CreateDirectoryAsync(there, Stop);

        await Write("mine.txt", "local");

        try
        {
            await Put(files, $"{there}/theirs.txt", "remote");

            SyncPlan plan = await SyncPlan.CompareAsync(files, Mine(), there, direction,
                                                        cancellationToken: Stop);

            Assert.NotEmpty(plan.OnlyOnDestination);
            Assert.Empty(plan.Deletions);
        }
        finally
        {
            await Remove(files, there);
        }
    }

    // ---- The comparison ----

    /// <summary>
    /// A file that has just been copied compares as unchanged.
    ///
    /// <para>This is the whole feature. NTFS keeps a hundred nanoseconds and SFTP version three
    /// keeps whole seconds, so without tolerance the file this client wrote a moment ago reads back
    /// as changed — and a sync that copies everything every time is one nobody runs twice.</para>
    /// </summary>
    [Fact]
    public async Task AFileJustCopiedComparesAsUnchanged()
    {
        SkipWithoutFixture();

        await using SshNetTransport session = new();

        await session.ConnectAsync(SshEndpoint.For(Host, "probe", Port), [Key()], Trusting, Stop);

        await using IFileTransferChannel files = await session.OpenFileTransferAsync(Stop);

        string there = $"/tmp/qs-{Guid.NewGuid():N}";

        await files.CreateDirectoryAsync(there, Stop);

        // A time with fractional seconds in it, which is what NTFS actually stores.
        await Write("one.txt", "content");

        File.SetLastWriteTimeUtc(Path.Combine(Mine(), "one.txt"),
                                 new DateTime(2024, 5, 6, 7, 8, 9, 987, DateTimeKind.Utc));

        try
        {
            SyncPlan first = await SyncPlan.CompareAsync(files, Mine(), there,
                                                         cancellationToken: Stop);

            Assert.Single(first.New);

            await first.ApplyAsync(files, cancellationToken: Stop);

            // The second run has nothing to do, which is the property that makes this usable.
            SyncPlan second = await SyncPlan.CompareAsync(files, Mine(), there,
                                                          cancellationToken: Stop);

            Assert.True(second.IsEmpty, $"the second run still wanted to do something: {second}");
            Assert.Empty(second.Changed);
        }
        finally
        {
            await Remove(files, there);
        }
    }

    /// <summary>A differing size is settled without reading either file.</summary>
    [Fact]
    public async Task ADifferingSizeIsChangedAndSaysSo()
    {
        SkipWithoutFixture();

        await using SshNetTransport session = new();

        await session.ConnectAsync(SshEndpoint.For(Host, "probe", Port), [Key()], Trusting, Stop);

        await using IFileTransferChannel files = await session.OpenFileTransferAsync(Stop);

        string there = $"/tmp/qs-{Guid.NewGuid():N}";

        await files.CreateDirectoryAsync(there, Stop);

        await Write("one.txt", "a longer piece of content");

        try
        {
            await Put(files, $"{there}/one.txt", "short");

            SyncPlan plan = await SyncPlan.CompareAsync(files, Mine(), there,
                                                        cancellationToken: Stop);

            SyncEntry entry = Assert.Single(plan.Changed);

            Assert.Contains("the sizes differ", entry.Why, StringComparison.Ordinal);
        }
        finally
        {
            await Remove(files, there);
        }
    }

    /// <summary>
    /// Contents can be compared where a user asks for it, and it catches what size and time cannot.
    ///
    /// <para>Two files of the same length and the same timestamp and different bytes: by size and
    /// time they agree, and only reading them says otherwise. It is opt-in because it costs the
    /// whole transfer over again.</para>
    /// </summary>
    [Fact]
    public async Task ComparingContentsCatchesWhatSizeAndTimeCannot()
    {
        SkipWithoutFixture();

        await using SshNetTransport session = new();

        await session.ConnectAsync(SshEndpoint.For(Host, "probe", Port), [Key()], Trusting, Stop);

        await using IFileTransferChannel files = await session.OpenFileTransferAsync(Stop);

        string there = $"/tmp/qs-{Guid.NewGuid():N}";

        await files.CreateDirectoryAsync(there, Stop);

        await Write("one.txt", "aaaaa");

        DateTimeOffset when = new(2024, 5, 6, 7, 8, 9, TimeSpan.Zero);

        File.SetLastWriteTimeUtc(Path.Combine(Mine(), "one.txt"), when.UtcDateTime);

        try
        {
            // Same length, same time, different bytes.
            await Put(files, $"{there}/one.txt", "bbbbb");

            await files.SetLastWriteTimeAsync($"{there}/one.txt", when, Stop);

            SyncPlan quick = await SyncPlan.CompareAsync(files, Mine(), there,
                                                         cancellationToken: Stop);

            Assert.True(quick.IsEmpty, "size and time should not have told these apart");

            SyncPlan thorough = await SyncPlan.CompareAsync(files, Mine(), there, contents: true,
                                                            cancellationToken: Stop);

            SyncEntry entry = Assert.Single(thorough.Changed);

            Assert.Contains("contents differ", entry.Why, StringComparison.Ordinal);
            Assert.True(thorough.ComparedContents);
        }
        finally
        {
            await Remove(files, there);
        }
    }

    /// <summary>A nested tree compares and copies whole.</summary>
    [Fact]
    public async Task ANestedTreeSynchronisesWhole()
    {
        SkipWithoutFixture();

        await using SshNetTransport session = new();

        await session.ConnectAsync(SshEndpoint.For(Host, "probe", Port), [Key()], Trusting, Stop);

        await using IFileTransferChannel files = await session.OpenFileTransferAsync(Stop);

        string there = $"/tmp/qs-{Guid.NewGuid():N}";

        await files.CreateDirectoryAsync(there, Stop);

        await Write("top.txt", "top");
        await Write(Path.Combine("one", "middle.txt"), "middle");
        await Write(Path.Combine("one", "two", "deep.txt"), "deep");

        try
        {
            SyncPlan plan = await SyncPlan.CompareAsync(files, Mine(), there,
                                                        cancellationToken: Stop);

            Assert.Equal(3, plan.New.Count);
            Assert.Contains(plan.New, entry => entry.Path == "one/two/deep.txt");

            await plan.ApplyAsync(files, cancellationToken: Stop);

            Assert.Equal("deep", await Read(files, $"{there}/one/two/deep.txt"));

            SyncPlan again = await SyncPlan.CompareAsync(files, Mine(), there,
                                                         cancellationToken: Stop);

            Assert.True(again.IsEmpty, $"a second run wanted: {again}");
        }
        finally
        {
            await Remove(files, there);
        }
    }

    // ---- Filters ----

    /// <summary>The patterns behave the way the same patterns behave everywhere else.</summary>
    [Theory]
    [InlineData("*.tmp", "notes.tmp", true)]
    [InlineData("*.tmp", "one/notes.tmp", true)]
    [InlineData("*.tmp", "notes.txt", false)]
    [InlineData("build/", "build/thing.o", true)]
    [InlineData("build/", "src/build/thing.o", true)]
    [InlineData("build/", "rebuild/thing.o", false)]
    [InlineData("src/*.o", "src/a.o", true)]
    [InlineData("src/*.o", "src/deep/a.o", false)]
    [InlineData("src/**/*.o", "src/deep/a.o", true)]
    [InlineData("node_modules", "one/node_modules/pkg/index.js", true)]
    [InlineData("?.txt", "a.txt", true)]
    [InlineData("?.txt", "ab.txt", false)]
    public void ThePatternsAreTheOnesPeopleAlreadyKnow(string pattern, string path, bool excluded) =>
        Assert.Equal(excluded, new SyncFilter(pattern).Excludes(path));

    /// <summary>And an excluded file is not compared, so it is neither copied nor deleted.</summary>
    [Fact]
    public async Task AnExcludedFileIsNeitherCopiedNorDeleted()
    {
        SkipWithoutFixture();

        await using SshNetTransport session = new();

        await session.ConnectAsync(SshEndpoint.For(Host, "probe", Port), [Key()], Trusting, Stop);

        await using IFileTransferChannel files = await session.OpenFileTransferAsync(Stop);

        string there = $"/tmp/qs-{Guid.NewGuid():N}";

        await files.CreateDirectoryAsync(there, Stop);

        await Write("keep.txt", "kept");
        await Write("scratch.tmp", "not wanted");

        try
        {
            await Put(files, $"{there}/theirs.tmp", "also not wanted");

            SyncPlan plan = await SyncPlan.CompareAsync(files, Mine(), there, SyncDirection.Mirror,
                                                        new SyncFilter("*.tmp"),
                                                        cancellationToken: Stop);

            Assert.Equal(["keep.txt"], [.. plan.New.Select(entry => entry.Path)]);

            // The excluded file on the destination is not a deletion, which is the dangerous half.
            Assert.Empty(plan.Deletions);

            await plan.ApplyAsync(files, _ => ValueTask.FromResult(true), Stop);

            List<string> left = await Names(files, there);

            Assert.Contains("keep.txt", left);
            Assert.Contains("theirs.tmp", left);
            Assert.DoesNotContain("scratch.tmp", left);
        }
        finally
        {
            await Remove(files, there);
        }
    }

    // ---- Tolerance on its own ----

    /// <summary>Two times within the coarser filesystem's resolution are the same moment.</summary>
    [Fact]
    public void TimesWithinTheResolutionAreTheSameMoment()
    {
        SyncTolerance close = SyncTolerance.Default;

        DateTimeOffset when = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);

        Assert.True(close.Alike(when, when.AddMilliseconds(999)));
        Assert.True(close.Alike(when, when.AddSeconds(2)));
        Assert.False(close.Alike(when, when.AddSeconds(3)));

        // And a clock that is known to be out can be allowed for, deliberately.
        SyncTolerance skewed = new(TimeSpan.FromSeconds(2), TimeSpan.FromMinutes(1));

        Assert.True(skewed.Alike(when, when.AddSeconds(61)));
        Assert.False(skewed.Alike(when, when.AddSeconds(63)));
    }

    // ---- plumbing ----

    private async Task Write(string relative, string content)
    {
        string path = Path.Combine(Mine(), relative);

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        await File.WriteAllTextAsync(path, content, Stop);
    }

    private static async Task Put(IFileTransferChannel files, string path, string content)
    {
        using MemoryStream from = new(Encoding.UTF8.GetBytes(content));

        await files.UploadAsync(from, path, null, Stop);
    }

    private static async Task<string> Read(IFileTransferChannel files, string path)
    {
        using MemoryStream back = new();

        await files.DownloadAsync(path, back, null, Stop);

        return Encoding.UTF8.GetString(back.ToArray());
    }

    private static async Task<List<string>> Names(IFileTransferChannel files, string directory)
    {
        List<string> found = [];

        await foreach (RemoteEntry entry in files.ListAsync(directory, Stop))
        {
            if (entry.Name is not ("." or ".."))
            {
                found.Add(entry.Name);
            }
        }

        return found;
    }

    private static async Task Remove(IFileTransferChannel files, string directory)
    {
        try
        {
            foreach (RemoteEntry entry in await Entries(files, directory))
            {
                if (entry.IsDirectory)
                {
                    await Remove(files, $"{directory}/{entry.Name}");
                }
                else
                {
                    await files.DeleteAsync($"{directory}/{entry.Name}", CancellationToken.None);
                }
            }

            await files.DeleteAsync(directory, CancellationToken.None);
        }
        catch (Exception)
        {
            // Cleaning up after a failure should not replace it.
        }
    }

    private static async Task<List<RemoteEntry>> Entries(IFileTransferChannel files, string directory)
    {
        List<RemoteEntry> found = [];

        await foreach (RemoteEntry entry in files.ListAsync(directory, CancellationToken.None))
        {
            if (entry.Name is not ("." or ".."))
            {
                found.Add(entry);
            }
        }

        return found;
    }

    private string Mine()
    {
        Directory.CreateDirectory(_here);

        return _here;
    }

    private static ValueTask<SshHostKeyVerdict> Trusting(SshEndpoint _, SshHostKey __,
                                                         CancellationToken ___) =>
        ValueTask.FromResult(SshHostKeyVerdict.Accept);

    private static SshCredential.PrivateKey Key() =>
        new(Path.Combine(RepositoryRoot(), "prototypes", "SshProbe", "fixture", "keys",
                         "probe_ed25519"));

    private static void SkipWithoutFixture()
    {
        bool up;

        try
        {
            using TcpClient probe = new();

            up = probe.ConnectAsync(Host, Port).Wait(TimeSpan.FromSeconds(2));
        }
        catch (Exception)
        {
            up = false;
        }

        Assert.SkipUnless(up, "nothing is listening on 127.0.0.1:2222: run prototypes/SshProbe/fixture/up.sh");
    }

    private static string RepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Quickshell.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);

        return directory.FullName;
    }
}
