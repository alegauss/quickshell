namespace Quickshell.Terminal;

/// <summary>
/// Bytes from a host into the events a terminal acts on, by Paul Williams' DEC ANSI state machine.
///
/// <para><b>It holds no terminal state at all.</b> No cursor, no colours, no modes — only where it
/// is in a sequence. What the events mean belongs to the layer above, and that separation is what
/// lets the emulator be tested by handing it events directly, with no bytes involved.</para>
///
/// <para><b>Nothing is allocated per parse.</b> The buffers are fixed and made once; the handler is
/// a generic parameter so its calls devirtualise; parameters and payloads reach the handler as
/// spans over those buffers. Bytes in, structs out.</para>
///
/// <para>It survives being fed one byte at a time: the state and the collected parameters live
/// across calls, because a sequence can straddle reads exactly as a character can.</para>
/// </summary>
public sealed class AnsiParser
{
    /// <summary>How many parameter values are kept. Beyond this the sequence is reported truncated.</summary>
    public const int MaximumParameters = 32;

    /// <summary>How many intermediate bytes are kept. Two is all any real sequence uses.</summary>
    public const int MaximumIntermediates = 2;

    private readonly int[] _values = new int[MaximumParameters];
    private readonly byte[] _groupLengths = new byte[MaximumParameters];
    private readonly byte[] _intermediates = new byte[MaximumIntermediates];

    private ParserState _state = ParserState.Ground;
    private int _valueCount;
    private int _groupCount;
    private int _intermediateCount;
    private bool _truncated;
    private bool _groupOpen;
    private bool _valueOpen;
    private bool _groupPending;

    /// <summary>Where the parser is in a sequence. Ground means nothing is in progress.</summary>
    public bool InGround => _state == ParserState.Ground;

    /// <summary>Throws away any half-received sequence, which is what a reconnect is.</summary>
    public void Reset()
    {
        _state = ParserState.Ground;
        Clear();
    }

    /// <summary>
    /// Feeds bytes. Every one of them has an answer: the table is complete over all 256 values in
    /// all fourteen states, so there is no input this returns without having handled.
    /// </summary>
    public void Parse<THandler>(ReadOnlySpan<byte> input, ref THandler handler)
        where THandler : IAnsiHandler
    {
        int index = 0;

        while (index < input.Length)
        {
            // Runs of one action are batched: a screenful of text, or a long OSC payload, is one
            // call rather than one per byte. The layer above wants the run anyway - to decode it,
            // or to read a title out of it - and a per-byte callback would hand it the job of
            // reassembling what this already had contiguous.
            ParserAction batched = AnsiTable.ActionFor(_state, input[index]);

            if (batched is ParserAction.Print or ParserAction.OscPut or ParserAction.Put)
            {
                ParserState state = _state;
                int run = index;

                while (run < input.Length && AnsiTable.ActionFor(state, input[run]) == batched
                       && AnsiTable.StateFor(state, input[run]) == state)
                {
                    run++;
                }

                switch (batched)
                {
                    case ParserAction.Print:
                        handler.Print(input[index..run]);
                        break;

                    case ParserAction.OscPut:
                        handler.OscPut(input[index..run]);
                        break;

                    default:
                        handler.DcsPut(input[index..run]);
                        break;
                }

                index = run;
                continue;
            }

            byte value = input[index++];
            Step(value, ref handler);
        }
    }

    private void Step<THandler>(byte input, ref THandler handler)
        where THandler : IAnsiHandler
    {
        ParserAction action = AnsiTable.ActionFor(_state, input);
        ParserState next = AnsiTable.StateFor(_state, input);

        // The dispatch actions read the state that was collected, so they run before the exit and
        // entry work below rearranges it.
        switch (action)
        {
            case ParserAction.Print:
                handler.Print(new ReadOnlySpan<byte>(in input));
                break;

            case ParserAction.Execute:
                handler.Execute(input);
                break;

            case ParserAction.Collect:
                Collect(input);
                break;

            case ParserAction.Param:
                Param(input);
                break;

            case ParserAction.EscapeDispatch:
                handler.EscapeDispatch(Intermediates, input);
                break;

            case ParserAction.CsiDispatch:
                CloseGroup();
                handler.CsiDispatch(Parameters(), Intermediates, input);
                break;

            case ParserAction.Put:
                handler.DcsPut(new ReadOnlySpan<byte>(in input));
                break;

            case ParserAction.OscPut:
                handler.OscPut(new ReadOnlySpan<byte>(in input));
                break;

            case ParserAction.Unassigned:
                throw new InvalidOperationException(
                    $"the transition table has no entry for {_state} and byte 0x{input:X2}, " +
                    "which its own construction check should have made impossible");

            default:
                break;
        }

        if (next != _state)
        {
            Leave(_state, ref handler);
            Enter(next, input, ref handler);
            _state = next;
        }
    }

