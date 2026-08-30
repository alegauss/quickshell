namespace Quickshell.Terminal;

/// <summary>
/// Text on its way from the clipboard to the host, and the two things that have to happen to it
/// first.
///
/// <para><b>This is the security half of copying and pasting.</b> Text pasted into a shell executes
/// the moment it contains a newline, and a command with a newline hidden inside it is an old and
/// effective trick: what a user reads on a web page and what their clipboard holds are not obliged
/// to match. So a paste is never simply forwarded.</para>
///
/// <para><b>Bracketed paste is the good answer and a confirmation is the fallback.</b> Where the
/// program has turned DECSET 2004 on it is told that what follows is pasted rather than typed, and
/// it can decline to run it — which shells and editors do. Where it has not, this client asks, and
/// the asking shows exactly what will be sent. A client that only had the first answer would be safe
/// with modern shells and silently unsafe with everything else.</para>
///
/// <para><b>Control characters are removed either way.</b> Nothing legitimate pastes an escape
/// sequence, and a paste that could carry one is a paste that could set a mode, change a title or
/// answer a query on the user's behalf. Tab and the line endings survive because they are text.</para>
/// </summary>
public static class Paste
{
    /// <summary>
    /// What tells a program that a paste is starting, and what tells it the paste has ended.
    ///
    /// <para>Built from a number rather than typed, for the reason QS100 is: an escape in a literal
    /// is one careless edit away from a raw control byte, which is invisible in every diff.</para>
    /// </summary>
    public static readonly string Start = new([(char)0x1B, '[', '2', '0', '0', '~']);

    /// <summary>And what tells it the paste has ended.</summary>
    public static readonly string Finish = new([(char)0x1B, '[', '2', '0', '1', '~']);

    /// <summary>
    /// Whether this paste has to be shown to the user before it is sent.
    ///
    /// <para>A newline is what makes a paste run itself, so a paste without one is only text. With
    /// bracketed paste the program is the thing deciding, and asking as well would be a dialogue in
    /// front of every ordinary paste.</para>
    /// </summary>
    public static bool NeedsConfirming(ReadOnlySpan<char> text, bool bracketed) =>
        !bracketed && text.IndexOfAny('\r', '\n') >= 0;

    /// <summary>
    /// How many characters cleaning would write, so a caller can size a buffer.
    /// </summary>
    public static int MeasureClean(ReadOnlySpan<char> text) => Clean(text, default, measuring: true);

    /// <summary>
    /// Removes what a paste may not carry and settles the line endings.
    ///
    /// <para>Every line ending becomes a carriage return, which is what the Enter key sends. A paste
    /// that kept the line feeds a Windows clipboard supplies would run every line twice on some
    /// shells and none of them on others.</para>
    /// </summary>
    /// <returns>How many characters were written, or -1 where the destination is too small.</returns>
    public static int Clean(ReadOnlySpan<char> text, Span<char> destination) =>
        Clean(text, destination, measuring: false);

    private static int Clean(ReadOnlySpan<char> text, Span<char> destination, bool measuring)
    {
        int written = 0;

        for (int index = 0; index < text.Length; index++)
        {
            char character = text[index];

            if (character is '\r' or '\n')
            {
                // A pair counts once, so a Windows clipboard does not double every line.
                if (character == '\r' && index + 1 < text.Length && text[index + 1] == '\n')
                {
                    index++;
                }

                character = '\r';
            }
            else if (char.IsControl(character) && character != '\t')
            {
                continue;
            }

            if (!measuring)
            {
                if (written >= destination.Length)
                {
                    return -1;
                }

                destination[written] = character;
            }

            written++;
        }

        return written;
    }
}
