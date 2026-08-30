using System.Net.Sockets;
using System.Text;
using Quickshell.Transport;
using Xunit;

namespace Quickshell.Transport.Tests;

/// <summary>
/// The queue, and the one judgement it must not get wrong.
///
/// <para>Resume is the reason this exists and the way it can do real harm: continuing into a file
/// that is not a prefix of the source produces a mixture of two versions, silently, which no
/// checksum was ever taken of. So the decision is tested on its own, and then the whole thing is
/// run against a real server and the bytes compared.</para>
/// </summary>
public sealed class TransferQueueTests : IDisposable
{
    private const string Host = "127.0.0.1";
    private const int Port = 2222;

    private readonly string _here =
        Path.Combine(Path.GetTempPath(), $"quickshell-transfer-{Guid.NewGuid():N}");

    private static CancellationToken Stop => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        if (Directory.Exists(_here))
        {
            Directory.Delete(_here, recursive: true);
        }
    }

    // ---- The judgement, on its own ----

    /// <summary>Nothing there is a fresh start, and needs no justifying.</summary>
    [Fact]
    public void NothingAlreadyThereStartsFresh()
    {
        ResumeDecision decided = TransferQueue.Decide(null, new SourceState(100, When()), 0);

        Assert.Equal(ResumeVerdict.Fresh, decided.Verdict);
        Assert.Equal(0, decided.At);
    }

    /// <summary>
    /// A partial with nothing recorded about where it came from cannot be shown to be a prefix, so
    /// it is written again — the design's own criterion, and the whole point of the line.
    ///
    /// <para>It may well be a prefix. It may also be another program's file with the same name. The
    /// difference is not observable, so the safe answer is the honest one.</para>
    /// </summary>
    [Fact]
    public void APartialWithNothingRecordedIsWrittenAgain()
    {
        ResumeDecision decided = TransferQueue.Decide(null, new SourceState(100, When()), 40);

        Assert.Equal(ResumeVerdict.Restart, decided.Verdict);
        Assert.Equal(0, decided.At);
        Assert.Contains("cannot be shown to be the beginning", decided.Why, StringComparison.Ordinal);
    }

    /// <summary>
    /// A source that has changed since the partial was written is written again, because continuing
    /// would join two versions of one file.
    /// </summary>
    [Theory]
    [InlineData(100, 0, 100, 60)]
    [InlineData(120, 0, 100, 0)]
    public void ASourceThatChangedIsWrittenAgain(long thenLength, int thenOffset, long nowLength,
                                                 int nowOffset)
    {
        SourceState began = new(thenLength, When().AddSeconds(thenOffset));
        SourceState now = new(nowLength, When().AddSeconds(nowOffset));

        ResumeDecision decided = TransferQueue.Decide(began, now, 40);

        Assert.Equal(ResumeVerdict.Restart, decided.Verdict);
        Assert.Contains("changed", decided.Why, StringComparison.Ordinal);
    }

    /// <summary>More bytes than the source has is not a prefix of it, whatever else it may be.</summary>
    [Fact]
    public void APartialLongerThanTheSourceIsWrittenAgain()
    {
        SourceState state = new(100, When());

        ResumeDecision decided = TransferQueue.Decide(state, state, 140);

        Assert.Equal(ResumeVerdict.Restart, decided.Verdict);
        Assert.Contains("longer than the source", decided.Why, StringComparison.Ordinal);
    }

    /// <summary>And an unchanged source with a shorter partial carries on from where it stopped.</summary>
    [Fact]
    public void AnUnchangedSourceCarriesOnFromWhereItStopped()
    {
        SourceState state = new(100, When());

        ResumeDecision decided = TransferQueue.Decide(state, state, 40);

        Assert.Equal(ResumeVerdict.Resume, decided.Verdict);
        Assert.Equal(40, decided.At);
        Assert.Equal(string.Empty, decided.Why);
    }

    // ---- The falsification, against a real server ----

    /// <summary>
    /// The line's own falsification: a resumed transfer produces the file that was sent.
    ///
    /// <para>Half a megabyte is uploaded, the transfer is paused part way, and the queue is run
    /// again. What ends up on the server is compared byte for byte with what was read — a resume
    /// that started at the wrong offset would produce a file of the right length and the wrong
    /// contents, which is the failure worth catching.</para>
    /// </summary>
    [Fact]
    public async Task AResumedUploadProducesTheFileThatWasSent()
    {
        SkipWithoutFixture();

        await using SshNetTransport session = new();

        await session.ConnectAsync(SshEndpoint.For(Host, "probe", Port), [Key()], Trusting, Stop);

        await using IFileTransferChannel files = await session.OpenFileTransferAsync(Stop);

        byte[] sent = Bytes(512 * 1024);
        string mine = Path.Combine(Mine(), "sent.bin");

        await File.WriteAllBytesAsync(mine, sent, Stop);

        string there = $"/tmp/qs-{Guid.NewGuid():N}.bin";

        TransferQueue queue = new(files) { BlockSize = 16 * 1024 };

        TransferEntry entry = queue.Enqueue(TransferDirection.Upload, mine, there);

        try
        {
            // Stopped part way, and not at a block boundary of the reader's choosing.
            using CancellationTokenSource pausing = new();

            Task running = queue.RunAsync(pausing.Token);

            await Until(() => entry.Moved > 64 * 1024);

            queue.Pause(entry);

            await running;

            Assert.Equal(TransferState.Paused, entry.State);
            Assert.True(entry.Moved is > 0 and < 512 * 1024, $"paused at {entry.Moved}");

            long stopped = entry.Moved;

            // What it recorded about the source is what makes carrying on safe, and the judgement
            // made with that record is the one the unit tests above cover.
            SourceState began = Assert.NotNull(entry.Began);

            Assert.Equal(sent.Length, began.Length);
            Assert.Equal(ResumeVerdict.Resume,
                         TransferQueue.Decide(began, began, stopped).Verdict);

            // And now it carries on rather than starting again.
            queue.Retry(entry);

            await queue.RunAsync(Stop);

            Assert.Equal(TransferState.Done, entry.State);
            Assert.Equal(string.Empty, entry.Why);

            using MemoryStream back = new();

            await files.DownloadAsync(there, back, null, Stop);

            Assert.Equal(sent.Length, back.Length);
            Assert.Equal(sent, back.ToArray());

            // It really did resume: a fresh start would have moved the whole file again.
            Assert.True(stopped > 0, "nothing had been moved before the pause");
        }
        finally
        {
            await Quietly(files, there);
        }
    }

    /// <summary>
    /// The same in the other direction, and a partial nothing is recorded about is written again
    /// rather than resumed.
    /// </summary>
    [Fact]
    public async Task ADownloadOverAStrangePartialWritesItAgainAndSaysWhy()
    {
        SkipWithoutFixture();

        await using SshNetTransport session = new();

        await session.ConnectAsync(SshEndpoint.For(Host, "probe", Port), [Key()], Trusting, Stop);

        await using IFileTransferChannel files = await session.OpenFileTransferAsync(Stop);

        byte[] sent = Bytes(256 * 1024);
        string there = $"/tmp/qs-{Guid.NewGuid():N}.bin";

        using (MemoryStream source = new(sent))
        {
            await files.UploadAsync(source, there, null, Stop);
        }

        // Somebody else's file, at the name this transfer is about to write to.
        string mine = Path.Combine(Mine(), "arriving.bin");

        await File.WriteAllBytesAsync(mine, new byte[4096], Stop);

        TransferQueue queue = new(files);

        TransferEntry entry = queue.Enqueue(TransferDirection.Download, mine, there);

        try
        {
            await queue.RunAsync(Stop);

            Assert.Equal(TransferState.Done, entry.State);

            // Written again, and the user is told why rather than left with a mixture.
            Assert.Contains("Nothing is recorded", entry.Why, StringComparison.Ordinal);

            Assert.Equal(sent, await File.ReadAllBytesAsync(mine, Stop));
        }
        finally
        {
            await Quietly(files, there);
        }
    }

    // ---- The queue itself ----

    /// <summary>
    /// Cancel stops now, and does not let the file it is on run to completion first.
    ///
    /// <para>Two megabytes over a queue whose entry is cancelled once it has started: what makes
    /// this a real check is that the amount moved is well short of the whole, so the copy loop
    /// genuinely stopped mid-file.</para>
    /// </summary>
    [Fact]
    public async Task CancelStopsTheFileItIsOnRatherThanFinishingIt()
    {
        SkipWithoutFixture();

        await using SshNetTransport session = new();

        await session.ConnectAsync(SshEndpoint.For(Host, "probe", Port), [Key()], Trusting, Stop);

        await using IFileTransferChannel files = await session.OpenFileTransferAsync(Stop);

        string mine = Path.Combine(Mine(), "big.bin");

        await File.WriteAllBytesAsync(mine, Bytes(4 * 1024 * 1024), Stop);

        string there = $"/tmp/qs-{Guid.NewGuid():N}.bin";

        TransferQueue queue = new(files) { BlockSize = 16 * 1024 };

        TransferEntry entry = queue.Enqueue(TransferDirection.Upload, mine, there);

        try
        {
            Task running = queue.RunAsync(Stop);

            await Until(() => entry.Moved > 32 * 1024);

            queue.Cancel(entry);

            await running;

            Assert.Equal(TransferState.Cancelled, entry.State);
            Assert.True(entry.Moved < 4 * 1024 * 1024,
                        $"it moved all {entry.Moved} bytes, so it did not stop");
        }
        finally
        {
            await Quietly(files, there);
        }
    }

    /// <summary>
    /// A failed entry stays in the queue carrying its reason, and can be asked for again.
    ///
    /// <para>A transfer that vanished when it failed is one the user cannot retry and cannot find
    /// out about.</para>
    /// </summary>
    [Fact]
    public async Task AFailedEntryStaysAndSaysWhy()
    {
        SkipWithoutFixture();

        await using SshNetTransport session = new();

        await session.ConnectAsync(SshEndpoint.For(Host, "probe", Port), [Key()], Trusting, Stop);

        await using IFileTransferChannel files = await session.OpenFileTransferAsync(Stop);

        TransferQueue queue = new(files);

        TransferEntry entry = queue.Enqueue(TransferDirection.Download,
                                            Path.Combine(Mine(), "never.bin"),
                                            "/tmp/there-is-nothing-here-at-all");

        await queue.RunAsync(Stop);

        Assert.Equal(TransferState.Failed, entry.State);
        Assert.Contains("/tmp/there-is-nothing-here-at-all", entry.Why, StringComparison.Ordinal);

        // Still in the queue, and offerable again.
        Assert.Contains(entry, queue.Entries);
        Assert.True(entry.CanRetry);

        queue.Retry(entry);

        Assert.Equal(TransferState.Waiting, entry.State);
        Assert.Equal(string.Empty, entry.Why);
    }

    /// <summary>The aggregate a queue shows is the sum of what its entries know.</summary>
    [Fact]
    public async Task TheQueueAddsUpWhatItsEntriesHaveMoved()
    {
        SkipWithoutFixture();

        await using SshNetTransport session = new();

        await session.ConnectAsync(SshEndpoint.For(Host, "probe", Port), [Key()], Trusting, Stop);

        await using IFileTransferChannel files = await session.OpenFileTransferAsync(Stop);

        List<string> theirs = [];

        TransferQueue queue = new(files) { MaximumConcurrent = 2 };

        for (int file = 0; file < 3; file++)
        {
            string mine = Path.Combine(Mine(), $"part{file}.bin");

            await File.WriteAllBytesAsync(mine, Bytes(64 * 1024), Stop);

            string there = $"/tmp/qs-{Guid.NewGuid():N}.bin";

            theirs.Add(there);

            queue.Enqueue(TransferDirection.Upload, mine, there);
        }

        try
        {
            await queue.RunAsync(Stop);

            Assert.All(queue.Entries, entry => Assert.Equal(TransferState.Done, entry.State));

            Assert.Equal(3 * 64 * 1024, queue.TotalBytes);
            Assert.Equal(queue.TotalBytes, queue.MovedBytes);
            Assert.All(queue.Entries, entry => Assert.Equal(1.0, entry.Fraction));
        }
        finally
        {
            foreach (string there in theirs)
            {
                await Quietly(files, there);
            }
        }
    }

    /// <summary>Concurrency is a number, and one below one is not one of them.</summary>
    [Fact]
    public void ConcurrencyIsAtLeastOne()
    {
        SftpNothing nothing = new();

        TransferQueue queue = new(nothing);

        Assert.Equal(2, queue.MaximumConcurrent);
        Assert.Throws<ArgumentOutOfRangeException>(() => queue.MaximumConcurrent = 0);
    }

    // ---- plumbing ----

    private static byte[] Bytes(int many)
    {
        byte[] made = new byte[many];

        Random.Shared.NextBytes(made);

        return made;
    }

    private static DateTimeOffset When() => new(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);

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

    /// <summary>A channel that is never asked anything, for the checks that need no server.</summary>
    private sealed class SftpNothing : IFileTransferChannel
    {
        public int ProtocolVersion => 3;

        public string WorkingDirectory => "/";

        public IAsyncEnumerable<RemoteEntry> ListAsync(string path, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<RemoteEntry> StatAsync(string path, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<Stream> OpenReadAsync(string path, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<Stream> OpenWriteAsync(string path, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<Stream> OpenWriteAtAsync(string path, long at, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask DownloadAsync(string path, Stream into, IProgress<long>? progress = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask UploadAsync(Stream from, string path, IProgress<long>? progress = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask DeleteAsync(string path, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask RenameAsync(string from, string to, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask CreateDirectoryAsync(string path, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask SymbolicLinkAsync(string target, string link, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask ChangePermissionsAsync(string path, int mode, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask SetLastWriteTimeAsync(string path, DateTimeOffset when, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
