using System.Net.Sockets;
using System.Text;
using Quickshell.Transport;
using Xunit;

namespace Quickshell.Transport.Tests;

/// <summary>
/// Copying a folder, and the four answers to a name that is already taken.
///
/// <para>Both are where a transfer tool quietly destroys data, so both are tested against a real
/// server rather than against a model of one.</para>
/// </summary>
public sealed class TransferPlanTests : IDisposable
{
    private const string Host = "127.0.0.1";
    private const int Port = 2222;

    private readonly string _here =
        Path.Combine(Path.GetTempPath(), $"quickshell-plan-{Guid.NewGuid():N}");

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
    /// The line's own falsification: an interrupted overwrite leaves the destination file intact.
    ///
    /// <para>A complete, valuable file is on the server. A different, larger one is uploaded over it
    /// and the transfer is cancelled part way. What must be true afterwards is that the original is
    /// still there and still complete — a client that wrote straight to the destination would have
    /// left a truncated mixture and destroyed the only copy.</para>
    /// </summary>
    [Fact]
    public async Task AnInterruptedOverwriteLeavesTheDestinationIntact()
    {
        SkipWithoutFixture();

        await using SshNetTransport session = new();

        await session.ConnectAsync(SshEndpoint.For(Host, "probe", Port), [Key()], Trusting, Stop);

        await using IFileTransferChannel files = await session.OpenFileTransferAsync(Stop);

        byte[] precious = Encoding.UTF8.GetBytes("the file that was already there, complete");
        string there = $"/tmp/qs-{Guid.NewGuid():N}.bin";

        using (MemoryStream first = new(precious))
        {
            await files.UploadAsync(first, there, null, Stop);
        }

        string mine = Path.Combine(Mine(), "replacement.bin");

        await File.WriteAllBytesAsync(mine, Bytes(4 * 1024 * 1024), Stop);

        TransferQueue queue = new(files)
        {
            BlockSize = 16 * 1024,
            OnCollision = (_, _) => ValueTask.FromResult(new CollisionChoice(CollisionAnswer.Overwrite)),
        };

        TransferEntry entry = queue.Enqueue(TransferDirection.Upload, mine, there);

        try
        {
            Task running = queue.RunAsync(Stop);

            await Until(() => entry.Moved > 64 * 1024);

            queue.Cancel(entry);

            await running;

            Assert.Equal(TransferState.Cancelled, entry.State);

            // The original, untouched and complete.
            using MemoryStream back = new();

            await files.DownloadAsync(there, back, null, Stop);

            Assert.Equal(precious, back.ToArray());
        }
        finally
        {
            await Quietly(files, there);
            await Quietly(files, $"{there}.qs-part");
        }
    }

    // ---- The four answers ----

    /// <summary>Overwrite replaces what was there, once every byte has arrived.</summary>
    [Fact]
    public async Task OverwriteReplacesWhatWasThere()
    {
        await Collides(CollisionAnswer.Overwrite, async (files, there, sent, entry) =>
        {
            Assert.Equal(TransferState.Done, entry.State);
            Assert.Equal(there, entry.Landed);

            Assert.Equal(sent, await Read(files, there));
        });
    }

    /// <summary>Skip leaves it alone and says so, rather than looking like a transfer that worked.</summary>
    [Fact]
    public async Task SkipLeavesWhatWasThere()
    {
        await Collides(CollisionAnswer.Skip, async (files, there, _, entry) =>
        {
            Assert.Equal(TransferState.Skipped, entry.State);
            Assert.Contains("left alone", entry.Why, StringComparison.Ordinal);

            Assert.Equal(Existing, await Read(files, there));
        });
    }

    /// <summary>Rename writes beside it, and says where it actually landed.</summary>
    [Fact]
    public async Task RenameWritesBesideWhatWasThere()
    {
        await Collides(CollisionAnswer.Rename, async (files, there, sent, entry) =>
        {
            Assert.Equal(TransferState.Done, entry.State);
            Assert.NotEqual(there, entry.Landed);
            Assert.Contains("(2)", entry.Landed, StringComparison.Ordinal);

            // Both of them, which is the point of this answer.
            Assert.Equal(Existing, await Read(files, there));
            Assert.Equal(sent, await Read(files, entry.Landed));

            await Quietly(files, entry.Landed);
        });
    }

