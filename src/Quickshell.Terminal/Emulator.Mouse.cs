namespace Quickshell.Terminal;

/// <summary>
/// Which mouse events a program has asked for. One value and not a set of flags, because the four
/// modes are alternatives: a program that enables 1002 after 1000 has replaced its request, and a
/// terminal that remembered both would report motion to something expecting only clicks.
/// </summary>
public enum MouseTracking : byte
{
    /// <summary>Nothing asked. The pointer belongs to the window.</summary>
    Off,

    /// <summary>DECSET 9, the X10 scheme: presses, with no release, no motion and no modifiers.</summary>
    PressOnly,

    /// <summary>DECSET 1000: presses and releases.</summary>
    PressRelease,

    /// <summary>DECSET 1002: presses, releases, and motion while a button is held.</summary>
    ButtonMotion,

    /// <summary>DECSET 1003: all of it, including motion with no button down.</summary>
    AnyMotion,
}

/// <summary>How a reported event is spelled on the wire.</summary>
public enum MouseEncoding : byte
{
    /// <summary>
    /// The original scheme: three bytes, each its value plus 32. Cannot express a coordinate past
    /// 223, which is the whole of the cliff this client refuses to fall off silently.
    /// </summary>
    Legacy,

    /// <summary>DECSET 1006: decimal parameters, no ceiling, and a final byte that says which of
    /// press and release happened.</summary>
    Sgr,
}

/// <summary>
/// Which button an event is about. <b>The values are the wire codes</b>, so the encoders add offsets
/// to a number rather than translating a name — one table instead of two that can disagree.
/// </summary>
public enum MouseButton : byte
{
    /// <summary>Button one.</summary>
    Left = 0,

    /// <summary>Button two, which is the wheel pressed on most mice.</summary>
    Middle = 1,

    /// <summary>Button three.</summary>
    Right = 2,

    /// <summary>No button down, which is a value the wire has: it is what motion reports carry.</summary>
    None = 3,

    /// <summary>The wheel arrives as a button. Up is four, down is five, in the high-bit range.</summary>
    WheelUp = 64,

    /// <summary>Down is five.</summary>
    WheelDown = 65,
}

/// <summary>What happened to the pointer.</summary>
public enum MouseAction : byte
{
    /// <summary>A button went down.</summary>
    Press,

    /// <summary>A button came back up.</summary>
    Release,

    /// <summary>The pointer moved, with or without a button held.</summary>
    Move,
}

/// <summary>
/// What was held while it happened. Shift is here to be <em>refused</em> rather than encoded — see
/// <see cref="Emulator.ReportMouse"/>.
/// </summary>
[Flags]
public enum MouseModifiers : byte
{
    /// <summary>Nothing held.</summary>
    None = 0,

    /// <summary>Held, and therefore the user's rather than the program's.</summary>
    Shift = 1,

    /// <summary>Alt, which the encoding calls meta. Bit eight of the button code.</summary>
    Meta = 2,

    /// <summary>Control. Bit sixteen of the button code.</summary>
    Control = 4,
}

/// <summary>
/// What became of an event handed to the terminal. <b>Five answers and not a boolean</b>, because the
/// caller has to tell "the program does not want this" from "the program wants it and we could not
/// say it" — conflating those is how the 223-column cliff stays invisible.
/// </summary>
public enum MouseDisposition : byte
{
    /// <summary>No program asked for this event. The window may do what it likes with it.</summary>
    NotAsked,

    /// <summary>Shift was held, so local selection wins. Never reported, whatever the mode.</summary>
    HeldForSelection,

    /// <summary>Motion that stayed inside the cell it was already in, and says nothing new.</summary>
    SameCell,

    /// <summary>Encoded, and waiting in <see cref="Emulator.Reply"/>.</summary>
    Reported,

    /// <summary>
    /// Wanted, and dropped: the program left this session on the legacy encoding and the coordinate
    /// is past column or row 223, which that encoding has no way to spell.
    /// </summary>
    BeyondLegacyReach,
}

