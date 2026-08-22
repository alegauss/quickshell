using System.Text;
using Quickshell.Terminal;
using Xunit;

namespace Quickshell.Terminal.Tests;

/// <summary>
/// The parser, against the sequences hosts actually send and the bytes a hostile one might.
/// </summary>
public sealed class AnsiParserTests
{
    /// <summary>
    /// The falsification this design names, and the reason the parser is a table at all: no input
    /// exists that it has no answer for. Every one of the fourteen states is asked about every one
    /// of the 256 byte values.
    /// </summary>
    [Fact]
    public void EveryByteHasATransitionInEveryState()
    {
        // The table refuses to build with a hole in it, so reaching this at all is half the claim;
        // the count is the other half, and it is stated so a shrunk table cannot pass quietly.
        Recorder recorder = new();
        AnsiParser parser = new();

        for (int value = 0; value <= 0xFF; value++)
        {
            parser.Reset();

            // Drive the parser into each state, then feed the byte. Nothing may throw.
            foreach (byte[] prefix in Prefixes)
            {
                parser.Reset();
                parser.Parse(prefix, ref recorder);
                parser.Parse([(byte)value], ref recorder);
            }
        }

        Assert.Empty(AnsiTable.Unassigned());
    }

    /// <summary>Every state, reached by the bytes that reach it.</summary>
    private static byte[][] Prefixes =>
    [
        [],                                       // ground
        [0x1B],                                   // escape
        [0x1B, (byte)' '],                        // escape intermediate
        [0x1B, (byte)'['],                        // csi entry
        [0x1B, (byte)'[', (byte)'1'],             // csi param
        [0x1B, (byte)'[', (byte)' '],             // csi intermediate
        [0x1B, (byte)'[', (byte)'1', 0x3C],       // csi ignore
        [0x1B, (byte)'P'],                        // dcs entry
        [0x1B, (byte)'P', (byte)'1'],             // dcs param
        [0x1B, (byte)'P', (byte)' '],             // dcs intermediate
        [0x1B, (byte)'P', (byte)'q'],             // dcs passthrough
        [0x1B, (byte)'P', (byte)'1', 0x3C],       // dcs ignore
        [0x1B, (byte)']'],                        // osc string
        [0x1B, (byte)'X'],                        // sos/pm/apc string
    ];

    // ---- Text ----

    [Fact]
    public void PlainTextIsOnePrintPerRunAndNotOnePerByte()
    {
        Recorder recorder = Feed("hello world");

        Assert.Equal(["print:hello world"], recorder.Events);
    }

    /// <summary>
    /// A UTF-8 sequence passes through whole. The parser is byte-oriented deliberately: no
    /// continuation byte can be mistaken for a control, because none of them is one.
    /// </summary>
    [Fact]
    public void MultiByteCharactersPassThroughAsPrintableBytes()
    {
        Recorder recorder = Feed("中文 \U0001F600");

        Assert.Equal(["print:中文 \U0001F600"], recorder.Events);
    }

    [Fact]
    public void ControlsAreExecutedAndTextAroundThemIsNot()
    {
        Recorder recorder = Feed("ab\r\ncd\t");

        Assert.Equal(["print:ab", "execute:0D", "execute:0A", "print:cd", "execute:09"], recorder.Events);
    }

    // ---- Control sequences ----

    [Fact]
    public void ACursorPositionArrivesWithItsTwoParameters()
    {
        Recorder recorder = Feed("[12;40H");

        Assert.Equal(["csi:H params=[12][40]"], recorder.Events);
    }

    [Fact]
    public void AnOmittedParameterIsADefaultAndNotAZero()
    {
        // CSI ;5H is row default, column five. Reading the blank as zero moves the cursor somewhere
        // the host did not ask for.
        Recorder recorder = Feed("[;5H");

        Assert.Equal(["csi:H params=[-1][5]"], recorder.Events);
    }

    [Fact]
    public void ATrailingSeparatorStillLeavesTheParameterItMeant()
    {
        Recorder recorder = Feed("[1;H");

        Assert.Equal(["csi:H params=[1][-1]"], recorder.Events);
    }

    [Fact]
    public void ASequenceWithNoParametersHasNone()
    {
        Recorder recorder = Feed("[H");

        Assert.Equal(["csi:H params="], recorder.Events);
    }

    /// <summary>
    /// The colon deviation, and the reason for it: this is how every program that emits true colour
    /// actually spells it. Williams sends the colon to csi_ignore, which would discard the sequence.
    /// </summary>
    [Fact]
    public void TrueColourArrivesAsOneGroupOfSubParameters()
    {
        Recorder recorder = Feed("[38:2::255:0:0m");

        Assert.Equal(["csi:m params=[38:2:-1:255:0:0]"], recorder.Events);
    }

