using System.IO.Compression;
using System.Text;
using Quickshell.Terminal;
using Xunit;

namespace Quickshell.Terminal.Tests;

/// <summary>
/// The two properties that have to hold for every input, because the input is chosen by a remote
/// machine.
///
/// <para><b>It does not fail</b>, and <b>it does not allocate</b>. A crash here is a remote host
/// ending the user's session; a hang is worse, because it looks like the network and gets diagnosed
/// as one. And a collection pause of thirty milliseconds is four dropped frames at 120 Hz, which
/// will land during somebody's <c>vim</c> session.</para>
/// </summary>
public sealed class HostileInputTests
{
    private const byte Escape = 0x1B;

    // Spelled as numbers, for the same reason Emulator.Replies.cs is: an escape in a literal is one
    // careless edit away from a raw control byte nothing can see. QS100.
    private static readonly string Dcs = new([(char)Escape, 'P']);
    private static readonly string St = new([(char)Escape, (char)0x5C]);

    /// <summary>The real captures, which are what mutation is seeded from.</summary>
    private static readonly string[] Corpus =
        ["htop", "vim-scroll", "tmux-resize", "dmesg", "ls-color-r"];

    /// <summary>
    /// The malformed shapes worth naming. <b>These are seeds, not discoveries</b> — each is a way a
    /// state machine is known to be got wrong, and they are here so a mutator starts from them rather
    /// than having to invent them.
    /// </summary>
    private static readonly (string What, byte[] Stream)[] Shapes =
    [
        ("a truncated CSI", [Escape, (byte)'[']),
        ("a CSI with no final byte, for ever", Repeat([Escape, (byte)'[', (byte)'1', (byte)';'], 4000)),
        ("parameters far past any array", Seq("[" + string.Join(';', Enumerable.Repeat("9999", 500)) + "m")),
        ("a parameter longer than an integer", Seq("[" + new string('9', 4000) + "m")),
        ("an OSC with no terminator", Seq("]0;" + new string('t', 100_000))),
        ("an OSC that is only a semicolon", Seq("];")),
        ("a DCS that never ends", Seq("P$q" + new string('m', 100_000))),
        ("nested introducers", Repeat([Escape, (byte)'[', Escape, (byte)']', Escape, (byte)'P'], 4000)),
        ("an escape then nothing", [Escape]),
        ("every byte in order, ten times", Repeat([.. Enumerable.Range(0, 256).Select(b => (byte)b)], 10)),
        ("a base and combining marks for ever", Utf8("a" + new string('́', 50_000))),
        ("lone surrogates as UTF-8", Repeat([0xED, 0xA0, 0x80], 4000)),
        ("truncated multi-byte characters", Repeat([0xF0, 0x9F], 4000)),
        ("one enormous line with no newline", Utf8(new string('x', 500_000))),
        ("a wide character at every margin", Repeat(Encoding.UTF8.GetBytes("一"), 4000)),
        ("cursor moves far outside the screen", Repeat(Seq("[99999;99999H"), 400)),
        ("a scrolling region inverted", Repeat(Seq("[40;2r"), 400)),
        ("insert and delete beyond the row", Repeat(Seq("[99999@" + (char)Escape + "[99999P"), 400)),
        ("resize requests as a sequence", Repeat(Seq("[8;1;1t"), 400)),
        ("clipboard writes with bad base64", Repeat(Seq("]52;c;!!!!" + (char)Escape + "\\"), 400)),
    ];

    /// <summary>The same shapes, as the theory sees them.</summary>
    public static TheoryData<string, byte[]> Pathological
    {
        get
        {
            TheoryData<string, byte[]> data = [];

            foreach ((string What, byte[] Stream) shape in Shapes)
            {
                data.Add(shape.What, shape.Stream);
            }

            return data;
        }
    }

    // ---- It does not fail ----

