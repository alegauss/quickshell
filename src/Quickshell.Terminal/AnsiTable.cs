namespace Quickshell.Terminal;

/// <summary>The states of Paul Williams' DEC ANSI parser, in his own names.</summary>
internal enum ParserState : byte
{
    Ground = 0,
    Escape,
    EscapeIntermediate,
    CsiEntry,
    CsiParam,
    CsiIntermediate,
    CsiIgnore,
    DcsEntry,
    DcsParam,
    DcsIntermediate,
    DcsPassthrough,
    DcsIgnore,
    OscString,
    SosPmApcString,
}

/// <summary>What a transition does on the way. Also his names, so the table reads as the diagram.</summary>
internal enum ParserAction : byte
{
    /// <summary>Nothing. The state changed and that was the whole of it.</summary>
    None = 0,
    Ignore,
    Print,
    Execute,
    Clear,
    Collect,
    Param,
    EscapeDispatch,
    CsiDispatch,
    Put,
    OscPut,

    /// <summary>Not a transition. The sentinel that proves every byte was accounted for.</summary>
    Unassigned = 15,
}

/// <summary>
/// The transition table: state and byte in, action and next state out.
///
/// <para><b>Why a table.</b> It is provably complete — all 256 byte values have an entry in every
/// state, checked at construction, so no input exists that the parser has no answer for. It is
/// auditable against a published document rather than against the author's memory of one. And it
/// costs one lookup and one dispatch per byte, with no branch tree whose depth depends on what
/// arrived.</para>
///
/// <para><b>Three deviations from the published diagram, each deliberate.</b></para>
///
/// <para><b>1. There are no single-byte C1 controls.</b> Williams has 0x80–0x9F acting as controls
/// from any state. This terminal is UTF-8, where those same bytes are continuation bytes in the
/// middle of ordinary characters — so honouring them would turn the second byte of every accented
/// letter into a control. They print in ground and are payload elsewhere. The two-byte <c>ESC</c>
/// forms of the same controls are unaffected and are how a host reaches them.</para>
///
/// <para><b>2. A colon is a parameter separator, not an error.</b> Williams sends <c>0x3A</c> to
/// csi_ignore. Every program that emits true colour or a styled underline spells it with colons, so
/// a parser that discards those sequences discards what modern hosts actually send.</para>
///
/// <para><b>3. BEL ends an OSC.</b> xterm's terminator, and universal in practice; the diagram only
/// has ST.</para>
/// </summary>
internal static class AnsiTable
{
    internal const int StateCount = 14;

    /// <summary>One byte per (state, input): the action in the high nibble, the next state in the low.</summary>
    private static readonly byte[] Transitions = Build();

    /// <summary>The action to take for this state and byte.</summary>
    internal static ParserAction ActionFor(ParserState state, byte input) =>
        (ParserAction)(Transitions[((int)state * 256) + input] >> 4);

    /// <summary>The state to move to for this state and byte.</summary>
    internal static ParserState StateFor(ParserState state, byte input) =>
        (ParserState)(Transitions[((int)state * 256) + input] & 0x0F);

    /// <summary>Every (state, byte) pair the table was never told about. Empty, or the table is wrong.</summary>
    internal static List<(ParserState State, byte Input)> Unassigned()
    {
        List<(ParserState, byte)> holes = [];

        for (int state = 0; state < StateCount; state++)
        {
            for (int input = 0; input < 256; input++)
            {
                if ((Transitions[(state * 256) + input] >> 4) == (byte)ParserAction.Unassigned)
                {
                    holes.Add(((ParserState)state, (byte)input));
                }
            }
        }

        return holes;
    }

