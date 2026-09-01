using System.IO;

namespace Quickshell.App;

/// <summary>
/// Watches the settings file and says when it has settled.
///
/// <para><b>QS169: without this, every setting applies live and none of them can be changed.</b> The
/// design's whole argument for exposing a font size is that somebody will try three of them in a
/// minute; a client they have to restart between each is one they try once.</para>
///
/// <para><b>Three kinds of event and not one, because that is how an editor saves.</b> Most write by
/// creating a temporary file and renaming it over the original — the original is never
/// <em>changed</em> at all, so a watcher listening for that alone hears nothing when Notepad, Vim or
/// Visual Studio Code save. Created and renamed are the same event as far as this is concerned.</para>
///
/// <para><b>Debounced, because one save is several events.</b> A write arrives as a size change and
/// a timestamp change at least, and reading between them is reading a file that is half there — which
/// this client answers by falling back to the defaults, and which a user reads as their settings
/// having been wiped by the thing they just edited.</para>
/// </summary>
public sealed class SettingsWatch : IDisposable
{
    /// <summary>
    /// How long the file must hold still before it is read.
    ///
    /// <para>Long enough that an editor's several events are one, short enough that somebody who
    /// saved and looked up has not finished looking up. A quarter of a second is both.</para>
    /// </summary>
    public static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(250);

    private readonly string _path;
    private readonly Action<Settings> _changed;
    private readonly Lock _guard = new();

    private FileSystemWatcher? _watcher;
    private Timer? _settling;
    private bool _disposed;

    private SettingsWatch(string path, Action<Settings> changed)
    {
        _path = path;
        _changed = changed;
    }

    /// <summary>How many times the file has been read because it changed.</summary>
    public long Reloads { get; private set; }

    /// <summary>What went wrong arming the watch, or null. A client without one still runs.</summary>
    public Exception? Failed { get; private set; }

    /// <summary>
    /// Starts watching, or answers a watch that is not watching anything.
    ///
    /// <para><b>A failure here is not a failure to start.</b> A settings file on a network share, a
    /// directory the watcher cannot open, a machine that has run out of handles: none of them is a
    /// reason for a terminal not to open, and <see cref="Reload"/> works without any of this.</para>
    /// </summary>
    /// <param name="path">The settings file, which need not exist yet.</param>
    /// <param name="changed">Told what the file now says, on a thread pool thread.</param>
    public static SettingsWatch On(string path, Action<Settings> changed)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(changed);

        SettingsWatch watch = new(path, changed);

        try
        {
            string? directory = Path.GetDirectoryName(Path.GetFullPath(path));

            if (directory is not { Length: > 0 })
            {
                return watch;
            }

            Directory.CreateDirectory(directory);

            FileSystemWatcher watcher = new(directory, Path.GetFileName(path))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
            };

            watcher.Changed += watch.Stirred;
            watcher.Created += watch.Stirred;
            watcher.Renamed += watch.Stirred;

            watcher.EnableRaisingEvents = true;

            watch._watcher = watcher;
        }
        catch (Exception error)
        {
            watch.Failed = error;
        }

        return watch;
    }

    /// <summary>
    /// Reads the file now and tells the caller what it says, whatever the watcher is doing.
    ///
    /// <para>What a chord calls, and what somebody reaches for when they have just edited the file
    /// in another window. It costs one read and it works where a watcher does not.</para>
    /// </summary>
    public Settings Reload()
    {
        Settings read = SettingsFile.ReadFrom(_path);

        Reloads++;

        _changed(read);

        return read;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_watcher is { } watcher)
        {
            watcher.EnableRaisingEvents = false;

            watcher.Changed -= Stirred;
            watcher.Created -= Stirred;
            watcher.Renamed -= Stirred;

            watcher.Dispose();
        }

        lock (_guard)
        {
            _settling?.Dispose();
            _settling = null;
        }
    }

    /// <summary>
    /// The file moved. Wait for it to stop moving, restarting the wait each time it does.
    /// </summary>
    private void Stirred(object sender, FileSystemEventArgs what)
    {
        lock (_guard)
        {
            if (_disposed)
            {
                return;
            }

            // Restarted rather than left to run: several events from one save must be one read, and
            // the read has to be after the last of them rather than after the first.
            _settling ??= new Timer(_ => Settled());

            _settling.Change(Quiet, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>The file has held still. Read it, and never let a bad read stop the watch.</summary>
    private void Settled()
    {
        try
        {
            Reload();
        }
        catch (Exception)
        {
            // A read that failed is a file somebody is still editing, and the next save brings
            // another event. Throwing here would come out on a timer thread and take the process.
        }
    }
}
