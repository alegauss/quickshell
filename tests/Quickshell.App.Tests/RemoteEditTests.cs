using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using Quickshell.App;
using Quickshell.Transport;
using Xunit;

namespace Quickshell.App.Tests;

/// <summary>
/// QS185: a remote file opened in a local editor has each save sent back, never over a change made
/// on the server since without asking, and never leaving the server's file half-written.
/// </summary>
public sealed class RemoteEditTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "qs185-" + Guid.NewGuid().ToString("N"));

    private static CancellationToken Stop => TestContext.Current.CancellationToken;

    /// <inheritdoc/>
    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    /// <summary>
    /// The falsification's first half: a save in the local editor reaches the server — through the
    /// watcher, as an editor's save would — with the file's mode as it was and nothing left beside it.
    /// </summary>
    [Fact]
    public async Task ASaveInTheLocalEditorReachesTheServer()
    {
        Server server = new();

        server.Put("/etc/app/app.conf", "port = 80\n", "-rw-------");

        string? opened = null;

        await using RemoteEdits edits = new(server) { Root = _root, Opens = path => opened = path };

        RemoteEdit edit = await edits.EditAsync("/etc/app/app.conf", Stop);

        Assert.Equal(edit.Local, opened);
        Assert.Equal("app.conf", Path.GetFileName(edit.Local));
        Assert.Equal("port = 80\n", await File.ReadAllTextAsync(edit.Local, Stop));

        await File.WriteAllTextAsync(edit.Local, "port = 8080\n", Stop);

        await Until(() => server.Text("/etc/app/app.conf") == "port = 8080\n");

        Assert.Equal(Convert.ToInt32("600", 8), server.Mode("/etc/app/app.conf"));
        Assert.DoesNotContain("/etc/app/app.conf.qs-part", server.Paths);
        Assert.True(edit.Landed);
    }

    /// <summary>
    /// The falsification's second half: a file changed on the server since it was opened is asked
    /// about, and kept where the answer is to keep it.
    /// </summary>
    [Fact]
    public async Task AChangeOnTheServerIsAskedAboutBeforeItIsReplaced()
    {
        Server server = new();

        server.Put("/srv/site.conf", "a\n", "-rw-r--r--");

        await using RemoteEdits edits = new(server) { Root = _root, Opens = _ => { } };

        List<OverwriteQuestion> asked = [];
        bool replace = false;

        edits.AskingToOverwrite = question =>
        {
            asked.Add(question);

            return Task.FromResult(replace);
        };

        RemoteEdit edit = await edits.EditAsync("/srv/site.conf", Stop);

        // Somebody else, between the open and the save.
        server.Put("/srv/site.conf", "theirs\n", "-rw-r--r--", modified: DateTimeOffset.UnixEpoch.AddDays(2));

        await File.WriteAllTextAsync(edit.Local, "mine\n", Stop);

        Assert.Equal(SaveOutcome.Refused, await edit.SaveAsync(Stop));
        Assert.Equal("theirs\n", server.Text("/srv/site.conf"));
        Assert.Single(asked);
        Assert.Contains("changed on the server", asked[0].Asking, StringComparison.Ordinal);

        replace = true;

        Assert.Equal(SaveOutcome.Landed, await edit.SaveAsync(Stop));
        Assert.Equal("mine\n", server.Text("/srv/site.conf"));

        // And its own write is not somebody else's change: the next save is not asked about.
        await File.WriteAllTextAsync(edit.Local, "mine again\n", Stop);

        Assert.Equal(SaveOutcome.Landed, await edit.SaveAsync(Stop));
        Assert.Equal(2, asked.Count);
    }

    /// <summary>A link that drops halfway through a write-back leaves the server's file as it was.</summary>
    [Fact]
    public async Task AWriteBackThatFailsLeavesTheFileAsItWas()
    {
        Server server = new();

        server.Put("/srv/site.conf", "whole\n", "-rw-r--r--");

        await using RemoteEdits edits = new(server) { Root = _root, Opens = _ => { } };

        RemoteEdit edit = await edits.EditAsync("/srv/site.conf", Stop);
        List<string> told = [];

        edits.Telling = (note, failed) => told.Add((failed ? "failed: " : string.Empty) + note);

        await File.WriteAllTextAsync(edit.Local, "new\n", Stop);

        server.RenameFails = true;

        Assert.Equal(SaveOutcome.Failed, await edit.SaveAsync(Stop));
        Assert.Equal("whole\n", server.Text("/srv/site.conf"));
        Assert.StartsWith("failed: site.conf was not saved", told.Single(), StringComparison.Ordinal);
        Assert.False(edit.Landed);
    }

    /// <summary>A save while the previous write-back is still running waits for it rather than racing it.</summary>
    [Fact]
    public async Task SavesDoNotRaceEachOther()
    {
        Server server = new() { Slow = TimeSpan.FromMilliseconds(150) };

        server.Put("/srv/big.conf", "0\n", "-rw-r--r--");

        await using RemoteEdits edits = new(server) { Root = _root, Opens = _ => { } };

        RemoteEdit edit = await edits.EditAsync("/srv/big.conf", Stop);

        await File.WriteAllTextAsync(edit.Local, "1\n", Stop);

        Task<SaveOutcome> first = edit.SaveAsync(Stop);
        Task<SaveOutcome> second = edit.SaveAsync(Stop);

        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(10), Stop);

        Assert.Equal(1, server.MostWritingAtOnce);
        Assert.Equal(SaveOutcome.Landed, await first);
        Assert.Equal(SaveOutcome.Unchanged, await second);
        Assert.Equal("1\n", server.Text("/srv/big.conf"));
    }

    /// <summary>The copy goes when the session ends once it has landed, and stays where it has not.</summary>
    [Fact]
    public async Task TheCopyOutlivesOnlyEditsThatNeverLanded()
    {
        Server server = new();

        server.Put("/a.conf", "a\n", "-rw-r--r--");
        server.Put("/b.conf", "b\n", "-rw-r--r--");

        RemoteEdits edits = new(server) { Root = _root, Opens = _ => { } };

        RemoteEdit landed = await edits.EditAsync("/a.conf", Stop);
        RemoteEdit unsent = await edits.EditAsync("/b.conf", Stop);

        await File.WriteAllTextAsync(landed.Local, "a2\n", Stop);
        await landed.SaveAsync(Stop);

        server.WritesFail = true;

        await File.WriteAllTextAsync(unsent.Local, "b2\n", Stop);
        await unsent.SaveAsync(Stop);

        List<string> told = [];

        edits.Telling = (note, _) => told.Add(note);

        await edits.DisposeAsync();

        Assert.False(File.Exists(landed.Local));
        Assert.Equal("b2\n", await File.ReadAllTextAsync(unsent.Local, Stop));
        Assert.Contains(told, note => note.Contains(unsent.Local, StringComparison.Ordinal));
    }

    /// <summary>A file opened twice is opened from the one copy, not downloaded over its edits.</summary>
    [Fact]
    public async Task OpeningAgainKeepsTheEditsInTheCopy()
    {
        Server server = new();

        server.Put("/a.conf", "a\n", "-rw-r--r--");

        int opens = 0;

        await using RemoteEdits edits = new(server) { Root = _root, Opens = _ => opens++ };

        RemoteEdit first = await edits.EditAsync("/a.conf", Stop);

        server.WritesFail = true;
        await File.WriteAllTextAsync(first.Local, "unsaved\n", Stop);

        RemoteEdit again = await edits.EditAsync("/a.conf", Stop);

        Assert.Same(first, again);
        Assert.Equal(2, opens);
        Assert.Equal("unsaved\n", await File.ReadAllTextAsync(first.Local, Stop));

        server.WritesFail = false;
    }

    private static async Task Until(Func<bool> done)
    {
        DateTime until = DateTime.UtcNow + TimeSpan.FromSeconds(10);

        while (!done())
        {
            Assert.True(DateTime.UtcNow < until, "the save never reached the server");

            await Task.Delay(50, Stop);
        }
    }

    /// <summary>A server held in memory, keeping each file's bytes, mode and modification time.</summary>
    private sealed class Server : IFileTransferChannel
    {
        private readonly Dictionary<string, (byte[] Bytes, string Permissions, DateTimeOffset Modified)> _files = [];
        private readonly object _guard = new();
        private int _writing;
        private int _clock;

        public TimeSpan Slow { get; init; }

        public bool RenameFails { get; set; }

        public bool WritesFail { get; set; }

        public int MostWritingAtOnce { get; private set; }

        public IReadOnlyList<string> Paths
        {
            get
            {
                lock (_guard)
                {
                    return [.. _files.Keys];
                }
            }
        }

        public int ProtocolVersion => 3;

        public string WorkingDirectory => "/";

        public void Put(string path, string text, string permissions, DateTimeOffset? modified = null)
        {
            lock (_guard)
            {
                _files[path] = (Encoding.UTF8.GetBytes(text), permissions, modified ?? Tick());
            }
        }

        public string Text(string path)
        {
            lock (_guard)
            {
                return Encoding.UTF8.GetString(_files[path].Bytes);
            }
        }

        public int Mode(string path)
        {
            lock (_guard)
            {
                return Convert.ToInt32(BrowserActions.Octal(_files[path].Permissions), 8);
            }
        }

        public ValueTask<RemoteEntry> StatAsync(string path, CancellationToken cancellationToken = default)
        {
            lock (_guard)
            {
                if (!_files.TryGetValue(path, out var file))
                {
                    throw Missing(path);
                }

                return ValueTask.FromResult(new RemoteEntry(path, file.Bytes.Length, false, file.Modified, file.Permissions));
            }
        }

        public async ValueTask DownloadAsync(string path, Stream into, IProgress<long>? progress = null,
                                             CancellationToken cancellationToken = default)
        {
            byte[] bytes;

            lock (_guard)
            {
                bytes = _files[path].Bytes;
            }

            await into.WriteAsync(bytes, cancellationToken);
        }

        public ValueTask<Stream> OpenWriteAsync(string path, CancellationToken cancellationToken = default)
        {
            if (WritesFail)
            {
                throw new SshException(SshFailureKind.Dropped, "the link dropped");
            }

            return ValueTask.FromResult<Stream>(new Landing(this, path));
        }

        public ValueTask DeleteAsync(string path, CancellationToken cancellationToken = default)
        {
            lock (_guard)
            {
                if (!_files.Remove(path))
                {
                    throw Missing(path);
                }
            }

            return ValueTask.CompletedTask;
        }

        public ValueTask RenameAsync(string from, string to, CancellationToken cancellationToken = default)
        {
            // The move of the new bytes into place, which is the step a dropped link interrupts.
            if (RenameFails && from.EndsWith(".qs-part", StringComparison.Ordinal))
            {
                throw new SshException(SshFailureKind.Dropped, "the link dropped");
            }

            lock (_guard)
            {
                // SFTP version 3: a rename does not go over a name that exists.
                if (_files.ContainsKey(to))
                {
                    throw new SshException(SshFailureKind.Dropped, $"{to}: already exists");
                }

                if (!_files.TryGetValue(from, out var file))
                {
                    throw Missing(from);
                }

                _files.Remove(from);
                _files[to] = (file.Bytes, file.Permissions, file.Modified);
            }

            return ValueTask.CompletedTask;
        }

        public ValueTask ChangePermissionsAsync(string path, int mode, CancellationToken cancellationToken = default)
        {
            lock (_guard)
            {
                var file = _files[path];
                char[] spelled = "-rwxrwxrwx".ToCharArray();

                for (int bit = 0; bit < 9; bit++)
                {
                    if ((mode & (1 << (8 - bit))) == 0)
                    {
                        spelled[bit + 1] = '-';
                    }
                }

                _files[path] = (file.Bytes, new string(spelled), file.Modified);
            }

            return ValueTask.CompletedTask;
        }

        private DateTimeOffset Tick() => DateTimeOffset.UnixEpoch.AddMinutes(++_clock);

        // A fresh file, with the server's default mode and the time it was closed.
        private void Landed(string path, byte[] bytes)
        {
            lock (_guard)
            {
                _files[path] = (bytes, "-rw-r--r--", Tick());
            }
        }

        private static SshException Missing(string path) =>
            new(SshFailureKind.Dropped, $"{path}: no such file");

        public IAsyncEnumerable<RemoteEntry> ListAsync(string path, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<Stream> OpenReadAsync(string path, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<Stream> OpenWriteAtAsync(string path, long at, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask CreateDirectoryAsync(string path, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask SymbolicLinkAsync(string target, string link, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<string> ReadLinkAsync(string path, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask SetLastWriteTimeAsync(string path, DateTimeOffset when,
                                               CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask UploadAsync(Stream from, string path, IProgress<long>? progress = null,
                                     CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        /// <summary>The stream a write-back writes into, counted while open so a race would show.</summary>
        private sealed class Landing : MemoryStream
        {
            private readonly Server _server;
            private readonly string _path;

            public Landing(Server server, string path)
            {
                _server = server;
                _path = path;

                int now = Interlocked.Increment(ref server._writing);

                lock (server._guard)
                {
                    server.MostWritingAtOnce = Math.Max(server.MostWritingAtOnce, now);
                }
            }

            public override async ValueTask DisposeAsync()
            {
                if (_server.Slow > TimeSpan.Zero)
                {
                    await Task.Delay(_server.Slow);
                }

                _server.Landed(_path, ToArray());
                Interlocked.Decrement(ref _server._writing);

                await base.DisposeAsync();
            }
        }
    }
}