    [Theory]
    [MemberData(nameof(Pathological))]
    public void APathologicalStreamIsAnsweredRatherThanThrownAt(string what, byte[] stream)
    {
        Assert.NotEmpty(what);

        Emulator emulator = new(80, 24);

        // In pieces, because a read boundary can fall anywhere and half the shapes above are only
        // dangerous when one falls in the middle of them.
        foreach (int size in new[] { 1, 2, 3, 7, 64, 4096 })
        {
            Emulator split = new(80, 24);

            Feed(split, stream, size);
            Bounded(split);
        }

        Feed(emulator, stream, stream.Length);
        Bounded(emulator);
    }

    /// <summary>
    /// The falsification, first half: <em>falsified by any input that throws</em>.
    ///
    /// <para>Mutation seeded from the real captures and from every shape above. Deterministic, so a
    /// failure is reproducible from the iteration number rather than being a story about a machine
    /// that once went red.</para>
    /// </summary>
    [Fact]
    public void NoMutationOfARealStreamThrowsOrRunsAway()
    {
        byte[][] seeds = [.. Corpus.Select(Read), .. Shapes.Select(shape => shape.Stream)];
        ulong state = 0x2545F4914F6CDD1D;

        for (int iteration = 0; iteration < 3000; iteration++)
        {
            byte[] seed = seeds[(int)(Next(ref state) % (ulong)seeds.Length)];
            int length = (int)(Next(ref state) % (ulong)Math.Min(seed.Length, 8192)) + 1;
            int offset = (int)(Next(ref state) % (ulong)(seed.Length - length + 1));
            byte[] mutant = seed.AsSpan(offset, length).ToArray();

            // Between one and thirty-two bytes replaced, which is enough to break a sequence's shape
            // without turning the stream into noise that exercises nothing.
            int edits = (int)(Next(ref state) % 32) + 1;

            for (int edit = 0; edit < edits; edit++)
            {
                mutant[(int)(Next(ref state) % (ulong)mutant.Length)] = (byte)(Next(ref state) & 0xFF);
            }

            Emulator emulator = new(80, 24);
            int size = (int)(Next(ref state) % 97) + 1;

            try
            {
                Feed(emulator, mutant, size);
                Bounded(emulator);
            }
            catch (Exception failure)
            {
                Assert.Fail($"iteration {iteration} threw {failure.GetType().Name}: {failure.Message}");
            }
        }
    }

    /// <summary>A resize between reads, which is the other thing a remote machine and a user do at
    /// the same time.</summary>
    [Fact]
    public void MutationWithResizesInTheMiddleIsAlsoAnswered()
    {
        byte[] seed = Read("ls-color-r");
        ulong state = 0x9E3779B97F4A7C15;

        for (int iteration = 0; iteration < 400; iteration++)
        {
            Emulator emulator = new(80, 24);
            int offset = (int)(Next(ref state) % (ulong)(seed.Length - 8192));

            for (int read = 0; read < 8; read++)
            {
                byte[] mutant = seed.AsSpan(offset + (read * 512), 512).ToArray();
                mutant[(int)(Next(ref state) % (ulong)mutant.Length)] = (byte)(Next(ref state) & 0xFF);

                emulator.Feed(mutant);
                emulator.ClearReply();
                emulator.Resize((int)(Next(ref state) % 200) + 1, (int)(Next(ref state) % 60) + 1);
            }

            Bounded(emulator);
        }
    }

    // ---- It does not allocate ----

