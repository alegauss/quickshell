namespace Quickshell.App;

/// <summary>
/// Where a copy puts text and a paste takes it from — QS180's one seam, read and write.
///
/// <para><b>The window touches the clipboard in two places and nowhere else</b>, and both go
/// through this. What a test is about is what the window does with the text — cleaning it,
/// bracketing it, asking before a newline runs — and none of that needs the one object every process
/// on the desktop shares. A test that used it anyway was a test that erased what the person at the
/// machine had copied, and that went red whenever something else on the desk opened the clipboard
/// between its write and its read.</para>
/// </summary>
public interface IClipboard
{
    /// <summary>
    /// The text it holds, or empty where it holds none — or cannot be read right now, which on a
    /// shared clipboard is a state and not a failure.
    /// </summary>
    string Read();

    /// <summary>Puts text on it.</summary>
    /// <returns>Whether it took, which it may not while another process holds the clipboard open.</returns>
    bool Write(string text);

    /// <summary>
    /// The text it holds, and whether it could be asked at all — which <see cref="Read"/> cannot
    /// say, since a busy clipboard and an empty one both read as nothing (QS183).
    /// </summary>
    /// <returns>False where the clipboard is held open by somebody else right now.</returns>
    bool TryRead(out string text)
    {
        text = Read();

        return true;
    }
}

/// <summary>
/// A tenth of a second of patience with a clipboard somebody else is holding (QS183).
///
/// <para><b>A clipboard held by a listener is held for milliseconds.</b> A phone-link service and a
/// remote desktop's bridge each open it just after every change, so a paste pressed in that moment
/// read empty and the keystroke was lost — the user pressed it again, it worked, and they learnt this
/// client's paste is unreliable. Asking again for a tenth of a second is long enough to outlast them
/// and short enough that nobody pressing a chord notices. Past that bound the failure is real, and
/// the caller says so rather than letting it vanish.</para>
/// </summary>
public static class Patiently
{
    /// <summary>How long a busy clipboard is waited for before the failure is the user's to see.</summary>
    public static readonly TimeSpan Bound = TimeSpan.FromMilliseconds(100);

    /// <summary>How long between asks, so ten of them fit inside the bound.</summary>
    private static readonly TimeSpan Between = TimeSpan.FromMilliseconds(10);

    /// <summary>Reads, asking again while the clipboard is busy and the bound allows.</summary>
    /// <returns>False where it stayed busy for the whole bound.</returns>
    public static bool Read(IClipboard clipboard, out string text)
    {
        ArgumentNullException.ThrowIfNull(clipboard);

        DateTime until = DateTime.UtcNow + Bound;

        while (true)
        {
            if (clipboard.TryRead(out text))
            {
                return true;
            }

            if (DateTime.UtcNow >= until)
            {
                return false;
            }

            Thread.Sleep(Between);
        }
    }

    /// <summary>Writes, asking again while the clipboard is busy and the bound allows.</summary>
    /// <returns>False where it stayed busy for the whole bound.</returns>
    public static bool Write(IClipboard clipboard, string text)
    {
        ArgumentNullException.ThrowIfNull(clipboard);

        DateTime until = DateTime.UtcNow + Bound;

        while (!clipboard.Write(text))
        {
            if (DateTime.UtcNow >= until)
            {
                return false;
            }

            Thread.Sleep(Between);
        }

        return true;
    }
}

/// <summary>
/// The system clipboard, which is what the client uses and what every test but one does not.
///
/// <para><b>Neither call throws.</b> Another process holding the clipboard open happens and
/// passes — a clipboard manager, a phone-link service, a remote desktop's bridge each open it just
/// after it changes — and a copy lost to that is not worth a dialog: the selection is still on
/// screen to try again with.</para>
/// </summary>
public sealed class SystemClipboard : IClipboard
{
    /// <summary>The one there is.</summary>
    public static SystemClipboard Instance { get; } = new();

    private SystemClipboard()
    {
    }

    /// <inheritdoc/>
    public string Read() => TryRead(out string text) ? text : string.Empty;

    /// <inheritdoc/>
    public bool TryRead(out string text)
    {
        try
        {
            text = System.Windows.Clipboard.ContainsText()
                ? System.Windows.Clipboard.GetText()
                : string.Empty;

            return true;
        }
        catch (Exception)
        {
            // CLIPBRD_E_CANT_OPEN and its kin: somebody else has it open. Busy, and not empty.
            text = string.Empty;

            return false;
        }
    }

    /// <inheritdoc/>
    public bool Write(string text)
    {
        try
        {
            System.Windows.Clipboard.SetText(text);

            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
