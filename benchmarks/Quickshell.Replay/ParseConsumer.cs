using Quickshell.Terminal;

namespace Quickshell.Replay;

/// <summary>
/// The headless arm: the parser alone, with a handler that counts and builds nothing.
///
/// <para>This is what the parser costs on top of touching the bytes at all, and the escape-scan
/// consumer beside it is the floor that difference is read against. A handler that did real work
/// would be measuring the handler.</para>
/// </summary>
public sealed class ParseConsumer : IStreamConsumer
{
    private readonly AnsiParser _parser = new();
    private Counting _handler;

    public string Name => "parse";

    public string What => "the parser alone - the Williams table over every byte, with a handler that only counts";

    public long Result => _handler.Events;

    public void Reset()
    {
        _parser.Reset();
        _handler = default;
    }

    public void Feed(ReadOnlySpan<byte> chunk) => _parser.Parse(chunk, ref _handler);

    /// <summary>
    /// Counts what arrived and keeps nothing. A struct, so the parser's generic dispatch stays
    /// devirtualised and this measures the parse path rather than an interface call per event.
    /// </summary>
    private struct Counting : IAnsiHandler
    {
        public long Events;

        public void Print(ReadOnlySpan<byte> text) => Events += text.Length;

        public void Execute(byte control) => Events++;

        public void EscapeDispatch(ReadOnlySpan<byte> intermediates, byte final) => Events++;

        public void CsiDispatch(in CsiParameters parameters, ReadOnlySpan<byte> intermediates, byte final)
        {
            // Read the parameters, because a consumer that ignored them would let a parser that
            // collected them lazily look faster than one that did the work.
            for (int group = 0; group < parameters.Count; group++)
            {
                Events += parameters.Group(group).Length;
            }

            Events++;
        }

        public void OscStart() => Events++;

        public void OscPut(ReadOnlySpan<byte> bytes) => Events += bytes.Length;

        public void OscEnd() => Events++;

        public void DcsHook(in CsiParameters parameters, ReadOnlySpan<byte> intermediates, byte final) => Events++;

        public void DcsPut(ReadOnlySpan<byte> bytes) => Events += bytes.Length;

        public void DcsUnhook() => Events++;
    }
}
