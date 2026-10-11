using System.Globalization;
using System.IO;

namespace Quickshell.App;

/// <summary>
/// What the render loop has done, written to a file every few seconds while the client runs (QS255).
///
/// <para><b>Why it exists.</b> An idle pane measured at 0.6 % of a core with its cursor blink off,
/// and the scheduler cannot say why: only the loop knows whether it woke because a parser said a
/// picture changed or because a blink was due, how many frames that cost, and whether any host sent
/// a byte meanwhile. <c>tools/Quickshell.Idle --frames</c> reads it at both ends of its interval, so
/// an idle figure comes with its causes.</para>
///
/// <para><b>Rewritten rather than appended</b>, through a file beside it and a move, so a reader
/// never sees half a report. The counters are cumulative; a reader takes differences.</para>
/// </summary>
internal static class FrameReport
{
    private static readonly TimeSpan Every = TimeSpan.FromSeconds(5);

    private static Timer? _timer;

    /// <summary>Starts writing the share's counters to <paramref name="file"/>, until the process ends.</summary>
    public static void Arm(string file, TerminalShare share)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(file);
        ArgumentNullException.ThrowIfNull(share);

        string full = Path.GetFullPath(file);

        _timer = new Timer(_ => Write(full, share), null, TimeSpan.Zero, Every);
    }

    private static void Write(string file, TerminalShare share)
    {
        string text = string.Create(CultureInfo.InvariantCulture,
            $$"""
            {
              "at": "{{DateTimeOffset.Now:O}}",
              "damageSets": {{share.Damage.Sets}},
              "wokenByDamage": {{share.WokenByDamage}},
              "wokenByBlink": {{share.WokenByBlink}},
              "frames": {{share.Frames}},
              "hostBytes": {{SessionPipeline.Received}}
            }
            """);

        try
        {
            string beside = file + ".writing";

            File.WriteAllText(beside, text);
            File.Move(beside, file, overwrite: true);
        }
        catch (IOException)
        {
            // A reader holding the file this instant; the next tick writes it.
        }
        catch (UnauthorizedAccessException)
        {
            // The same, as Windows sometimes says it.
        }
    }
}
