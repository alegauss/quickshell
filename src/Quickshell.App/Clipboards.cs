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
    public string Read()
    {
        try
        {
            return System.Windows.Clipboard.ContainsText()
                ? System.Windows.Clipboard.GetText()
                : string.Empty;
        }
        catch (Exception)
        {
            return string.Empty;
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