    private static byte[] Build()
    {
        byte[] table = new byte[StateCount * 256];
        Array.Fill(table, (byte)((byte)ParserAction.Unassigned << 4));

        foreach (ParserState state in Enum.GetValues<ParserState>())
        {
            // The anywhere transitions first, so a state's own rules overwrite them where they
            // disagree. This is the diagram's own precedence.
            Set(table, state, 0x18, 0x18, ParserAction.Execute, ParserState.Ground);
            Set(table, state, 0x1A, 0x1A, ParserAction.Execute, ParserState.Ground);
            Set(table, state, 0x1B, 0x1B, ParserAction.None, ParserState.Escape);
        }

        Ground(table);
        Escape(table);
        EscapeIntermediate(table);
        CsiEntry(table);
        CsiParam(table);
        CsiIntermediate(table);
        CsiIgnore(table);
        DcsEntry(table);
        DcsParam(table);
        DcsIntermediate(table);
        DcsPassthrough(table);
        DcsIgnore(table);
        OscString(table);
        SosPmApcString(table);

        // The completeness claim, enforced where it is made. A table with a hole in it is a parser
        // with an input it has no answer for, and finding that out at construction beats finding it
        // out when a host sends the byte.
        for (int entry = 0; entry < table.Length; entry++)
        {
            if ((table[entry] >> 4) == (byte)ParserAction.Unassigned)
            {
                throw new InvalidOperationException(
                    $"the transition table has no entry for {(ParserState)(entry / 256)} and byte " +
                    $"0x{entry % 256:X2}: every state must answer for all 256 values");
            }
        }

        return table;
    }

    private static void Ground(byte[] table)
    {
        Controls(table, ParserState.Ground, ParserAction.Execute);

        // 0x20 through 0xFF print. Above 0x7F that is the UTF-8 deviation: those bytes are parts of
        // characters, and the layer that decodes them is the one that knows it.
        Set(table, ParserState.Ground, 0x20, 0xFF, ParserAction.Print, ParserState.Ground);
        Set(table, ParserState.Ground, 0x7F, 0x7F, ParserAction.Ignore, ParserState.Ground);
        Reassert(table, ParserState.Ground);
    }

    private static void Escape(byte[] table)
    {
        Controls(table, ParserState.Escape, ParserAction.Execute);

        Set(table, ParserState.Escape, 0x20, 0x2F, ParserAction.Collect, ParserState.EscapeIntermediate);
        Set(table, ParserState.Escape, 0x30, 0x7E, ParserAction.EscapeDispatch, ParserState.Ground);
        Set(table, ParserState.Escape, 0x50, 0x50, ParserAction.None, ParserState.DcsEntry);        // P
        Set(table, ParserState.Escape, 0x58, 0x58, ParserAction.None, ParserState.SosPmApcString);  // X
        Set(table, ParserState.Escape, 0x5B, 0x5B, ParserAction.None, ParserState.CsiEntry);        // [
        Set(table, ParserState.Escape, 0x5D, 0x5D, ParserAction.None, ParserState.OscString);       // ]
        Set(table, ParserState.Escape, 0x5E, 0x5F, ParserAction.None, ParserState.SosPmApcString);  // ^ _
        Set(table, ParserState.Escape, 0x7F, 0x7F, ParserAction.Ignore, ParserState.Escape);
        Set(table, ParserState.Escape, 0x80, 0xFF, ParserAction.Ignore, ParserState.Ground);
        Reassert(table, ParserState.Escape);
    }

    private static void EscapeIntermediate(byte[] table)
    {
        Controls(table, ParserState.EscapeIntermediate, ParserAction.Execute);

        Set(table, ParserState.EscapeIntermediate, 0x20, 0x2F, ParserAction.Collect, ParserState.EscapeIntermediate);
        Set(table, ParserState.EscapeIntermediate, 0x30, 0x7E, ParserAction.EscapeDispatch, ParserState.Ground);
        Set(table, ParserState.EscapeIntermediate, 0x7F, 0x7F, ParserAction.Ignore, ParserState.EscapeIntermediate);
        Set(table, ParserState.EscapeIntermediate, 0x80, 0xFF, ParserAction.Ignore, ParserState.Ground);
        Reassert(table, ParserState.EscapeIntermediate);
    }

    private static void CsiEntry(byte[] table)
    {
        Controls(table, ParserState.CsiEntry, ParserAction.Execute);

        Set(table, ParserState.CsiEntry, 0x20, 0x2F, ParserAction.Collect, ParserState.CsiIntermediate);
        Set(table, ParserState.CsiEntry, 0x30, 0x3B, ParserAction.Param, ParserState.CsiParam);
        Set(table, ParserState.CsiEntry, 0x3C, 0x3F, ParserAction.Collect, ParserState.CsiParam);
        Set(table, ParserState.CsiEntry, 0x40, 0x7E, ParserAction.CsiDispatch, ParserState.Ground);
        Set(table, ParserState.CsiEntry, 0x7F, 0x7F, ParserAction.Ignore, ParserState.CsiEntry);
        Set(table, ParserState.CsiEntry, 0x80, 0xFF, ParserAction.Ignore, ParserState.Ground);
        Reassert(table, ParserState.CsiEntry);
    }

