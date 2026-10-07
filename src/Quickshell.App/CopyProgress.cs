using System.Globalization;
using System.IO;
using Quickshell.Transport;

namespace Quickshell.App;

/// <summary>
/// One copy the browser started, as a line a person can watch and a button that stops it (QS186).
///
/// <para><b>The queue does not change; this is its state, read when the strip is drawn.</b> The
/// queue already knows each entry's bytes against its length, a rate over a short window, and
/// whether it is waiting, running, failed or skipped — a tree of a few gigabytes over a slow link
/// was a browser showing nothing for minutes only because none of that was read.</para>
///
/// <para><b>The rate is the queue's own, over the last few seconds</b>, and the time left is the
/// file being moved's and not the whole copy's: a size is known only once a file is started, so a
/// whole-copy estimate would be a number made up for the files nobody has looked at yet.</para>
/// </summary>
public sealed class CopyProgress
{
    /// <summary>A copy over a queue, described by where it goes.</summary>
    /// <param name="queue">The queue moving it.</param>
    /// <param name="to">Where it goes, as the strip names it: a directory and its side.</param>
    public CopyProgress(TransferQueue queue, string to)
    {
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentException.ThrowIfNullOrWhiteSpace(to);

        Queue = queue;
        To = to;
    }

    /// <summary>The queue moving it.</summary>
    public TransferQueue Queue { get; }

    /// <summary>Where it goes.</summary>
    public string To { get; }

    /// <summary>Whether it is still moving, which is what decides between the stop and the retry.</summary>
    public bool Running { get; internal set; } = true;

    /// <summary>What failed, each with its reason — kept on the strip with a retry rather than folded into a sentence.</summary>
    public IReadOnlyList<TransferEntry> Failed =>
        [.. Queue.Entries.Where(entry => entry.State == TransferState.Failed)];

    /// <summary>
    /// Whether something in it stopped short — failed, or stopped by the user — and so has a retry,
    /// which resumes each file where the queue can show its bytes are the start of the source.
    /// </summary>
    public bool Unfinished => Queue.Entries.Any(entry => entry.State is TransferState.Failed
                                                         or TransferState.Cancelled or TransferState.Paused);

    /// <summary>How far along, from zero to one: the files finished and the parts of those moving.</summary>
    public double Fraction
    {
        get
        {
            IReadOnlyList<TransferEntry> entries = Queue.Entries;

            if (entries.Count == 0)
            {
                return 0;
            }

            double done = entries.Sum(entry => entry.State switch
            {
                TransferState.Done or TransferState.Skipped => 1,
                TransferState.Running or TransferState.Paused or TransferState.Failed
                    or TransferState.Cancelled => entry.Fraction,
                _ => 0,
            });

            return Math.Clamp(done / entries.Count, 0, 1);
        }
    }

    /// <summary>The line the strip shows: what is moving, how far it has got, how fast and how long is left.</summary>
    public string Line
    {
        get
        {
            IReadOnlyList<TransferEntry> entries = Queue.Entries;
            int total = entries.Count;
            int done = entries.Count(entry => entry.State is TransferState.Done);
            long moved = entries.Sum(entry => entry.Moved);

            string files = total == 1 ? "1 file" : $"{Number(done)} of {Number(total)} files";

            if (!Running)
            {
                IReadOnlyList<TransferEntry> failed = Failed;

                if (failed.Count > 0)
                {
                    return $"Copy to {To}: {Number(failed.Count)} failed, first {Path.GetFileName(failed[0].To)}: "
                           + failed[0].Why;
                }

                return Unfinished
                    ? $"Copy to {To} stopped after {Number(done)} of {Number(total)} files; retry carries on "
                      + "from where each one got to."
                    : $"Copied {files} to {To}.";
            }

            TransferEntry? now = entries.FirstOrDefault(entry => entry.State == TransferState.Running);

            if (now is null)
            {
                return $"Copying {files} to {To}, starting.";
            }

            double rate = entries.Where(entry => entry.State == TransferState.Running)
                                 .Sum(entry => entry.BytesPerSecond);

            string line = $"Copying {Path.GetFileName(now.From)} to {To} - {files}, "
                          + $"{FileItem.Human(moved)} moved";

            if (rate > 0)
            {
                line += $", {FileItem.Human((long)rate)}/s";
            }

            if (now.Remaining is { } left)
            {
                line += $", this file about {Left(left)} left";
            }

            return line + ".";
        }
    }

    /// <summary>Stops every file in it now, keeping what each has moved.</summary>
    public void Stop() => Queue.CancelAll();

    private static string Number(int how) => how.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>A duration as somebody watching a copy reads one: seconds, then minutes, then hours.</summary>
    public static string Left(TimeSpan left) => left.TotalSeconds switch
    {
        < 1 => "a second",
        < 90 => $"{Math.Ceiling(left.TotalSeconds).ToString(CultureInfo.InvariantCulture)} s",
        < 5400 => $"{Math.Ceiling(left.TotalMinutes).ToString(CultureInfo.InvariantCulture)} min",
        _ => $"{left.TotalHours.ToString("F1", CultureInfo.InvariantCulture)} h",
    };
}
