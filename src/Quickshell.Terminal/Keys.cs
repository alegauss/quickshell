namespace Quickshell.Terminal;

/// <summary>
/// A key as the window reports it, before anything has decided what it sends.
///
/// <para>Only the keys whose bytes are not simply their character. A letter, a digit and a symbol
/// arrive as text and go out as text; what needs a table is everything that has no character of its
/// own, and everything a modifier changes the shape of.</para>
/// </summary>
public enum Key
{
    /// <summary>Not a key this map knows, which encodes to nothing.</summary>
    None = 0,

    /// <summary>The up arrow.</summary>
    Up,
    /// <summary>The down arrow.</summary>
    Down,
    /// <summary>The right arrow.</summary>
    Right,
    /// <summary>The left arrow.</summary>
    Left,
    /// <summary>Home.</summary>
    Home,
    /// <summary>End.</summary>
    End,
    /// <summary>Insert.</summary>
    Insert,
    /// <summary>Forward delete, which is not backspace.</summary>
    Delete,
    /// <summary>Page up.</summary>
    PageUp,
    /// <summary>Page down.</summary>
    PageDown,

    /// <summary>Backspace, which sends delete and not backspace. See <see cref="Keys"/>.</summary>
    Backspace,
    /// <summary>Tab, and back-tab with shift.</summary>
    Tab,
    /// <summary>The return key, which sends a carriage return.</summary>
    Enter,
    /// <summary>The escape key.</summary>
    Escape,

    /// <summary>Function key 1.</summary>
    F1,
    /// <summary>Function key 2.</summary>
    F2,
    /// <summary>Function key 3.</summary>
    F3,
    /// <summary>Function key 4.</summary>
    F4,
    /// <summary>Function key 5.</summary>
    F5,
    /// <summary>Function key 6.</summary>
    F6,
    /// <summary>Function key 7.</summary>
    F7,
    /// <summary>Function key 8.</summary>
    F8,
    /// <summary>Function key 9.</summary>
    F9,
    /// <summary>Function key 10.</summary>
    F10,
    /// <summary>Function key 11.</summary>
    F11,
    /// <summary>Function key 12.</summary>
    F12,
    /// <summary>Function key 13.</summary>
    F13,
    /// <summary>Function key 14.</summary>
    F14,
    /// <summary>Function key 15.</summary>
    F15,
    /// <summary>Function key 16.</summary>
    F16,
    /// <summary>Function key 17.</summary>
    F17,
    /// <summary>Function key 18.</summary>
    F18,
    /// <summary>Function key 19.</summary>
    F19,
    /// <summary>Function key 20.</summary>
    F20,

    /// <summary>The numeric pad's own Enter, which application mode gives its own sequence.</summary>
    KeypadEnter,
    /// <summary>The pad’s divide.</summary>
    KeypadDivide,
    /// <summary>The pad’s multiply.</summary>
    KeypadMultiply,
    /// <summary>The pad’s minus.</summary>
    KeypadSubtract,
    /// <summary>The pad’s plus.</summary>
    KeypadAdd,
    /// <summary>The pad’s decimal point.</summary>
    KeypadDecimal,
    /// <summary>The pad’s 0.</summary>
    Keypad0,
    /// <summary>The pad’s 1.</summary>
    Keypad1,
    /// <summary>The pad’s 2.</summary>
    Keypad2,
    /// <summary>The pad’s 3.</summary>
    Keypad3,
    /// <summary>The pad’s 4.</summary>
    Keypad4,
    /// <summary>The pad’s 5.</summary>
    Keypad5,
    /// <summary>The pad’s 6.</summary>
    Keypad6,
    /// <summary>The pad’s 7.</summary>
    Keypad7,
    /// <summary>The pad’s 8.</summary>
    Keypad8,
    /// <summary>The pad’s 9.</summary>
    Keypad9,
}

/// <summary>
/// What was held with the key.
///
/// <para>The values are not the wire encoding: xterm's modifier parameter is one plus a bitmask
/// where shift is one, alt is two and control is four, and <see cref="Keys"/> is where that
/// arithmetic happens.</para>
/// </summary>
[Flags]
public enum KeyModifiers
{
    /// <summary>Nothing held.</summary>
    None = 0,

