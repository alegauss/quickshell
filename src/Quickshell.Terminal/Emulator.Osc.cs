using System.Globalization;
using System.Text;

namespace Quickshell.Terminal;

public sealed partial class Emulator
{
    /// <summary>How much of an operating system command is kept before the rest is dropped.</summary>
    public const int MaximumOscLength = 4096;

    private readonly List<byte> _osc = [];
    private bool _oscTruncated;

    /// <summary>
    /// The window title, as the host set it. This is what a tab strip shows, which is how a user
    /// tells one tab from another.
    /// </summary>
    public string Title { get; private set; } = string.Empty;

    /// <summary>
    /// Where the host says it is. The whole mechanism behind opening a new tab in the same place,
    /// and the reason a terminal can offer that at all without guessing from a prompt.
    /// </summary>
    public string WorkingDirectory { get; private set; } = string.Empty;

    void IAnsiHandler.OscStart()
    {
        FlushText();
        _osc.Clear();
        _oscTruncated = false;
    }

    void IAnsiHandler.OscPut(ReadOnlySpan<byte> bytes)
    {
        // Bounded, because the payload's length is the host's choice. A title of ten megabytes is
        // not a title, and the alternative to a ceiling here is a remote machine deciding how much
        // memory this process holds.
        int room = MaximumOscLength - _osc.Count;

        if (bytes.Length > room)
        {
            _oscTruncated = true;
            bytes = bytes[..Math.Max(0, room)];
        }

        _osc.AddRange(bytes);
    }

    void IAnsiHandler.OscEnd()
    {
        if (_oscTruncated || _osc.Count == 0)
        {
            Unhandled++;
            _osc.Clear();
            return;
        }

        // Decoded here and not as it arrived: a title is text, and the encoding it is in is the
        // session's, which is the same thing the printed path is told.
        string payload = Encoding.UTF8.GetString([.. _osc]);
        _osc.Clear();

        int separator = payload.IndexOf(';', StringComparison.Ordinal);
        string number = separator < 0 ? payload : payload[..separator];
        string argument = separator < 0 ? string.Empty : payload[(separator + 1)..];

        if (!int.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out int command))
        {
            Unhandled++;
            return;
        }

        Dispatch(command, argument);
    }

    private void Dispatch(int command, string argument)
    {
        switch (command)
        {
            case 0:
            case 2:
                Title = argument;
                break;

            case 4:
                SetPaletteEntries(argument);
                break;

            case 10:
                ReadColour(argument, colour => Palette.Foreground = colour);
                break;

            case 11:
                ReadColour(argument, colour => Palette.Background = colour);
                break;

            case 12:
                ReadColour(argument, colour => Palette.Cursor = colour);
                break;

            case 7:
                WorkingDirectory = argument;
                break;

            case 8:
                SetHyperlink(argument);
                break;

            case 52:
                SetClipboard(argument);
                break;

            default:
                Unhandled++;
                break;
        }
    }

    /// <summary>
    /// OSC 4: pairs of index and colour. Several pairs in one command is ordinary, which is how a
    /// theme arrives in a single write rather than two hundred.
    /// </summary>
    private void SetPaletteEntries(string argument)
    {
        string[] parts = argument.Split(';');

        for (int pair = 0; pair + 1 < parts.Length; pair += 2)
        {
            if (!int.TryParse(parts[pair], NumberStyles.None, CultureInfo.InvariantCulture, out int index)
                || index is < 0 or > 255)
            {
                Unhandled++;
                continue;
            }

            ReadColour(parts[pair + 1], colour => Palette[(byte)index] = colour);
        }
    }

    /// <summary>
    /// The X colour spellings a host actually sends: <c>rgb:RR/GG/BB</c> in one to four hex digits
    /// per channel, and <c>#RRGGBB</c>. Anything else is counted rather than approximated — a colour
    /// guessed wrongly is a theme that looks broken, which is worse than one that did not change.
    /// </summary>
    private void ReadColour(string text, Action<Rgb> apply)
    {
        if (Parse(text) is Rgb colour)
        {
            apply(colour);
            return;
        }

        Unhandled++;
    }

    private static Rgb? Parse(string text)
    {
        if (text.StartsWith("rgb:", StringComparison.Ordinal))
        {
            string[] channels = text[4..].Split('/');

            if (channels.Length != 3)
            {
                return null;
            }

            byte[] values = new byte[3];

            for (int channel = 0; channel < 3; channel++)
            {
                if (Scale(channels[channel]) is not byte value)
                {
                    return null;
                }

                values[channel] = value;
            }

            return new Rgb(values[0], values[1], values[2]);
        }

        if (text.StartsWith('#') && text.Length == 7)
        {
            return Scale(text[1..3]) is byte red && Scale(text[3..5]) is byte green
                   && Scale(text[5..7]) is byte blue
                ? new Rgb(red, green, blue)
                : null;
        }

        return null;
    }

    /// <summary>
    /// One channel, however many hex digits the host chose to spell it in.
    ///
    /// <para>X allows one to four, and they are scaled rather than truncated: <c>rgb:f/f/f</c> is
    /// white, not <c>0f0f0f</c>. Truncating is how a one-digit spelling comes out almost black.</para>
    /// </summary>
    private static byte? Scale(string digits)
    {
        if (digits.Length is < 1 or > 4
            || !int.TryParse(digits, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int value))
        {
            return null;
        }

        int maximum = (1 << (4 * digits.Length)) - 1;
        return (byte)(value * 255 / maximum);
    }

    /// <summary>
    /// OSC 8: a hyperlink over the run of cells that follows, until the next OSC 8 with no URI.
    ///
    /// <para>The cell carries an identifier into a table the buffer holds, so the renderer needs no
    /// change at all: a link is a fact about a cell, not a thing drawn differently.</para>
    /// </summary>
    private void SetHyperlink(string argument)
    {
        int separator = argument.IndexOf(';', StringComparison.Ordinal);
        string uri = separator < 0 ? string.Empty : argument[(separator + 1)..];

        _pen = _pen with { Link = uri.Length == 0 ? 0 : Buffer.InternLink(uri) };
    }
}
