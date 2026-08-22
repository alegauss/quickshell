namespace Quickshell.Render;

/// <summary>
/// Whether the cursor is showing at a given moment, and when that answer next changes.
///
/// <para><b>A blinking cursor is the one thing that legitimately wakes an idle window.</b> Nothing
/// else on a terminal's screen changes without the host sending a byte or the user pressing a key,
/// so the block criterion "an idle window issues no draw calls" is a claim about this class more
/// than about any other. <see cref="NextChangeAfter"/> is what an idle loop schedules its wake-up
/// from, and it answers <c>null</c> when blinking is off — which is what makes turning blinking off
/// genuinely stop the wake-up rather than merely stop the flicker.</para>
///
/// <para>It holds no clock of its own. Elapsed time is an argument, so the phase is a pure function
/// of it and a test does not need to wait half a second to see the cursor go out.</para>
/// </summary>
public sealed class CursorBlink
{
    /// <summary>
    /// Windows' own caret blink period. Half of it on, half of it off, which is what makes a
    /// terminal cursor here beat at the same rate as every other caret on the desktop.
    /// </summary>
    public static readonly TimeSpan WindowsDefault = TimeSpan.FromMilliseconds(530);

    /// <summary>Whether the cursor blinks at all. Off means always showing, and never waking.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>How long the cursor is on, and then off. Refused at zero: that is a division.</summary>
    public TimeSpan Interval { get; init; } = WindowsDefault;

    /// <summary>Whether the cursor is showing this many ticks after the phase began.</summary>
    public bool IsShowingAt(TimeSpan elapsed)
    {
        if (!Enabled)
        {
            return true;
        }

        // Negative elapsed time is a clock somebody reset, and a cursor that vanishes because of it
        // is a bug report about the cursor rather than about the clock.
        long half = Math.Max(1, Interval.Ticks);
        long phase = Math.Abs(elapsed.Ticks) / half;

        return phase % 2 == 0;
    }

    /// <summary>
    /// How long until <see cref="IsShowingAt"/> answers differently, or null when it never will.
    ///
    /// <para>Null is the whole point: an idle loop that has this is one that sleeps until the host
    /// says something, and a window that never wakes issues no draw calls.</para>
    /// </summary>
    public TimeSpan? NextChangeAfter(TimeSpan elapsed)
    {
        if (!Enabled)
        {
            return null;
        }

        long half = Math.Max(1, Interval.Ticks);
        long into = Math.Abs(elapsed.Ticks) % half;

        return TimeSpan.FromTicks(half - into);
    }
}