    /// <summary>Shift.</summary>
    Shift = 1,

    /// <summary>Alt, which a terminal calls meta.</summary>
    Alt = 2,

    /// <summary>Control.</summary>
    Control = 4,
}

/// <summary>How the alt key is sent, which is a preference with an old argument behind it.</summary>
public enum AltSends
{
    /// <summary>
    /// An escape before the character. What every shell's readline expects, and the default.
    /// </summary>
    Escape,

    /// <summary>
    /// The high bit set on the character instead. What some users coming from other terminals
    /// expect, and wrong for any session that is not in a single-byte encoding.
    /// </summary>
    Meta,
}

/// <summary>
/// What a key sends, which is a function of the key, its modifiers, and state the host has changed.
///
/// <para><b>A static table would be wrong before it was finished.</b> Application cursor key mode
/// swaps the arrows between their CSI and SS3 forms, so a shell editing a line and the same shell
/// running <c>vim</c> disagree about what Up sends — and the terminal is the thing that has to know
/// which. Application keypad mode does the same for the numeric pad. So this takes the modes as
/// arguments and the emulator's own <c>Encode</c> is what supplies them.</para>
///
/// <para><b>A modified key is always the CSI form, whatever the cursor mode says.</b> That is
/// xterm's rule and not an oversight: <c>ESC O A</c> has nowhere to put a parameter, so
/// control-Up has to be <c>CSI 1 ; 5 A</c>. A client that sent the application form with a modifier
/// attached would be sending something no program parses, which is the quiet way control-arrow ends
/// up doing nothing.</para>
///
/// <para><b>Backspace sends delete.</b> <c>0x7F</c> and not <c>0x08</c>, because that is what
/// <c>$TERM</c> says and what every shell's line editor is configured for; control-Backspace sends
/// <c>0x08</c>, which is the word-erase most shells bind. Getting this pair the wrong way round is
/// why backspace prints <c>^?</c> on so many first attempts.</para>
///
/// <para><b>This is asked last, not first.</b> A chord the window has bound locally never reaches
/// here: the local layer takes priority, and every chord it reserves is one stolen from the remote
/// program, so that set stays small and stays documented. There is no window yet and therefore no
/// reserved chord — the set is empty, which is the only size at which this ordering is free. The
/// layer itself belongs with the window and its settings surface, in Block G.</para>
///
/// <para>Nothing here allocates: every method writes into the caller's buffer and answers how much
/// it wrote, so a keystroke costs no more than the path it goes down — which QS27 measured.</para>
/// </summary>
public static class Keys
{
    /// <summary>
    /// The terminal this client says it is.
    ///
    /// <para><b>Naming it is what makes the key map checkable.</b> A client that sets one
    /// <c>$TERM</c> and sends another terminal's sequences is one whose arrow keys work until a
    /// program believes the name — and the program is right to. This constant is what the tests read
    /// the expected forms from, and what a session will send in its pty request.</para>
    /// </summary>
    public const string TerminalType = "xterm-256color";

    /// <summary>The longest a key's encoding can be, so a caller can size a buffer once.</summary>
    public const int MaximumLength = 16;

    private const byte Escape = 0x1B;
    private const byte Bracket = (byte)'[';
    private const byte Ess = (byte)'O';

    /// <summary>
    /// Writes what a key sends into <paramref name="destination"/>, and answers how much.
    /// </summary>
    /// <param name="key">The key.</param>
    /// <param name="modifiers">What was held with it.</param>
    /// <param name="cursorKeys">Whether the host has asked for application cursor keys, DECCKM.</param>
    /// <param name="keypad">Whether the host has asked for the application keypad.</param>
    /// <param name="destination">At least <see cref="MaximumLength"/> bytes.</param>
    /// <returns>How many bytes were written, or zero where the key sends nothing.</returns>
    public static int Encode(
        Key key,
        KeyModifiers modifiers,
        bool cursorKeys,
        bool keypad,
        Span<byte> destination)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(destination.Length, MaximumLength);

        int parameter = Parameter(modifiers);

