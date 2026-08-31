using BenchmarkDotNet.Attributes;
using Quickshell.Terminal;

namespace Quickshell.Benchmarks;

/// <summary>
/// What placing one character is made of.
///
/// QS144 measured the stage and cleared scrolling of it: the ring rotates and its fill is a
/// seventh, so 87 per cent of `emulate` is `PrintCluster` at about 87 ns a character. QS146 is
/// separating that, and its own rule is that the split is measured before anything is changed —
/// three of this session's hypotheses were disproved by the measurement that followed them.
///
/// So these price the calls `PrintCluster` makes for one plain ASCII character, which is what
/// almost every character in the corpus is. Each is measured per character rather than per call
/// so the numbers add up against the 87.
/// </summary>
[MemoryDiagnoser]
public class PrintPathBenchmarks
{
    /// <summary>How many characters each measurement covers, so a call's cost is readable.</summary>
    private const int Characters = 1_000;

    private readonly Pen _pen = Pen.Default;

    private TerminalBuffer _buffer = new(200, 50, scrollback: 2_000);
    private Emulator _emulator = new(200, 50, scrollback: 2_000);
    private byte[] _line = [];

    [GlobalSetup]
    public void Prepare()
    {
        _buffer = new TerminalBuffer(200, 50, scrollback: 2_000);
        _emulator = new Emulator(200, 50, scrollback: 2_000);

        // A hundred printable ASCII characters and a newline, which is what the corpus mostly is:
        // the average captured line is about forty columns.
        _line = new byte[Characters];

        for (int at = 0; at < _line.Length; at++)
        {
            _line[at] = (byte)('a' + (at % 26));
        }
    }

    /// <summary>
    /// The width lookup, which runs for every printed character including plain ASCII.
    ///
    /// <para>The suspicion this exists to price: for `a`, <c>Of</c> falls through its control-range
    /// checks into <c>IsZeroWidth</c> and then <c>IsWide</c>, and each of those ends in a binary
    /// search over a generated range table. The lowest zero range starts at U+0300 and the lowest
    /// wide range at U+1100, so both searches are guaranteed to miss for every ASCII character
    /// ever printed.</para>
    /// </summary>
    [Benchmark(OperationsPerInvoke = Characters)]
    public int WidthOfAscii()
    {
        int total = 0;

        // Read from the prepared line rather than computed, both because BenchmarkDotNet requires
        // an instance method and because a constant would let the JIT fold the whole lookup away.
        foreach (byte character in _line)
        {
            total += CharacterWidth.Of(character);
        }

        return total;
    }

    /// <summary>Building the cell that gets written, with no buffer involved.</summary>
    [Benchmark(OperationsPerInvoke = Characters)]
    public int BuildingACell()
    {
        int total = 0;

        for (int at = 0; at < Characters; at++)
        {
            Cell cell = Cell.For('a' + (at % 26), _pen.Foreground, _pen.Background, _pen.Flags,
                                 _pen.Underline, 1, _pen.Link);

            total += cell.Width;
        }

        return total;
    }

    /// <summary>
    /// Writing a built cell into the buffer, which is the ring arithmetic and the generation bump.
    /// </summary>
    [Benchmark(OperationsPerInvoke = Characters)]
    public void WritingACell()
    {
        Cell cell = Cell.For('a', _pen.Foreground, _pen.Background, _pen.Flags, _pen.Underline, 1,
                             _pen.Link);

        for (int at = 0; at < Characters; at++)
        {
            _buffer.Write(at % _buffer.Rows, at % _buffer.Columns, cell);
        }
    }

    /// <summary>
    /// The whole path, for the number the other three are read against: bytes in at one end and
    /// cells in the grid at the other.
    /// </summary>
    [Benchmark(OperationsPerInvoke = Characters, Baseline = true)]
    public void TheWholePath() => _emulator.Feed(_line);
}
