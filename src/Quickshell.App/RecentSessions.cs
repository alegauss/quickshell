using System.Globalization;
using System.IO;
using System.Text.Json;

namespace Quickshell.App;

/// <summary>A saved session connected recently, and when its connection last went live.</summary>
/// <param name="Path">The session's path in the store.</param>
/// <param name="At">When it last connected.</param>
public sealed record RecentSession(string Path, DateTimeOffset At);

/// <summary>
/// The sessions connected last, newest first, kept across restarts in a file of the client's own
/// beside the session store (QS279): Home's cards and the palette's Open session both rank on it.
///
/// <para><b>Recorded when a connection goes live, never when one is merely asked for.</b> A session
/// that failed to connect is not one a user wants offered first next time.</para>
///
/// <para><b>Kept in step with the store.</b> A rename or a move carries the entry with it and a delete
/// drops it, so nothing here names a path the store lacks. A store edited by hand can still lose one;
/// a reader asks the store before it trusts an entry.</para>
///
/// <para><b>A file this client owns and nobody edits.</b> One that will not read is treated as empty
/// rather than refused: losing the recent list costs a user a ranking, and a dialog about it would cost
/// more.</para>
/// </summary>
public sealed class RecentSessions
{
    /// <summary>How many are kept.</summary>
    public const int Kept = 20;

    private static readonly JsonSerializerOptions Writing = new() { WriteIndented = true };

    private readonly List<RecentSession> _sessions;
    private readonly string _file;

    private RecentSessions(string file, List<RecentSession> sessions)
    {
        _file = file;
        _sessions = sessions;
    }

    /// <summary>Newest first.</summary>
    public IReadOnlyList<RecentSession> Sessions => _sessions;

    /// <summary>The paths, newest first, which is what a ranking reads.</summary>
    public IReadOnlyList<string> Paths => [.. _sessions.Select(each => each.Path)];

    /// <summary>The list in a file, empty where the file is not there or will not read.</summary>
    public static RecentSessions ReadFrom(string file)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(file);

        try
        {
            if (File.Exists(file)
                && JsonSerializer.Deserialize<List<RecentSession>>(File.ReadAllBytes(file)) is { } read)
            {
                return new RecentSessions(file, [.. read.Where(each => each.Path is { Length: > 0 })
                                                        .OrderByDescending(each => each.At)
                                                        .Take(Kept)]);
            }
        }
        catch (Exception unreadable) when (unreadable is JsonException or IOException or UnauthorizedAccessException)
        {
            // Empty, as the summary says: a lost ranking is not worth a dialog.
        }

        return new RecentSessions(file, []);
    }

    /// <summary>A session's connection went live at this moment: it moves to the front, and the file is written.</summary>
    public void Connected(string path, DateTimeOffset at)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        _sessions.RemoveAll(each => Same(each.Path, path));
        _sessions.Insert(0, new RecentSession(path, at));

        if (_sessions.Count > Kept)
        {
            _sessions.RemoveRange(Kept, _sessions.Count - Kept);
        }

        Write();
    }

    /// <summary>A session, or a folder and everything in it, now lives at another path.</summary>
    public void Moved(string from, string to)
    {
        bool changed = false;

        for (int at = 0; at < _sessions.Count; at++)
        {
            if (Under(_sessions[at].Path, from) is { } rest)
            {
                _sessions[at] = _sessions[at] with { Path = to + rest };
                changed = true;
            }
        }

        if (changed)
        {
            Write();
        }
    }

    /// <summary>A session, or a folder and everything in it, was deleted.</summary>
    public void Dropped(string path)
    {
        if (_sessions.RemoveAll(each => Under(each.Path, path) is not null) > 0)
        {
            Write();
        }
    }

    /// <summary>
    /// When, in the words a card says it: "Connected now" while a tab holds it, "Today", "Yesterday",
    /// "Last week" for the six days before that, then the date.
    /// </summary>
    /// <param name="at">When it last connected.</param>
    /// <param name="now">The moment the card is drawn.</param>
    /// <param name="open">Whether a tab holds it now.</param>
    public static string When(DateTimeOffset at, DateTimeOffset now, bool open)
    {
        if (open)
        {
            return "Connected now";
        }

        int days = (now.ToLocalTime().Date - at.ToLocalTime().Date).Days;

        return days switch
        {
            <= 0 => "Today",
            1 => "Yesterday",
            <= 7 => "Last week",
            _ => at.ToLocalTime().Year == now.ToLocalTime().Year
                ? at.ToLocalTime().ToString("d MMMM", CultureInfo.CurrentCulture)
                : at.ToLocalTime().ToString("d MMMM yyyy", CultureInfo.CurrentCulture),
        };
    }

    /// <summary>What follows a prefix path in a path at or under it — "" for the path itself — or null where it is not under it.</summary>
    private static string? Under(string path, string prefix)
    {
        if (Same(path, prefix))
        {
            return string.Empty;
        }

        return path.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase) ? path[prefix.Length..] : null;
    }

    private static bool Same(string one, string other) => string.Equals(one, other, StringComparison.OrdinalIgnoreCase);

    private void Write()
    {
        try
        {
            string? directory = System.IO.Path.GetDirectoryName(_file);

            if (directory is { Length: > 0 })
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllBytes(_file, JsonSerializer.SerializeToUtf8Bytes(_sessions, Writing));
        }
        catch (Exception unwritable) when (unwritable is IOException or UnauthorizedAccessException)
        {
            // The list stays right in memory for this window; the next start ranks on what was last written.
        }
    }
}