        return key switch
        {
            Key.Up or Key.Down or Key.Right or Key.Left or Key.Home or Key.End =>
                Cursor(key, parameter, cursorKeys, destination),

            Key.Insert or Key.PageUp or Key.PageDown =>
                Tilde(Number(key), parameter, destination),

            // Delete is a tilde key like the three above it, and is separated only because the
            // legacy form question below is about it and about nothing else.
            Key.Delete => Tilde(3, parameter, destination),

            Key.F1 or Key.F2 or Key.F3 or Key.F4 => Low(key, parameter, destination),

            >= Key.F5 and <= Key.F20 => Tilde(Number(key), parameter, destination),

            Key.Backspace => Backspace(modifiers, destination),
            Key.Tab => Tab(modifiers, destination),
            Key.Enter => Simple(0x0D, modifiers, destination),
            Key.Escape => Simple(Escape, modifiers, destination),

            >= Key.KeypadEnter and <= Key.Keypad9 => Keypad(key, keypad, destination),

            _ => 0,
        };
    }

    /// <summary>
    /// Writes what a character key sends, which is the character unless alt was held.
    /// </summary>
    /// <param name="text">The character the window resolved, already through the keyboard layout.</param>
    /// <param name="modifiers">What was held with it.</param>
    /// <param name="alt">How this session sends alt.</param>
    /// <param name="destination">At least <see cref="MaximumLength"/> bytes.</param>
    /// <returns>How many bytes were written.</returns>
    public static int EncodeText(
        ReadOnlySpan<char> text,
        KeyModifiers modifiers,
        AltSends alt,
        Span<byte> destination)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(destination.Length, MaximumLength);

        if (text.IsEmpty)
        {
            return 0;
        }

        int written = 0;

        // Alt as an escape prefix, which is what a shell's line editor reads as meta. The alternative
        // sets the high bit instead, and is offered because users of other clients expect it — but it
        // is not the default, because it is meaningless in any session that is not single-byte.
        if ((modifiers & KeyModifiers.Alt) != 0 && alt == AltSends.Escape)
        {
            destination[written++] = Escape;
        }

        int encoded = System.Text.Encoding.UTF8.GetBytes(text, destination[written..]);

        if ((modifiers & KeyModifiers.Alt) != 0 && alt == AltSends.Meta && encoded == 1)
        {
            destination[written] |= 0x80;
        }

        return written + encoded;
    }

    /// <summary>
    /// xterm's modifier parameter: one plus the bits, so an unmodified key is one and control alone
    /// is five. Zero here means no modifier, and a key that would have written <c>;1</c> writes
    /// nothing instead — which is what every program expects to see for a bare arrow.
    /// </summary>
    private static int Parameter(KeyModifiers modifiers) =>
        modifiers == KeyModifiers.None ? 0 : 1 + (int)modifiers;

    private static int Cursor(Key key, int parameter, bool cursorKeys, Span<byte> destination)
    {
        byte final = key switch
        {
            Key.Up => (byte)'A',
            Key.Down => (byte)'B',
            Key.Right => (byte)'C',
            Key.Left => (byte)'D',
            Key.Home => (byte)'H',
            _ => (byte)'F',
        };

        if (parameter == 0)
        {
            // The one place application cursor key mode changes anything.
            destination[0] = Escape;
            destination[1] = cursorKeys ? Ess : Bracket;
            destination[2] = final;

            return 3;
        }

        // Modified: always CSI, because SS3 has nowhere to put the parameter.
        int written = 0;

        destination[written++] = Escape;
        destination[written++] = Bracket;
        destination[written++] = (byte)'1';
        destination[written++] = (byte)';';
        written += Digits(parameter, destination[written..]);
        destination[written++] = final;

        return written;
    }

    /// <summary>F1 to F4, which are SS3 unmodified and CSI with a parameter.</summary>
    private static int Low(Key key, int parameter, Span<byte> destination)
    {
        byte final = (byte)('P' + (key - Key.F1));
        int written = 0;

        destination[written++] = Escape;

        if (parameter == 0)
        {
            destination[written++] = Ess;
            destination[written++] = final;

            return written;
        }

        destination[written++] = Bracket;
        destination[written++] = (byte)'1';
        destination[written++] = (byte)';';
        written += Digits(parameter, destination[written..]);
        destination[written++] = final;

        return written;
    }

    /// <summary>The <c>CSI n ~</c> family, with the modifier as a second parameter.</summary>
    private static int Tilde(int number, int parameter, Span<byte> destination)
    {
        int written = 0;

        destination[written++] = Escape;
        destination[written++] = Bracket;
        written += Digits(number, destination[written..]);

        if (parameter != 0)
        {
            destination[written++] = (byte)';';
            written += Digits(parameter, destination[written..]);
        }

        destination[written++] = (byte)'~';

        return written;
    }

    /// <summary>Which number the tilde family gives a key. xterm's, including its own gaps.</summary>
    private static int Number(Key key) => key switch
    {
        Key.Insert => 2,
        Key.Delete => 3,
        Key.PageUp => 5,
        Key.PageDown => 6,
        Key.F5 => 15,
        Key.F6 => 17,
        Key.F7 => 18,
        Key.F8 => 19,
        Key.F9 => 20,
        Key.F10 => 21,
        Key.F11 => 23,
        Key.F12 => 24,
        Key.F13 => 25,
        Key.F14 => 26,
        Key.F15 => 28,
        Key.F16 => 29,
        Key.F17 => 31,
        Key.F18 => 32,
        Key.F19 => 33,
        Key.F20 => 34,
        _ => 0,
    };

    /// <summary>
    /// Delete for backspace, backspace for control-backspace, and an escape in front for alt.
    /// </summary>
    private static int Backspace(KeyModifiers modifiers, Span<byte> destination)
    {
        int written = 0;

        if ((modifiers & KeyModifiers.Alt) != 0)
        {
            destination[written++] = Escape;
        }

        destination[written++] = (modifiers & KeyModifiers.Control) != 0 ? (byte)0x08 : (byte)0x7F;

        return written;
    }

    /// <summary>Tab, and the back-tab shift makes it.</summary>
    private static int Tab(KeyModifiers modifiers, Span<byte> destination)
    {
        if ((modifiers & KeyModifiers.Shift) != 0)
        {
            destination[0] = Escape;
            destination[1] = Bracket;
            destination[2] = (byte)'Z';

            return 3;
        }

        return Simple(0x09, modifiers, destination);
    }

    /// <summary>A control byte, with an escape in front where alt was held.</summary>
    private static int Simple(byte code, KeyModifiers modifiers, Span<byte> destination)
    {
        int written = 0;

        if ((modifiers & KeyModifiers.Alt) != 0)
        {
            destination[written++] = Escape;
        }

        destination[written++] = code;

        return written;
    }

    /// <summary>
    /// The numeric pad, which sends its characters until the host asks for the application form.
    ///
    /// <para>A program that turned application keypad mode on is one that wants to tell the pad's
    /// keys from the ones above the letters, and a client that sends the character either way has
    /// taken that ability away from it.</para>
    /// </summary>
    private static int Keypad(Key key, bool application, Span<byte> destination)
    {
        (byte plain, byte applied) = key switch
        {
            Key.KeypadEnter => ((byte)0x0D, (byte)'M'),
            Key.KeypadDivide => ((byte)'/', (byte)'o'),
            Key.KeypadMultiply => ((byte)'*', (byte)'j'),
            Key.KeypadSubtract => ((byte)'-', (byte)'m'),
            Key.KeypadAdd => ((byte)'+', (byte)'k'),
            Key.KeypadDecimal => ((byte)'.', (byte)'n'),
            _ => ((byte)('0' + (key - Key.Keypad0)), (byte)('p' + (key - Key.Keypad0))),
        };

        if (!application)
        {
            destination[0] = plain;

            return 1;
        }

        destination[0] = Escape;
        destination[1] = Ess;
        destination[2] = applied;

        return 3;
    }

    /// <summary>A number as its digits, without a formatter and without allocating.</summary>
    private static int Digits(int value, Span<byte> destination)
    {
        if (value < 10)
        {
            destination[0] = (byte)('0' + value);

            return 1;
        }

        destination[0] = (byte)('0' + (value / 10));
        destination[1] = (byte)('0' + (value % 10));

        return 2;
    }
}
