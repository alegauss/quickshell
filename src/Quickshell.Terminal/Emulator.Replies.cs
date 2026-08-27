using System.Runtime.InteropServices;

namespace Quickshell.Terminal;

/// <summary>Which answer the terminal is sending. There is no other way to name one.</summary>
internal enum Answer : byte
{
    /// <summary>DA1: what kind of terminal this is.</summary>
    DeviceAttributes,

    /// <summary>DA2: which firmware, which is a constant here because there is no firmware.</summary>
    SecondaryDeviceAttributes,

    /// <summary>DSR: nothing is wrong.</summary>
    Ok,

    /// <summary>CPR: where the cursor is, in rows and columns.</summary>
    CursorPosition,

    /// <summary>DECXCPR: the same, with the page number a single-page terminal always answers 1 to.</summary>
    ExtendedCursorPosition,

    /// <summary>The window's size, in rows and columns of text.</summary>
    ScreenSize,

    /// <summary>
    /// DECRQSS: one setting, reported in its own syntax so the asker can send it straight back, or
    /// refused outright. Which setting arrives as the first number.
    /// </summary>
    SettingReport,

    /// <summary>A pointer event in the original encoding: three bytes, each its value plus 32.</summary>
    MouseLegacy,

    /// <summary>A pointer event in SGR encoding, DECSET 1006, that is not a release.</summary>
    MouseSgrPress,

    /// <summary>The same, released — which is a different final byte and nothing else.</summary>
    MouseSgrRelease,
}

public sealed partial class Emulator
{
    /// <summary>
    /// How much the terminal will owe the host before it stops answering.
    ///
    /// <para>A host can ask for the cursor position faster than anything drains the answers, and the
    /// bound is what stops that being a way to make this process grow without limit.</para>
    /// </summary>
    public const int MaximumReplyLength = 4096;

    /// <summary>
    /// The bytes that frame a reply, named rather than escaped.
    ///
    /// <para>Spelled as numbers on purpose. An escape in a string literal is one careless edit away
    /// from being a raw control byte instead, which is invisible in every diff and every editor —
    /// QS100 is what that cost, and there is no escape anywhere in this file for it to happen to.</para>
    /// </summary>
    private const byte Escape = 0x1B;
    private const byte Bracket = (byte)'[';
    private const byte Backslash = 0x5C;

    private readonly List<byte> _reply = [];

    /// <summary>
    /// What the terminal owes the host: bytes for whoever owns the pty to write back.
    ///
    /// <para>Always ASCII, and always built here from a constant and some numbers. See
    /// <see cref="Send"/> for why that is the whole security property.</para>
    /// </summary>
    public ReadOnlySpan<byte> Reply => CollectionsMarshal.AsSpan(_reply);

    /// <summary>Drops what has been written back, called by whoever wrote it.</summary>
    public void ClearReply() => _reply.Clear();

    /// <summary>
    /// Whether this session lets the host write the local clipboard. <b>Off, and per session.</b>
    ///
    /// <para>OSC 52 is how copying out of a remote <c>tmux</c> works at all, and equally how a
    /// compromised host silently replaces what the user is about to paste into a local shell. The
    /// read direction has no setting because it is not implemented.</para>
    /// </summary>
    public bool ClipboardWriteEnabled { get; set; }

    /// <summary>What the host last asked to put on the clipboard, for the layer that owns it.</summary>
    public string ClipboardWrite { get; private set; } = string.Empty;

    /// <summary>
    /// The only way a byte leaves this terminal.
    ///
    /// <para><b>It takes no text, and that is the point.</b> The rule this file exists to keep is
    /// that no reply may contain a byte the host supplied — because a host that can plant text and
    /// then ask for it back has a way to type at the user's shell. A method that accepted a string
    /// would put that one careless call site away; an enum and three integers cannot express it at
    /// all, so the next sequence someone adds here inherits the property for free.</para>
    /// </summary>
    private void Send(Answer answer, int first = 0, int second = 0, int third = 0)
    {
        // Bounded, and counted rather than dropped silently: a host asking faster than the pty
        // drains is a fact worth being able to see.
        if (_reply.Count >= MaximumReplyLength)
        {
            Unhandled++;
            return;
        }

        switch (answer)
        {
            case Answer.DeviceAttributes:
                // VT220 with ANSI colour. What programs check for before they use 256 colours.
                Csi();
                Literal("?62;22c");
                break;

            case Answer.SecondaryDeviceAttributes:
                Csi();
                Literal(">1;0;0c");
                break;

            case Answer.Ok:
                Csi();
                Literal("0n");
                break;

            case Answer.CursorPosition:
                Csi();
                Number(first);
                Literal(";");
                Number(second);
                Literal("R");
                break;

            case Answer.ExtendedCursorPosition:
                Csi();
                Literal("?");
                Number(first);
                Literal(";");
                Number(second);
                Literal(";1R");
                break;

            case Answer.ScreenSize:
                Csi();
                Literal("8;");
                Number(first);
                Literal(";");
                Number(second);
                Literal("t");
                break;

            case Answer.SettingReport:
                // A device control string rather than a control sequence, because that is the shape
                // DECRQSS asks in and the shape the asker parses.
                _reply.Add(Escape);
                _reply.Add((byte)'P');
                SettingReport((Setting)first);
                _reply.Add(Escape);
                _reply.Add(Backslash);
                break;

            case Answer.MouseLegacy:
                // Three bytes rather than three numbers, and the only place in this file where a
                // reply byte is arithmetic instead of a digit. The caller has already checked that
                // each sum fits, because a byte that overflowed here would name another cell.
                Csi();
                _reply.Add((byte)'M');
                _reply.Add((byte)(first + 32));
                _reply.Add((byte)(second + 32));
                _reply.Add((byte)(third + 32));
                break;

            case Answer.MouseSgrPress:
            case Answer.MouseSgrRelease:
                Csi();
                Literal("<");
                Number(first);
                Literal(";");
                Number(second);
                Literal(";");
                Number(third);
                Literal(answer == Answer.MouseSgrRelease ? "m" : "M");
                break;

            default:
                Unhandled++;
                break;
        }
    }

