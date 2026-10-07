using System.IO;
using System.Runtime.CompilerServices;
using Quickshell.App;
using Quickshell.Transport;
using Xunit;

namespace Quickshell.App.Tests;

/// <summary>
/// QS186: a copy started from the browser is a line under the panes while it runs — how far, how
/// fast, how long — with a stop, and one that stopped short stays there with its retry.
/// </summary>
public sealed class CopyStripTests : IDisposable
{
    private const int Size = 2 * 1024 * 1024;

    private readonly string _here = Path.Combine(Path.GetTempPath(), "qs186-" + Guid.NewGuid().ToString("N"));

    public CopyStripTests() => Directory.CreateDirectory(_here);

    private static CancellationToken Stop => TestContext.Current.CancellationToken;

    /// <inheritdoc/>
    public void Dispose() => Directory.Delete(_here, recursive: true);

    /// <summary>
    /// The falsification, through the window: a copy that has moved bytes for a second shows its
    /// progress on the browser's strip.
    /// </summary>
    [Fact]
    public void ACopyMovingForASecondShowsItsProgressInTheBrowser()
    {
        Server server = new();

        (IReadOnlyList<string> during, IReadOnlyList<string> after) = OnSta(() =>
        {
            FileBrowser browser = new(new LocalFiles(), new RemoteFiles(server, "web1"));

            Pick(browser.Actions, browser.Local, browser.Remote!);

            Task copying = browser.Actions.CopyAsync(Stop);

            Thread.Sleep(TimeSpan.FromSeconds(1));
            browser.DrawCopies();

            IReadOnlyList<string> shown = browser.CopyLines;

            copying.Wait(TimeSpan.FromSeconds(20), Stop);
            browser.DrawCopies();

            return (shown, browser.CopyLines);
        });

        string line = Assert.Single(during);

        Assert.StartsWith("Copying big.bin to " + _here, line, StringComparison.Ordinal);
        Assert.Contains("moved", line, StringComparison.Ordinal);
        Assert.Contains("/s", line, StringComparison.Ordinal);

        // Everything landed, so the strip has nothing left to show.
        Assert.Empty(after);
        Assert.Equal(Size, new FileInfo(Path.Combine(_here, "big.bin")).Length);
    }

    /// <summary>
    /// Stopped from the strip, a copy stays on it with its retry, and the retry is the resume: it
    /// carries on from the bytes already moved and lands a file equal to the source.
    /// </summary>
    [Fact]
    public async Task AStoppedCopyStaysWithARetryThatResumes()
    {
        Server server = new();
        BrowserActions actions = Actions(server, out DirectoryPane local, out DirectoryPane remote);

        Pick(actions, local, remote);

        Task copying = actions.CopyAsync(Stop);
        CopyProgress copy = await Moving(actions);

        copy.Stop();
        await copying.WaitAsync(TimeSpan.FromSeconds(10), Stop);

        Assert.Same(copy, Assert.Single(actions.Copies));
        Assert.False(copy.Running);
        Assert.True(copy.Unfinished);
        Assert.StartsWith("Copy to " + _here, copy.Line, StringComparison.Ordinal);
        Assert.Contains("stopped after 0 of 1 files", copy.Line, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(_here, "big.bin")), "a stopped copy wrote over the destination");

        long stoppedAt = copy.Queue.Entries[0].Moved;

        await actions.RetryAsync(copy, Stop).WaitAsync(TimeSpan.FromSeconds(20), Stop);

