namespace Quickshell.Terminal;

/// <summary>
/// What the parser emits. Nothing here means anything: <c>CsiDispatch</c> with a final byte of
/// <c>'H'</c> is a cursor move only to the layer above, and that separation is what lets the
/// emulator be tested by handing it events directly, with no bytes involved.
///
/// <para>Implement it on a <b>struct</b> and pass it by reference. The parser takes the handler as
/// a generic parameter so the calls devirtualise, which is what keeps the parse path free of the
/// interface dispatch and the allocation a boxed handler would cost per byte.</para>
/// </summary>
public interface IAnsiHandler
{
    /// <summary>
    /// Printable bytes, batched into the longest run the input allowed. They are bytes and not
    /// characters on purpose: a UTF-8 sequence passes through here whole, and decoding it is the
    /// job of the layer that knows what encoding the session is in.
    /// </summary>
    void Print(ReadOnlySpan<byte> text);

    /// <summary>A C0 control that acts immediately: carriage return, line feed, tab, bell.</summary>
    void Execute(byte control);

    /// <summary>An escape sequence with no CSI: <c>ESC 7</c>, <c>ESC ( B</c> and the rest.</summary>
    void EscapeDispatch(ReadOnlySpan<byte> intermediates, byte final);

    /// <summary>A control sequence, with everything it collected on the way to its final byte.</summary>
    void CsiDispatch(in CsiParameters parameters, ReadOnlySpan<byte> intermediates, byte final);

    /// <summary>An operating system command is starting. Nothing has arrived yet.</summary>
    void OscStart();

    /// <summary>Bytes of an operating system command's payload, in the runs they arrived in.</summary>
    void OscPut(ReadOnlySpan<byte> bytes);

    /// <summary>The operating system command ended, by BEL or by ST.</summary>
    void OscEnd();

    /// <summary>A device control string is starting, with the parameters it was introduced by.</summary>
    void DcsHook(in CsiParameters parameters, ReadOnlySpan<byte> intermediates, byte final);

    /// <summary>Bytes of a device control string's payload.</summary>
    void DcsPut(ReadOnlySpan<byte> bytes);

    /// <summary>The device control string ended.</summary>
    void DcsUnhook();
}
