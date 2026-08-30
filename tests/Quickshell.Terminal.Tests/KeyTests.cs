using System.Text;
using Quickshell.Terminal;
using Xunit;

namespace Quickshell.Terminal.Tests;

/// <summary>
/// What a key sends, which is a function of the key, its modifiers, and state the host has changed.
/// </summary>
public sealed class KeyTests
{
    private const char Escape = (char)0x1B;
    private static readonly string Csi = new([Escape, '[']);
    private static readonly string Ss3 = new([Escape, 'O']);

    /// <summary>What backspace actually sends, spelled as a number for the reason QS100 is.</summary>
    private static readonly string Delete = ((char)0x7F).ToString();

    // ---- The falsification ----

    /// <summary>
    /// The design's own falsification: <em>falsified when the <c>$TERM</c> this client sets claims a
    /// form the key map does not send</em>.
    ///
    /// <para>The name this client answers to is <c>xterm-256color</c>, so every form below is the one
    /// that name promises: <c>kcuu1</c>, <c>kcud1</c>, <c>kcuf1</c>, <c>kcub1</c> for the arrows,
    /// <c>khome</c> and <c>kend</c>, <c>kich1</c>, <c>kdch1</c>, <c>kpp</c> and <c>knp</c>, and the
    /// function keys' own two families. A client that set this name and sent something else would
    /// have arrow keys that work until a program believes the name — and the program would be right
    /// to.</para>
    /// </summary>
    [Theory]
    // The arrows, in the form terminfo names for a terminal not in application mode.
    [InlineData(Key.Up, "A")]
    [InlineData(Key.Down, "B")]
    [InlineData(Key.Right, "C")]
    [InlineData(Key.Left, "D")]
    [InlineData(Key.Home, "H")]
    [InlineData(Key.End, "F")]
    public void AnArrowSendsTheFormThisTerminalsNamePromises(Key key, string final)
    {
        Assert.Equal(Csi + final, Sent(key, KeyModifiers.None));
    }

    [Theory]
    [InlineData(Key.Insert, "2~")]
    [InlineData(Key.Delete, "3~")]
    [InlineData(Key.PageUp, "5~")]
    [InlineData(Key.PageDown, "6~")]
    [InlineData(Key.F5, "15~")]
    [InlineData(Key.F6, "17~")]
    [InlineData(Key.F7, "18~")]
    [InlineData(Key.F8, "19~")]
    [InlineData(Key.F9, "20~")]
    [InlineData(Key.F10, "21~")]
    [InlineData(Key.F11, "23~")]
    [InlineData(Key.F12, "24~")]
    public void TheTildeFamilyUsesXtermsOwnNumbering(Key key, string rest)
    {
        Assert.Equal(Csi + rest, Sent(key, KeyModifiers.None));
    }

    /// <summary>The gaps are xterm's and not a slip: sixteen, twenty-two, twenty-seven, thirty and
    /// thirty-five are not function keys.</summary>
    [Fact]
    public void TheNumberingHasXtermsOwnGaps()
    {
        Assert.Equal(Csi + "25~", Sent(Key.F13, KeyModifiers.None));
        Assert.Equal(Csi + "26~", Sent(Key.F14, KeyModifiers.None));
        Assert.Equal(Csi + "28~", Sent(Key.F15, KeyModifiers.None));
        Assert.Equal(Csi + "29~", Sent(Key.F16, KeyModifiers.None));
        Assert.Equal(Csi + "31~", Sent(Key.F17, KeyModifiers.None));
        Assert.Equal(Csi + "34~", Sent(Key.F20, KeyModifiers.None));
    }

    [Theory]
    [InlineData(Key.F1, "P")]
    [InlineData(Key.F2, "Q")]
    [InlineData(Key.F3, "R")]
    [InlineData(Key.F4, "S")]
    public void TheFirstFourFunctionKeysAreTheSs3Family(Key key, string final)
    {
        Assert.Equal(Ss3 + final, Sent(key, KeyModifiers.None));
    }

    // ---- The mode the host changed ----

    /// <summary>
    /// Application cursor key mode, which is why a static table would be wrong. The same key, the
    /// same modifiers, a different answer — because the host said so.
    /// </summary>
    [Fact]
    public void TheArrowsChangeShapeWhenTheHostAsksForApplicationCursorKeys()
    {
        Emulator emulator = new(80, 24);

        Assert.Equal(Csi + "A", Sent(emulator, Key.Up, KeyModifiers.None));

        emulator.Feed(Bytes(Csi + "?1h"));

        Assert.True(emulator.ApplicationCursorKeys);
        Assert.Equal(Ss3 + "A", Sent(emulator, Key.Up, KeyModifiers.None));
        Assert.Equal(Ss3 + "H", Sent(emulator, Key.Home, KeyModifiers.None));

        emulator.Feed(Bytes(Csi + "?1l"));

        Assert.Equal(Csi + "A", Sent(emulator, Key.Up, KeyModifiers.None));
    }

