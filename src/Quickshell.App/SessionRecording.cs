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
///
/// <para><b>Bounded, by a stop and not a roll (QS133).</b> A log is independent lines and dropping
/// the oldest costs the oldest; a recording is one stream feeding a state machine, and cutting its
/// front off leaves a file that starts mid-sequence and reproduces nothing. So at <see cref="Limit"/>
/// — compressed, which is what reaches the disk and what somebody has to send — it stops, writes a
/// last line saying where and why, and says so through <see cref="Stopped"/>. A recording that went
/// quiet at a limit without saying so would lose the defect that happened afterwards silently,
/// which is worse than a full disk.</para>
/// </summary>
public sealed class SessionRecording : IAsyncDisposable
{
    /// <summary>
    /// The default bound on the compressed file. A <c>cat</c> of a large file is thirty megabytes
    /// raw in seconds and a few compressed, so this is hours of a busy session and still a file
    /// somebody can attach.
    /// </summary>
    public const long DefaultLimit = 256L * 1024 * 1024;

    private readonly Lock _guard = new();
    private readonly FileStream _file;
    private readonly GZipStream _compressing;

    private long _bytes;
    private bool _closed;
    private bool _cut;

    private SessionRecording(string path, FileStream file, GZipStream compressing, long limit)
    {
        Path = path;
        _file = file;
        _compressing = compressing;
        Limit = limit;
    }

    /// <summary>
    /// Raised once, from the thread that fed the last bytes, when the recording stopped at
    /// <see cref="Limit"/> — so whatever shows that a recording is running can show that it is not.
    /// </summary>
    public event EventHandler? Stopped;

    /// <summary>The file being written, so the user can be told where it is.</summary>
    public string Path { get; }

    /// <summary>
    /// How large the compressed file may grow before the recording stops — said before it starts,
    /// so nobody learns the bound by hitting it.
    /// </summary>
    public long Limit { get; }

    /// <summary>Whether it stopped at <see cref="Limit"/> rather than being closed.</summary>
    public bool Cut
    {
        get
        {
            lock (_guard)
            {
                return _cut;
            }
        }
    }

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
    /// <param name="limit">The bound on the compressed file; <see cref="DefaultLimit"/> where not given.</param>
    public static SessionRecording Start(string folder, string name, long limit = DefaultLimit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);

        Directory.CreateDirectory(folder);

        string path = System.IO.Path.Combine(folder, $"{Safe(name)}.raw.gz");

        FileStream file = new(path, FileMode.Create, FileAccess.Write, FileShare.Read);

        // Optimal and not SmallestSize: this runs beside a live session, and the point is a file
        // small enough to send rather than the smallest one achievable.
        GZipStream compressing = new(file, CompressionLevel.Optimal);

        return new SessionRecording(path, file, compressing, limit);
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

            // What the compressor has handed the file, which trails what it was given by a block at
            // most — so the file ends a little past the limit, never far past it.
            if (_file.Position < Limit)
            {
                return;
            }

            CutHere();
        }

        Stopped?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Ends the stream with a line saying where it was cut, and closes it.
    ///
    /// <para>The line opens with CAN, which abandons whatever escape sequence the last bytes left
    /// half-sent, so a replay reads the line as text rather than as the rest of a sequence.</para>
    /// </summary>
    private void CutHere()
    {
        string said = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"\u0018\r\n[quickshell: this recording stopped here, at its limit of "
            + $"{Limit / (1024 * 1024.0):0.###} MB compressed, after {_bytes:N0} bytes from the host]\r\n");

        _compressing.Write(System.Text.Encoding.UTF8.GetBytes(said));

        _cut = true;
        _closed = true;

#pragma warning disable CA1849
        _compressing.Flush();
#pragma warning restore CA1849
        _compressing.Dispose();
        _file.Dispose();
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