    /// <summary>
    /// Take-newer keeps what is there when it is not older, which is the answer that does nothing
    /// most of the time and must therefore say that it did nothing.
    /// </summary>
    [Fact]
    public async Task TakeNewerKeepsWhatIsNotOlder()
    {
        await Collides(CollisionAnswer.TakeNewer, async (files, there, _, entry) =>
        {
            Assert.Equal(TransferState.Skipped, entry.State);
            Assert.Contains("same age or newer", entry.Why, StringComparison.Ordinal);

            Assert.Equal(Existing, await Read(files, there));
        },
        // The destination is given a time far in the future, so it is plainly not the older one.
        age: TimeSpan.FromDays(365));
    }

    /// <summary>
    /// With nobody to ask, nothing is overwritten. A queue running unattended must not be the one
    /// that destroys a file.
    /// </summary>
    [Fact]
    public async Task WithNobodyToAskNothingIsOverwritten()
    {
        await Collides(null, async (files, there, _, entry) =>
        {
            Assert.Equal(TransferState.Skipped, entry.State);
            Assert.Equal(Existing, await Read(files, there));
        });
    }

    /// <summary>
    /// An answer given for the rest is not asked again.
    ///
    /// <para>A user answering the same question four hundred times picks whichever option ends it
    /// soonest, so asking once is what keeps the answer meaningful.</para>
    /// </summary>
    [Fact]
    public async Task AnAnswerForTheRestIsNotAskedAgain()
    {
        SkipWithoutFixture();

        await using SshNetTransport session = new();

        await session.ConnectAsync(SshEndpoint.For(Host, "probe", Port), [Key()], Trusting, Stop);

        await using IFileTransferChannel files = await session.OpenFileTransferAsync(Stop);

        int asked = 0;
        List<string> theirs = [];

        TransferQueue queue = new(files)
        {
            MaximumConcurrent = 1,
            OnCollision = (_, _) =>
            {
                Interlocked.Increment(ref asked);

                return ValueTask.FromResult(
                    new CollisionChoice(CollisionAnswer.Skip, ForTheRest: true));
            },
        };

        try
        {
            for (int file = 0; file < 3; file++)
            {
                string there = $"/tmp/qs-{Guid.NewGuid():N}.bin";

                theirs.Add(there);

                using (MemoryStream first = new(Existing))
                {
                    await files.UploadAsync(first, there, null, Stop);
                }

                string mine = Path.Combine(Mine(), $"over{file}.bin");

                await File.WriteAllBytesAsync(mine, Bytes(2048), Stop);

                queue.Enqueue(TransferDirection.Upload, mine, there);
            }

            await queue.RunAsync(Stop);

            Assert.Equal(1, asked);
            Assert.All(queue.Entries, entry => Assert.Equal(TransferState.Skipped, entry.State));
        }
        finally
        {
            foreach (string there in theirs)
            {
                await Quietly(files, there);
            }
        }
    }

    // ---- The walk ----

    /// <summary>
    /// A directory is copied whole: every file, every nested folder, and the empty ones too.
    ///
    /// <para>An empty directory disappearing is the most commonly skipped part of a recursive copy,
    /// because a walk that enumerates files never sees one.</para>
    /// </summary>
    [Fact]
    public async Task ADirectoryIsCopiedWholeIncludingItsEmptyFolders()
    {
        SkipWithoutFixture();

        await using SshNetTransport session = new();

        await session.ConnectAsync(SshEndpoint.For(Host, "probe", Port), [Key()], Trusting, Stop);

        await using IFileTransferChannel files = await session.OpenFileTransferAsync(Stop);

        string root = Path.Combine(Mine(), "tree");

        Directory.CreateDirectory(Path.Combine(root, "one", "two"));
        Directory.CreateDirectory(Path.Combine(root, "empty"));

        await File.WriteAllTextAsync(Path.Combine(root, "top.txt"), "top", Stop);
        await File.WriteAllTextAsync(Path.Combine(root, "one", "middle.txt"), "middle", Stop);
        await File.WriteAllTextAsync(Path.Combine(root, "one", "two", "deep.txt"), "deep", Stop);

        string there = $"/tmp/qs-{Guid.NewGuid():N}";

        TransferPlan plan = TransferPlan.ToCopyUp(root, there);

        // Shallowest first, so nothing is written into a folder that does not exist yet.
        Assert.Equal(4, plan.Directories.Count);
        Assert.Equal([0, 1, 1, 2], [.. plan.Directories.Select(directory => directory.Depth)]);
        Assert.Equal(3, plan.Files.Count);

        // The empty one is in the plan, which is the whole point.
        Assert.Contains(plan.Directories, directory => directory.Path.EndsWith("empty", StringComparison.Ordinal));

        TransferQueue queue = new(files);

        try
        {
            await plan.EnqueueAsync(queue, files, Stop);

            await queue.RunAsync(Stop);

            Assert.All(queue.Entries, entry => Assert.Equal(TransferState.Done, entry.State));

            Assert.Equal("deep", Encoding.UTF8.GetString(await Read(files, $"{there}/one/two/deep.txt")));

            // And the empty directory really is a directory over there.
            Assert.True((await files.StatAsync($"{there}/empty", Stop)).IsDirectory);
        }
        finally
        {
            await Remove(files, there);
        }
    }