    /// <summary>
    /// The falsification, second half: <em>falsified by one allocated byte in the steady-state
    /// path</em>.
    ///
    /// <para>Measured with the allocated-bytes counter across a replay of the real captures, and it
    /// fails the build. Not by inspection: the reason this number is worth asserting is that every
    /// plausible-looking path in this assembly was inspected before it was measured at fifty-five
    /// kilobytes per megabyte.</para>
    ///
    /// <para><b>Steady state means after the buffers have reached their size.</b> The decoder's
    /// character buffer and the segmenter's are grown to fit the longest line a host sends and then
    /// never again, so the warm-up feeds the same stream once before the measurement starts. What is
    /// asserted is that the second pass costs nothing, because that is the pass a session spends its
    /// life in.</para>
    /// </summary>
    [Theory]
    [InlineData("htop")]
    [InlineData("vim-scroll")]
    [InlineData("tmux-resize")]
    [InlineData("dmesg")]
    [InlineData("ls-color-r")]
    public void ReplayingARealStreamAllocatesNothingInSteadyState(string name)
    {
        byte[] stream = Read(name);
        Emulator emulator = new(200, 50);

        // The warm-up is the same emulator and the same stream: whatever the first pass had to grow,
        // the second one finds already grown.
        Feed(emulator, stream, 64 * 1024);

        long before = GC.GetAllocatedBytesForCurrentThread();

        Feed(emulator, stream, 64 * 1024);

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(
            allocated == 0,
            $"replaying {name} allocated {allocated} bytes in steady state, and the parse path may allocate none");
    }

    /// <summary>
    /// How much the hostile sequence may still cost, and <b>this number is not zero on purpose</b>.
    ///
    /// <para>Ninety-six bytes of it are real and unattributed: three of the twenty shapes cost
    /// thirty-two bytes each, every pass, and only when the whole sequence runs — each of them
    /// measures zero on its own and zero beside any one other shape. So something oscillates between
    /// two states across the sequence. It is a fixed cost of the sequence and not a cost per byte:
    /// seven hundred kilobytes of hostile input or seven megabytes both pay it once. QS101 is the
    /// task to attribute and remove it.</para>
    ///
    /// <para>The ceiling is what makes this a gate anyway. A per-byte regression — the fifty-five
    /// kilobytes per megabyte this path allocated before QS24 — cannot hide under it.</para>
    /// </summary>
    private const long HostileAllocationCeiling = 256;

