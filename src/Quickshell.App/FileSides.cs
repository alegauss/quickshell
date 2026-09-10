using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using Quickshell.Transport;

namespace Quickshell.App;

/// <summary>
/// One entry in a listing, on either side of the browser.
///
/// <para><b>What a row shows is computed when a row is drawn</b>, and only then: the list is
/// virtualised, so of fifty thousand entries the forty on screen are the only ones anybody formats.
/// </para>
/// </summary>
/// <param name="Name">What the entry is called, exactly as its side spells it.</param>
/// <param name="Length">Its size in bytes; nothing for a directory.</param>
/// <param name="IsDirectory">Whether opening it lists it rather than being a file.</param>
/// <param name="Modified">When its content last changed.</param>
/// <param name="Permissions">The mode as its side writes it: <c>drwxr-xr-x</c> remotely.</param>
/// <param name="IsHidden">Whether the side considers it hidden, which the pane's toggle is about.</param>
public sealed record FileItem(string Name, long Length, bool IsDirectory, DateTimeOffset Modified,
                              string Permissions, bool IsHidden)
{
    /// <summary>The size as a person reads one: nothing for a directory, a unit for a file.</summary>
    public string Size => IsDirectory ? string.Empty : Human(Length);

    /// <summary>When, in the viewer's own time and in one unambiguous order.</summary>
    public string When =>
        Modified.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    /// <summary>The name, which is what a screen reader announces for a row and nothing else.</summary>
    public override string ToString() => Name;

    /// <summary>
    /// A byte count in the largest unit that keeps it above one, to one decimal place below ten.
    ///
    /// <para>Binary units, because that is what every file manager on this platform has shown for
    /// thirty years, and a size that disagreed with Explorer's would read as a wrong one.</para>
    /// </summary>
    public static string Human(long bytes)
    {
        string[] units = ["KB", "MB", "GB", "TB"];

        if (bytes < 1024)
        {
            return bytes.ToString(CultureInfo.InvariantCulture) + " B";
        }

        double size = bytes;
        int unit = -1;

        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return (size < 10 ? size.ToString("0.0", CultureInfo.InvariantCulture)
                          : size.ToString("0", CultureInfo.InvariantCulture)) + " " + units[unit];
    }
}

/// <summary>
/// Where one half of the browser gets its listing from — this machine, or the host a session is
/// connected to. QS60.
///
/// <para><b>Both sides are one shape</b>, so a pane is written once: it does not know whether a
/// listing is crossing a disk or an ocean, only that it arrives an entry at a time and may stop
/// arriving for a while.</para>
/// </summary>
public interface IFileSide
{
    /// <summary>What the pane is headed with: this computer, or the host.</summary>
    string Title { get; }

    /// <summary>Where the pane opens.</summary>
    string Home { get; }

    /// <summary>
    /// What is in a directory, streamed rather than gathered. Never <c>.</c> or <c>..</c>: going up
    /// is the pane's gesture, not an entry.
    /// </summary>
    IAsyncEnumerable<FileItem> ListAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>The path of an entry inside a directory, in this side's own spelling.</summary>
    string Into(string directory, string name);

    /// <summary>The directory a path sits in, or null at the top.</summary>
    string? Parent(string path);

    /// <summary>Moves or renames an entry, which on both sides is one operation.</summary>
    Task RenameAsync(string from, string to, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes an entry, and a directory with everything in it. A symbolic link is removed as a
    /// link and never followed: a delete that walked through one into another tree would be
    /// deleting files nobody selected.
    /// </summary>
    Task DeleteAsync(FileItem entry, string path, CancellationToken cancellationToken = default);

    /// <summary>Makes a directory.</summary>
    Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>Sets an entry's mode, given the way the server writes one: <c>0o755</c>.</summary>
    Task ChangeModeAsync(string path, int mode, CancellationToken cancellationToken = default);
}

/// <summary>
/// This machine's files.
///
/// <para><b>Hidden is either of the two things it means here:</b> the attribute Windows sets, and a
/// name that starts with a dot, which is how the configuration a user keeps under their profile —
/// <c>.ssh</c> above all — is named on every system this client talks to.</para>
/// </summary>
public sealed class LocalFiles : IFileSide
{
    /// <summary>Opens on the given directory, or on the user's profile.</summary>
    public LocalFiles(string? home = null)
    {
        Home = home ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    /// <inheritdoc/>
    public string Title => "This computer";

    /// <inheritdoc/>
    public string Home { get; }

    /// <summary>
    /// The entries of one directory, skipping any it may not read rather than failing on them.
    ///
    /// <para>Enumerated where the caller runs it, which for the pane is off the window's thread: a
    /// directory of fifty thousand files is a second of disk reads, and the pane is what decides
    /// that none of them happen where a repaint would wait.</para>
    /// </summary>
    public async IAsyncEnumerable<FileItem> ListAsync(
        string path, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask.ConfigureAwait(false);

        EnumerationOptions options = new()
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = false,
            AttributesToSkip = 0,
        };

        foreach (FileSystemInfo entry in new DirectoryInfo(path).EnumerateFileSystemInfos("*", options))
        {
            cancellationToken.ThrowIfCancellationRequested();

            bool directory = (entry.Attributes & FileAttributes.Directory) != 0;
            bool readOnly = (entry.Attributes & FileAttributes.ReadOnly) != 0;

            yield return new FileItem(
                entry.Name,
                directory ? 0 : ((FileInfo)entry).Length,
                directory,
                new DateTimeOffset(entry.LastWriteTimeUtc),
                (directory ? "d" : "-") + (readOnly ? "r-" : "rw"),
                (entry.Attributes & FileAttributes.Hidden) != 0 || entry.Name.StartsWith('.'));
        }
    }

