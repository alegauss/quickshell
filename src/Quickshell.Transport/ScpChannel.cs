using System.Globalization;
using System.Text;
using Renci.SshNet;

namespace Quickshell.Transport;

/// <summary>
/// A path as a POSIX shell will read it, which for this protocol is the whole security question.
/// </summary>
public static class ShellWord
{
    /// <summary>
    /// Wraps a path so a shell treats every character in it as part of the name.
    ///
    /// <para><b>Single quotes, always, with no exception for names that look safe.</b> Inside single
    /// quotes a POSIX shell interprets nothing at all — no <c>$</c>, no backtick, no <c>;</c>, no
    /// glob — and the one character that ends the quoting is the quote itself, which is closed,
    /// escaped and reopened. An implementation that quoted only names containing something
    /// suspicious would be an implementation whose safety depends on a list of suspicious
    /// characters being complete, and that list is what this protocol's entire vulnerability class
    /// is made of.</para>
    /// </summary>
    /// <param name="word">Any path or argument.</param>
    /// <returns>The same word, safe to place in a command line.</returns>
    public static string Quote(string word)
    {
        ArgumentNullException.ThrowIfNull(word);

        return $"'{word.Replace("'", @"'\''", StringComparison.Ordinal)}'";
    }
}

/// <summary>
/// Moving a file with <c>scp</c>, kept for the host that offers nothing else.
///
/// <para><b>This is a fallback and says so.</b> SCP has no directory listing, no resume, no reliable
/// progress and a long history of filename-handling flaws — OpenSSH moved its own <c>scp</c> onto
/// SFTP for exactly those reasons. It is here for the embedded device, the network appliance and the
/// old server whose sshd offers no subsystem, where it is the difference between transferring a file
/// and not transferring one.</para>
///
/// <para><b>It is deliberately minimal: send and receive, and nothing else.</b> There is no listing
/// member and there will not be one. The usual way to fake a listing over SCP is to parse the output
/// of a remote <c>ls</c>, which is precisely where this protocol's injection flaws live.</para>
///
/// <para><b>Every path is quoted for the remote shell, without exception.</b> The remote side of an
/// SCP transfer is a command line, so a file name is code until it is quoted. See
/// <see cref="ShellWord.Quote"/>.</para>
/// </summary>
public sealed class ScpChannel : IAsyncDisposable
{
    private const byte Ok = 0;
    private const byte Warning = 1;
    private const byte Fatal = 2;

    private readonly SshClient _over;

    private bool _disposed;

    internal ScpChannel(SshClient over) => _over = over;

    /// <summary>How many bytes are pushed at a time.</summary>
    public int BlockSize { get; init; } = 32 * 1024;