    /// <summary>
    /// And a modified arrow is the CSI form either way. <c>ESC O A</c> has nowhere to put a
    /// parameter, so a client that sent the application form with a modifier attached would be
    /// sending something no program parses.
    /// </summary>
    [Fact]
    public void AModifiedArrowIsTheControlSequenceFormEvenInApplicationMode()
    {
        Emulator emulator = new(80, 24);
        emulator.Feed(Bytes(Csi + "?1h"));

        Assert.Equal(Csi + "1;5A", Sent(emulator, Key.Up, KeyModifiers.Control));
        Assert.Equal(Csi + "1;2A", Sent(emulator, Key.Up, KeyModifiers.Shift));
    }

    /// <summary>The numeric pad's own mode, which the host sets with an escape rather than a
    /// sequence.</summary>
    [Fact]
    public void TheKeypadChangesShapeWhenTheHostAsksForIt()
    {
        Emulator emulator = new(80, 24);

        Assert.Equal("7", Sent(emulator, Key.Keypad7, KeyModifiers.None));
        Assert.Equal("+", Sent(emulator, Key.KeypadAdd, KeyModifiers.None));

        emulator.Feed([(byte)Escape, (byte)'=']);

        Assert.True(emulator.ApplicationKeypad);
        Assert.Equal(Ss3 + "w", Sent(emulator, Key.Keypad7, KeyModifiers.None));
        Assert.Equal(Ss3 + "k", Sent(emulator, Key.KeypadAdd, KeyModifiers.None));
        Assert.Equal(Ss3 + "M", Sent(emulator, Key.KeypadEnter, KeyModifiers.None));

        emulator.Feed([(byte)Escape, (byte)'>']);

        Assert.False(emulator.ApplicationKeypad);
        Assert.Equal("7", Sent(emulator, Key.Keypad7, KeyModifiers.None));
    }

    [Fact]
    public void AResetPutsBothModesBack()
    {
        Emulator emulator = new(80, 24);

        emulator.Feed(Bytes(Csi + "?1h"));
        emulator.Feed([(byte)Escape, (byte)'=']);
        emulator.Feed([(byte)Escape, (byte)'c']);

        Assert.False(emulator.ApplicationCursorKeys);
        Assert.False(emulator.ApplicationKeypad);
    }

    // ---- Modifiers ----

    /// <summary>
    /// The parameter is one plus the bits: shift one, alt two, control four. Control-Left is
    /// <c>CSI 1 ; 5 D</c>, which is what a modern shell binds word-wise movement to — and its absence
    /// is why control-arrow silently does nothing on so many clients.
    /// </summary>
    [Theory]
    [InlineData(KeyModifiers.Shift, 2)]
    [InlineData(KeyModifiers.Alt, 3)]
    [InlineData(KeyModifiers.Shift | KeyModifiers.Alt, 4)]
    [InlineData(KeyModifiers.Control, 5)]
    [InlineData(KeyModifiers.Control | KeyModifiers.Shift, 6)]
    [InlineData(KeyModifiers.Control | KeyModifiers.Alt, 7)]
    [InlineData(KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Shift, 8)]
    public void TheModifierParameterIsOnePlusTheBits(KeyModifiers modifiers, int parameter)
    {
        Assert.Equal($"{Csi}1;{parameter}D", Sent(Key.Left, modifiers));
        Assert.Equal($"{Csi}3;{parameter}~", Sent(Key.Delete, modifiers));
    }

    /// <summary>An unmodified key writes no parameter at all, rather than writing one.</summary>
    [Fact]
    public void AnUnmodifiedKeyWritesNoParameter()
    {
        Assert.Equal(Csi + "D", Sent(Key.Left, KeyModifiers.None));
        Assert.Equal(Csi + "3~", Sent(Key.Delete, KeyModifiers.None));
        Assert.Equal(Ss3 + "P", Sent(Key.F1, KeyModifiers.None));
    }

    [Fact]
    public void AModifiedLowFunctionKeyMovesToTheControlSequenceForm()
    {
        Assert.Equal(Csi + "1;5P", Sent(Key.F1, KeyModifiers.Control));
        Assert.Equal(Csi + "1;2S", Sent(Key.F4, KeyModifiers.Shift));
    }

    // ---- The keys whose bytes are not a sequence ----

    /// <summary>
    /// Backspace sends delete and control-Backspace sends backspace. Getting this pair the wrong way
    /// round is why backspace prints <c>^?</c> on so many first attempts.
    /// </summary>
    [Fact]
    public void BackspaceSendsDeleteAndControlBackspaceSendsBackspace()
    {
        Assert.Equal(Delete, Sent(Key.Backspace, KeyModifiers.None));
        Assert.Equal("\b", Sent(Key.Backspace, KeyModifiers.Control));
        Assert.Equal(Escape + Delete, Sent(Key.Backspace, KeyModifiers.Alt));
    }

    [Fact]
    public void TabIsTabAndShiftTabIsBackTab()
    {
        Assert.Equal("\t", Sent(Key.Tab, KeyModifiers.None));
        Assert.Equal(Csi + "Z", Sent(Key.Tab, KeyModifiers.Shift));
    }

