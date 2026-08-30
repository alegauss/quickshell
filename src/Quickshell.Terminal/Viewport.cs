namespace Quickshell.Terminal;

/// <summary>Where a wheel notch goes, which is not always the scrollback.</summary>
public enum WheelGoes
{
    /// <summary>Back through the history, which is the ordinary case.</summary>
    ToScrollback,

    /// <summary>To the program as a mouse event, because it asked for the mouse.</summary>
    ToTheProgram,

    /// <summary>
    /// To the program as arrow keys, because it is full-screen and did not ask for the mouse.
    ///
    /// <para>This is what makes the wheel scroll inside <c>less</c> and <c>man</c> rather than
    /// scrolling the terminal out from under them.</para>
    /// </summary>
    ToArrowKeys,
}

/// <summary>
/// Which part of the history is on screen.
///
/// <para>The ring already holds it, so this is an anchor and not a data structure.</para>
///
/// <para><b>The anchor is an absolute line, and that is the whole design.</b> Held as "so many lines
/// back from the bottom", a viewport would slide upward through the text every time the host printed
/// something — the reader would watch the paragraph they were halfway through walk off the top. Held
/// as the line itself, new output moves nothing, which is the behaviour the design asks for and the
/// reason QS22 gave lines identities.</para>
///
/// <para><b>Typing returns to the bottom and output does not.</b> Somebody reading does not want the
/// screen stolen; somebody typing has finished reading. The arriving output is remembered rather than
/// followed, so a scrollbar can say that it came.</para>
/// </summary>
public sealed class Viewport
{
    private long _top;

    /// <summary>Whether the view is following the newest output.</summary>
    public bool IsAtBottom { get; private set; } = true;

    /// <summary>
    /// Whether output has arrived since the view stopped following it, which is what a scrollbar
    /// shows and what returning to the bottom clears.
    /// </summary>
    public bool HasUnseenOutput { get; private set; }

    /// <summary>
    /// The line at the top of the view, clamped to what the ring still holds.
    ///
    /// <para>Clamped on the way out rather than on the way in: a line the ring has since evicted is
    /// not an error, it is a reader who stayed still while a great deal was printed.</para>
    /// </summary>
    public long Top(TerminalBuffer buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);

        if (IsAtBottom)
        {
            return buffer.TopLine;
        }

        return Math.Clamp(_top, First(buffer), buffer.TopLine);
    }

    /// <summary>How far back the view currently is, in lines.</summary>
    public long Depth(TerminalBuffer buffer) => buffer.TopLine - Top(buffer);

    /// <summary>
    /// Moves the view. Negative goes back through the history, positive returns towards the bottom.
    /// </summary>
    /// <returns>Whether anything moved, which is false where there is no history to move into.</returns>
    public bool ScrollBy(TerminalBuffer buffer, int lines)
    {
        ArgumentNullException.ThrowIfNull(buffer);

        long from = Top(buffer);
        long to = Math.Clamp(from + lines, First(buffer), buffer.TopLine);

        if (to == buffer.TopLine)
        {
            // Arriving at the bottom is the same thing as following again, so a reader who scrolls
            // down to the end does not then have to be told to follow.
            ToBottom();

            return from != to;
        }

        IsAtBottom = false;
        _top = to;

        return from != to;
    }

    /// <summary>Returns to the newest output and starts following it again.</summary>
    public void ToBottom()
    {
        IsAtBottom = true;
        HasUnseenOutput = false;
        _top = 0;
    }

    /// <summary>Typing means the reading is finished, so the view goes back to the bottom.</summary>
    public void Typed() => ToBottom();

    /// <summary>
    /// Output arrived. The view does not move; it only remembers, so that something can say so.
    /// </summary>
    public void Produced()
    {
        if (!IsAtBottom)
        {
            HasUnseenOutput = true;
        }
    }

    /// <summary>
    /// Where a wheel notch should go, given what the program has asked for.
    ///
    /// <para>Under the alternate screen there is no scrollback to move into — a full-screen program
    /// owns the whole screen — so the notch belongs to the program. It goes as a mouse event where
    /// the program asked for the mouse, and as arrow keys where it did not, which is what makes the
    /// wheel work inside a pager that never heard of one.</para>
    /// </summary>
    public static WheelGoes Wheel(Emulator emulator)
    {
        ArgumentNullException.ThrowIfNull(emulator);

        if (!emulator.Screens.IsAlternate)
        {
            return WheelGoes.ToScrollback;
        }

        return emulator.MouseReporting == MouseTracking.Off
            ? WheelGoes.ToArrowKeys
            : WheelGoes.ToTheProgram;
    }

    private static long First(TerminalBuffer buffer) => buffer.TopLine - buffer.ScrollbackLines;
}
