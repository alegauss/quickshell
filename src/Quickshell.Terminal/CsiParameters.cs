namespace Quickshell.Terminal;

/// <summary>
/// The numbers a control sequence collected, in the groups the host separated them into.
///
/// <para><b>A group is what a semicolon separates; a sub-parameter is what a colon separates.</b>
/// <c>SGR 38:2::255:0:0</c> is one group of six, and <c>SGR 4:3;38;5;1</c> is a group of two then
/// three groups of one. Flattening the two apart is not optional: a colon inside an SGR parameter
/// is how true colour and styled underlines are actually spelled by the programs that emit them,
/// and a parser that treats a colon as a semicolon reads a request for underline style three as a
/// request for underline and then for style three.</para>
///
/// <para>It is a <c>ref struct</c> over the parser's own buffers, so reading parameters costs
/// nothing and a handler cannot keep them past the dispatch that gave them.</para>
/// </summary>
public readonly ref struct CsiParameters
{
    private readonly ReadOnlySpan<int> _values;
    private readonly ReadOnlySpan<byte> _lengths;

    internal CsiParameters(ReadOnlySpan<int> values, ReadOnlySpan<byte> lengths, bool truncated)
    {
        _values = values;
        _lengths = lengths;
        Truncated = truncated;
    }

    /// <summary>How many semicolon-separated groups arrived.</summary>
    public int Count => _lengths.Length;

    /// <summary>
    /// Whether the host sent more than the fixed buffers hold and the rest was dropped. A sequence
    /// with two hundred parameters is a malformed one, and saying so beats growing a buffer for it.
    /// </summary>
    public bool Truncated { get; }

    /// <summary>
    /// The first value of a group, or zero where the group is absent or was left empty. Where the
    /// default is not zero — and for a cursor position it is one — use <see cref="Value"/> instead.
    /// </summary>
    public int this[int group] => Value(group, 0);

    /// <summary>The first value of a group, with the default a caller wants when it is missing.</summary>
    public int Value(int group, int fallback)
    {
        if (group < 0 || group >= _lengths.Length)
        {
            return fallback;
        }

        ReadOnlySpan<int> values = Group(group);
        return values.Length == 0 || values[0] < 0 ? fallback : values[0];
    }

    /// <summary>Every value in one group: the whole of <c>38:2::255:0:0</c> when asked for group zero.</summary>
    public ReadOnlySpan<int> Group(int group)
    {
        if (group < 0 || group >= _lengths.Length)
        {
            return [];
        }

        int start = 0;

        for (int index = 0; index < group; index++)
        {
            start += _lengths[index];
        }

        return _values.Slice(start, _lengths[group]);
    }
}
