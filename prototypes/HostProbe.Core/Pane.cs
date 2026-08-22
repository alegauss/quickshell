using System.Diagnostics;

namespace HostProbe.Core;

/// <summary>
/// The state every host's render loop reads and every host's input handler writes. Keeping
/// it here is what makes the three hosts comparable: they differ in how a frame reaches the
/// screen and in nothing else.
/// </summary>
public sealed class Pane
{
    /// <summary>How long the pane stays lit after a click before it falls back to idle.</summary>
    private static readonly long ArmedTicks = Stopwatch.Frequency / 4;

    private const long NeverClicked = long.MinValue;

    private long _armedAt = NeverClicked;
    private long _frames;

    /// <summary>Presents completed since the pane started. Read by the driver, written by the loop.</summary>
    public long Frames => Interlocked.Read(ref _frames);

    /// <summary>
    /// True while the pane is answering a click, so the loop draws the response colour. The
    /// sentinel is tested rather than subtracted: <c>Clock.Now - long.MinValue</c> overflows, and
    /// the first version of this read left the pane lit from launch, which the duplication probe
    /// then reported as a pane that was answering a click nobody had made.
    /// </summary>
    public bool Lit
    {
        get
        {
            long armed = Interlocked.Read(ref _armedAt);
            return armed != NeverClicked && Clock.Now - armed < ArmedTicks;
        }
    }

    /// <summary>Called by the host's input handler, on whatever thread the host delivers input on.</summary>
    public void Click() => Interlocked.Exchange(ref _armedAt, Clock.Now);

    /// <summary>Called by the host's render loop once a present has completed.</summary>
    public void CountFrame() => Interlocked.Increment(ref _frames);

    /// <summary>
    /// The colour this frame draws. Idle cycles so no frame is a duplicate of the last one -
    /// a compositor that skipped an identical frame would flatter whichever host it skipped
    /// for. The response is near-white and the idle band is dark, so the pixel poll that
    /// times a click cannot mistake one for the other.
    /// </summary>
    public (float R, float G, float B) Colour()
    {
        if (Lit)
        {
            return (1.0f, 1.0f, 1.0f);
        }

        long phase = Interlocked.Read(ref _frames) % 60;
        float wobble = 0.04f + (phase / 60.0f * 0.06f);
        return (wobble, wobble * 0.5f, 0.18f + wobble);
    }
}