    /// <summary>
    /// The same, over the pathological shapes rather than over real output — because "on any input"
    /// is the claim, and a hostile host is not going to send real output.
    /// </summary>
    [Fact]
    public void ReplayingThePathologicalShapesAllocatesAlmostNothingInSteadyState()
    {
        byte[][] shapes = [.. Shapes.Select(shape => shape.Stream)];
        Emulator emulator = new(200, 50);
        long bytes = 0;

        foreach (byte[] shape in shapes)
        {
            bytes += shape.Length;
            Feed(emulator, shape, 4096);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();

        foreach (byte[] shape in shapes)
        {
            Feed(emulator, shape, 4096);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(
            allocated <= HostileAllocationCeiling,
            $"replaying the pathological shapes allocated {allocated} bytes over {bytes} bytes of "
            + $"input, and the ceiling is {HostileAllocationCeiling}");
    }

    /// <summary>
    /// And the cost is the sequence's and not the input's, which is the claim that makes the ceiling
    /// above a ceiling rather than a budget: ten times the input costs the same.
    /// </summary>
    [Fact]
    public void TheHostileCostIsPerSequenceAndNotPerByte()
    {
        byte[][] shapes = [.. Shapes.Select(shape => shape.Stream)];
        Emulator emulator = new(200, 50);

        foreach (byte[] shape in shapes)
        {
            Feed(emulator, shape, 4096);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();

        for (int round = 0; round < 10; round++)
        {
            foreach (byte[] shape in shapes)
            {
                Feed(emulator, shape, 4096);
            }
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(
            allocated <= HostileAllocationCeiling * 10,
            $"ten rounds allocated {allocated} bytes, which is more than ten times one round's ceiling");
    }

    // ---- Bounded state ----

    /// <summary>
    /// An unterminated string must not grow a buffer without limit. The ceilings are constants, so
    /// this reads them rather than restating them.
    /// </summary>
    [Fact]
    public void AnUnterminatedStringIsDiscardedRatherThanAccumulated()
    {
        Emulator emulator = new(80, 24);

        // No terminator, so the command never ends and is never acted on. The point is that the
        // bytes stopped being kept long before the host stopped sending them.
        emulator.Feed(Seq("]0;" + new string('t', Emulator.MaximumOscLength * 4)));

        Assert.Equal(string.Empty, emulator.Title);

        // And with a terminator, it is refused rather than acted on for the prefix that fitted.
        emulator.Feed(Utf8(St));

        Assert.Equal(string.Empty, emulator.Title);
        Assert.True(emulator.Unhandled > 0);
    }

    [Fact]
    public void AnUnterminatedDeviceControlStringIsRefusedRatherThanMatched()
    {
        Emulator emulator = new(80, 24);

        emulator.Feed(Seq("P$qm" + new string('m', Emulator.MaximumDcsLength * 4) + St));

        // Refused with the five bytes that end the asker's wait, rather than matched on the prefix
        // that happened to fit.
        Assert.Equal(Dcs + "0$r" + St, Encoding.ASCII.GetString(emulator.Reply));
    }

    // ---- Helpers ----

    /// <summary>
    /// What must still be true whatever arrived: the state a host can grow is inside its ceiling, and
    /// nothing about the buffer contradicts itself.
    /// </summary>
    private static void Bounded(Emulator emulator)
    {
        Assert.True(emulator.Reply.Length <= Emulator.MaximumReplyLength);
        Assert.True(emulator.Title.Length <= Emulator.MaximumOscLength);
        Assert.True(emulator.WorkingDirectory.Length <= Emulator.MaximumOscLength);
        Assert.True(emulator.Buffer.ClusterCount <= TerminalBuffer.MaximumClusters);
        Assert.InRange(emulator.Buffer.CursorRow, 0, emulator.Buffer.Rows - 1);
        Assert.InRange(emulator.Buffer.CursorColumn, 0, emulator.Buffer.Columns - 1);
        Assert.InRange(emulator.Buffer.LineCount, emulator.Buffer.Rows, emulator.Buffer.Capacity);
        Assert.InRange(emulator.MarginTop, 0, emulator.Buffer.Rows - 1);
        Assert.InRange(emulator.MarginBottom, 0, emulator.Buffer.Rows - 1);
    }

    private static void Feed(Emulator emulator, ReadOnlySpan<byte> bytes, int size)
    {
        while (!bytes.IsEmpty)
        {
            int take = Math.Min(size, bytes.Length);

            emulator.Feed(bytes[..take]);
            emulator.ClearReply();

            bytes = bytes[take..];
        }
    }

    /// <summary>xorshift64*, so a failing iteration is reproducible from its number.</summary>
    private static ulong Next(ref ulong state)
    {
        state ^= state >> 12;
        state ^= state << 25;
        state ^= state >> 27;

        return state * 0x2545F4914F6CDD1D;
    }

    /// <summary>An escape and then the rest, which is how every sequence above starts.</summary>
    private static byte[] Seq(string rest) => Utf8((char)Escape + rest);

    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

    private static byte[] Repeat(byte[] pattern, int times)
    {
        byte[] all = new byte[pattern.Length * times];

        for (int index = 0; index < times; index++)
        {
            pattern.CopyTo(all, index * pattern.Length);
        }

        return all;
    }

    /// <summary>
    /// Where the captured streams are, found by walking up from the test binary rather than by a
    /// count of parent directories: the output tree's depth is a build detail and this is not the
    /// place to encode it.
    /// </summary>
    private static string CorpusDirectory
    {
        get
        {
            DirectoryInfo? directory = new(AppContext.BaseDirectory);

            while (directory is not null)
            {
                string candidate = Path.Combine(directory.FullName, "benchmarks", "corpus", "streams");

                if (Directory.Exists(candidate))
                {
                    return candidate;
                }

                directory = directory.Parent;
            }

            throw new DirectoryNotFoundException(
                $"no benchmarks/corpus/streams above {AppContext.BaseDirectory}");
        }
    }

    private static byte[] Read(string name)
    {
        using FileStream file = File.OpenRead(Path.Combine(CorpusDirectory, $"{name}.raw.gz"));
        using GZipStream gz = new(file, CompressionMode.Decompress);
        using MemoryStream all = new();

        gz.CopyTo(all);

        return all.ToArray();
    }
}
