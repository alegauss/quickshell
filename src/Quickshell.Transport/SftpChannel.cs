using System.Globalization;
using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Sftp;

namespace Quickshell.Transport;

/// <summary>
/// A file transfer running as a second channel of a session that is already open.
///
/// <para><b>Not a second connection, which is the whole point.</b> Most clients open a fresh
/// connection for the file browser, and it costs the user another password, another second factor
/// and another entry in the server's auth log — to reach a machine they are already logged in to.
/// Here the shell and the files are two channels of one session, and closing the session closes
/// both.</para>
///
/// <para><b>Paths belong to the server.</b> Nothing here normalises a separator, folds a case or
/// rejects a character. A remote filesystem is case-sensitive, permits names Windows will not, and
/// has its own length limits — and a client that tidied a name would rename the user's file. Paths
/// go out as they were given and come back as the server spells them.</para>
///
/// <para><b>Moving a file and reading one are different members on purpose.</b> A stream costs a
/// round trip per block, which over a link with latency runs at a fraction of the bandwidth
/// available and is universally misdiagnosed as a network problem. <see cref="DownloadAsync"/> and
/// <see cref="UploadAsync"/> keep several requests in flight and are what a transfer should
/// use.</para>
///
/// <para><b>Internal, because opening one takes an SSH.NET client.</b> Callers reach it through
/// <see cref="ISshTransport.OpenFileTransferAsync"/> and hold it as an
/// <see cref="IFileTransferChannel"/>, so no library type crosses the seam QS36 drew — which
/// <c>SeamTests</c> checks rather than trusts.</para>
/// </summary>
internal sealed class SftpChannel : IFileTransferChannel
{
    private readonly SftpClient _client;
    private readonly IDisposable _session;

    private bool _disposed;

    private SftpChannel(SftpClient client, IDisposable session)
    {
        _client = client;
        _session = session;
    }

    /// <inheritdoc/>
    public int ProtocolVersion { get; private set; }

    /// <inheritdoc/>
    public string WorkingDirectory => _client.WorkingDirectory;

