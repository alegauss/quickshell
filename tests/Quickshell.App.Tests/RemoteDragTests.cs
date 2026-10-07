using System.IO;
using Quickshell.App;
using Quickshell.Transport;
using Xunit;

namespace Quickshell.App.Tests;

/// <summary>
/// QS188: files dragged out of the host's pane are offered as the shell's virtual files, read from
/// the server only as the drop reads them, on a thread that is not the window's.
/// </summary>
public sealed class RemoteDragTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "qs188-" + Guid.NewGuid().ToString("N"));

    public RemoteDragTests() => Directory.CreateDirectory(_folder);

    /// <inheritdoc/>
    public void Dispose() => Directory.Delete(_folder, recursive: true);

    /// <summary>
    /// The falsification, as far as a drop without a desk goes: what a drop target reads out of
    /// the dragged object through COM — across apartments, as Explorer's reads arrive — lands in a
    /// local folder byte for byte, inside the object's background operation.
    /// </summary>
    [Fact]
    public void WhatTheDropReadsLandsByteForByte()
    {
        Server server = new();

        server.Files["/srv/app.conf"] = "port = 80\n"u8.ToArray();
        server.Files["/srv/data.bin"] = [.. Enumerable.Range(0, 300_000).Select(at => (byte)(at * 7 % 253))];

        (string, FileItem)[] files =
        [
            ("/srv/app.conf", Item("app.conf", server.Files["/srv/app.conf"].Length)),
            ("/srv/data.bin", Item("data.bin", server.Files["/srv/data.bin"].Length)),
        ];

        IReadOnlyList<string> landed;
        bool operated;

        using (RemoteDrag.Served served = RemoteDrag.Serve(server, files))
        {
            landed = RemoteDrag.Extract(served.Proxy, _folder);
            operated = served.Operated;
        }

        Assert.Equal([Path.Combine(_folder, "app.conf"), Path.Combine(_folder, "data.bin")], landed);
        Assert.Equal(server.Files["/srv/app.conf"], File.ReadAllBytes(landed[0]));
        Assert.Equal(server.Files["/srv/data.bin"], File.ReadAllBytes(landed[1]));

        // Explorer copies after the drop returns only where the object offers that, through a
        // proxy whose interface Windows has to be able to marshal.
        Assert.True(operated, "the drop never started the object's background operation");

        // And the server was read on the drag's own thread, never the caller's.
        Assert.Equal(["Remote drag", "Remote drag"], server.ReadOn);
    }

    /// <summary>Nothing is read from the server until the drop asks for a file's bytes.</summary>
    [Fact]
    public void StartingADragReadsNothing()
    {
        Server server = new();

        server.Files["/srv/huge.iso"] = new byte[16];

        using (RemoteDrag.Served served = RemoteDrag.Serve(server, [("/srv/huge.iso", Item("huge.iso", 4L << 30))]))
        {
            Assert.NotEqual(0, served.Proxy);
        }

        Assert.Empty(server.ReadOn);
    }

    private static FileItem Item(string name, long length) =>
        new(name, length, false, new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero), "-rw-r--r--", false);

    /// <summary>A server holding a few files, which records the thread each read was opened on.</summary>
    private sealed class Server : IFileTransferChannel
    {
        private readonly List<string> _readOn = [];

        public Dictionary<string, byte[]> Files { get; } = [];

        public IReadOnlyList<string> ReadOn
        {
            get
            {
                lock (_readOn)
                {
                    return [.. _readOn];
                }
            }
        }

        public int ProtocolVersion => 3;

        public string WorkingDirectory => "/srv";

        public ValueTask<Stream> OpenReadAsync(string path, CancellationToken cancellationToken = default)
        {
            lock (_readOn)
            {
                _readOn.Add(Thread.CurrentThread.Name ?? "unnamed");
            }

            return ValueTask.FromResult<Stream>(new MemoryStream(Files[path], writable: false));
        }

        public IAsyncEnumerable<RemoteEntry> ListAsync(string path, CancellationToken cancellationToken = default) =>
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
    }
}
