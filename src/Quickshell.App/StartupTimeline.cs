using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace Quickshell.App;

/// <summary>
/// When each part of a start happened, measured from the moment Windows created the process — the
/// instrument behind figure 5 of the performance budget. QS75.
///
/// <para><b>From process creation and not from <c>Main</c></b>, because the budget's clock starts
/// when the process does: the runtime loading itself is the first thing a user waits for, and a
/// figure measured from the first line of managed code would leave it out. Windows keeps the
/// creation time, so the process can read its own start without a harness's clock and its own
/// disagreeing about when that was.</para>
///
/// <para><b>Nothing happens unless it is asked for.</b> Armed only by <c>--startup-report</c>; an
/// unarmed mark is a null check, which is the whole cost of this class to a start nobody is
/// measuring.</para>
///
/// <para><b>The last milestone is the one the budget names</b>: the first frame on the glass that
/// carries the shell's own output — its banner and its prompt, not a window that has appeared. The
/// report is written then, once, and moved into place so a harness waiting for it never reads half
/// of one.</para>
/// </summary>
public static class StartupTimeline
{
    private static readonly ConcurrentDictionary<string, double> Marks = new(StringComparer.Ordinal);

    private static string? _report;
    private static DateTime _created;
    private static int _written;

    /// <summary>Whether a report was asked for and has not been written yet.</summary>
    public static bool Waiting => _report is not null && Volatile.Read(ref _written) == 0;

    /// <summary>Asks for a report, written to <paramref name="report"/> once the shell is on screen.</summary>
    public static void Arm(string report)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(report);

        using Process self = Process.GetCurrentProcess();

        _created = self.StartTime.ToUniversalTime();
        _report = report;
    }

    /// <summary>Records a milestone the first time it is reached; later times of the same one are not a start.</summary>
    public static void Mark(string milestone)
    {
        if (!Waiting)
        {
            return;
        }

        Marks.TryAdd(milestone, (DateTime.UtcNow - _created).TotalMilliseconds);
    }

    /// <summary>
    /// The shell's output is on the glass: records that, and writes the report — once, however many
    /// frames call this.
    /// </summary>
    public static void Interactive()
    {
        if (!Waiting)
        {
            return;
        }

        Mark("interactive");

        if (Interlocked.Exchange(ref _written, 1) != 0 || _report is not { } report)
        {
            return;
        }

        StringBuilder json = new("{");

        foreach ((string milestone, double at) in Marks.OrderBy(mark => mark.Value))
        {
            json.Append(json.Length > 1 ? "," : string.Empty)
                .Append('"').Append(milestone).Append("\":")
                .Append(at.ToString("0.0", CultureInfo.InvariantCulture));
        }

        json.Append('}');

        try
        {
            string partial = report + ".partial";

            File.WriteAllText(partial, json.ToString());
            File.Move(partial, report, overwrite: true);
        }
        catch (Exception)
        {
            // A report that could not be written is a measurement that did not happen, and the
            // harness waiting for it says so. It is not a reason for the client to fail.
        }
    }
}