    /// <summary>The control sequence introducer, which nearly every reply begins with.</summary>
    private void Csi()
    {
        _reply.Add(Escape);
        _reply.Add(Bracket);
    }

    /// <summary>A constant from this file, which is the only text a reply is ever built from.</summary>
    private void Literal(string ascii)
    {
        foreach (char character in ascii)
        {
            _reply.Add((byte)character);
        }
    }

    private void Number(int value)
    {
        Span<char> digits = stackalloc char[11];

        if (value.TryFormat(digits, out int written, provider: System.Globalization.CultureInfo.InvariantCulture))
        {
            foreach (char digit in digits[..written])
            {
                _reply.Add((byte)digit);
            }
        }
    }

    /// <summary>
    /// The status reports: <c>CSI 5 n</c> and <c>CSI 6 n</c>, and the private cursor report.
    ///
    /// <para>Both answer with a constant or a number, which is what makes them safe to answer at
    /// all. Anything else asked for here is counted.</para>
    /// </summary>
    private void DeviceStatus(int request, bool priv)
    {
        switch (request)
        {
            case 5 when !priv:
                Send(Answer.Ok);
                break;

            case 6:
                Send(
                    priv ? Answer.ExtendedCursorPosition : Answer.CursorPosition,
                    ReportedRow(),
                    Buffer.CursorColumn + 1);
                break;

            default:
                Unhandled++;
                break;
        }
    }

    /// <summary>
    /// Which row number the host is told. Under DECOM it is relative to the top margin, because that
    /// is the coordinate system the host asked to be in and it is the one it will send back.
    /// </summary>
    private int ReportedRow() => OriginMode
        ? Buffer.CursorRow - MarginTop + 1
        : Buffer.CursorRow + 1;

    /// <summary>
    /// The window manipulations. Only the one that answers with the geometry is answered.
    ///
    /// <para><b>Reports 20 and 21 are the attack QS19 was about</b> and are refused here rather than
    /// left to a default. They ask for the icon label and the window title, and a host that has just
    /// set the title with OSC 2 can use them to have the terminal type its own text at the shell.
    /// This client sets titles and never reports them.</para>
    /// </summary>
    private void WindowOperation(int operation)
    {
        switch (operation)
        {
            case 18:
                Send(Answer.ScreenSize, Buffer.Rows, Buffer.Columns);
                break;

            case 20:
            case 21:
                Unhandled++;
                break;

            default:
                Unhandled++;
                break;
        }
    }

    /// <summary>
    /// OSC 52: the clipboard, off unless this session turned it on, and write-only when it is.
    ///
    /// <para>The read direction — the host asking what the user last copied — is not implemented and
    /// is not offered as a setting, because there is no session in which a remote machine needs to be
    /// told what is on a local clipboard.</para>
    /// </summary>
    private void SetClipboard(string argument)
    {
        int separator = argument.IndexOf(';', StringComparison.Ordinal);
        string data = separator < 0 ? argument : argument[(separator + 1)..];

        if (!ClipboardWriteEnabled || data is "?" || !TryDecode(data, out string text))
        {
            Unhandled++;
            return;
        }

        ClipboardWrite = text;
    }

    private static bool TryDecode(string base64, out string text)
    {
        byte[] decoded = new byte[(base64.Length / 4 * 3) + 3];

        if (Convert.TryFromBase64String(base64, decoded, out int written))
        {
            text = System.Text.Encoding.UTF8.GetString(decoded, 0, written);
            return true;
        }

        text = string.Empty;
        return false;
    }
}