    /// <summary>
    /// Opens a file transfer as a channel of a connection that is already authenticated.
    /// </summary>
    /// <param name="over">The connected client whose session carries it.</param>
    /// <param name="timeout">How long one operation may take.</param>
    /// <exception cref="SshException">The channel could not be opened on this session.</exception>
    internal static ValueTask<IFileTransferChannel> OpenAsync(SshClient over, TimeSpan timeout)
    {
        (SftpClient client, IDisposable session) = SharedSftpSession.OpenOn(over, timeout);

        SftpChannel channel = new(client, session)
        {
            ProtocolVersion = Version(session),
        };

        return ValueTask.FromResult<IFileTransferChannel>(channel);
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<RemoteEntry> ListAsync(
        string path,
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken cancellationToken = default)
    {
        Live();

        // Streamed and not gathered: a home directory on a shared box can be fifty thousand entries,
        // and a browser that waits for the last one before showing the first is a browser that hangs.
        await foreach (ISftpFile entry in _client.ListDirectoryAsync(path, cancellationToken)
                                                 .ConfigureAwait(false))
        {
            yield return Describe(entry);
        }
    }

    /// <inheritdoc/>
    public async ValueTask<RemoteEntry> StatAsync(string path,
                                                  CancellationToken cancellationToken = default)
    {
        Live();

        return await Translated(async () =>
            Describe(await _client.GetAsync(path, cancellationToken).ConfigureAwait(false)),
            path).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async ValueTask<Stream> OpenReadAsync(string path,
                                                 CancellationToken cancellationToken = default)
    {
        Live();

        return await Translated(
            async () => await _client.OpenAsync(path, FileMode.Open, FileAccess.Read,
                                                cancellationToken).ConfigureAwait(false),
            path).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async ValueTask<Stream> OpenWriteAsync(string path,
                                                  CancellationToken cancellationToken = default)
    {
        Live();

        return await Translated(
            async () => await _client.OpenAsync(path, FileMode.Create, FileAccess.Write,
                                                cancellationToken).ConfigureAwait(false),
            path).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async ValueTask DownloadAsync(string path, Stream into, IProgress<long>? progress = null,
                                         CancellationToken cancellationToken = default)
    {
        Live();

        ArgumentNullException.ThrowIfNull(into);

        await Translated(async () =>
        {
            await _client.DownloadFileAsync(path, into, Downloaded(progress), cancellationToken)
                         .ConfigureAwait(false);

            return true;
        }, path).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async ValueTask UploadAsync(Stream from, string path, IProgress<long>? progress = null,
                                       CancellationToken cancellationToken = default)
    {
        Live();

        ArgumentNullException.ThrowIfNull(from);

        await Translated(async () =>
        {
            await _client.UploadFileAsync(from, path, Uploaded(progress), cancellationToken)
                         .ConfigureAwait(false);

            return true;
        }, path).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async ValueTask DeleteAsync(string path, CancellationToken cancellationToken = default)
    {
        Live();

        await Translated(async () =>
        {
            bool gone;

            try
            {
                // The path as given, not as the server would resolve it: see
                // SharedSftpSession.RemoveAsync for the file this otherwise destroys.
                await SharedSftpSession.RemoveAsync(_session, path, cancellationToken)
                                       .ConfigureAwait(false);

                gone = true;
            }
            catch (Renci.SshNet.Common.SshException)
            {
                gone = false;
            }

            if (!gone)
            {
                // Remove refuses a directory, so a directory costs one extra round trip and the
                // ordinary case costs none.
                await _client.DeleteDirectoryAsync(path, cancellationToken).ConfigureAwait(false);
            }

            return true;
        }, path).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async ValueTask RenameAsync(string from, string to,
                                       CancellationToken cancellationToken = default)
    {
        Live();

        await Translated(async () =>
        {
            await SharedSftpSession.RenameAsync(_session, from, to, cancellationToken)
                                   .ConfigureAwait(false);

            return true;
        }, from).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async ValueTask CreateDirectoryAsync(string path,
                                                CancellationToken cancellationToken = default)
    {
        Live();

        await Translated(async () =>
        {
            await _client.CreateDirectoryAsync(path, cancellationToken).ConfigureAwait(false);

            return true;
        }, path).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async ValueTask SymbolicLinkAsync(string target, string link,
                                             CancellationToken cancellationToken = default)
    {
        Live();

        // SSH.NET has no async spelling of this one, so it is put on the pool rather than run on the
        // caller's thread: every other member here yields, and one that did not would be the one
        // that froze a window.
        await Translated(
            () => Task.Run(() => { _client.SymbolicLink(target, link); return true; },
                           cancellationToken),
            link).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async ValueTask ChangePermissionsAsync(string path, int mode,
                                                  CancellationToken cancellationToken = default)
    {
        Live();

        ArgumentOutOfRangeException.ThrowIfNegative(mode);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(mode, 0x1FF);

        // SSH.NET's ChangePermissions does not take a mode: it takes the three octal digits read as
        // a decimal number, so 0o644 must be handed over as the number 644. Above this seam a mode
        // is a mode, because a caller passing 420 and getting -r---w---- would be right and the API
        // would be wrong.
        short digits = (short)(((mode >> 6) & 7) * 100 + ((mode >> 3) & 7) * 10 + (mode & 7));

        await Translated(
            () => Task.Run(() => { _client.ChangePermissions(path, digits); return true; },
                           cancellationToken),
            path).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async ValueTask SetLastWriteTimeAsync(string path, DateTimeOffset when,
                                                 CancellationToken cancellationToken = default)
    {
        Live();

        await Translated(
            () => Task.Run(() =>
            {
                _client.SetLastWriteTimeUtc(path, when.UtcDateTime);

                return true;
            }, cancellationToken),
            path).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // The channel and not the connection: the session belongs to whoever opened it, and the
        // shell on it carries on. Closing that session is what closes this.
        _session.Dispose();

        await ValueTask.CompletedTask.ConfigureAwait(false);
    }

    /// <summary>
    /// One entry as a file pane reads it, with the mode spelled the way <c>ls</c> spells it.
    /// </summary>
    private static RemoteEntry Describe(ISftpFile entry) =>
        new(entry.Name,
            entry.IsDirectory ? 0 : entry.Length,
            entry.IsDirectory,
            new DateTimeOffset(DateTime.SpecifyKind(entry.LastWriteTimeUtc, DateTimeKind.Utc)),
            Mode(entry));

    private static string Mode(ISftpFile entry)
    {
        SftpFileAttributes what = entry.Attributes;

        char kind = what.IsDirectory ? 'd' : what.IsSymbolicLink ? 'l' : '-';

        return string.Create(CultureInfo.InvariantCulture,
                             $"{kind}{Bit(what.OwnerCanRead, 'r')}{Bit(what.OwnerCanWrite, 'w')}"
                             + $"{Bit(what.OwnerCanExecute, 'x')}{Bit(what.GroupCanRead, 'r')}"
                             + $"{Bit(what.GroupCanWrite, 'w')}{Bit(what.GroupCanExecute, 'x')}"
                             + $"{Bit(what.OthersCanRead, 'r')}{Bit(what.OthersCanWrite, 'w')}"
                             + $"{Bit(what.OthersCanExecute, 'x')}");

        static char Bit(bool set, char letter) => set ? letter : '-';
    }

    private static int Version(IDisposable session) =>
        session.GetType()
               .GetProperty("ProtocolVersion",
                            System.Reflection.BindingFlags.Public
                            | System.Reflection.BindingFlags.NonPublic
                            | System.Reflection.BindingFlags.Instance)
               ?.GetValue(session) is uint version
            ? (int)version
            : 0;

    private static CountingDownload? Downloaded(IProgress<long>? progress) =>
        progress is null ? null : new CountingDownload(progress);

    private static CountingUpload? Uploaded(IProgress<long>? progress) =>
        progress is null ? null : new CountingUpload(progress);

    /// <summary>
    /// The library's failures, said in this client's words and with the path that caused them.
    ///
    /// <para>A file pane showing "Failure" — which is what the protocol's own status code is called
    /// — tells a user nothing. The path is what they need, because the usual cause is that it is not
    /// the path they meant.</para>
    /// </summary>
    private static async Task<T> Translated<T>(Func<Task<T>> work, string path)
    {
        try
        {
            return await work().ConfigureAwait(false);
        }
        catch (SftpPathNotFoundException missing)
        {
            throw new SshException(SshFailureKind.Unrecognised,
                                   $"There is nothing at {path} on the server.",
                                   "The path was not found as it was spelled.",
                                   "Remote paths are the server's: they are case-sensitive and use "
                                   + "forward slashes.",
                                   missing.Message);
        }
        catch (SftpPermissionDeniedException refused)
        {
            throw new SshException(SshFailureKind.Refused,
                                   $"The server refused access to {path}.",
                                   "The account this session logged in as may not do that here.",
                                   string.Empty,
                                   refused.Message);
        }
        catch (SshException)
        {
            throw;
        }
        catch (Renci.SshNet.Common.SshException other)
        {
            throw new SshException(SshFailureKind.Unrecognised,
                                   $"The file transfer failed on {path}.",
                                   other.Message,
                                   string.Empty,
                                   other.Message);
        }
    }

    private void Live()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    /// <summary>
    /// Bytes moved, as the library counts them and as this client's callers expect them.
    ///
    /// <para>Two classes and not one because the library reports downloads and uploads through two
    /// unrelated types, and above the seam a byte is a byte.</para>
    /// </summary>
    private sealed class CountingDownload(IProgress<long> to) : IProgress<DownloadFileProgressReport>
    {
        public void Report(DownloadFileProgressReport value) =>
            to.Report((long)value.TotalBytesDownloaded);
    }

    /// <inheritdoc cref="CountingDownload"/>
    private sealed class CountingUpload(IProgress<long> to) : IProgress<UploadFileProgressReport>
    {
        public void Report(UploadFileProgressReport value) =>
            to.Report((long)value.TotalBytesUploaded);
    }
}