    /// <summary>
    /// Sends one file to a path on the server.
    /// </summary>
    /// <param name="from">What to read. Its length must be known.</param>
    /// <param name="length">How many bytes to send, which SCP requires in advance.</param>
    /// <param name="to">Where it goes, as the server spells it.</param>
    /// <param name="mode">The permissions to ask for, as a bitmask.</param>
    /// <param name="progress">Told how many bytes have gone.</param>
    /// <param name="cancellationToken">Abandons the transfer.</param>
    /// <exception cref="SshException">The server refused, or the name cannot be sent.</exception>
    public async ValueTask SendAsync(Stream from, long length, string to, int mode = 0b_110_100_100,
                                     IProgress<long>? progress = null,
                                     CancellationToken cancellationToken = default)
    {
        Live();

        ArgumentNullException.ThrowIfNull(from);
        ArgumentException.ThrowIfNullOrWhiteSpace(to);

        string name = Named(to);

        using SshCommand scp = _over.CreateCommand($"scp -t -- {ShellWord.Quote(to)}");

        // Started first, and the input stream taken afterwards: SSH.NET opens the channel in
        // BeginExecute and refuses to hand over a stream for a command that is not running yet.
        IAsyncResult started = scp.BeginExecute();

        using Stream writing = scp.CreateInputStream();

        try
        {
            await Acknowledged(scp.OutputStream, cancellationToken).ConfigureAwait(false);

            await Say(writing, Record('C', mode, length, name), cancellationToken)
                .ConfigureAwait(false);

            await Acknowledged(scp.OutputStream, cancellationToken).ConfigureAwait(false);

            await Push(from, writing, length, progress, cancellationToken).ConfigureAwait(false);

            // The end of the file's bytes, which is a zero and not a status: the status is what the
            // far end says back.
            await writing.WriteAsync(new byte[] { Ok }, cancellationToken).ConfigureAwait(false);
            await writing.FlushAsync(cancellationToken).ConfigureAwait(false);

            await Acknowledged(scp.OutputStream, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            writing.Close();

            await Finished(scp, started).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Receives one file from the server.
    /// </summary>
    /// <param name="path">What to read, as the server spells it.</param>
    /// <param name="into">Where the bytes go.</param>
    /// <param name="progress">Told how many bytes have arrived.</param>
    /// <param name="cancellationToken">Abandons the transfer.</param>
    /// <exception cref="SshException">The server refused, or sent something this cannot read.</exception>
    public async ValueTask ReceiveAsync(string path, Stream into, IProgress<long>? progress = null,
                                        CancellationToken cancellationToken = default)
    {
        Live();

        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(into);

        using SshCommand scp = _over.CreateCommand($"scp -f -- {ShellWord.Quote(path)}");

        IAsyncResult started = scp.BeginExecute();

        using Stream writing = scp.CreateInputStream();

        try
        {
            await Ack(writing, cancellationToken).ConfigureAwait(false);

            string header = await Line(scp.OutputStream, cancellationToken).ConfigureAwait(false);

            if (header.Length == 0 || header[0] != 'C')
            {
                throw Refused($"The server answered with {Describe(header)} rather than a file.",
                              path);
            }

            long length = Length(header, path);

            await Ack(writing, cancellationToken).ConfigureAwait(false);

            await Pull(scp.OutputStream, into, length, progress, cancellationToken)
                .ConfigureAwait(false);

            // One status byte closes the file, and then this side acknowledges the whole transfer.
            int last = scp.OutputStream.ReadByte();

            if (last > 0)
            {
                throw Refused("The server reported a problem after sending the file.", path);
            }

            await Ack(writing, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            writing.Close();

            await Finished(scp, started).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Sends a whole directory, contents and all.
    /// </summary>
    /// <param name="from">The local directory.</param>
    /// <param name="to">The directory on the server it goes into.</param>
    /// <param name="progress">Told how many bytes have gone, across every file.</param>
    /// <param name="cancellationToken">Abandons the transfer.</param>
    public async ValueTask SendDirectoryAsync(string from, string to,
                                              IProgress<long>? progress = null,
                                              CancellationToken cancellationToken = default)
    {
        Live();

        ArgumentException.ThrowIfNullOrWhiteSpace(from);
        ArgumentException.ThrowIfNullOrWhiteSpace(to);

        using SshCommand scp = _over.CreateCommand($"scp -rt -- {ShellWord.Quote(to)}");

        IAsyncResult started = scp.BeginExecute();

        using Stream writing = scp.CreateInputStream();

        try
        {
            await Acknowledged(scp.OutputStream, cancellationToken).ConfigureAwait(false);

            long sent = 0;

            await Walk(new DirectoryInfo(from), writing, scp.OutputStream,
                       moved => progress?.Report(sent += moved), cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            writing.Close();

            await Finished(scp, started).ConfigureAwait(false);
        }
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        _disposed = true;

        // Nothing is held open between transfers: each one is its own exec channel, which is what
        // SCP is. The session it runs on belongs to whoever opened that.
        return ValueTask.CompletedTask;
    }

    private static async ValueTask Walk(DirectoryInfo here, Stream writing, Stream reading,
                                        Action<long> moved, CancellationToken cancellationToken)
    {
        await Say(writing, Record('D', 0b_111_101_101, 0, Named(here.Name)), cancellationToken)
            .ConfigureAwait(false);

        await Acknowledged(reading, cancellationToken).ConfigureAwait(false);

        foreach (FileInfo file in here.EnumerateFiles())
        {
            await Say(writing, Record('C', 0b_110_100_100, file.Length, Named(file.Name)),
                      cancellationToken).ConfigureAwait(false);

            await Acknowledged(reading, cancellationToken).ConfigureAwait(false);

            await using (FileStream bytes = file.OpenRead())
            {
                await Push(bytes, writing, file.Length, new Reporting(moved), cancellationToken)
                    .ConfigureAwait(false);
            }

            await writing.WriteAsync(new byte[] { Ok }, cancellationToken).ConfigureAwait(false);
            await writing.FlushAsync(cancellationToken).ConfigureAwait(false);

            await Acknowledged(reading, cancellationToken).ConfigureAwait(false);
        }

        foreach (DirectoryInfo below in here.EnumerateDirectories())
        {
            await Walk(below, writing, reading, moved, cancellationToken).ConfigureAwait(false);
        }

        await Say(writing, "E\n", cancellationToken).ConfigureAwait(false);

        await Acknowledged(reading, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The last component of a path, refused where it holds a character the protocol cannot carry.
    ///
    /// <para>A newline in a name would end the protocol's own record and everything after it would
    /// be read as a command to this transfer. That is refused rather than escaped, because there is
    /// no escaping in this protocol to do it with.</para>
    /// </summary>
    private static string Named(string path)
    {
        int slash = path.LastIndexOf('/');
        string name = slash < 0 ? path : path[(slash + 1)..];

        if (name.Contains('\n', StringComparison.Ordinal)
            || name.Contains('\r', StringComparison.Ordinal))
        {
            throw new SshException(
                SshFailureKind.Unrecognised,
                "A file name containing a line break cannot be sent over scp.",
                "The protocol ends each of its own records with a line break, so a name holding "
                + "one cannot be told apart from the next instruction.",
                "Rename the file, or use a server that offers the sftp subsystem.");
        }

        return name;
    }

    private static string Record(char kind, int mode, long length, string name) =>
        string.Create(CultureInfo.InvariantCulture,
                      $"{kind}{Octal(mode)} {length} {name}\n");

    private static string Octal(int mode) =>
        string.Create(CultureInfo.InvariantCulture,
                      $"0{(mode >> 6) & 7}{(mode >> 3) & 7}{mode & 7}");

    private static long Length(string header, string path)
    {
        // C0644 <length> <name>
        string[] words = header.Split(' ', 3);

        if (words.Length < 3
            || !long.TryParse(words[1], NumberStyles.Integer, CultureInfo.InvariantCulture,
                              out long length))
        {
            throw Refused($"The server's file record could not be read: {Describe(header)}.", path);
        }

        return length;
    }

    private static async ValueTask Push(Stream from, Stream to, long length,
                                        IProgress<long>? progress,
                                        CancellationToken cancellationToken)
    {
        byte[] block = new byte[32 * 1024];
        long sent = 0;

        while (sent < length)
        {
            cancellationToken.ThrowIfCancellationRequested();

            int wanted = (int)Math.Min(block.Length, length - sent);
            int got = await from.ReadAsync(block.AsMemory(0, wanted), cancellationToken)
                               .ConfigureAwait(false);

            if (got == 0)
            {
                // The length was declared before the first byte was sent, so a stream that ends
                // early leaves the far end waiting for bytes that will never come.
                throw new SshException(
                    SshFailureKind.Dropped,
                    $"The file ended after {sent} bytes, having said it was {length}.",
                    "scp sends the length before the bytes, so a source that shrinks part way "
                    + "cannot be corrected.");
            }

            await to.WriteAsync(block.AsMemory(0, got), cancellationToken).ConfigureAwait(false);

            sent += got;

            progress?.Report(got);
        }

        await to.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask Pull(Stream from, Stream to, long length,
                                        IProgress<long>? progress,
                                        CancellationToken cancellationToken)
    {
        byte[] block = new byte[32 * 1024];
        long got = 0;

        while (got < length)
        {
            cancellationToken.ThrowIfCancellationRequested();

            int wanted = (int)Math.Min(block.Length, length - got);
            int read = await from.ReadAsync(block.AsMemory(0, wanted), cancellationToken)
                                 .ConfigureAwait(false);

            if (read == 0)
            {
                throw new SshException(
                    SshFailureKind.Dropped,
                    $"The transfer ended after {got} of {length} bytes.",
                    "The channel closed before the file had all arrived.");
            }

            await to.WriteAsync(block.AsMemory(0, read), cancellationToken).ConfigureAwait(false);

            got += read;

            progress?.Report(got);
        }
    }

    private static async ValueTask Say(Stream to, string what, CancellationToken cancellationToken)
    {
        await to.WriteAsync(Encoding.UTF8.GetBytes(what), cancellationToken).ConfigureAwait(false);
        await to.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask Ack(Stream to, CancellationToken cancellationToken)
    {
        await to.WriteAsync(new byte[] { Ok }, cancellationToken).ConfigureAwait(false);
        await to.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads one status byte, and turns anything but zero into what the far end said about it.
    /// </summary>
    private static async ValueTask Acknowledged(Stream from, CancellationToken cancellationToken)
    {
        byte[] one = new byte[1];

        int read = await from.ReadAsync(one, cancellationToken).ConfigureAwait(false);

        if (read == 0)
        {
            throw new SshException(
                SshFailureKind.ShellRefused,
                "The server closed the scp channel without answering.",
                "The remote scp may not exist, or the account may not be allowed to run it.",
                "This host offers neither the sftp subsystem nor a usable scp.");
        }

        if (one[0] == Ok)
        {
            return;
        }

        string said = await Line(from, cancellationToken).ConfigureAwait(false);

        throw new SshException(
            one[0] == Fatal ? SshFailureKind.Refused : SshFailureKind.Unrecognised,
            $"The server refused the transfer: {said}",
            one[0] == Warning ? "scp reported this as a warning." : "scp reported this as fatal.",
            string.Empty,
            said);
    }

    private static async ValueTask<string> Line(Stream from, CancellationToken cancellationToken)
    {
        StringBuilder said = new();
        byte[] one = new byte[1];

        while (said.Length < 4096)
        {
            int read = await from.ReadAsync(one, cancellationToken).ConfigureAwait(false);

            if (read == 0 || one[0] == (byte)'\n')
            {
                break;
            }

            said.Append((char)one[0]);
        }

        return said.ToString();
    }

    private static string Describe(string what) =>
        what.Length == 0 ? "nothing" : $"\"{what}\"";

    private static SshException Refused(string what, string path) =>
        new(SshFailureKind.Unrecognised, what,
            $"This was an scp transfer of {path}, which is the fallback for a host with no sftp "
            + "subsystem.",
            "A server that offers sftp gives better errors than this one can.");

    /// <summary>
    /// Waits for the remote command, and lets a transfer that already failed keep its own failure.
    ///
    /// <para>On the pool because <c>EndExecute</c> blocks: every other member here yields, and one
    /// that did not would be the one that froze a window at the end of a transfer.</para>
    /// </summary>
    private static async ValueTask Finished(SshCommand scp, IAsyncResult started)
    {
        try
        {
            await Task.Run(() => scp.EndExecute(started), CancellationToken.None)
                      .ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The transfer's own exception is the useful one; this is only how the channel ended.
        }
    }

    private void Live() => ObjectDisposedException.ThrowIf(_disposed, this);

    /// <summary>Turns each block's size into a running total for the caller.</summary>
    private sealed class Reporting(Action<long> to) : IProgress<long>
    {
        public void Report(long value) => to(value);
    }
}
