using System.Buffers;
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
    /// <summary>The longest UTF-8 sequence, and so the most a split character can leave behind.</summary>
    private const int LongestSequence = 4;

    private readonly Decoder _decoder;
    private readonly bool _utf8;

    // UTF-8's split character, held here rather than inside a Decoder — QS101, below.
    private readonly byte[] _tail = new byte[LongestSequence * 2];
    private int _held;

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
        _utf8 = Encoding.CodePage == Encoding.UTF8.CodePage;
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
        int maximum = Encoding.GetMaxCharCount(bytes.Length + LongestSequence);

        if (_buffer.Length < maximum)
        {
            // Doubled rather than fitted exactly, and the difference is not academic. A run of
            // printable bytes is as long as the host's longest line, and a file of gradually longer
            // lines grew this by a few hundred characters at a time — one new buffer per line, which
            // measured out at two megabytes of garbage over a thirty-two megabyte `cat`. Doubling
            // makes the total twice the largest line and then nothing at all.
            _buffer = new char[Math.Max(_buffer.Length * 2, maximum)];
        }

        if (_utf8)
        {
            return DecodeUtf8(bytes);
        }

        _decoder.Convert(bytes, _buffer, false, out _, out int written, out bool complete);
        HasPending = !complete || bytes.Length > 0 && written == 0 && bytes.Length < 4;

        return _buffer.AsSpan(0, written);
    }

    /// <summary>
    /// UTF-8 without a Decoder's state, which is QS101.
    ///
    /// <para><b>Why not the Decoder.</b> A byte a Decoder holds across reads, and which the next read
    /// shows to be invalid, is replaced through the fallback's legacy entry point, and that takes the
    /// unknown bytes as a <c>byte[]</c>: thirty-two bytes of garbage every time a host splits a broken
    /// character across a read. Inside one read the same byte costs nothing. So this keeps the tail
    /// itself, and decodes with <see cref="System.Text.Unicode.Utf8.ToUtf16"/>, which is stateless,
    /// says where an incomplete tail starts instead of swallowing it, and replaces by the same
    /// maximal-subpart rule.</para>
    /// </summary>
    private ReadOnlySpan<char> DecodeUtf8(ReadOnlySpan<byte> bytes)
    {
        int written = 0;

        if (_held > 0)
        {
            // The held bytes and just enough of this read to finish them, decoded together.
            int borrowed = Math.Min(bytes.Length, LongestSequence);
            bytes[..borrowed].CopyTo(_tail.AsSpan(_held));

            OperationStatus joined = System.Text.Unicode.Utf8.ToUtf16(
                _tail.AsSpan(0, _held + borrowed), _buffer, out int read, out written,
                replaceInvalidSequences: true, isFinalBlock: false);

            if (joined == OperationStatus.NeedMoreData && borrowed == bytes.Length)
            {
                // Still not a whole character, and this read was all of it: keep what is left.
                int left = _held + borrowed - read;

                _tail.AsSpan(read, left).CopyTo(_tail);
                _held = left;
                HasPending = true;

                return _buffer.AsSpan(0, written);
            }

            // What the joined decode took from this read is behind us; the rest decodes as usual.
            bytes = bytes[Math.Max(0, read - _held)..];
            _held = 0;
        }

        System.Text.Unicode.Utf8.ToUtf16(bytes, _buffer.AsSpan(written), out int consumed,
                                         out int more, replaceInvalidSequences: true,
                                         isFinalBlock: false);

        bytes[consumed..].CopyTo(_tail);
        _held = bytes.Length - consumed;
        HasPending = _held > 0;

        return _buffer.AsSpan(0, written + more);
    }

    /// <summary>
    /// Ends the stream: anything still held back was never going to be completed, so it is flushed
    /// as replacement characters. A connection that dropped mid-character produces one, which is
    /// the honest picture of what arrived.
    /// </summary>
    public ReadOnlySpan<char> Flush()
    {
        int written;

        if (_utf8)
        {
            System.Text.Unicode.Utf8.ToUtf16(_tail.AsSpan(0, _held), _buffer, out _, out written,
                                             replaceInvalidSequences: true, isFinalBlock: true);
            _held = 0;
        }
        else
        {
            _decoder.Convert([], _buffer, true, out _, out written, out _);
        }

        HasPending = false;

        return _buffer.AsSpan(0, written);
    }

    /// <summary>Forgets any pending bytes and starts again, which is what a reconnect is.</summary>
    public void Reset()
    {
        _decoder.Reset();
        _held = 0;
        HasPending = false;
    }
}