    private static void CsiParam(byte[] table)
    {
        Controls(table, ParserState.CsiParam, ParserAction.Execute);

        Set(table, ParserState.CsiParam, 0x20, 0x2F, ParserAction.Collect, ParserState.CsiIntermediate);
        Set(table, ParserState.CsiParam, 0x30, 0x3B, ParserAction.Param, ParserState.CsiParam);
        Set(table, ParserState.CsiParam, 0x3C, 0x3F, ParserAction.Ignore, ParserState.CsiIgnore);
        Set(table, ParserState.CsiParam, 0x40, 0x7E, ParserAction.CsiDispatch, ParserState.Ground);
        Set(table, ParserState.CsiParam, 0x7F, 0x7F, ParserAction.Ignore, ParserState.CsiParam);
        Set(table, ParserState.CsiParam, 0x80, 0xFF, ParserAction.Ignore, ParserState.Ground);
        Reassert(table, ParserState.CsiParam);
    }

    private static void CsiIntermediate(byte[] table)
    {
        Controls(table, ParserState.CsiIntermediate, ParserAction.Execute);

        Set(table, ParserState.CsiIntermediate, 0x20, 0x2F, ParserAction.Collect, ParserState.CsiIntermediate);
        Set(table, ParserState.CsiIntermediate, 0x30, 0x3F, ParserAction.Ignore, ParserState.CsiIgnore);
        Set(table, ParserState.CsiIntermediate, 0x40, 0x7E, ParserAction.CsiDispatch, ParserState.Ground);
        Set(table, ParserState.CsiIntermediate, 0x7F, 0x7F, ParserAction.Ignore, ParserState.CsiIntermediate);
        Set(table, ParserState.CsiIntermediate, 0x80, 0xFF, ParserAction.Ignore, ParserState.Ground);
        Reassert(table, ParserState.CsiIntermediate);
    }

    private static void CsiIgnore(byte[] table)
    {
        Controls(table, ParserState.CsiIgnore, ParserAction.Execute);

        Set(table, ParserState.CsiIgnore, 0x20, 0x3F, ParserAction.Ignore, ParserState.CsiIgnore);
        Set(table, ParserState.CsiIgnore, 0x40, 0x7E, ParserAction.None, ParserState.Ground);
        Set(table, ParserState.CsiIgnore, 0x7F, 0x7F, ParserAction.Ignore, ParserState.CsiIgnore);
        Set(table, ParserState.CsiIgnore, 0x80, 0xFF, ParserAction.Ignore, ParserState.Ground);
        Reassert(table, ParserState.CsiIgnore);
    }

    private static void DcsEntry(byte[] table)
    {
        Controls(table, ParserState.DcsEntry, ParserAction.Ignore);

        Set(table, ParserState.DcsEntry, 0x20, 0x2F, ParserAction.Collect, ParserState.DcsIntermediate);
        Set(table, ParserState.DcsEntry, 0x30, 0x3B, ParserAction.Param, ParserState.DcsParam);
        Set(table, ParserState.DcsEntry, 0x3C, 0x3F, ParserAction.Collect, ParserState.DcsParam);
        Set(table, ParserState.DcsEntry, 0x40, 0x7E, ParserAction.None, ParserState.DcsPassthrough);
        Set(table, ParserState.DcsEntry, 0x7F, 0x7F, ParserAction.Ignore, ParserState.DcsEntry);
        Set(table, ParserState.DcsEntry, 0x80, 0xFF, ParserAction.Ignore, ParserState.Ground);
        Reassert(table, ParserState.DcsEntry);
    }

