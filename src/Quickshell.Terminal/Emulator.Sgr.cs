namespace Quickshell.Terminal;

public sealed partial class Emulator
{
    /// <summary>
    /// Applies one SGR sequence.
    ///
    /// <para><b>Every attribute has its own reset code, and none of them is a blanket reset.</b>
    /// Programs turn one thing off and expect the rest to survive — <c>SGR 24</c> ends an underline
    /// and must leave the bold beside it alone — so each of these clears exactly its own bit.</para>
    ///
    /// <para><b>Extended colour comes in two spellings and both are real.</b> <c>38;5;n</c> and
    /// <c>38;2;r;g;b</c> put the arguments in separate parameter groups; <c>38:5:n</c> and
    /// <c>38:2::r:g:b</c> put them in one, sub-separated, and that is what a modern host emits. The
    /// colon form also has an optional colour-space slot that is almost always empty, which is why
    /// the reader below counts from the end rather than from a fixed offset.</para>
    /// </summary>
    private void ApplySgr(in CsiParameters parameters)
    {
        if (parameters.Count == 0)
        {
            _pen = Pen.Default;
            return;
        }

        for (int group = 0; group < parameters.Count; group++)
        {
            ReadOnlySpan<int> values = parameters.Group(group);
            int code = values.Length == 0 || values[0] < 0 ? 0 : values[0];

            // The colon spelling: everything the attribute needs is inside this one group.
            if (values.Length > 1 && code is 38 or 48 or 58)
            {
                ApplyExtended(code, values[1..]);
                continue;
            }

            // The semicolon spelling: the arguments are the groups that follow, so they are read
            // here and the loop is advanced past them.
            if (code is 38 or 48 or 58 && group + 1 < parameters.Count)
            {
                group += ReadExtendedFromGroups(code, parameters, group + 1);
                continue;
            }

            ApplySimple(code);
        }
    }

    private void ApplySimple(int code)
    {
        switch (code)
        {
            case 0:
                _pen = Pen.Default;
                break;

            case 1:
                _pen = _pen.Set(CellFlags.Bold);
                break;

            case 2:
                _pen = _pen.Set(CellFlags.Faint);
                break;

            case 3:
                _pen = _pen.Set(CellFlags.Slant);
                break;

            case 4:
                _pen = _pen with { Underline = UnderlineStyle.Single };
                break;

            case 5:
            case 6:
                // Slow and rapid blink. One flag: nothing downstream draws two rates, and claiming
                // to tell them apart would be a distinction no pixel ever shows.
                _pen = _pen.Set(CellFlags.Blink);
                break;

            case 7:
                _pen = _pen.Set(CellFlags.Inverse);
                break;

            case 8:
                _pen = _pen.Set(CellFlags.Conceal);
                break;

            case 9:
                _pen = _pen.Set(CellFlags.Strike);
                break;

            case 21:
                _pen = _pen with { Underline = UnderlineStyle.Double };
                break;

            case 22:
                _pen = _pen.Clear(CellFlags.Bold).Clear(CellFlags.Faint);
                break;

            case 23:
                _pen = _pen.Clear(CellFlags.Slant);
                break;

            case 24:
                _pen = _pen with { Underline = UnderlineStyle.None };
                break;

            case 25:
                _pen = _pen.Clear(CellFlags.Blink);
                break;

            case 27:
                _pen = _pen.Clear(CellFlags.Inverse);
                break;

            case 28:
                _pen = _pen.Clear(CellFlags.Conceal);
                break;

            case 29:
                _pen = _pen.Clear(CellFlags.Strike);
                break;

            case >= 30 and <= 37:
                _pen = _pen with { Foreground = Colour.Indexed((byte)(code - 30)) };
                break;

            case 39:
                // Back to the theme's, not to whatever the theme's currently is.
                _pen = _pen with { Foreground = Colour.Default };
                break;

            case >= 40 and <= 47:
                _pen = _pen with { Background = Colour.Indexed((byte)(code - 40)) };
                break;

            case 49:
                _pen = _pen with { Background = Colour.Default };
                break;

            case 53:
                _pen = _pen.Set(CellFlags.Overline);
                break;

            case 55:
                _pen = _pen.Clear(CellFlags.Overline);
                break;

            case 59:
                _pen = _pen with { Underline = UnderlineStyle.None };
                break;

            case >= 90 and <= 97:
                _pen = _pen with { Foreground = Colour.Indexed((byte)(code - 90 + 8)) };
                break;

            case >= 100 and <= 107:
                _pen = _pen with { Background = Colour.Indexed((byte)(code - 100 + 8)) };
                break;

            default:
                Unhandled++;
                break;
        }
    }

    /// <summary>The colon spelling, where the arguments are sub-parameters of one group.</summary>
    private void ApplyExtended(int slot, ReadOnlySpan<int> arguments)
    {
        Colour? colour = ReadExtended(arguments);

        if (colour is null)
        {
            Unhandled++;
            return;
        }

        Assign(slot, colour.Value);
    }

    /// <summary>
    /// The semicolon spelling, where the arguments are the groups after this one. Answers how many
    /// of them were consumed, so the caller can step past them.
    /// </summary>
    private int ReadExtendedFromGroups(int slot, in CsiParameters parameters, int first)
    {
        int kind = parameters.Value(first, -1);

        if (kind == 5 && first + 1 < parameters.Count)
        {
            Assign(slot, Colour.Indexed((byte)Math.Clamp(parameters.Value(first + 1, 0), 0, 255)));
            return 2;
        }

        if (kind == 2 && first + 3 < parameters.Count)
        {
            Assign(slot, Colour.Direct(
                Channel(parameters.Value(first + 1, 0)),
                Channel(parameters.Value(first + 2, 0)),
                Channel(parameters.Value(first + 3, 0))));
            return 4;
        }

        Unhandled++;
        return 1;
    }

    /// <summary>
    /// One extended colour out of a run of sub-parameters.
    ///
    /// <para>Counted from the end, because the colon form has an optional colour-space identifier
    /// after the <c>2</c> that hosts leave empty: <c>38:2::255:0:0</c> and <c>38:2:255:0:0</c> are
    /// the same red, and a reader that took a fixed offset gets one of them wrong.</para>
    /// </summary>
    private static Colour? ReadExtended(ReadOnlySpan<int> arguments)
    {
        if (arguments.Length == 0)
        {
            return null;
        }

        if (arguments[0] == 5 && arguments.Length >= 2)
        {
            return Colour.Indexed((byte)Math.Clamp(arguments[^1], 0, 255));
        }

        if (arguments[0] == 2 && arguments.Length >= 4)
        {
            return Colour.Direct(Channel(arguments[^3]), Channel(arguments[^2]), Channel(arguments[^1]));
        }

        return null;
    }

    private void Assign(int slot, Colour colour)
    {
        _pen = slot switch
        {
            38 => _pen with { Foreground = colour },
            48 => _pen with { Background = colour },

            // 58 is the underline's own colour. Nothing draws it yet, so it is counted rather than
            // stored: a field written and never read is a claim the picture does not support.
            _ => Counted(),
        };

        Pen Counted()
        {
            Unhandled++;
            return _pen;
        }
    }

    private static byte Channel(int value) => (byte)Math.Clamp(value, 0, 255);
}
