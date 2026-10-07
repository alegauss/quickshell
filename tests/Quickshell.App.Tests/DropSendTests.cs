using System.IO;
using System.Runtime.CompilerServices;
using Quickshell.App;
using Quickshell.Transport;
using Xunit;

namespace Quickshell.App.Tests;

/// <summary>
/// QS189: files dropped onto an SSH terminal with Shift held are sent into the directory the shell
/// reported, and nothing is sent to a guess.
/// </summary>
public sealed class DropSendTests : IDisposable
{
    private readonly string _here = Path.Combine(Path.GetTempPath(), "qs189-" + Guid.NewGuid().ToString("N"));

    public DropSendTests() => Directory.CreateDirectory(_here);

    private static CancellationToken Stop => TestContext.Current.CancellationToken;

    /// <inheritdoc/>
    public void Dispose() => Directory.Delete(_here, recursive: true);

    /// <summary>
    /// The falsification: a file dropped with Shift onto an SSH pane whose shell reported its
    /// directory arrives in that directory, over the pane's own connection.
    /// </summary>
    [Fact]
    public async Task ADroppedFileArrivesWhereTheShellIs()
    {
        string file = Path.Combine(_here, "release notes.txt");

        await File.WriteAllTextAsync(file, "v2\n", Stop);

        Server server = new();
        Connection connection = new(server);

        string said = await DropSend.SendAsync(connection, "file://web1/srv/app", "web1.example.com", [file], Stop);

        Assert.Equal("Sent 1 file to /srv/app.", said);
        Assert.Equal("v2\n"u8.ToArray(), server.Files["/srv/app/release notes.txt"]);
        Assert.Equal(1, connection.Opened);
        Assert.True(server.Closed, "the drop's file channel was left open");
    }

    /// <summary>A name already on the server is left alone, and the sentence says so.</summary>
    [Fact]
    public async Task ATakenNameIsLeftAlone()
    {
        string file = Path.Combine(_here, "app.conf");

        await File.WriteAllTextAsync(file, "mine\n", Stop);

        Server server = new();

        server.Files["/srv/app/app.conf"] = "theirs\n"u8.ToArray();

        string said = await DropSend.SendAsync(new Connection(server), "file://web1/srv/app", "web1", [file], Stop);

        Assert.Equal("Sent 0 files to /srv/app; 1 already there, left alone.", said);
        Assert.Equal("theirs\n"u8.ToArray(), server.Files["/srv/app/app.conf"]);
    }

    /// <summary>
    /// Nothing reported, another machine reported, or no SSH session at all: nothing is sent, the
    /// connection is not even asked for a channel, and the sentence says why.
    /// </summary>
    [Theory]
    [InlineData("", "the shell has not said which directory it is in")]
    [InlineData("file://db2/var/lib", "the shell has not said which directory it is in")]
    public async Task NoReportedDirectorySendsNothing(string reported, string why)
    {
        Server server = new();
        Connection connection = new(server);

        string said = await DropSend.SendAsync(connection, reported, "web1", [Path.Combine(_here, "x")], Stop);

        Assert.Contains(why, said, StringComparison.Ordinal);
        Assert.Equal(0, connection.Opened);
        Assert.Empty(server.Files);
    }

    /// <summary>A local pane, through the window: the title says nothing was sent and why.</summary>
    [Fact]
    public void ALocalPaneSaysItHasNowhereToSend()
    {
        (string said, string? notice) = OnSta(() =>
        {
            MainWindow window = new();
            TerminalTab tab = TerminalTab.Open(Settings.Default, Shared, "cmd.exe");

            window.Add(tab);

            Task<string> sending = window.SendDropped(tab.Focused, [Path.Combine(_here, "x")]);

            return (sending.GetAwaiter().GetResult(), window.Notice);
        });

        Assert.StartsWith("Nothing was sent: this pane is not an SSH session", said, StringComparison.Ordinal);
        Assert.Equal(said, notice);
    }

    private static readonly TerminalShare Shared = new();

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

        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "the STA thread never finished");

        if (failed is not null)
        {
            throw new InvalidOperationException("the window could not be built", failed);
        }

        return result;
    }

    /// <summary>An SSH connection whose only use here is the file channel it opens.</summary>
    private sealed class Connection(Server server) : ISshTransport
    {
        public int Opened { get; private set; }

        public ValueTask<IFileTransferChannel> OpenFileTransferAsync(CancellationToken cancellationToken = default)
        {
            Opened++;

            return ValueTask.FromResult<IFileTransferChannel>(server);
        }

        public bool IsConnected => true;

        public SshEndpoint Endpoint => SshEndpoint.For("web1.example.com", "deploy", SshEndpoint.DefaultPort);

        public TimeSpan KeepAlive { get; set; }

        public TimeSpan Timeout { get; set; }

        public IProgress<SshSignInStep>? SignIn { get; set; }

        public Task<SshException?> Disconnected { get; } = new TaskCompletionSource<SshException?>().Task;

        public ValueTask ConnectAsync(SshEndpoint endpoint, IReadOnlyList<SshCredential> credentials,
                                      SshHostKeyCheck? hostKey = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<IPtyChannel> OpenShellAsync(int columns, int rows, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<IForwardedChannel> OpenForwardAsync(string host, int port,
                                                             CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>A server's files in memory, written the way the queue writes an upload.</summary>
    private sealed class Server : IFileTransferChannel
    {
        public Dictionary<string, byte[]> Files { get; } = [];

        public bool Closed { get; private set; }

        public int ProtocolVersion => 3;

        public string WorkingDirectory => "/home/deploy";

        public ValueTask<RemoteEntry> StatAsync(string path, CancellationToken cancellationToken = default) =>
            Files.TryGetValue(path, out byte[]? bytes)
                ? ValueTask.FromResult(new RemoteEntry(path, bytes.Length, false, DateTimeOffset.UnixEpoch, "-rw-r--r--"))
                : throw new SshException(SshFailureKind.Dropped, $"{path}: no such file");

        public ValueTask<Stream> OpenWriteAsync(string path, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<Stream>(new Landing(this, path));

        public ValueTask DeleteAsync(string path, CancellationToken cancellationToken = default) =>
            Files.Remove(path) ? ValueTask.CompletedTask : throw new SshException(SshFailureKind.Dropped, "no such file");

        public ValueTask RenameAsync(string from, string to, CancellationToken cancellationToken = default)
        {
            Files[to] = Files[from];
            Files.Remove(from);

            return ValueTask.CompletedTask;
        }

        public ValueTask SetLastWriteTimeAsync(string path, DateTimeOffset when,
                                               CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public async IAsyncEnumerable<RemoteEntry> ListAsync(
            string path, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;

            yield break;
        }

        public ValueTask CreateDirectoryAsync(string path, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask<Stream> OpenReadAsync(string path, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<Stream> OpenWriteAtAsync(string path, long at, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask SymbolicLinkAsync(string target, string link, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<string> ReadLinkAsync(string path, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask ChangePermissionsAsync(string path, int mode, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask DownloadAsync(string path, Stream into, IProgress<long>? progress = null,
                                       CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask UploadAsync(Stream from, string path, IProgress<long>? progress = null,
                                     CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask DisposeAsync()
        {
            Closed = true;

            return ValueTask.CompletedTask;
        }

        private sealed class Landing(Server server, string path) : MemoryStream
        {
            public override ValueTask DisposeAsync()
            {
                server.Files[path] = ToArray();

                return base.DisposeAsync();
            }
        }
    }
}
