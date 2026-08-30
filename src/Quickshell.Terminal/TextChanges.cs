namespace Quickshell.Terminal;

/// <summary>
/// When to tell a screen reader that the text changed.
///
/// <para><b>This is the half that decides whether any of it works.</b> A provider that raises a
/// text-changed event per row during a <c>cat</c> hands a reader thousands of notifications, and a
/// reader given thousands of notifications reads none of them usefully — it is still working
/// through the first second of output a minute later, by which time the user has lost the session
/// entirely. The correct behaviour under a flood is to say <em>less</em>.</para>
///
/// <para><b>The same reasoning that governs frames, and not the same throttle.</b> A frame dropped
/// is a frame nobody sees; a notification dropped is a sentence nobody hears, and the two are
/// coalesced at different rates for that reason. A reader wants to know that something changed and
/// then be left alone to read it.</para>
///
/// <para>The clock is a parameter, because a throttle tested against a real one is a test that
/// sleeps and then reports what the machine was doing.</para>
/// </summary>
public sealed class TextChanges
{
    /// <summary>
    /// How long after announcing a change before another may be announced.
    ///
    /// <para>Half a second: long enough that a screenful of output is one announcement, short
    /// enough that a prompt appearing after a command is not perceptibly late.</para>
    /// </summary>
    public static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(500);

    private readonly TimeSpan _quiet;

    private TimeSpan _announced;
    private bool _ever;
    private bool _pending;

    /// <summary>A throttle with the usual interval, or another for a test.</summary>
    public TextChanges(TimeSpan? quiet = null) => _quiet = quiet ?? Quiet;

    /// <summary>How many changes have been announced. What a reader actually heard about.</summary>
    public int Announced { get; private set; }

    /// <summary>How many changes arrived. The distance from <see cref="Announced"/> is the point.</summary>
    public int Arrived { get; private set; }

    /// <summary>Whether a change is waiting to be announced when the quiet period ends.</summary>
    public bool Waiting => _pending;

    /// <summary>
    /// Something changed. Answers whether to announce it now.
    /// </summary>
    /// <param name="now">The clock, passed in so a test is not a sleep.</param>
    public bool Changed(TimeSpan now)
    {
        Arrived++;

        // A sentinel time rather than a flag is the obvious thing and overflows: TimeSpan.MinValue
        // minus anything is not a TimeSpan. So the first announcement is a state.
        if (_ever && now - _announced < _quiet)
        {
            // Held rather than dropped: the last change in a burst is the one worth announcing, and
            // a reader told nothing after a flood would be left believing the screen is as it was.
            _pending = true;

            return false;
        }

        _ever = true;
        _announced = now;
        _pending = false;
        Announced++;

        return true;
    }

    /// <summary>
    /// Whether a held change is now due. A caller polls this on whatever beat it already has.
    /// </summary>
    public bool Due(TimeSpan now)
    {
        if (!_pending || (_ever && now - _announced < _quiet))
        {
            return false;
        }

        _announced = now;
        _pending = false;
        Announced++;

        return true;
    }
}