    [Fact]
    public void EnterAndEscapeAreTheirOwnBytes()
    {
        Assert.Equal("\r", Sent(Key.Enter, KeyModifiers.None));
        Assert.Equal(Escape.ToString(), Sent(Key.Escape, KeyModifiers.None));
        Assert.Equal(Escape.ToString() + "\r", Sent(Key.Enter, KeyModifiers.Alt));
    }

    [Fact]
    public void AKeyThisMapDoesNotKnowSendsNothing()
    {
        Assert.Equal(string.Empty, Sent(Key.None, KeyModifiers.None));
    }

    // ---- Text ----

    /// <summary>
    /// Alt is an escape prefix, because that is what a shell's line editor reads as meta. The meta-bit
    /// alternative is offered and is not the default: it is meaningless in any session that is not
    /// single-byte.
    /// </summary>
    [Fact]
    public void AltPrefixesAnEscapeByDefault()
    {
        Emulator emulator = new(80, 24);

        Assert.Equal(Escape + "b", Text(emulator, "b", KeyModifiers.Alt));

        emulator.AltSends = AltSends.Meta;

        Assert.Equal(((char)((byte)'b' | 0x80)).ToString(), Text(emulator, "b", KeyModifiers.Alt));
    }

    [Fact]
    public void AnOrdinaryCharacterIsItself()
    {
        Assert.Equal("a", Text(new Emulator(80, 24), "a", KeyModifiers.None));
        Assert.Equal("A", Text(new Emulator(80, 24), "A", KeyModifiers.Shift));
    }

    /// <summary>Text outside the first hundred and twenty-eight is sent as the session's encoding
    /// has it, and the meta bit is not applied to it — there is no high bit to spare.</summary>
    [Fact]
    public void TextBeyondAsciiIsSentAsTheSessionEncodesIt()
    {
        Emulator emulator = new(80, 24) { AltSends = AltSends.Meta };

        Assert.Equal("é", Text(emulator, "é", KeyModifiers.None));
        Assert.Equal(Escape + "é", Text(new Emulator(80, 24), "é", KeyModifiers.Alt));
    }

    // ---- The path it goes down ----

    /// <summary>
    /// Nothing here allocates, so a keystroke costs no more than the path QS27 measured.
    /// </summary>
    [Fact]
    public void EncodingAKeyAllocatesNothing()
    {
        Emulator emulator = new(80, 24);
        byte[] buffer = new byte[Keys.MaximumLength];

        for (int warm = 0; warm < 200; warm++)
        {
            emulator.Encode(Key.Up, KeyModifiers.Control, buffer);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();

        for (int stroke = 0; stroke < 1000; stroke++)
        {
            emulator.Encode(Key.Up, KeyModifiers.Control, buffer);
            emulator.Encode(Key.F12, KeyModifiers.None, buffer);
            emulator.Encode(Key.Backspace, KeyModifiers.Alt, buffer);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    /// <summary>Every key fits the buffer this map says to size, which is what makes that
    /// constant safe to build a stack buffer from.</summary>
    [Fact]
    public void EveryKeyFitsTheLengthThisMapPromises()
    {
        byte[] buffer = new byte[Keys.MaximumLength];

        foreach (Key key in Enum.GetValues<Key>())
        {
            foreach (KeyModifiers modifiers in AllModifiers())
            {
                foreach (bool cursorKeys in new[] { false, true })
                {
                    foreach (bool keypad in new[] { false, true })
                    {
                        int written = Keys.Encode(key, modifiers, cursorKeys, keypad, buffer);

                        Assert.InRange(written, 0, Keys.MaximumLength);
                    }
                }
            }
        }
    }

    private static IEnumerable<KeyModifiers> AllModifiers()
    {
        for (int bits = 0; bits <= 7; bits++)
        {
            yield return (KeyModifiers)bits;
        }
    }

    private static string Sent(Key key, KeyModifiers modifiers) =>
        Sent(new Emulator(80, 24), key, modifiers);

    private static string Sent(Emulator emulator, Key key, KeyModifiers modifiers)
    {
        Span<byte> buffer = stackalloc byte[Keys.MaximumLength];

        return Latin1(buffer[..emulator.Encode(key, modifiers, buffer)]);
    }

    private static string Text(Emulator emulator, string text, KeyModifiers modifiers)
    {
        Span<byte> buffer = stackalloc byte[Keys.MaximumLength];
        int written = emulator.Encode(text, modifiers, buffer);

        // Decoded as the session encodes it, except where the meta bit has been set - which is a
        // byte no UTF-8 decoder would accept, and the point of the setting.
        return modifiers.HasFlag(KeyModifiers.Alt) && emulator.AltSends == AltSends.Meta
            ? Latin1(buffer[..written])
            : Encoding.UTF8.GetString(buffer[..written]);
    }

    private static string Latin1(ReadOnlySpan<byte> bytes)
    {
        char[] characters = new char[bytes.Length];

        for (int index = 0; index < bytes.Length; index++)
        {
            characters[index] = (char)bytes[index];
        }

        return new string(characters);
    }

    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);
}