    /// <summary>What a state does on the way out. Only the two string states have anything to say.</summary>
    private static void Leave<THandler>(ParserState state, ref THandler handler)
        where THandler : IAnsiHandler
    {
        switch (state)
        {
            case ParserState.OscString:
                handler.OscEnd();
                break;

            case ParserState.DcsPassthrough:
                handler.DcsUnhook();
                break;

            default:
                break;
        }
    }

    /// <summary>What a state does on the way in: clear the collection, or announce a string.</summary>
    private void Enter<THandler>(ParserState state, byte input, ref THandler handler)
        where THandler : IAnsiHandler
    {
        switch (state)
        {
            case ParserState.Escape:
            case ParserState.CsiEntry:
            case ParserState.DcsEntry:
                Clear();
                break;

            case ParserState.OscString:
                handler.OscStart();
                break;

            case ParserState.DcsPassthrough:
                CloseGroup();
                handler.DcsHook(Parameters(), Intermediates, input);
                break;

            default:
                break;
        }
    }

    private ReadOnlySpan<byte> Intermediates => _intermediates.AsSpan(0, _intermediateCount);

    private CsiParameters Parameters() =>
        new(_values.AsSpan(0, _valueCount), _groupLengths.AsSpan(0, _groupCount), _truncated);

    private void Clear()
    {
        _valueCount = 0;
        _groupCount = 0;
        _intermediateCount = 0;
        _truncated = false;
        _groupOpen = false;
        _valueOpen = false;
        _groupPending = false;
    }

    private void Collect(byte input)
    {
        if (_intermediateCount < MaximumIntermediates)
        {
            _intermediates[_intermediateCount++] = input;
        }
        else
        {
            _truncated = true;
        }
    }

    /// <summary>
    /// One byte of a parameter list.
    ///
    /// <para>A semicolon ends a group; a colon ends a value inside the group it is in. An omitted
    /// value is stored as -1 rather than 0, because a parameter a host left out means "the default"
    /// and the default is not always zero — <c>CSI ;5H</c> is row default, column five, and reading
    /// that leading blank as a zero moves the cursor somewhere the host did not ask for.</para>
    /// </summary>
    private void Param(byte input)
    {
        switch (input)
        {
            case (byte)';':
                if (!_groupOpen)
                {
                    OpenGroup();
                    PushValue(-1);
                }

                _groupOpen = false;
                _valueOpen = false;
                _groupPending = true;
                break;

            case (byte)':':
                if (!_groupOpen)
                {
                    OpenGroup();
                }

                if (!_valueOpen)
                {
                    PushValue(-1);
                }

                _valueOpen = false;
                break;

            default:
                if (!_groupOpen)
                {
                    OpenGroup();
                }

                if (!_valueOpen)
                {
                    PushValue(0);
                    _valueOpen = true;
                }

                Accumulate((byte)(input - (byte)'0'));
                break;
        }
    }

    private void Accumulate(byte digit)
    {
        if (_valueCount == 0)
        {
            return;
        }

        int current = _values[_valueCount - 1];

        // Bounded rather than wrapped: a host sending a hundred digits gets a large number and not
        // a negative one, and nothing above this is a parameter any sequence means.
        _values[_valueCount - 1] = current > 65535 ? current : (current * 10) + digit;
    }

    private void OpenGroup()
    {
        if (_groupCount >= MaximumParameters)
        {
            _truncated = true;
            return;
        }

        _groupLengths[_groupCount++] = 0;
        _groupOpen = true;
        _groupPending = false;
    }

    private void PushValue(int initial)
    {
        if (_valueCount >= MaximumParameters || _groupCount == 0)
        {
            _truncated = true;
            return;
        }

        _values[_valueCount++] = initial;
        _groupLengths[_groupCount - 1]++;
    }

    /// <summary>
    /// Settles the parameter list at the moment of dispatch. A trailing semicolon leaves a group
    /// the host meant and never filled — <c>CSI 1;H</c> is two parameters, the second defaulted —
    /// so the pending group is written here rather than lost.
    /// </summary>
    private void CloseGroup()
    {
        if (_groupPending)
        {
            OpenGroup();
            PushValue(-1);
        }

        _groupOpen = false;
        _valueOpen = false;
        _groupPending = false;
    }
}
