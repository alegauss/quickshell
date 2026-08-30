using System.Security.Cryptography;
using System.Text;

namespace Quickshell.Transport;

/// <summary>
/// A password, or anything else that must not be left lying about, held where it can be erased.
///
/// <para><b>Never a <see cref="string"/>, and that is the whole reason this type exists.</b> A
/// string is immutable, so it cannot be overwritten; it is garbage-collected, so it stays in the
/// heap until a collection nobody controls decides otherwise; and a compacting collection may copy
/// it first, leaving a second copy at an address no code will ever see again. A password put in one
/// is a password that stays in the process image until the process ends, and then in the page file
/// after that.</para>
///
/// <para><b>Pinned, so it is never copied.</b> Allocated on the pinned object heap: the collector
/// will not move it, so the bytes zeroed on <see cref="Dispose"/> are the only bytes there ever
/// were. An unpinned buffer can be zeroed perfectly and still leave a copy behind.</para>
///
/// <para>It is not a defence against a debugger attached to a live process, and nothing here
/// pretends otherwise. It is a defence against the secret outliving its use.</para>
/// </summary>
public sealed class Secret : IDisposable
{
    private readonly byte[] _bytes;

    private bool _erased;

    private Secret(byte[] pinned) => _bytes = pinned;

    /// <summary>How many bytes it is. Readable after erasure, because a length is not a secret.</summary>
    public int Length => _bytes.Length;

    /// <summary>Whether this has been erased, in which case its bytes are all zero.</summary>
    public bool IsErased => _erased;

    /// <summary>Takes a copy of these bytes into a pinned buffer.</summary>
    public static Secret From(ReadOnlySpan<byte> bytes)
    {
        byte[] pinned = GC.AllocateArray<byte>(bytes.Length, pinned: true);

        bytes.CopyTo(pinned);

        return new Secret(pinned);
    }

    /// <summary>
    /// Takes characters — what a text box hands over — and encodes them once, into a pinned buffer.
    ///
    /// <para>The caller's characters are still the caller's problem: a <see cref="string"/> handed
    /// to this cannot be erased by it, which is why the surfaces above take a
    /// <see cref="ReadOnlySpan{T}"/> and why a settings dialog should read into a char array.</para>
    /// </summary>
    public static Secret From(ReadOnlySpan<char> characters)
    {
        int length = Encoding.UTF8.GetByteCount(characters);
        byte[] pinned = GC.AllocateArray<byte>(length, pinned: true);

        Encoding.UTF8.GetBytes(characters, pinned);

        return new Secret(pinned);
    }

    /// <summary>
    /// The bytes, for the one call that needs them.
    ///
    /// <para>A span rather than an array, so a caller cannot keep it: the moment this is disposed
    /// every span handed out is looking at zeroes, which is the intended behaviour and not a hazard
    /// to work around.</para>
    /// </summary>
    public ReadOnlySpan<byte> Bytes => _bytes;

    /// <summary>
    /// A copy for a library that insists on owning an array.
    ///
    /// <para>Named for what it costs. Every call leaves a second copy of the secret in the ordinary
    /// heap, outside this object's control and beyond its ability to erase — so it exists for the
    /// exact places a dependency's signature leaves no choice, and each of those is worth a comment
    /// saying which dependency.</para>
    /// </summary>
    public byte[] ToUnprotectedArray() => _bytes.ToArray();

    /// <summary>Overwrites the bytes with zeroes. Called twice is not an error.</summary>
    public void Dispose()
    {
        if (_erased)
        {
            return;
        }

        CryptographicOperations.ZeroMemory(_bytes);
        _erased = true;
    }
}