    /// <inheritdoc/>
    public string Into(string directory, string name) => Path.Combine(directory, name);

    /// <inheritdoc/>
    public string? Parent(string path) => Directory.GetParent(Path.TrimEndingDirectorySeparator(path))?.FullName;

    /// <inheritdoc/>
    public Task RenameAsync(string from, string to, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            if (Directory.Exists(from))
            {
                Directory.Move(from, to);
            }
            else
            {
                File.Move(from, to);
            }
        }, cancellationToken);

    /// <summary>
    /// Removes a file, or a directory and what is in it. A junction or a symbolic link to a
    /// directory is removed as the link: the framework's recursive delete does not follow reparse
    /// points, which is the property the interface asks for.
    /// </summary>
    public Task DeleteAsync(FileItem entry, string path, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            if (entry.IsDirectory)
            {
                Directory.Delete(path, recursive: true);
            }
            else
            {
                File.Delete(path);
            }
        }, cancellationToken);

    /// <inheritdoc/>
    public Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            if (Directory.Exists(path) || File.Exists(path))
            {
                throw new IOException($"{Path.GetFileName(path)} already exists");
            }

            Directory.CreateDirectory(path);
        }, cancellationToken);

    /// <summary>
    /// Windows keeps one bit of a mode: whether the owner may write. A mode without it makes the
    /// entry read-only and a mode with it clears that; the other eight bits have nowhere to go on
    /// this side, and the dialog asking for a mode says so.
    /// </summary>
    public Task ChangeModeAsync(string path, int mode, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            FileAttributes attributes = File.GetAttributes(path);
            bool writable = (mode & 0b_010_000_000) != 0;

            File.SetAttributes(path, writable ? attributes & ~FileAttributes.ReadOnly
                                              : attributes | FileAttributes.ReadOnly);
        }, cancellationToken);
}

/// <summary>
/// The files on the host a session is connected to, over the session's own file channel.
///
/// <para><b>Paths belong to the server</b>, which is <see cref="IFileTransferChannel"/>'s rule and
/// holds here: a name is joined with a slash and never normalised, folded or checked, because a
/// client that tidied a remote name would be renaming somebody's file.</para>
/// </summary>
public sealed class RemoteFiles : IFileSide
{
    private readonly IFileTransferChannel _channel;

    /// <summary>Lists over a file channel that is already open.</summary>
    /// <param name="channel">The session's file channel, which this does not own or close.</param>
    /// <param name="title">What the pane is headed with, which is the host a user knows it by.</param>
    public RemoteFiles(IFileTransferChannel channel, string title)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);

        _channel = channel;
        Title = title;
    }

    /// <inheritdoc/>
    public string Title { get; }

    /// <summary>
    /// The session's file channel, which a copy between the panes runs over. Held by the side
    /// rather than handed round separately, so a copy cannot be pointed at one host while its pane
    /// is listing another.
    /// </summary>
    public IFileTransferChannel Channel => _channel;

    /// <inheritdoc/>
    public string Home => _channel.WorkingDirectory is { Length: > 0 } home ? home : "/";

    /// <inheritdoc/>
    public async IAsyncEnumerable<FileItem> ListAsync(
        string path, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (RemoteEntry entry in _channel.ListAsync(path, cancellationToken)
                                                    .ConfigureAwait(false))
        {
            if (entry.Name is "." or "..")
            {
                continue;
            }

            yield return new FileItem(entry.Name, entry.Length, entry.IsDirectory, entry.Modified,
                                      entry.Permissions, entry.Name.StartsWith('.'));
        }
    }

    /// <inheritdoc/>
    public string Into(string directory, string name) =>
        directory.EndsWith('/') ? directory + name : directory + "/" + name;

    /// <inheritdoc/>
    public string? Parent(string path)
    {
        string trimmed = path.Length > 1 ? path.TrimEnd('/') : path;

        if (trimmed is "/" or "" )
        {
            return null;
        }

        int slash = trimmed.LastIndexOf('/');

        return slash switch
        {
            < 0 => null,
            0 => "/",
            _ => trimmed[..slash],
        };
    }

    /// <inheritdoc/>
    public async Task RenameAsync(string from, string to, CancellationToken cancellationToken = default) =>
        await _channel.RenameAsync(from, to, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Removes a file, or a directory by removing what is in it first — SFTP removes only an empty
    /// directory. The walk goes into what the listing calls a directory and nowhere else, and a
    /// listing reports a link by its own mode, so a link to a directory is removed and never walked.
    /// </summary>
    public async Task DeleteAsync(FileItem entry, string path, CancellationToken cancellationToken = default)
    {
        if (entry.IsDirectory && !entry.Permissions.StartsWith('l'))
        {
            List<FileItem> inside = [];

            await foreach (FileItem child in ListAsync(path, cancellationToken).ConfigureAwait(false))
            {
                inside.Add(child);
            }

            foreach (FileItem child in inside)
            {
                await DeleteAsync(child, Into(path, child.Name), cancellationToken).ConfigureAwait(false);
            }
        }

        await _channel.DeleteAsync(path, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default) =>
        await _channel.CreateDirectoryAsync(path, cancellationToken).ConfigureAwait(false);

    /// <inheritdoc/>
    public async Task ChangeModeAsync(string path, int mode, CancellationToken cancellationToken = default) =>
        await _channel.ChangePermissionsAsync(path, mode, cancellationToken).ConfigureAwait(false);
}