public sealed partial class Emulator
{
    /// <summary>
    /// The last coordinate the legacy encoding can express: it adds 32 to a one-based coordinate and
    /// packs the sum into one byte, so 223 is where it runs out of room.
    /// </summary>
    public const int LegacyMouseLimit = 223;

    /// <summary>The bit that says an event is motion rather than a press.</summary>
    private const int MotionBit = 32;

    private MouseTracking _tracking;
    private MouseEncoding _encoding;
    private int _lastMouseColumn = -1;
    private int _lastMouseRow = -1;

    /// <summary>Which events a program has asked for, if any.</summary>
    public MouseTracking MouseReporting => _tracking;

    /// <summary>Which spelling the next report will use.</summary>
    public MouseEncoding MouseReportEncoding => _encoding;

    /// <summary>
    /// Hands the terminal one pointer event, in zero-based cells, and gets back what became of it.
    ///
    /// <para><b>Shift held is never reported</b>, in any mode. A full-screen program that has taken
    /// the mouse would otherwise make copying text out of it impossible, and there has to be one
    /// gesture a user can always fall back to. The modifier is in <see cref="MouseModifiers"/>
    /// because callers report what was held; it is answered with
    /// <see cref="MouseDisposition.HeldForSelection"/> and goes no further.</para>
    ///
    /// <para>Coordinates are clamped into the screen rather than refused, because a drag that leaves
    /// the window is an ordinary thing to do and the program wants to know the drag is still
    /// happening at the edge.</para>
    /// </summary>
    /// <returns>What was done with the event, which is also what tells the caller whether the window
    /// should now handle it locally.</returns>
    public MouseDisposition ReportMouse(
        MouseButton button,
        MouseAction action,
        int column,
        int row,
        MouseModifiers modifiers = MouseModifiers.None)
    {
        if (_tracking == MouseTracking.Off)
        {
            return MouseDisposition.NotAsked;
        }

        if ((modifiers & MouseModifiers.Shift) != 0)
        {
            return MouseDisposition.HeldForSelection;
        }

        if (!Wanted(button, action))
        {
            return MouseDisposition.NotAsked;
        }

        column = Math.Clamp(column, 0, Math.Max(0, Buffer.Columns - 1));
        row = Math.Clamp(row, 0, Math.Max(0, Buffer.Rows - 1));

        // Motion is only news when it crosses into another cell. A pixel-level stream turned into a
        // report each time is what makes mode 1003 saturate a link that is doing nothing else.
        if (action == MouseAction.Move && column == _lastMouseColumn && row == _lastMouseRow)
        {
            return MouseDisposition.SameCell;
        }

        _lastMouseColumn = column;
        _lastMouseRow = row;

        return Encode(button, action, column + 1, row + 1, modifiers);
    }

    /// <summary>
    /// Whether the mode a program asked for covers this event.
    ///
    /// <para>The wheel is a press in every mode that reports anything: it has no release to wait for,
    /// and a program that asked for clicks and got no scroll would look broken.</para>
    /// </summary>
    private bool Wanted(MouseButton button, MouseAction action)
    {
        if (button is MouseButton.WheelUp or MouseButton.WheelDown)
        {
            return action == MouseAction.Press;
        }

        return action switch
        {
            MouseAction.Press => true,
            MouseAction.Release => _tracking != MouseTracking.PressOnly,
            MouseAction.Move => _tracking switch
            {
                MouseTracking.ButtonMotion => button != MouseButton.None,
                MouseTracking.AnyMotion => true,
                _ => false,
            },
            _ => false,
        };
    }

