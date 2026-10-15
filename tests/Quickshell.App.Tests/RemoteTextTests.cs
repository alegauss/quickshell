using System.IO;
using System.Runtime.CompilerServices;
using System.Text;

using Quickshell.Transport;

using Xunit;

namespace Quickshell.App.Tests;

/// <summary>
/// QS326: a remote text file read with its line numbers and edited by one exact replacement, with
/// the original kept beside it, the diff returned, and no moment at which the file is half written.
/// The channel here keeps SFTP version 3's rule that a rename does not replace a file that exists.
/// </summary>
public sealed class RemoteTextTests
{
    private const string Config = "/etc/app/app.conf";

    private static CancellationToken Stop => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Now = new(2026, 10, 15, 22, 30, 5, TimeSpan.Zero);

    /// <summary>The lines asked for, each led by its number, and how many the file has.</summary>
    [Fact]
    public async Task ARangeIsReadWithItsLineNumbers()
    {
        Channel server = new();

        server.Put(Config, "one\ntwo\nthree\nfour\n");

        RemoteRead read = await RemoteText.ReadAsync(server, Config, 2, 3, Stop);

        Assert.Equal("2\ttwo\n3\tthree\n", read.Text);
        Assert.Equal(4, read.Lines);
    }

    /// <summary>A file holding a NUL is binary, and is refused rather than shown as text.</summary>
    [Fact]
    public async Task ABinaryFileIsRefused()
    {
        Channel server = new();

        server.Put("/bin/tool", "ELF\0\u0001");

        RemoteTextRefusal refused = await Assert.ThrowsAsync<RemoteTextRefusal>(() => RemoteText.ReadAsync(server, "/bin/tool", null, null, Stop));

        Assert.Contains("binary", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A replacement holding quotes and ending in a newline lands exactly; the original is the backup,
    /// byte for byte and with its mode; the edited file keeps that mode; nothing temporary is left;
    /// and the diff says what changed with the lines around it.
    /// </summary>
    [Fact]
    public async Task AReplacementLandsAndTheOriginalIsTheBackup()
    {
        Channel server = new();
        const string original = "# app\nlisten = 80\nname = \"web\"\nworkers = 2\nend\n";

        server.Put(Config, original, "-rw-r-----");

        RemoteTextEdit edit = await RemoteText.EditAsync(server, Config, "name = \"web\"\n", "name = \"it's \\\"web\\\"\"\nalias = 'w'\n", Now, Stop);

        Assert.Equal("# app\nlisten = 80\nname = \"it's \\\"web\\\"\"\nalias = 'w'\nworkers = 2\nend\n", server.Text(Config));
        Assert.Equal("/etc/app/app.conf.qs-bak-20261015T223005Z", edit.Backup);
        Assert.Equal(original, server.Text(edit.Backup));
        Assert.Equal("-rw-r-----", server.Mode(Config));
        Assert.Equal([Config, edit.Backup], server.Paths.Order(StringComparer.Ordinal));

        Assert.Equal("""
            --- /etc/app/app.conf
            +++ /etc/app/app.conf
            @@ -1,5 +1,6 @@
             # app
             listen = 80
            -name = "web"
            +name = "it's \"web\""
            +alias = 'w'
             workers = 2
             end

            """.ReplaceLineEndings("\n"), edit.Diff);
    }

    /// <summary>An old string that occurs twice names no one place, and nothing is written.</summary>
    [Fact]
    public async Task AnOldStringThatOccursTwiceChangesNothing()
    {
        Channel server = new();

        server.Put(Config, "port = 80\nport = 80\n");

        RemoteTextRefusal refused = await Assert.ThrowsAsync<RemoteTextRefusal>(
            () => RemoteText.EditAsync(server, Config, "port = 80", "port = 8080", Now, Stop));

        Assert.Contains("occurs 2 times", refused.Message, StringComparison.Ordinal);
        Assert.Equal("port = 80\nport = 80\n", server.Text(Config));
        Assert.Equal([Config], server.Paths);
    }

    /// <summary>
    /// The link cut while the new text is being written: the file is as it was, under its own name,
    /// with no backup taken and nothing half written in its place.
    /// </summary>
    [Fact]
    public async Task ALinkCutMidWriteLeavesTheFileAsItWas()
    {
        Channel server = new() { CutDuringUpload = true };

        server.Put(Config, "listen = 80\n");

        await Assert.ThrowsAsync<SshException>(() => RemoteText.EditAsync(server, Config, "80", "8080", Now, Stop));

        Assert.Equal("listen = 80\n", server.Text(Config));
        Assert.DoesNotContain(server.Paths, path => path.Contains(".qs-bak-", StringComparison.Ordinal));
    }

    /// <summary>
    /// The last rename refused: the original is moved back where it was, so a failed edit is never a
    /// missing file.
    /// </summary>
    [Fact]
    public async Task ALastRenameRefusedPutsTheOriginalBack()
    {
        Channel server = new() { RefuseRenameOnto = Config };

        server.Put(Config, "listen = 80\n");

        await Assert.ThrowsAsync<SshException>(() => RemoteText.EditAsync(server, Config, "80", "8080", Now, Stop));

        Assert.Equal("listen = 80\n", server.Text(Config));
        Assert.Equal([Config], server.Paths);
    }

    /// <summary>An in-memory server with SFTP v3's rename, which refuses to replace a file that exists.</summary>
    private sealed class Channel : IFileTransferChannel
    {
        private readonly Dictionary<string, (byte[] Bytes, string Mode)> _files = new(StringComparer.Ordinal);

        public bool CutDuringUpload { get; init; }

        public string? RefuseRenameOnto { get; init; }

        public IEnumerable<string> Paths => _files.Keys;

        public int ProtocolVersion => 3;

        public string WorkingDirectory => "/";

        public void Put(string path, string text, string mode = "-rw-r--r--") => _files[path] = (Encoding.UTF8.GetBytes(text), mode);

        public string Text(string path) => Encoding.UTF8.GetString(_files[path].Bytes);

        public string Mode(string path) => _files[path].Mode;

        public ValueTask<RemoteEntry> StatAsync(string path, CancellationToken cancellationToken = default) =>
            _files.TryGetValue(path, out (byte[] Bytes, string Mode) file)
                ? ValueTask.FromResult(new RemoteEntry(path[(path.LastIndexOf('/') + 1)..], file.Bytes.Length, false, DateTimeOffset.UnixEpoch, file.Mode))
                : throw new SshException(SshFailureKind.ShellRefused, $"No such file {path}.");

        public async ValueTask DownloadAsync(string path, Stream into, IProgress<long>? progress = null, CancellationToken cancellationToken = default) =>
            await into.WriteAsync(_files[path].Bytes, cancellationToken);

        public async ValueTask UploadAsync(Stream from, string path, IProgress<long>? progress = null, CancellationToken cancellationToken = default)
        {
            using MemoryStream copy = new();

            await from.CopyToAsync(copy, cancellationToken);

            byte[] bytes = copy.ToArray();

            if (CutDuringUpload)
            {
                // Half of it reached the server before the link went.
                _files[path] = (bytes[..(bytes.Length / 2)], "-rw-r--r--");

                throw new SshException(SshFailureKind.Dropped, "The connection dropped.");
            }

            _files[path] = (bytes, "-rw-r--r--");
        }

        public ValueTask ChangePermissionsAsync(string path, int mode, CancellationToken cancellationToken = default)
        {
            string bits = string.Concat(Enumerable.Range(0, 9).Select(bit => (mode >> (8 - bit) & 1) == 1 ? "rwx"[bit % 3] : '-'));

            _files[path] = (_files[path].Bytes, "-" + bits);

            return ValueTask.CompletedTask;
        }

        public ValueTask RenameAsync(string from, string to, CancellationToken cancellationToken = default)
        {
            if (_files.ContainsKey(to))
            {
                throw new SshException(SshFailureKind.ShellRefused, $"{to} exists, and version 3 does not replace it.");
            }

            if (to == RefuseRenameOnto && from.Contains(".qs-new-", StringComparison.Ordinal))
            {
                throw new SshException(SshFailureKind.ShellRefused, "Permission denied.");
            }

            _files[to] = _files[from];
            _files.Remove(from);

            return ValueTask.CompletedTask;
        }

        public ValueTask DeleteAsync(string path, CancellationToken cancellationToken = default)
        {
            _files.Remove(path);

            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public async IAsyncEnumerable<RemoteEntry> ListAsync(string path, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;

            yield break;
        }

        public ValueTask<Stream> OpenReadAsync(string path, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask<Stream> OpenWriteAsync(string path, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask<Stream> OpenWriteAtAsync(string path, long at, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask CreateDirectoryAsync(string path, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask SymbolicLinkAsync(string target, string link, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask<string> ReadLinkAsync(string path, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask SetLastWriteTimeAsync(string path, DateTimeOffset when, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