    /// <summary>
    /// A symbolic link is copied as a link and not followed.
    ///
    /// <para>Following is how a recursive copy walks into a loop, or drags in an entire filesystem
    /// through a link somebody left in their home directory. The link here points at its own
    /// directory, so a walk that followed it would not terminate.</para>
    /// </summary>
    [Fact]
    public async Task ALinkIsCopiedAsALinkRatherThanFollowed()
    {
        SkipWithoutFixture();

        await using SshNetTransport session = new();

        await session.ConnectAsync(SshEndpoint.For(Host, "probe", Port), [Key()], Trusting, Stop);

        await using IFileTransferChannel files = await session.OpenFileTransferAsync(Stop);

        string there = $"/tmp/qs-{Guid.NewGuid():N}";

        await files.CreateDirectoryAsync(there, Stop);

        try
        {
            await using (Stream writing = await files.OpenWriteAsync($"{there}/real.txt", Stop))
            {
                await writing.WriteAsync(Encoding.UTF8.GetBytes("real"), Stop);
            }

            // A link pointing at its own directory: following it is an infinite walk.
            await files.SymbolicLinkAsync(there, $"{there}/loop", Stop);

            TransferPlan plan = await TransferPlan.ToCopyDownAsync(
                files, there, Path.Combine(Mine(), "down"), LinkPolicy.Copy, Stop);

            // It terminated, which following it would not have.
            Assert.Single(plan.Files);

            // And it is left out with the reason attached rather than recreated from a guess: what
            // a remote link points at cannot be read over this connection at all.
            Assert.Empty(plan.Links);
            Assert.Contains("cannot be read", Assert.Single(plan.Skipped), StringComparison.Ordinal);

            // Skipping says the same thing more briefly.
            TransferPlan skipping = await TransferPlan.ToCopyDownAsync(
                files, there, Path.Combine(Mine(), "down2"), LinkPolicy.Skip, Stop);

            Assert.Empty(skipping.Links);
            Assert.Contains("left out", Assert.Single(skipping.Skipped), StringComparison.Ordinal);
        }
        finally
        {
            await Remove(files, there);
        }
    }

    /// <summary>
    /// What cannot be carried across is said once for the queue, not once per file.
    ///
    /// <para>A warning repeated four hundred times is a warning nobody reads, and the thing it
    /// warned about happens anyway.</para>
    /// </summary>
    [Fact]
    public async Task WhatCannotBeCarriedIsSaidOnce()
    {
        SkipWithoutFixture();

        await using SshNetTransport session = new();

        await session.ConnectAsync(SshEndpoint.For(Host, "probe", Port), [Key()], Trusting, Stop);

        await using IFileTransferChannel files = await session.OpenFileTransferAsync(Stop);

        List<string> theirs = [];

        TransferQueue queue = new(files);

        try
        {
            for (int file = 0; file < 3; file++)
            {
                string mine = Path.Combine(Mine(), $"loss{file}.bin");

                await File.WriteAllBytesAsync(mine, Bytes(512), Stop);

                string there = $"/tmp/qs-{Guid.NewGuid():N}.bin";

                theirs.Add(there);

                queue.Enqueue(TransferDirection.Upload, mine, there);
            }

            await queue.RunAsync(Stop);

            // Three files, one statement.
            string said = Assert.Single(queue.Losses);

            Assert.Contains("Unix mode", said, StringComparison.Ordinal);
        }
        finally
        {
            foreach (string there in theirs)
            {
                await Quietly(files, there);
            }
        }
    }