    /// <summary>
    /// Builds the button byte and sends whichever of the two encodings is live.
    ///
    /// <para><b>The two encodings disagree about release, and that is the point of the newer one.</b>
    /// The legacy scheme has one code for "something was let go" and cannot say which button it was;
    /// SGR keeps the button and moves the distinction into the final byte, so a program can tell a
    /// right-click release from a left one.</para>
    /// </summary>
    private MouseDisposition Encode(
        MouseButton button,
        MouseAction action,
        int column,
        int row,
        MouseModifiers modifiers)
    {
        int code = (int)button;

        if (action == MouseAction.Move)
        {
            code |= MotionBit;
        }

        // X10 predates the modifier bits, and a program that asked for it is one that would misread
        // them as a different button.
        if (_tracking != MouseTracking.PressOnly)
        {
            if ((modifiers & MouseModifiers.Meta) != 0)
            {
                code |= 8;
            }

            if ((modifiers & MouseModifiers.Control) != 0)
            {
                code |= 16;
            }
        }

        if (_encoding == MouseEncoding.Sgr)
        {
            Send(
                action == MouseAction.Release ? Answer.MouseSgrRelease : Answer.MouseSgrPress,
                code,
                column,
                row);

            return MouseDisposition.Reported;
        }

        if (action == MouseAction.Release)
        {
            // All the legacy scheme can say. The button bits are replaced rather than kept, because
            // 3 is the code that means released and it occupies the same two bits.
            code = (code & ~3) | (int)MouseButton.None;
        }

        if (column > LegacyMouseLimit || row > LegacyMouseLimit)
        {
            // The falsification, made loud. Reporting the wrapped byte would tell the host a column
            // on the left-hand side of the window was clicked, and the host would act on it — a
            // wrong answer being worse than a missing one, exactly as with the replies in QS19.
            Unhandled++;
            return MouseDisposition.BeyondLegacyReach;
        }

        Send(Answer.MouseLegacy, code, column, row);

        return MouseDisposition.Reported;
    }

    /// <summary>
    /// The mouse half of DECSET and DECRESET.
    ///
    /// <para>Reset only turns tracking off when it names the mode that is actually live: a program
    /// leaving full-screen mode sends a reset for every mode it might have set, and one that switched
    /// 1000 off after asking for 1002 has not stopped wanting motion.</para>
    /// </summary>
    /// <returns>Whether the mode was one of the mouse modes at all.</returns>
    private bool MouseMode(int mode, bool set)
    {
        switch (mode)
        {
            case 9:
                Track(MouseTracking.PressOnly, set);
                return true;

            case 1000:
                Track(MouseTracking.PressRelease, set);
                return true;

            case 1002:
                Track(MouseTracking.ButtonMotion, set);
                return true;

            case 1003:
                Track(MouseTracking.AnyMotion, set);
                return true;

            case 1005:
                // The UTF-8 extension: a third encoding, which resolves the 223 ceiling by making a
                // coordinate a multi-byte sequence that programs then disagree about the length of.
                // SGR does the same job unambiguously, so this is refused rather than half-kept —
                // and counted, so a session that needed it is visible rather than merely quiet.
                Unhandled++;
                return true;

            case 1006:
                _encoding = set ? MouseEncoding.Sgr : MouseEncoding.Legacy;
                return true;

            default:
                return false;
        }
    }

    private void Track(MouseTracking tracking, bool set)
    {
        if (set)
        {
            _tracking = tracking;
        }
        else if (_tracking == tracking)
        {
            _tracking = MouseTracking.Off;
        }
        else
        {
            return;
        }

        // Whichever way it went, the remembered cell is about to be wrong: the next motion under a
        // new mode is news even if the pointer has not moved since the last one under the old.
        _lastMouseColumn = -1;
        _lastMouseRow = -1;
    }

    /// <summary>Puts the mouse back to nobody having asked, which is what a reset means here.</summary>
    private void ResetMouse()
    {
        _tracking = MouseTracking.Off;
        _encoding = MouseEncoding.Legacy;
        _lastMouseColumn = -1;
        _lastMouseRow = -1;
    }
}