    [Fact]
    public void AStyledUnderlineIsAGroupOfTwoAndNotTwoParameters()
    {
        Recorder recorder = Feed("[4:3m");

        Assert.Equal(["csi:m params=[4:3]"], recorder.Events);
    }

    [Fact]
    public void ColonsAndSemicolonsInOneSequenceKeepTheirOwnMeanings()
    {
        Recorder recorder = Feed("[4:3;38;5;1m");

        Assert.Equal(["csi:m params=[4:3][38][5][1]"], recorder.Events);
    }

    [Fact]
    public void APrivateMarkerAndAnIntermediateBothReachTheDispatch()
    {
        Recorder recorder = Feed("[?25h");
        Assert.Equal(["csi:h inter=? params=[25]"], recorder.Events);

        recorder = Feed("[0 q");
        Assert.Equal(["csi:q inter=  params=[0]"], recorder.Events);
    }

    [Fact]
    public void AnEscapeSequenceWithAnIntermediateDispatchesWithIt()
    {
        Recorder recorder = Feed("(B");

        Assert.Equal(["esc:B inter=("], recorder.Events);
    }

    // ---- Strings ----

    [Fact]
    public void AnOscRunsFromItsStartToItsTerminator()
    {
        Recorder recorder = Feed("]0;a title");

        Assert.Equal(["osc-start", "osc:0;a title", "osc-end"], recorder.Events);
    }

    /// <summary>
    /// BEL is the terminator every program uses; ST is the one the diagram has. ST is two bytes and
    /// the second of them is an escape dispatch, which the layer above ignores - the string already
    /// ended when the escape took the parser out of the string state.
    /// </summary>
    [Fact]
    public void AnOscEndsOnEitherTerminator()
    {
        Assert.Equal(["osc-start", "osc:2;x", "osc-end"], Feed("]2;x").Events);
        Assert.Equal(["osc-start", "osc:2;x", "osc-end", "esc:\\"], Feed("]2;x\\").Events);
    }

    [Fact]
    public void ADeviceControlStringHooksItsParametersAndPassesItsPayload()
    {
        Recorder recorder = Feed("P1;2q payload\\");

        Assert.Equal(["dcs-hook:q params=[1][2]", "dcs: payload", "dcs-unhook", "esc:\\"], recorder.Events);
    }

    [Fact]
    public void AnApcStringIsSwallowedWhole()
    {
        Recorder recorder = Feed("before_anything at all\\after");

        Assert.Equal(["print:before", "esc:\\", "print:after"], recorder.Events);
    }

    // ---- Recovery ----

    [Fact]
    public void CancelAbandonsAHalfSentSequence()
    {
        Recorder recorder = Feed("[12;3ok");

        Assert.Equal(["execute:18", "print:ok"], recorder.Events);
    }

    [Fact]
    public void AnEscapeInsideASequenceStartsANewOne()
    {
        Recorder recorder = Feed("[12;[3A");

        Assert.Equal(["csi:A params=[3]"], recorder.Events);
    }

    [Fact]
    public void MoreParametersThanTheBufferHoldsIsReportedRatherThanGrown()
    {
        string many = "[" + string.Join(";", Enumerable.Repeat("1", 40)) + "m";
        Recorder recorder = Feed(many);

        Assert.Single(recorder.Events);
        Assert.Contains("truncated", recorder.Events[0], StringComparison.Ordinal);
    }

    /// <summary>
    /// The same claim the decoder makes, for the same reason: reads are split where the network
    /// chose, and a sequence straddling two of them is the ordinary case rather than the odd one.
    /// </summary>
    [Theory]
    [InlineData("[38:2::255:0:0mtext[0m")]
    [InlineData("]0;titlebody")]
    [InlineData("a[1;2Hb[?25lc")]
    [InlineData("P1;2q payload\\rest")]
    public void OneByteAtATimeIsTheSameAsAllAtOnce(string stream)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(stream);

        Recorder whole = new();
        AnsiParser wholeParser = new();
        wholeParser.Parse(bytes, ref whole);

        Recorder trickle = new();
        AnsiParser trickleParser = new();

        foreach (byte value in bytes)
        {
            trickleParser.Parse([value], ref trickle);
        }

