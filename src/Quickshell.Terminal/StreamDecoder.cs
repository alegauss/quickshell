using System.Text;

namespace Quickshell.Terminal;

/// <summary>
/// Bytes off the wire into text, across reads whose boundaries nobody chose.
///
/// <para>A read returns whatever arrived. A three-byte character can straddle two reads, so this
/// holds state and resumes: <see cref="Encoding.GetString(byte[])"/> over a chunk is the wrong
/// shape at any size, because it has nowhere to keep the tail and turns every split character into
/// two broken ones.</para>
///
/// <para><b>Nothing here throws.</b> The bytes come from a machine the user may not control, and a
/// host that can end a session by sending arbitrary bytes is a denial of service with no exploit
/// needed. Invalid sequences become U+FFFD by Unicode's substitution rules — one replacement per
/// maximal subpart, which is what .NET's own decoder implements and what this delegates to rather
/// than reimplementing.</para>
///
/// <para><b>The encoding is a setting, not a guess.</b> A stream that is not UTF-8 lands here too;
/// which encoding it is, is something a session knows and this is told.</para>
/// </summary>
public sealed class StreamDecoder
{
    private readonly Decoder _decoder;
    private char[] _buffer = new char[1024];

    /// <summary>Opens a decoder for an encoding, UTF-8 unless a session says otherwise.</summary>
    public StreamDecoder(Encoding? encoding = null)
    {
        // Constructed with throwOnInvalidBytes false, which is what selects replacement over an
        // exception. Encoding.UTF8 itself would do, and is spelled out here so the choice is
        // visible at the place it is made rather than inherited from a static somebody changed.
        Encoding = encoding ?? new UTF8Encoding(encoderShouldEmitUTF8Identifier: false,
                                                throwOnInvalidBytes: false);
        _decoder = Encoding.GetDecoder();
    }

    /// <summary>The encoding this was told the stream is in.</summary>
    public Encoding Encoding { get; }

    /// <summary>How many bytes are held back, waiting for the rest of their character.</summary>
    public bool HasPending { get; private set; }

    /// <summary>
    /// Decodes one read. What comes back is every complete character in it; anything trailing that
    /// is only part of a character is kept for the next call.
    /// </summary>
    public ReadOnlySpan<char> Decode(ReadOnlySpan<byte> bytes)
    {
        int maximum = Encoding.GetMaxCharCount(bytes.Length);

        if (_buffer.Length < maximum)
        {
            // Doubled rather than fitted exactly, and the difference is not academic. A run of
            // printable bytes is as long as the host's longest line, and a file of gradually longer
            // lines grew this by a few hundred characters at a time — one new buffer per line, which
            // measured out at two megabytes of garbage over a thirty-two megabyte `cat`. Doubling
            // makes the total twice the largest line and then nothing at all.
            _buffer = new char[Math.Max(_buffer.Length * 2, maximum)];
        }

        _decoder.Convert(bytes, _buffer, false, out _, out int written, out bool complete);
        HasPending = !complete || bytes.Length > 0 && written == 0 && bytes.Length < 4;

        return _buffer.AsSpan(0, written);
    }

    /// <summary>
    /// Ends the stream: anything still held back was never going to be completed, so it is flushed
    /// as replacement characters. A connection that dropped mid-character produces one, which is
    /// the honest picture of what arrived.
    /// </summary>
    public ReadOnlySpan<char> Flush()
    {
        _decoder.Convert([], _buffer, true, out _, out int written, out _);
        HasPending = false;

        return _buffer.AsSpan(0, written);
    }

    /// <summary>Forgets any pending bytes and starts again, which is what a reconnect is.</summary>
    public void Reset()
    {
        _decoder.Reset();
        HasPending = false;
    }
}
