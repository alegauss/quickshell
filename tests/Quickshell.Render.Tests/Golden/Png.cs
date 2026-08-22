using System.IO;
using System.IO.Compression;

namespace Quickshell.Render.Tests.Golden;

/// <summary>
/// Just enough PNG to write a reference and read it back: eight-bit RGB, no interlace, no palette.
///
/// <para>A reference image has to be something a person can open. A failure that reports only a
/// number is a failure nobody diagnoses, so the format is one every viewer on every machine already
/// knows rather than a raw buffer this repository would have to ship a reader for.</para>
///
/// <para>It is hand-rolled because the alternative is a dependency: <c>System.Drawing</c> is
/// Windows-only and deprecated for this, and an imaging package is a supply-chain entry for two
/// hundred lines of well-specified format.</para>
/// </summary>
internal static class Png
{
    private static readonly byte[] Signature = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>Encodes tightly packed BGRA pixels as RGB, dropping the alpha nobody reads.</summary>
    internal static byte[] Encode(byte[] bgra, int width, int height)
    {
        using MemoryStream raw = new();

        for (int row = 0; row < height; row++)
        {
            // Filter type 0: the images are small and the references are committed once, so the
            // bytes a smarter filter would save are not worth the code that could get them wrong.
            raw.WriteByte(0);

            for (int column = 0; column < width; column++)
            {
                int offset = ((row * width) + column) * 4;
                raw.WriteByte(bgra[offset + 2]);
                raw.WriteByte(bgra[offset + 1]);
                raw.WriteByte(bgra[offset]);
            }
        }

        using MemoryStream compressed = new();

        using (ZLibStream deflate = new(compressed, CompressionLevel.Optimal, true))
        {
            deflate.Write(raw.ToArray());
        }

        using MemoryStream file = new();
        file.Write(Signature);

        byte[] header = new byte[13];
        BigEndian(header, 0, (uint)width);
        BigEndian(header, 4, (uint)height);
        header[8] = 8;   // bit depth
        header[9] = 2;   // colour type: truecolour

        Chunk(file, "IHDR", header);
        Chunk(file, "IDAT", compressed.ToArray());
        Chunk(file, "IEND", []);

        return file.ToArray();
    }

    /// <summary>Decodes back to tightly packed BGRA, with the alpha filled in opaque.</summary>
    internal static byte[] Decode(byte[] file, out int width, out int height)
    {
        for (int index = 0; index < Signature.Length; index++)
        {
            if (file[index] != Signature[index])
            {
                throw new InvalidDataException("not a PNG: the signature does not match");
            }
        }

        width = 0;
        height = 0;

        using MemoryStream payload = new();
        int position = Signature.Length;

        while (position + 8 <= file.Length)
        {
            int length = (int)ReadBigEndian(file, position);
            string type = System.Text.Encoding.ASCII.GetString(file, position + 4, 4);
            int data = position + 8;

            if (type == "IHDR")
            {
                width = (int)ReadBigEndian(file, data);
                height = (int)ReadBigEndian(file, data + 4);

                if (file[data + 8] != 8 || file[data + 9] != 2 || file[data + 12] != 0)
                {
                    throw new InvalidDataException(
                        "this reader handles eight-bit truecolour PNG with no interlace and nothing else");
                }
            }
            else if (type == "IDAT")
            {
                payload.Write(file, data, length);
            }
            else if (type == "IEND")
            {
                break;
            }

            position = data + length + 4;   // the chunk's data, then its CRC
        }

        payload.Position = 0;

        using ZLibStream inflate = new(payload, CompressionMode.Decompress);
        using MemoryStream raw = new();
        inflate.CopyTo(raw);

        return Unfilter(raw.ToArray(), width, height);
    }

    /// <summary>
    /// Undoes the per-row filters. Every row carries one byte saying which of the five was applied,
    /// and each is defined against the byte to the left and the byte above.
    /// </summary>
    private static byte[] Unfilter(byte[] raw, int width, int height)
    {
        const int Channels = 3;

        int stride = width * Channels;
        byte[] pixels = new byte[width * height * 4];
        byte[] previous = new byte[stride];
        byte[] current = new byte[stride];

        for (int row = 0; row < height; row++)
        {
            int source = row * (stride + 1);
            byte filter = raw[source];
            Array.Copy(raw, source + 1, current, 0, stride);

            for (int index = 0; index < stride; index++)
            {
                int left = index >= Channels ? current[index - Channels] : 0;
                int above = previous[index];
                int corner = index >= Channels ? previous[index - Channels] : 0;

                current[index] = filter switch
                {
                    0 => current[index],
                    1 => (byte)(current[index] + left),
                    2 => (byte)(current[index] + above),
                    3 => (byte)(current[index] + ((left + above) / 2)),
                    4 => (byte)(current[index] + Paeth(left, above, corner)),
                    _ => throw new InvalidDataException($"row {row} carries filter type {filter}"),
                };
            }

            for (int column = 0; column < width; column++)
            {
                int target = ((row * width) + column) * 4;
                pixels[target] = current[(column * Channels) + 2];       // blue
                pixels[target + 1] = current[(column * Channels) + 1];   // green
                pixels[target + 2] = current[column * Channels];         // red
                pixels[target + 3] = 255;
            }

            (previous, current) = (current, previous);
        }

        return pixels;
    }

    private static int Paeth(int left, int above, int corner)
    {
        int estimate = left + above - corner;
        int toLeft = Math.Abs(estimate - left);
        int toAbove = Math.Abs(estimate - above);
        int toCorner = Math.Abs(estimate - corner);

        if (toLeft <= toAbove && toLeft <= toCorner)
        {
            return left;
        }

        return toAbove <= toCorner ? above : corner;
    }

    private static void Chunk(Stream file, string type, byte[] data)
    {
        byte[] length = new byte[4];
        BigEndian(length, 0, (uint)data.Length);
        file.Write(length);

        byte[] payload = new byte[4 + data.Length];
        payload[0] = (byte)type[0];
        payload[1] = (byte)type[1];
        payload[2] = (byte)type[2];
        payload[3] = (byte)type[3];
        data.CopyTo(payload, 4);
        file.Write(payload);

        byte[] crc = new byte[4];
        BigEndian(crc, 0, Crc32(payload));
        file.Write(crc);
    }

    private static void BigEndian(byte[] buffer, int offset, uint value)
    {
        buffer[offset] = (byte)(value >> 24);
        buffer[offset + 1] = (byte)(value >> 16);
        buffer[offset + 2] = (byte)(value >> 8);
        buffer[offset + 3] = (byte)value;
    }

    private static uint ReadBigEndian(byte[] buffer, int offset) =>
        ((uint)buffer[offset] << 24) | ((uint)buffer[offset + 1] << 16)
        | ((uint)buffer[offset + 2] << 8) | buffer[offset + 3];

    private static uint Crc32(byte[] data)
    {
        uint crc = 0xFFFFFFFF;

        foreach (byte value in data)
        {
            crc ^= value;

            for (int bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
            }
        }

        return crc ^ 0xFFFFFFFF;
    }
}
