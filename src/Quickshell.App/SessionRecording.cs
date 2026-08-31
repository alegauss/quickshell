using System.IO;
using System.IO.Compression;

namespace Quickshell.App;

/// <summary>
/// A session's output, kept as the bytes a terminal received and nothing else.
///
/// <para><b>Why bytes and not a description.</b> A terminal defect is close to impossible to write
/// down and trivial to reproduce from a stream: <em>the box drawing looks wrong on this router</em>
/// is a sentence nobody can act on, and the same session as a file reproduces it on a maintainer's
/// machine on the first try. This writes <c>&lt;name&gt;.raw.gz</c> — the exact shape
/// <c>benchmarks/corpus/streams</c> holds — so a defect found this way becomes a regression test by
/// moving one file rather than by writing one.</para>
///
/// <para><b>Output only, and the surface is named so that recording input would be a lie somebody
/// has to type.</b> There is one method here and it is called <see cref="HostSent"/>. What the user
/// typed is not output: it is a password at a prompt, a passphrase, a token pasted into a command —
/// and a recording that captured it would be a file the user was invited to send to a stranger.
/// Nothing on the keystroke path can reach this object; <c>SessionPipeline.TypeAsync</c> writes
/// straight to the channel and shares nothing with the stage that feeds this.</para>
///
/// <para><b>Explicit, per session, and visible.</b> It records because somebody asked this session to
/// record, and <see cref="Path"/> is public so they can be told where it went. Nothing starts one on
/// its own.</para>
/// </summary>
public sealed class SessionRecording : IAsyncDisposable
{
    private readonly Lock _guard = new();
    private readonly FileStream _file;
    private readonly GZipStream _compressing;

    private long _bytes;
    private bool _closed;

    private SessionRecording(string path, FileStream file, GZipStream compressing)
    {
        Path = path;
        _file = file;
        _compressing = compressing;
    }

    /// <summary>The file being written, so the user can be told where it is.</summary>
    public string Path { get; }

    /// <summary>How much the host has sent since this started.</summary>
    public long Bytes
    {
        get
        {
            lock (_guard)
            {
                return _bytes;
            }
        }
    }

    /// <summary>Whether it is still taking bytes.</summary>
    public bool Running
    {
        get
        {
            lock (_guard)
            {
                return !_closed;
            }
        }
    }

    /// <summary>
    /// Starts one.
    /// </summary>
    /// <param name="folder">Where recordings go. Created where it is not there.</param>
    /// <param name="name">
    /// What to call it. The corpus names a stream for what was run — <c>vim-scroll</c>,
    /// <c>tmux-resize</c> — and a recording that will become one should be named the same way.
    /// </param>
    public static SessionRecording Start(string folder, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        Directory.CreateDirectory(folder);

        string path = System.IO.Path.Combine(folder, $"{Safe(name)}.raw.gz");

        FileStream file = new(path, FileMode.Create, FileAccess.Write, FileShare.Read);

        // Optimal and not SmallestSize: this runs beside a live session, and the point is a file
        // small enough to send rather than the smallest one achievable.
        GZipStream compressing = new(file, CompressionLevel.Optimal);

        return new SessionRecording(path, file, compressing);
    }

    /// <summary>
    /// Bytes the host sent, exactly as they arrived.
    ///
    /// <para>Nothing is interpreted and nothing is filtered: a corpus that had been cleaned up is a
    /// corpus that no longer contains the defect. Called from the parser stage, which is the only
    /// stage that sees host output.</para>
    /// </summary>
    public void HostSent(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
        {
            return;
        }

        lock (_guard)
        {
            if (_closed)
            {
                return;
            }

            _compressing.Write(bytes);

            _bytes += bytes.Length;
        }
    }

    /// <summary>
    /// Closes the file, which is what makes it readable: a gzip stream that was never finished is a
    /// recording nobody can open.
    /// </summary>
    public ValueTask DisposeAsync()
    {
        lock (_guard)
        {
            if (_closed)
            {
                return ValueTask.CompletedTask;
            }

            _closed = true;

#pragma warning disable CA1849
            _compressing.Flush();
#pragma warning restore CA1849
            _compressing.Dispose();
            _file.Dispose();
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>A name that is a file name, since the caller's is a session's and may not be.</summary>
    private static string Safe(string name)
    {
        char[] taken = [.. name];

        foreach (char forbidden in System.IO.Path.GetInvalidFileNameChars())
        {
            for (int at = 0; at < taken.Length; at++)
            {
                if (taken[at] == forbidden)
                {
                    taken[at] = '-';
                }
            }
        }

        return new string(taken);
    }
}
