using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Quickshell.Transport;

/// <summary>
/// Argon2d and Argon2id, version 1.3, as RFC 9106 defines them: any number of lanes, any salt length,
/// a secret and associated data (QS308).
///
/// <para><b>Why a managed copy beside libsodium.</b> libsodium computes one lane, a 16-byte salt and
/// Argon2i or Argon2id only, which is what <see cref="MasterKey"/> and this client's own vaults use.
/// A vault KeePassXC made is Argon2d by default, over as many lanes as the machine had cores, with a
/// 32-byte salt, and reading one back needs all three. BLAKE2b, the hash underneath, is still
/// libsodium's through NSec; only the memory-hard part is here, checked against RFC 9106's own
/// vectors and against libsodium where the two overlap.</para>
/// </summary>
public static class Argon2
{
    /// <summary>The two variants a KDBX file names.</summary>
    public enum Variant
    {
        /// <summary>Data-dependent addressing throughout: KeePassXC's default.</summary>
        D = 0,

        /// <summary>Data-independent for the first half of the first pass, then data-dependent.</summary>
        Id = 2,
    }

    private const int BlockWords = 128;
    private const int SyncPoints = 4;
    private const int Version = 0x13;

    /// <summary>Derives <paramref name="output"/>.Length bytes.</summary>
    /// <param name="variant">Argon2d or Argon2id.</param>
    /// <param name="password">The password (in a KDBX file, the composite key).</param>
    /// <param name="salt">The salt, at least 8 bytes.</param>
    /// <param name="lanes">The degree of parallelism; the lanes are computed one after another here.</param>
    /// <param name="memoryKibibytes">Memory, in KiB, at least eight per lane.</param>
    /// <param name="passes">Passes over the memory.</param>
    /// <param name="output">Where the tag goes, 32 bytes or more.</param>
    /// <param name="secret">The optional secret value K.</param>
    /// <param name="associated">The optional associated data X.</param>
    public static void Derive(Variant variant, ReadOnlySpan<byte> password, ReadOnlySpan<byte> salt, int lanes,
                              int memoryKibibytes, int passes, Span<byte> output,
                              ReadOnlySpan<byte> secret = default, ReadOnlySpan<byte> associated = default)
    {
        if (salt.Length < 8 || lanes < 1 || passes < 1 || output.Length < 32 || memoryKibibytes < 8 * lanes)
        {
            throw new ArgumentException("Argon2's parameters are outside what RFC 9106 allows.");
        }

        int segment = memoryKibibytes / (SyncPoints * lanes);
        int laneLength = segment * SyncPoints;
        int blocks = laneLength * lanes;

        ulong[] memory = new ulong[(long)blocks * BlockWords];
        byte[] h0 = new byte[72];

        try
        {
            Initial(variant, password, salt, lanes, memoryKibibytes, passes, output.Length, secret, associated, h0);

            byte[] block = new byte[1024];

            for (int lane = 0; lane < lanes; lane++)
            {
                for (int first = 0; first < 2; first++)
                {
                    BinaryPrimitives.WriteInt32LittleEndian(h0.AsSpan(64), first);
                    BinaryPrimitives.WriteInt32LittleEndian(h0.AsSpan(68), lane);
                    Long(h0, block);
                    Load(block, memory.AsSpan((lane * laneLength + first) * BlockWords, BlockWords));
                }
            }

            CryptographicOperations.ZeroMemory(block);

            Fill(variant, memory, lanes, laneLength, segment, blocks, passes);

            ulong[] last = new ulong[BlockWords];

            for (int lane = 0; lane < lanes; lane++)
            {
                ReadOnlySpan<ulong> final = memory.AsSpan((lane * laneLength + laneLength - 1) * BlockWords, BlockWords);

                for (int word = 0; word < BlockWords; word++)
                {
                    last[word] ^= final[word];
                }
            }

            byte[] bytes = new byte[1024];

            for (int word = 0; word < BlockWords; word++)
            {
                BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(word * 8), last[word]);
            }

            Long(bytes, output);

            CryptographicOperations.ZeroMemory(bytes);
            CryptographicOperations.ZeroMemory(System.Runtime.InteropServices.MemoryMarshal.AsBytes(last.AsSpan()));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(System.Runtime.InteropServices.MemoryMarshal.AsBytes(memory.AsSpan()));
            CryptographicOperations.ZeroMemory(h0);
        }
    }

    /// <summary>H0, the 64-byte digest of every parameter and input, with room after it for two words.</summary>
    private static void Initial(Variant variant, ReadOnlySpan<byte> password, ReadOnlySpan<byte> salt, int lanes,
                                int memoryKibibytes, int passes, int tag, ReadOnlySpan<byte> secret,
                                ReadOnlySpan<byte> associated, byte[] h0)
    {
        using MemoryStream input = new();

        foreach (int value in new[] { lanes, tag, memoryKibibytes, passes, Version, (int)variant })
        {
            Word(input, value);
        }

        Word(input, password.Length);
        input.Write(password);
        Word(input, salt.Length);
        input.Write(salt);
        Word(input, secret.Length);
        input.Write(secret);
        Word(input, associated.Length);
        input.Write(associated);

        byte[] all = input.ToArray();

        try
        {
            NSec.Cryptography.HashAlgorithm.Blake2b_512.Hash(all, h0.AsSpan(0, 64));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(all);
            CryptographicOperations.ZeroMemory(input.GetBuffer());
        }
    }

    private static void Word(Stream into, int value)
    {
        Span<byte> bytes = stackalloc byte[4];

        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        into.Write(bytes);
    }

    /// <summary>H', the variable-length hash: BLAKE2b chained 64 bytes at a time, keeping 32 of each but the last.</summary>
    private static void Long(ReadOnlySpan<byte> input, Span<byte> output)
    {
        byte[] prefixed = new byte[4 + input.Length];

        BinaryPrimitives.WriteInt32LittleEndian(prefixed, output.Length);
        input.CopyTo(prefixed.AsSpan(4));

        try
        {
            if (output.Length <= 64)
            {
                Blake2b(prefixed, output);

                return;
            }

            Span<byte> v = stackalloc byte[64];
            Span<byte> next = stackalloc byte[64];

            Blake2b(prefixed, v);
            v[..32].CopyTo(output);

            int at = 32;

            while (output.Length - at > 64)
            {
                Blake2b(v, next);
                next.CopyTo(v);
                v[..32].CopyTo(output[at..]);
                at += 32;
            }

            Span<byte> tail = stackalloc byte[64];

            Blake2b(v, tail[..(output.Length - at)]);
            tail[..(output.Length - at)].CopyTo(output[at..]);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(prefixed);
        }
    }

    /// <summary>BLAKE2b with an output of 32 to 64 bytes, through libsodium; H' never asks for less.</summary>
    private static void Blake2b(ReadOnlySpan<byte> input, Span<byte> output) =>
        new NSec.Cryptography.Blake2b(output.Length).Hash(input, output);

    private static void Load(ReadOnlySpan<byte> bytes, Span<ulong> block)
    {
        for (int word = 0; word < BlockWords; word++)
        {
            block[word] = BinaryPrimitives.ReadUInt64LittleEndian(bytes[(word * 8)..]);
        }
    }

    private static void Fill(Variant variant, ulong[] memory, int lanes, int laneLength, int segment, int blocks, int passes)
    {
        ulong[] zero = new ulong[BlockWords];
        ulong[] input = new ulong[BlockWords];
        ulong[] address = new ulong[BlockWords];
        ulong[] scratch = new ulong[BlockWords * 2];

        for (int pass = 0; pass < passes; pass++)
        {
            for (int slice = 0; slice < SyncPoints; slice++)
            {
                for (int lane = 0; lane < lanes; lane++)
                {
                    bool independent = variant == Variant.Id && pass == 0 && slice < SyncPoints / 2;

                    if (independent)
                    {
                        Array.Clear(input);
                        input[0] = (ulong)pass;
                        input[1] = (ulong)lane;
                        input[2] = (ulong)slice;
                        input[3] = (ulong)blocks;
                        input[4] = (ulong)passes;
                        input[5] = (ulong)variant;
                    }

                    int start = 0;

                    if (pass == 0 && slice == 0)
                    {
                        start = 2;

                        if (independent)
                        {
                            NextAddresses(zero, input, address, scratch);
                        }
                    }

                    int current = lane * laneLength + slice * segment + start;
                    int previous = current % laneLength == 0 ? current + laneLength - 1 : current - 1;

                    for (int index = start; index < segment; index++, current++, previous++)
                    {
                        if (current % laneLength == 1)
                        {
                            previous = current - 1;
                        }

                        ulong random;

                        if (independent)
                        {
                            if (index % BlockWords == 0)
                            {
                                NextAddresses(zero, input, address, scratch);
                            }

                            random = address[index % BlockWords];
                        }
                        else
                        {
                            random = memory[(long)previous * BlockWords];
                        }

                        int referenceLane = pass == 0 && slice == 0 ? lane : (int)((random >> 32) % (ulong)lanes);
                        int referenceIndex = Alpha(pass, slice, index, segment, laneLength, (uint)random, referenceLane == lane);
                        long reference = (long)referenceLane * laneLength + referenceIndex;

                        Compress(memory.AsSpan((int)(previous * (long)BlockWords), BlockWords),
                                 memory.AsSpan((int)(reference * BlockWords), BlockWords),
                                 memory.AsSpan((int)(current * (long)BlockWords), BlockWords),
                                 withXor: pass != 0, scratch);
                    }
                }
            }
        }
    }

    /// <summary>Which block of the reference lane a new block depends on.</summary>
    private static int Alpha(int pass, int slice, int index, int segment, int laneLength, uint random, bool sameLane)
    {
        long area;

        if (pass == 0)
        {
            area = slice == 0
                ? index - 1
                : sameLane ? (slice * segment) + index - 1 : (slice * segment) + (index == 0 ? -1 : 0);
        }
        else
        {
            area = sameLane ? laneLength - segment + index - 1 : laneLength - segment + (index == 0 ? -1 : 0);
        }

        ulong relative = (ulong)random * random >> 32;
        relative = (ulong)area - 1 - ((ulong)area * relative >> 32);

        long start = pass != 0 && slice != SyncPoints - 1 ? (slice + 1) * segment : 0;

        return (int)((start + (long)relative) % laneLength);
    }

    private static void NextAddresses(ulong[] zero, ulong[] input, ulong[] address, ulong[] scratch)
    {
        input[6]++;
        Compress(zero, input, address, withXor: false, scratch);
        Compress(zero, address, address, withXor: false, scratch);
    }

    /// <summary>The compression function G: R = X ^ Y, P over its rows then its columns, then R ^ that, into next.</summary>
    private static void Compress(ReadOnlySpan<ulong> previous, ReadOnlySpan<ulong> reference, Span<ulong> next, bool withXor, ulong[] scratch)
    {
        Span<ulong> r = scratch.AsSpan(0, BlockWords);
        Span<ulong> keep = scratch.AsSpan(BlockWords, BlockWords);

        for (int word = 0; word < BlockWords; word++)
        {
            r[word] = previous[word] ^ reference[word];
            keep[word] = withXor ? r[word] ^ next[word] : r[word];
        }

        for (int row = 0; row < 8; row++)
        {
            int b = row * 16;

            Round(r, b, b + 1, b + 2, b + 3, b + 4, b + 5, b + 6, b + 7, b + 8, b + 9, b + 10, b + 11, b + 12, b + 13, b + 14, b + 15);
        }

        for (int column = 0; column < 8; column++)
        {
            int b = column * 2;

            Round(r, b, b + 1, b + 16, b + 17, b + 32, b + 33, b + 48, b + 49, b + 64, b + 65, b + 80, b + 81, b + 96, b + 97, b + 112, b + 113);
        }

        for (int word = 0; word < BlockWords; word++)
        {
            next[word] = keep[word] ^ r[word];
        }
    }

    private static void Round(Span<ulong> v, int v0, int v1, int v2, int v3, int v4, int v5, int v6, int v7,
                              int v8, int v9, int v10, int v11, int v12, int v13, int v14, int v15)
    {
        G(v, v0, v4, v8, v12);
        G(v, v1, v5, v9, v13);
        G(v, v2, v6, v10, v14);
        G(v, v3, v7, v11, v15);
        G(v, v0, v5, v10, v15);
        G(v, v1, v6, v11, v12);
        G(v, v2, v7, v8, v13);
        G(v, v3, v4, v9, v14);
    }

    /// <summary>BLAKE2b's mixing step with BlaMka's multiplication folded into each addition.</summary>
    private static void G(Span<ulong> v, int a, int b, int c, int d)
    {
        v[a] = v[a] + v[b] + (2 * (v[a] & 0xFFFFFFFF) * (v[b] & 0xFFFFFFFF));
        v[d] = ulong.RotateRight(v[d] ^ v[a], 32);
        v[c] = v[c] + v[d] + (2 * (v[c] & 0xFFFFFFFF) * (v[d] & 0xFFFFFFFF));
        v[b] = ulong.RotateRight(v[b] ^ v[c], 24);
        v[a] = v[a] + v[b] + (2 * (v[a] & 0xFFFFFFFF) * (v[b] & 0xFFFFFFFF));
        v[d] = ulong.RotateRight(v[d] ^ v[a], 16);
        v[c] = v[c] + v[d] + (2 * (v[c] & 0xFFFFFFFF) * (v[d] & 0xFFFFFFFF));
        v[b] = ulong.RotateRight(v[b] ^ v[c], 63);
    }
}