    /// <summary>And the modification time is carried across, because that one can be.</summary>
    [Fact]
    public async Task TheModificationTimeIsCarriedAcross()
    {
        SkipWithoutFixture();

        await using SshNetTransport session = new();

        await session.ConnectAsync(SshEndpoint.For(Host, "probe", Port), [Key()], Trusting, Stop);

        await using IFileTransferChannel files = await session.OpenFileTransferAsync(Stop);

        string mine = Path.Combine(Mine(), "aged.bin");

        await File.WriteAllBytesAsync(mine, Bytes(512), Stop);

        DateTime then = new(2011, 3, 4, 5, 6, 7, DateTimeKind.Utc);

        File.SetLastWriteTimeUtc(mine, then);

        string there = $"/tmp/qs-{Guid.NewGuid():N}.bin";

        TransferQueue queue = new(files);

        queue.Enqueue(TransferDirection.Upload, mine, there);

        try
        {
            await queue.RunAsync(Stop);

            Assert.Equal(new DateTimeOffset(then), (await files.StatAsync(there, Stop)).Modified);
        }
        finally
        {
            await Quietly(files, there);
        }
    }

    // ---- plumbing ----

    private static readonly byte[] Existing = Encoding.UTF8.GetBytes("what was already there");

    /// <summary>
    /// Sets up a name that is already taken, answers it the given way, and hands the result over.
    /// </summary>
    private async Task Collides(CollisionAnswer? answer,
                                Func<IFileTransferChannel, string, byte[], TransferEntry, Task> then,
                                TimeSpan age = default)
    {
        SkipWithoutFixture();

        await using SshNetTransport session = new();

        await session.ConnectAsync(SshEndpoint.For(Host, "probe", Port), [Key()], Trusting, Stop);

        await using IFileTransferChannel files = await session.OpenFileTransferAsync(Stop);

        string there = $"/tmp/qs-{Guid.NewGuid():N}.bin";

        using (MemoryStream first = new(Existing))
        {
            await files.UploadAsync(first, there, null, Stop);
        }

        if (age != default)
        {
            await files.SetLastWriteTimeAsync(there, DateTimeOffset.UtcNow + age, Stop);
        }

        byte[] sent = Bytes(4096);
        string mine = Path.Combine(Mine(), "over.bin");

        await File.WriteAllBytesAsync(mine, sent, Stop);

        TransferQueue queue = new(files)
        {
            OnCollision = answer is { } chosen
                ? (_, _) => ValueTask.FromResult(new CollisionChoice(chosen))
                : null,
        };

        TransferEntry entry = queue.Enqueue(TransferDirection.Upload, mine, there);

        try
        {
            await queue.RunAsync(Stop);

            await then(files, there, sent, entry);
        }
        finally
        {
            await Quietly(files, there);
        }
    }

    private static async Task<byte[]> Read(IFileTransferChannel files, string path)
    {
        using MemoryStream back = new();

        await files.DownloadAsync(path, back, null, Stop);

        return back.ToArray();
    }

    /// <summary>Empties a remote directory and removes it, for a test that made a tree.</summary>
    private static async Task Remove(IFileTransferChannel files, string directory)
    {
        try
        {
            List<RemoteEntry> here = [];

            await foreach (RemoteEntry entry in files.ListAsync(directory, CancellationToken.None))
            {
                if (entry.Name is not ("." or ".."))
                {
                    here.Add(entry);
                }
            }

            foreach (RemoteEntry entry in here)
            {
                if (entry.IsDirectory)
                {
                    await Remove(files, $"{directory}/{entry.Name}");
                }
                else
                {
                    await Quietly(files, $"{directory}/{entry.Name}");
                }
            }

            await Quietly(files, directory);
        }
        catch (Exception)
        {
            // Cleaning up after a failure should not replace it.
        }
    }

    private static byte[] Bytes(int many)
    {
        byte[] made = new byte[many];

        Random.Shared.NextBytes(made);

        return made;
    }

    private string Mine()
    {
        Directory.CreateDirectory(_here);

        return _here;
    }

    private static async Task Until(Func<bool> ready)
    {
        using CancellationTokenSource waiting = CancellationTokenSource.CreateLinkedTokenSource(Stop);
        waiting.CancelAfter(TimeSpan.FromSeconds(20));

        while (!ready())
        {
            await Task.Delay(10, waiting.Token);
        }
    }

    private static async Task Quietly(IFileTransferChannel files, string path)
    {
        try
        {
            await files.DeleteAsync(path, CancellationToken.None);
        }
        catch (Exception)
        {
            // Cleaning up after a failure should not replace it.
        }
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