    private static void DcsParam(byte[] table)
    {
        Controls(table, ParserState.DcsParam, ParserAction.Ignore);

        Set(table, ParserState.DcsParam, 0x20, 0x2F, ParserAction.Collect, ParserState.DcsIntermediate);
        Set(table, ParserState.DcsParam, 0x30, 0x3B, ParserAction.Param, ParserState.DcsParam);
        Set(table, ParserState.DcsParam, 0x3C, 0x3F, ParserAction.Ignore, ParserState.DcsIgnore);
        Set(table, ParserState.DcsParam, 0x40, 0x7E, ParserAction.None, ParserState.DcsPassthrough);
        Set(table, ParserState.DcsParam, 0x7F, 0x7F, ParserAction.Ignore, ParserState.DcsParam);
        Set(table, ParserState.DcsParam, 0x80, 0xFF, ParserAction.Ignore, ParserState.Ground);
        Reassert(table, ParserState.DcsParam);
    }

    private static void DcsIntermediate(byte[] table)
    {
        Controls(table, ParserState.DcsIntermediate, ParserAction.Ignore);

        Set(table, ParserState.DcsIntermediate, 0x20, 0x2F, ParserAction.Collect, ParserState.DcsIntermediate);
        Set(table, ParserState.DcsIntermediate, 0x30, 0x3F, ParserAction.Ignore, ParserState.DcsIgnore);
        Set(table, ParserState.DcsIntermediate, 0x40, 0x7E, ParserAction.None, ParserState.DcsPassthrough);
        Set(table, ParserState.DcsIntermediate, 0x7F, 0x7F, ParserAction.Ignore, ParserState.DcsIntermediate);
        Set(table, ParserState.DcsIntermediate, 0x80, 0xFF, ParserAction.Ignore, ParserState.Ground);
        Reassert(table, ParserState.DcsIntermediate);
    }

    private static void DcsPassthrough(byte[] table)
    {
        Controls(table, ParserState.DcsPassthrough, ParserAction.Put);

        Set(table, ParserState.DcsPassthrough, 0x20, 0xFF, ParserAction.Put, ParserState.DcsPassthrough);
        Set(table, ParserState.DcsPassthrough, 0x7F, 0x7F, ParserAction.Ignore, ParserState.DcsPassthrough);
        Reassert(table, ParserState.DcsPassthrough);
    }

    private static void DcsIgnore(byte[] table)
    {
        Controls(table, ParserState.DcsIgnore, ParserAction.Ignore);

        Set(table, ParserState.DcsIgnore, 0x20, 0xFF, ParserAction.Ignore, ParserState.DcsIgnore);
        Reassert(table, ParserState.DcsIgnore);
    }

    private static void OscString(byte[] table)
    {
        Controls(table, ParserState.OscString, ParserAction.Ignore);

        // BEL ends it. The diagram has only ST; xterm has had BEL since the beginning and every
        // program that sets a window title uses it.
        Set(table, ParserState.OscString, 0x07, 0x07, ParserAction.None, ParserState.Ground);
        Set(table, ParserState.OscString, 0x20, 0xFF, ParserAction.OscPut, ParserState.OscString);
        Reassert(table, ParserState.OscString);
    }

    private static void SosPmApcString(byte[] table)
    {
        Controls(table, ParserState.SosPmApcString, ParserAction.Ignore);

        Set(table, ParserState.SosPmApcString, 0x20, 0xFF, ParserAction.Ignore, ParserState.SosPmApcString);
        Reassert(table, ParserState.SosPmApcString);
    }

    /// <summary>The C0 range every state answers for, minus the three the anywhere rules own.</summary>
    private static void Controls(byte[] table, ParserState state, ParserAction action)
    {
        Set(table, state, 0x00, 0x17, action, state);
        Set(table, state, 0x19, 0x19, action, state);
        Set(table, state, 0x1C, 0x1F, action, state);
    }

    /// <summary>
    /// Puts the three anywhere transitions back after a state has written over their range. CAN,
    /// SUB and ESC interrupt whatever is in progress, from every state, which is what makes a
    /// terminal recoverable from a half-sent sequence.
    /// </summary>
    private static void Reassert(byte[] table, ParserState state)
    {
        Set(table, state, 0x18, 0x18, ParserAction.Execute, ParserState.Ground);
        Set(table, state, 0x1A, 0x1A, ParserAction.Execute, ParserState.Ground);
        Set(table, state, 0x1B, 0x1B, ParserAction.None, ParserState.Escape);
    }

    private static void Set(byte[] table, ParserState state, int first, int last,
                            ParserAction action, ParserState next)
    {
        for (int input = first; input <= last; input++)
        {
            table[((int)state * 256) + input] = (byte)(((byte)action << 4) | (byte)next);
        }
    }
}
