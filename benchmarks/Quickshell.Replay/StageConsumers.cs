using Quickshell.Terminal;

namespace Quickshell.Replay;

/// <summary>
/// The parser plus UTF-8 decoding, and nothing after it.
///
/// <para><b>The first rung of the ladder QS141 asks for.</b> `parse` reports 1,200 MB/s on the 32 MB
/// stream and `emulate` reports 10, and a ratio of a hundred is not a diagnosis — it says the
/// emulator is slow without saying which part. This arm and the one below it split that gap into
/// named work, so the answer is a stage rather than a shrug.</para>
///
/// <para>Decoding is what <see cref="Emulator"/> does first with printed text, so it is what the
/// first rung has to be.</para>
/// </summary>
public sealed class DecodeConsumer : IStreamConsumer
{
    private readonly AnsiParser _parser = new();
    private Decoding _handler = new();

    /// <inheritdoc/>
    public string Name => "decode";

    /// <inheritdoc/>
    public string What => "the parser and UTF-8 decoding, counting characters and building no cells";

    /// <inheritdoc/>
    public long Result => _handler.Characters;

    /// <inheritdoc/>
    public void Reset() => _handler = new Decoding();

    /// <inheritdoc/>
    public void Feed(ReadOnlySpan<byte> chunk) => _parser.Parse(chunk, ref _handler);

    private struct Decoding : IAnsiHandler
    {
        private readonly StreamDecoder _decoder = new();

        public Decoding()
        {
        }

        public long Characters { get; private set; }

        public void Print(ReadOnlySpan<byte> text) => Characters += _decoder.Decode(text).Length;

        public void Execute(byte control) => Characters++;

        public void EscapeDispatch(ReadOnlySpan<byte> intermediates, byte final) => Characters++;

        public void CsiDispatch(in CsiParameters parameters, ReadOnlySpan<byte> intermediates, byte final)
        {
            for (int group = 0; group < parameters.Count; group++)
            {
                Characters += parameters.Group(group).Length;
            }

            Characters++;
        }

        public void OscStart() => Characters++;

        public void OscPut(ReadOnlySpan<byte> bytes) => Characters += bytes.Length;

        public void OscEnd() => Characters++;

        public void DcsHook(in CsiParameters parameters, ReadOnlySpan<byte> intermediates, byte final) =>
            Characters++;

        public void DcsPut(ReadOnlySpan<byte> bytes) => Characters += bytes.Length;

        public void DcsUnhook() => Characters++;
    }
}

/// <summary>
/// The parser, decoding, and grapheme segmentation — everything <see cref="Emulator"/> does to
/// printed text except write it into a cell.
///
/// <para>The second rung. What is left between this and `emulate` is the terminal itself: cursor
/// movement, wrapping, scrolling, and the cell writes. If the hundredfold lives there rather than
/// here, that is where to look, and this arm is what says so.</para>
/// </summary>
public sealed class SegmentConsumer : IStreamConsumer
{
    private readonly AnsiParser _parser = new();
    private Segmenting _handler = new();

    /// <inheritdoc/>
    public string Name => "segment";

    /// <inheritdoc/>
    public string What =>
        "the parser, decoding and grapheme clustering - everything done to printed text short of "
        + "writing a cell";

    /// <inheritdoc/>
    public long Result => _handler.Clusters;

    /// <inheritdoc/>
    public void Reset() => _handler = new Segmenting();

    /// <inheritdoc/>
    public void Feed(ReadOnlySpan<byte> chunk)
    {
        _parser.Parse(chunk, ref _handler);

        // Flushed per read, exactly as the emulator flushes it, so this arm is not made to look
        // cheaper by holding the last cluster back.
        _handler.Flush();
    }

    private struct Segmenting : IAnsiHandler
    {
        private readonly StreamDecoder _decoder = new();
        private readonly GraphemeSegmenter _segmenter = new();

        public Segmenting()
        {
        }

        public long Clusters { get; private set; }

        public void Print(ReadOnlySpan<byte> text)
        {
            _segmenter.Append(_decoder.Decode(text));

            while (_segmenter.TryNext(out ReadOnlySpan<char> cluster))
            {
                Clusters += cluster.Length;
            }
        }

        public void Flush()
        {
            while (_segmenter.TryFlush(out ReadOnlySpan<char> cluster))
            {
                Clusters += cluster.Length;
            }
        }

        public void Execute(byte control) => Clusters++;

        public void EscapeDispatch(ReadOnlySpan<byte> intermediates, byte final) => Clusters++;

        public void CsiDispatch(in CsiParameters parameters, ReadOnlySpan<byte> intermediates, byte final)
        {
            for (int group = 0; group < parameters.Count; group++)
            {
                Clusters += parameters.Group(group).Length;
            }

            Clusters++;
        }

        public void OscStart() => Clusters++;

        public void OscPut(ReadOnlySpan<byte> bytes) => Clusters += bytes.Length;

        public void OscEnd() => Clusters++;

        public void DcsHook(in CsiParameters parameters, ReadOnlySpan<byte> intermediates, byte final) =>
            Clusters++;

        public void DcsPut(ReadOnlySpan<byte> bytes) => Clusters += bytes.Length;

        public void DcsUnhook() => Clusters++;
    }
}
