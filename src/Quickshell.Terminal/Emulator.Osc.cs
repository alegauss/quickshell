using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace Quickshell.Terminal;

public sealed partial class Emulator
{
    /// <summary>How much of an operating system command is kept before the rest is dropped.</summary>
    public const int MaximumOscLength = 4096;

    private readonly List<byte> _osc = [];

    /// <summary>
    /// Where a recognised payload is decoded, once, and reused for the next one.
    ///
    /// <para>The ceiling above is in bytes and this is in characters, and UTF-8 never produces more
    /// characters than it took bytes — so this is big enough for anything that got past the cap.</para>
    /// </summary>
    private readonly char[] _oscText = new char[MaximumOscLength];

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

    /// <summary>
    /// The command ended, and this is where the payload is finally looked at.
    ///
    /// <para><b>Nothing is decoded until a command this client acts on has been recognised.</b> The
    /// number is read from the raw bytes; a payload only becomes a string in the branches that keep
    /// one. A stream that is not a terminal stream at all — a <c>cat</c> of a binary file — is full
    /// of accidental introducers, and decoding each of them cost two megabytes of garbage per thirty
    /// megabytes of that stream before this was measured.</para>
    /// </summary>
    void IAnsiHandler.OscEnd()
    {
        if (_oscTruncated || _osc.Count == 0)
        {
            Unhandled++;
            _osc.Clear();
            return;
        }

        ReadOnlySpan<byte> payload = CollectionsMarshal.AsSpan(_osc);
        int separator = payload.IndexOf((byte)';');
        ReadOnlySpan<byte> number = separator < 0 ? payload : payload[..separator];

        if (TryCommand(number, out int command))
        {
            Dispatch(command, separator < 0 ? default : payload[(separator + 1)..]);
        }
        else
        {
            Unhandled++;
        }

        _osc.Clear();
    }

    /// <summary>
    /// The leading number, read as digits rather than through a decode and a parse.
    ///
    /// <para>Five digits is the ceiling because the largest command anyone has defined is two, and a
    /// number longer than that is not one this client will recognise anyway.</para>
    /// </summary>
    private static bool TryCommand(ReadOnlySpan<byte> digits, out int command)
    {
        command = 0;

        if (digits.IsEmpty || digits.Length > 5)
        {
            return false;
        }

        foreach (byte digit in digits)
        {
            if (digit is < (byte)'0' or > (byte)'9')
            {
                return false;
            }

            command = (command * 10) + (digit - '0');
        }

        return true;
    }

    /// <summary>
    /// Acts on one recognised command.
    ///
    /// <para><b>Every handler takes text and none of them takes a string.</b> Each of these is a piece
    /// of session state a host can set, and each is something a host can set ten thousand times a
    /// second. A string is built only where the state actually keeps one, and only where the new value
    /// differs from the one already there — a shell that re-sets the same title on every prompt is the
    /// ordinary case, not an unusual one.</para>
    /// </summary>
    private void Dispatch(int command, ReadOnlySpan<byte> payload)
    {
        int written = Encoding.UTF8.GetChars(payload, _oscText);
        ReadOnlySpan<char> argument = _oscText.AsSpan(0, written);

        switch (command)
        {
            case 0:
            case 2:
                Title = Same(Title, argument) ? Title : new string(argument);
                break;

            case 4:
                SetPaletteEntries(argument);
                break;

            case 10:
                Palette.Foreground = ReadColour(argument) ?? Palette.Foreground;
                break;

            case 11:
                Palette.Background = ReadColour(argument) ?? Palette.Background;
                break;

            case 12:
                Palette.Cursor = ReadColour(argument) ?? Palette.Cursor;
                break;

            case 7:
                WorkingDirectory = Same(WorkingDirectory, argument) ? WorkingDirectory : new string(argument);
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

    /// <summary>Whether a piece of state is already the value a host has just asked for.</summary>
    private static bool Same(string held, ReadOnlySpan<char> incoming) => incoming.SequenceEqual(held);

    /// <summary>
    /// OSC 4: pairs of index and colour. Several pairs in one command is ordinary, which is how a
    /// theme arrives in a single write rather than two hundred.
    /// </summary>
    private void SetPaletteEntries(ReadOnlySpan<char> argument)
    {
        while (!argument.IsEmpty)
        {
            if (!TryField(ref argument, out ReadOnlySpan<char> number)
                || !TryField(ref argument, out ReadOnlySpan<char> colour))
            {
                Unhandled++;
                return;
            }

            if (!int.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out int index)
                || index is < 0 or > 255
                || ReadColour(colour) is not Rgb value)
            {
                Unhandled++;
                continue;
            }

            Palette[(byte)index] = value;
        }
    }

    /// <summary>Takes the next semicolon-separated field, leaving the rest.</summary>
    private static bool TryField(ref ReadOnlySpan<char> text, out ReadOnlySpan<char> field)
    {
        if (text.IsEmpty)
        {
            field = default;
            return false;
        }

        int separator = text.IndexOf(';');

        if (separator < 0)
        {
            field = text;
            text = default;
        }
        else
        {
            field = text[..separator];
            text = text[(separator + 1)..];
        }

        return true;
    }

    /// <summary>
    /// The X colour spellings a host actually sends: <c>rgb:RR/GG/BB</c> in one to four hex digits
    /// per channel, and <c>#RRGGBB</c>. Anything else is counted rather than approximated — a colour
    /// guessed wrongly is a theme that looks broken, which is worse than one that did not change.
    /// </summary>
    private Rgb? ReadColour(ReadOnlySpan<char> text)
    {
        if (Parse(text) is Rgb colour)
        {
            return colour;
        }

        Unhandled++;
        return null;
    }

    private static Rgb? Parse(ReadOnlySpan<char> text)
    {
        if (text.StartsWith("rgb:", StringComparison.Ordinal))
        {
            ReadOnlySpan<char> channels = text[4..];
            Span<byte> values = stackalloc byte[3];

            for (int channel = 0; channel < 3; channel++)
            {
                int separator = channels.IndexOf('/');
                ReadOnlySpan<char> digits = separator < 0 ? channels : channels[..separator];

                if (Scale(digits) is not byte value || (separator < 0 && channel < 2))
                {
                    return null;
                }

                values[channel] = value;
                channels = separator < 0 ? default : channels[(separator + 1)..];
            }

            // A fourth channel means the host sent something this is not: rgb: takes three.
            return channels.IsEmpty ? new Rgb(values[0], values[1], values[2]) : null;
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
    private static byte? Scale(ReadOnlySpan<char> digits)
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
    private void SetHyperlink(ReadOnlySpan<char> argument)
    {
        int separator = argument.IndexOf(';');
        ReadOnlySpan<char> uri = separator < 0 ? default : argument[(separator + 1)..];

        _pen = _pen with { Link = uri.IsEmpty ? 0 : Buffer.InternLink(uri) };
    }
}