        Assert.Empty(actions.Copies);
        Assert.Equal(server.Bytes, await File.ReadAllBytesAsync(Path.Combine(_here, "big.bin"), Stop));
        Assert.True(server.LastSeek >= stoppedAt && stoppedAt > 0,
                    $"the retry read from {server.LastSeek}, after the stop had moved {stoppedAt}");
    }

    /// <summary>A file that fails stays on the strip with its reason, rather than being folded into a sentence.</summary>
    [Fact]
    public async Task AFailureStaysOnTheStripWithItsReason()
    {
        Server server = new() { FailAfter = 256 * 1024 };
        BrowserActions actions = Actions(server, out DirectoryPane local, out DirectoryPane remote);

        Pick(actions, local, remote);

        await actions.CopyAsync(Stop).WaitAsync(TimeSpan.FromSeconds(20), Stop);

        CopyProgress copy = Assert.Single(actions.Copies);

        Assert.Equal($"Copy to {_here} on This computer: 1 failed, first big.bin: the link dropped", copy.Line);

        actions.Dismiss(copy);

        Assert.Empty(actions.Copies);
    }

    /// <summary>The time left reads as somebody watching reads it.</summary>
    [Theory]
    [InlineData(0.4, "a second")]
    [InlineData(12.2, "13 s")]
    [InlineData(600, "10 min")]
    [InlineData(9000, "2.5 h")]
    public void TimeLeftIsSaidInTheUnitThatFits(double seconds, string said)
    {
        Assert.Equal(said, CopyProgress.Left(TimeSpan.FromSeconds(seconds)));
    }

    private static BrowserActions Actions(Server server, out DirectoryPane local, out DirectoryPane remote)
    {
        PanePump pump = new();

        local = new DirectoryPane(new LocalFiles(), pump.Post);
        remote = new DirectoryPane(new RemoteFiles(server, "web1"), pump.Post);

        return new BrowserActions(local, remote, pump.Post);
    }

    /// <summary>The remote file selected in the host's pane, with this computer's pane on the scratch directory.</summary>
    private void Pick(BrowserActions actions, DirectoryPane local, DirectoryPane remote)
    {
        _ = local.Go(_here);
        _ = remote.Go("/data");

        remote.Selected = [new FileItem("big.bin", Size, false, DateTimeOffset.UnixEpoch, "-rw-r--r--", false)];
        actions.Active = remote;
        actions.AskingToCopy = _ => true;
    }

    /// <summary>The copy on the strip, once it has moved some bytes.</summary>
    private static async Task<CopyProgress> Moving(BrowserActions actions)
    {
        DateTime until = DateTime.UtcNow + TimeSpan.FromSeconds(10);

        while (true)
        {
            if (actions.Copies is [CopyProgress copy] && copy.Queue.Entries is [{ Moved: > 0 }])
            {
                return copy;
            }

            Assert.True(DateTime.UtcNow < until, "the copy never started moving");

            await Task.Delay(20, Stop);
        }
    }

    private static T OnSta<T>(Func<T> work)
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

        Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "the STA thread never finished");

        if (failed is not null)
        {
            throw new InvalidOperationException("the window could not be built", failed);
        }

        return result;
    }

    /// <summary>One file on a server that hands it over at the pace of a slow link.</summary>
    private sealed class Server : IFileTransferChannel
    {
        public byte[] Bytes { get; } = Enumerable.Range(0, Size).Select(at => (byte)(at * 31 % 251)).ToArray();

        /// <summary>Where the last read began, which a resume makes more than zero.</summary>
        public long LastSeek { get; private set; } = -1;

        /// <summary>How far a read gets before the link drops, or never.</summary>
        public long FailAfter { get; init; } = long.MaxValue;

        public int ProtocolVersion => 3;

        public string WorkingDirectory => "/data";

        public ValueTask<RemoteEntry> StatAsync(string path, CancellationToken cancellationToken = default) =>
            path == "/data/big.bin"
                ? ValueTask.FromResult(new RemoteEntry("big.bin", Size, false, DateTimeOffset.UnixEpoch, "-rw-r--r--"))
                : throw new SshException(SshFailureKind.Dropped, $"{path}: no such file");

        public ValueTask<Stream> OpenReadAsync(string path, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<Stream>(new Slow(this));

        public async IAsyncEnumerable<RemoteEntry> ListAsync(
            string path, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;

            yield return new RemoteEntry("big.bin", Size, false, DateTimeOffset.UnixEpoch, "-rw-r--r--");
        }

        public ValueTask<Stream> OpenWriteAsync(string path, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<Stream> OpenWriteAtAsync(string path, long at, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask DeleteAsync(string path, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask RenameAsync(string from, string to, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask CreateDirectoryAsync(string path, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask SymbolicLinkAsync(string target, string link, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<string> ReadLinkAsync(string path, CancellationToken cancellationToken = default) =>
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

        /// <summary>Thirty-two kilobytes every twenty milliseconds: about a megabyte and a half a second.</summary>
        private sealed class Slow(Server server) : Stream
        {
            private long _at;

            public override bool CanRead => true;

            public override bool CanSeek => true;

            public override bool CanWrite => false;

            public override long Length => server.Bytes.Length;

            public override long Position { get => _at; set => _at = value; }

            public override long Seek(long offset, SeekOrigin origin)
            {
                _at = offset;
                server.LastSeek = offset;

                return _at;
            }

            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                await Task.Delay(20, cancellationToken);

                if (_at >= server.FailAfter)
                {
                    throw new IOException("the link dropped");
                }

                int count = (int)Math.Min(Math.Min(buffer.Length, 32 * 1024), server.Bytes.Length - _at);

                server.Bytes.AsMemory((int)_at, count).CopyTo(buffer);
                _at += count;

                return count;
            }

            public override int Read(byte[] buffer, int offset, int count) =>
                ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

            public override void Flush()
            {
            }

            public override void SetLength(long value) => throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}
