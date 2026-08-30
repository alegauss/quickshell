namespace Quickshell.Transport;

/// <summary>Which protocol is actually moving the bytes.</summary>
public enum TransferProtocol
{
    /// <summary>A subsystem channel on this session: listings, offsets, real errors.</summary>
    Sftp,

    /// <summary>A remote command over an exec channel, kept for hosts offering nothing else.</summary>
    Scp,
}

/// <summary>
/// The best way this server will move a file, and what that costs.
///
/// <para><b>The fallback announces itself.</b> A client that quietly dropped to SCP would leave a
/// user wondering why the file pane went empty, why a transfer that stopped restarted from zero, and
/// why the progress bar lied — three symptoms with one cause that nothing on screen mentioned.</para>
///
/// <para><b><see cref="Browsing"/> is null under SCP, and that is the design rather than an
/// oversight.</b> The design says the browser does not run on top of SCP, because a browser needs a
/// listing and this protocol has none. Making it null means that cannot be forgotten: there is no
/// listing to call. The usual way round it — parsing the output of a remote <c>ls</c> — is exactly
/// where this protocol's injection flaws live.</para>
///
/// <para><b>This is not on the seam.</b> Exactly three kinds of channel cross <c>ISshTransport</c>,
/// and scp needs a fourth — it runs a command on the far side, over an exec channel the seam does
/// not carry. Widening the seam to admit it would spend QS36's central constraint on a fallback for
/// old appliances, so instead this is offered by the transports that can run a command, and a caller
/// asking for it is knowingly leaving the seam behind.</para>
/// </summary>
public interface IFileCopy : IAsyncDisposable
{
    /// <summary>What is carrying the bytes.</summary>
    TransferProtocol Protocol { get; }

    /// <summary>Whether a directory can be listed. False under SCP.</summary>
    bool CanList { get; }

    /// <summary>Whether an interrupted transfer can continue. False under SCP.</summary>
    bool CanResume { get; }

    /// <summary>Whether progress is counted rather than guessed. False under SCP.</summary>
    bool CanMeasureProgress { get; }

    /// <summary>
    /// What to tell the user, in their words. Empty where there is nothing to say, which is the
    /// ordinary case.
    /// </summary>
    string Announcement { get; }

    /// <summary>
    /// The full channel, for listing and everything else. Null where the protocol cannot do it.
    /// </summary>
    IFileTransferChannel? Browsing { get; }

    /// <summary>Sends one file.</summary>
    /// <param name="from">What to read.</param>
    /// <param name="length">How many bytes, which SCP must be told in advance.</param>
    /// <param name="to">Where it goes on the server.</param>
    /// <param name="progress">Told how many bytes have gone.</param>
    /// <param name="cancellationToken">Abandons the transfer.</param>
    ValueTask SendAsync(Stream from, long length, string to, IProgress<long>? progress = null,
                        CancellationToken cancellationToken = default);

    /// <summary>Sends a whole directory, contents and all.</summary>
    /// <param name="from">The local directory.</param>
    /// <param name="to">The directory on the server it goes into.</param>
    /// <param name="progress">Told how many bytes have gone, across every file.</param>
    /// <param name="cancellationToken">Abandons the transfer.</param>
    ValueTask SendDirectoryAsync(string from, string to, IProgress<long>? progress = null,
                                 CancellationToken cancellationToken = default);

    /// <summary>Receives one file.</summary>
    /// <param name="path">What to read on the server.</param>
    /// <param name="into">Where the bytes go.</param>
    /// <param name="progress">Told how many bytes have arrived.</param>
    /// <param name="cancellationToken">Abandons the transfer.</param>
    ValueTask ReceiveAsync(string path, Stream into, IProgress<long>? progress = null,
                           CancellationToken cancellationToken = default);
}

/// <summary>The ordinary case: a full channel, with nothing to warn anybody about.</summary>
internal sealed class SftpFileCopy(IFileTransferChannel over) : IFileCopy
{
    public TransferProtocol Protocol => TransferProtocol.Sftp;

    public bool CanList => true;

    public bool CanResume => true;

    public bool CanMeasureProgress => true;

    public string Announcement => string.Empty;

    public IFileTransferChannel? Browsing => over;

    public ValueTask SendAsync(Stream from, long length, string to, IProgress<long>? progress = null,
                               CancellationToken cancellationToken = default) =>
        over.UploadAsync(from, to, progress, cancellationToken);

    public ValueTask ReceiveAsync(string path, Stream into, IProgress<long>? progress = null,
                                  CancellationToken cancellationToken = default) =>
        over.DownloadAsync(path, into, progress, cancellationToken);

    /// <summary>
    /// The walk QS62 built, which knows about empty directories, links and collisions — none of
    /// which the scp side can answer.
    /// </summary>
    public async ValueTask SendDirectoryAsync(string from, string to,
                                              IProgress<long>? progress = null,
                                              CancellationToken cancellationToken = default)
    {
        TransferPlan plan = TransferPlan.ToCopyUp(from, $"{to.TrimEnd('/')}/{new DirectoryInfo(from).Name}");

        TransferQueue queue = new(over);

        await plan.EnqueueAsync(queue, over, cancellationToken).ConfigureAwait(false);

        await queue.RunAsync(cancellationToken).ConfigureAwait(false);

        progress?.Report(queue.MovedBytes);
    }

    public ValueTask DisposeAsync() => over.DisposeAsync();
}

/// <summary>The fallback, saying what it is and what it cannot do.</summary>
internal sealed class ScpFileCopy(ScpChannel over, string because) : IFileCopy
{
    public TransferProtocol Protocol => TransferProtocol.Scp;

    public bool CanList => false;

    public bool CanResume => false;

    public bool CanMeasureProgress => false;

    public string Announcement =>
        $"This host offers no SFTP subsystem, so files are moved with scp instead ({because}). "
        + "Directories cannot be browsed, an interrupted transfer starts again from the "
        + "beginning, and progress is an estimate.";

    public IFileTransferChannel? Browsing => null;

    public ValueTask SendAsync(Stream from, long length, string to, IProgress<long>? progress = null,
                               CancellationToken cancellationToken = default) =>
        over.SendAsync(from, length, to, progress: progress, cancellationToken: cancellationToken);

    public ValueTask ReceiveAsync(string path, Stream into, IProgress<long>? progress = null,
                                  CancellationToken cancellationToken = default) =>
        over.ReceiveAsync(path, into, progress, cancellationToken);

    public ValueTask SendDirectoryAsync(string from, string to, IProgress<long>? progress = null,
                                        CancellationToken cancellationToken = default) =>
        over.SendDirectoryAsync(from, to, progress, cancellationToken);

    public ValueTask DisposeAsync() => over.DisposeAsync();
}
