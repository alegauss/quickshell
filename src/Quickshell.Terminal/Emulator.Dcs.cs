namespace Quickshell.Terminal;

/// <summary>Which setting a DECRQSS request named, or that it named none this client tracks.</summary>
internal enum Setting : byte
{
    /// <summary>Nothing here reports this one, which is answered as invalid rather than ignored.</summary>
    Unknown,

    /// <summary>SGR: the pen the next character would be printed with.</summary>
    Graphics,

    /// <summary>DECSTBM: the scrolling region.</summary>
    ScrollRegion,

    /// <summary>DECSCL: which conformance level this claims, which is the one DA1 claims.</summary>
    ConformanceLevel,
}

public sealed partial class Emulator
{
    /// <summary>
    /// How much of a device control string is kept. The same reasoning as the operating system
    /// commands: the length is the host's choice, and a setting name is a few bytes long.
    /// </summary>
    public const int MaximumDcsLength = 64;

    private readonly List<byte> _dcs = [];
    private bool _requestingSetting;
    private bool _dcsTruncated;

    /// <summary>
    /// A device control string is starting. Only one is answered here — DECRQSS, which arrives under
    /// its own intermediate — and the rest are counted when they end.
    /// </summary>
    void IAnsiHandler.DcsHook(in CsiParameters parameters, ReadOnlySpan<byte> intermediates, byte final)
    {
        FlushText();
        _dcs.Clear();
        _dcsTruncated = false;
        _requestingSetting = final == (byte)'q'
            && intermediates.Length == 1
            && intermediates[0] == (byte)'$';
    }

    void IAnsiHandler.DcsPut(ReadOnlySpan<byte> bytes)
    {
        int room = MaximumDcsLength - _dcs.Count;

        if (bytes.Length > room)
        {
            _dcsTruncated = true;
            bytes = bytes[..Math.Max(0, room)];
        }

        _dcs.AddRange(bytes);
    }

    /// <summary>
    /// The string ended, and this is where the whole of QS20 happens.
    ///
    /// <para><b>An unrecognised request is answered as invalid, never ignored.</b> The asker is
    /// blocked on a reply, and silence leaves it there until whatever timeout it has runs out — which
    /// is how a shell prompt comes up a second late every time it starts. Saying "no" costs five
    /// bytes and ends the wait immediately.</para>
    /// </summary>
    void IAnsiHandler.DcsUnhook()
    {
        if (!_requestingSetting)
        {
            Unhandled++;
            _dcs.Clear();
            return;
        }

        Setting setting = _dcsTruncated ? Setting.Unknown : Recognise(_dcs);
        _dcs.Clear();
        _requestingSetting = false;

        Send(Answer.SettingReport, (int)setting);

        if (setting == Setting.Unknown)
        {
            Unhandled++;
        }
    }

    /// <summary>
    /// Which setting a name means. The names are byte sequences from DEC's own tables, so they are
    /// compared as bytes and never decoded — nothing here needs to know what encoding the session is
    /// in to tell <c>r</c> from <c>"p</c>.
    /// </summary>
    private static Setting Recognise(List<byte> name)
    {
        ReadOnlySpan<byte> bytes = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(name);

        return bytes switch
        {
            [(byte)'m'] => Setting.Graphics,
            [(byte)'r'] => Setting.ScrollRegion,
            [(byte)'"', (byte)'p'] => Setting.ConformanceLevel,
            _ => Setting.Unknown,
        };
    }

    /// <summary>
    /// The report itself, in the setting's own syntax, which is what DECRQSS asks for: a host reads
    /// the answer by sending it straight back.
    /// </summary>
    private void SettingReport(Setting setting)
    {
        switch (setting)
        {
            case Setting.Graphics:
                Literal("1$r");
                GraphicsReport();
                Literal("m");
                break;

            case Setting.ScrollRegion:
                Literal("1$r");
                Number(MarginTop + 1);
                Literal(";");
                Number(MarginBottom + 1);
                Literal("r");
                break;

            case Setting.ConformanceLevel:
                // The same number DA1 reports, because two different answers to what this terminal
                // is would be a claim it cannot both keep.
                Literal("1$r62;1\"p");
                break;

            default:
                // The five bytes that end the wait.
                Literal("0$r");
                break;
        }
    }

    /// <summary>
    /// The pen as SGR parameters.
    ///
    /// <para>Built from the pen's own fields and not from anything the host sent: a colour comes back
    /// as the number its index or its channels are, so the reply carries no host bytes even though it
    /// describes what the host asked for.</para>
    /// </summary>
    private void GraphicsReport()
    {
        Literal("0");

        Attribute(CellFlags.Bold, 1);
        Attribute(CellFlags.Faint, 2);
        Attribute(CellFlags.Slant, 3);
        Attribute(CellFlags.Blink, 5);
        Attribute(CellFlags.Inverse, 7);
        Attribute(CellFlags.Conceal, 8);
        Attribute(CellFlags.Strike, 9);
        Attribute(CellFlags.Overline, 53);

        if (_pen.Underline != UnderlineStyle.None)
        {
            // Reported with its style, because SGR 4 alone would say the curly one is a straight one
            // and the host would set it back that way.
            Literal(";4:");
            Number((int)_pen.Underline);
        }

        ColourReport(_pen.Foreground, 30, 38, 90);
        ColourReport(_pen.Background, 40, 48, 100);
    }

    private void Attribute(CellFlags flag, int parameter)
    {
        if ((_pen.Flags & flag) != 0)
        {
            Literal(";");
            Number(parameter);
        }
    }

    /// <summary>
    /// One colour, in whichever of the three spellings says what it actually is. A default colour is
    /// left out entirely rather than reported as a concrete one, because those are different states
    /// and reporting the wrong one is how a host pins the theme's colour into the text.
    /// </summary>
    private void ColourReport(Colour colour, int basic, int extended, int bright)
    {
        switch (colour.Kind)
        {
            case ColourKind.Indexed when colour.Index < 8:
                Literal(";");
                Number(basic + colour.Index);
                break;

            case ColourKind.Indexed when colour.Index < 16:
                Literal(";");
                Number(bright + colour.Index - 8);
                break;

            case ColourKind.Indexed:
                Literal(";");
                Number(extended);
                Literal(";5;");
                Number(colour.Index);
                break;

            case ColourKind.Direct:
                Literal(";");
                Number(extended);
                Literal(";2;");
                Number(colour.Rgb.Red);
                Literal(";");
                Number(colour.Rgb.Green);
                Literal(";");
                Number(colour.Rgb.Blue);
                break;

            default:
                break;
        }
    }
}