        // Printable runs batch differently when the reads do, so compare the text as one string.
        Assert.Equal(Coalesce(whole.Events), Coalesce(trickle.Events));
    }

    /// <summary>
    /// Joins consecutive payload events of the same kind. How far a run batches is a function of
    /// where the reads fell, which is the network's choice and not the parser's - so what these
    /// compare is the payload, not how many calls it arrived in.
    /// </summary>
    private static List<string> Coalesce(List<string> events)
    {
        string[] payloads = ["print:", "osc:", "dcs:"];
        List<string> merged = [];

        foreach (string entry in events)
        {
            string? kind = payloads.FirstOrDefault(
                candidate => entry.StartsWith(candidate, StringComparison.Ordinal));

            if (kind is not null && merged.Count > 0
                && merged[^1].StartsWith(kind, StringComparison.Ordinal))
            {
                merged[^1] += entry[kind.Length..];
                continue;
            }

            merged.Add(entry);
        }

        return merged;
    }

    /// <summary>
    /// The design says the parse path allocates nothing, and the block asks the same as a criterion.
    /// Measured rather than asserted in prose: the handler here counts instead of recording, because
    /// a handler that builds strings would be measuring the test.
    /// </summary>
    [Fact]
    public void TheParsePathAllocatesNothing()
    {
        byte[] stream = Encoding.UTF8.GetBytes(
            "\x1b[38:2::255:0:0mhello \u4e2d\u6587\x1b[0m\r\n"
            + "\x1b]0;title\a\x1b[1;2H\x1bP1;2qpayload\x1b\\");

        AnsiParser parser = new();
        Counter counter = new();

        // Warm up: the first pass through jits the generic instantiation and grows nothing after.
        for (int pass = 0; pass < 100; pass++)
        {
            parser.Parse(stream, ref counter);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();

        for (int pass = 0; pass < 1000; pass++)
        {
            parser.Parse(stream, ref counter);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(counter.Events > 0, "the stream produced no events, so this measured nothing");
        Assert.True(allocated == 0,
            $"parsing 1000 copies of a {stream.Length}-byte stream allocated {allocated} bytes");
    }

    /// <summary>Counts events and builds nothing, so the measurement is of the parser.</summary>
    private struct Counter : IAnsiHandler
    {
        public int Events { get; private set; }

        public void Print(ReadOnlySpan<byte> text) => Events++;

        public void Execute(byte control) => Events++;

        public void EscapeDispatch(ReadOnlySpan<byte> intermediates, byte final) => Events++;

        public void CsiDispatch(in CsiParameters parameters, ReadOnlySpan<byte> intermediates, byte final)
        {
            // Read the parameters, so a lazy implementation that allocated on access is measured too.
            for (int group = 0; group < parameters.Count; group++)
            {
                Events += parameters.Group(group).Length;
            }

            Events++;
        }

        public void OscStart() => Events++;

        public void OscPut(ReadOnlySpan<byte> bytes) => Events++;

        public void OscEnd() => Events++;

        public void DcsHook(in CsiParameters parameters, ReadOnlySpan<byte> intermediates, byte final) => Events++;

        public void DcsPut(ReadOnlySpan<byte> bytes) => Events++;

        public void DcsUnhook() => Events++;
    }

    private static Recorder Feed(string stream)
    {
        Recorder recorder = new();
        AnsiParser parser = new();
        parser.Parse(Encoding.UTF8.GetBytes(stream), ref recorder);

        return recorder;
    }

    /// <summary>Writes every event down as a string, so a test reads as the sequence it fed.</summary>
    private struct Recorder : IAnsiHandler
    {
        public Recorder() => Events = [];

        public List<string> Events { get; }

        public void Print(ReadOnlySpan<byte> text) => Events.Add("print:" + Encoding.UTF8.GetString(text));

        public void Execute(byte control) => Events.Add($"execute:{control:X2}");

        public void EscapeDispatch(ReadOnlySpan<byte> intermediates, byte final) =>
            Events.Add($"esc:{(char)final}{Inter(intermediates)}");

        public void CsiDispatch(in CsiParameters parameters, ReadOnlySpan<byte> intermediates, byte final) =>
            Events.Add($"csi:{(char)final}{Inter(intermediates)} params={Params(parameters)}");

        public void OscStart() => Events.Add("osc-start");

        public void OscPut(ReadOnlySpan<byte> bytes) => Events.Add("osc:" + Encoding.UTF8.GetString(bytes));

        public void OscEnd() => Events.Add("osc-end");

        public void DcsHook(in CsiParameters parameters, ReadOnlySpan<byte> intermediates, byte final) =>
            Events.Add($"dcs-hook:{(char)final}{Inter(intermediates)} params={Params(parameters)}");

        public void DcsPut(ReadOnlySpan<byte> bytes) => Events.Add("dcs:" + Encoding.UTF8.GetString(bytes));

        public void DcsUnhook() => Events.Add("dcs-unhook");

        private static string Inter(ReadOnlySpan<byte> intermediates) =>
            intermediates.IsEmpty ? "" : " inter=" + Encoding.ASCII.GetString(intermediates);

        private static string Params(in CsiParameters parameters)
        {
            if (parameters.Truncated)
            {
                return "truncated";
            }

            StringBuilder text = new();

            for (int group = 0; group < parameters.Count; group++)
            {
                text.Append('[').AppendJoin(':', parameters.Group(group).ToArray()).Append(']');
            }

            return text.ToString();
        }
    }
}
