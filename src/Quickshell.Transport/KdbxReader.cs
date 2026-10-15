using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Xml;

namespace Quickshell.Transport;

/// <summary>Why a vault was not read, in a sentence that names the file.</summary>
public sealed class KdbxException(string message) : Exception(message);

/// <summary>A vault as read: its groups and entries, with every password held until it is disposed.</summary>
public sealed class KdbxVault : IDisposable
{
    private readonly List<Secret> _held;

    internal KdbxVault(KdbxGroup root, List<Secret> held)
    {
        Root = root;
        _held = held;
    }

    /// <summary>The root group. Entries in the recycle bin, and every entry's history, are left out.</summary>
    public KdbxGroup Root { get; }

    /// <summary>Every entry, group by group.</summary>
    public IEnumerable<KdbxEntry> Entries => Walk(Root);

    /// <summary>Erases every password read.</summary>
    public void Dispose()
    {
        foreach (Secret secret in _held)
        {
            secret.Dispose();
        }
    }

    private static IEnumerable<KdbxEntry> Walk(KdbxGroup group) =>
        group.Entries.Concat(group.Groups.SelectMany(Walk));
}

/// <summary>
/// Reads a KeePass database (QS308): KDBX 4, as this client and KeePassXC write it, and KDBX 3.1, as
/// keepassxc-cli and older KeePass still do.
///
/// <para><b>Authenticated before it is believed.</b> A KDBX 4 file's header and every block carry an
/// HMAC, and a KDBX 3.1 file's blocks a SHA-256 and its payload a known first 32 bytes. Any of them
/// failing refuses the whole file, naming it; nothing is read from a file that was changed after it
/// was written, because the alternative is importing a password somebody substituted.</para>
///
/// <para><b>What it reads.</b> AES-256 or ChaCha20, Argon2d, Argon2id or AES-KDF, gzip or none,
/// a key file. Twofish, KDBX 3.0 and older, and a YubiKey challenge are refused by name.</para>
/// </summary>
public static class KdbxReader
{
    private static ReadOnlySpan<byte> Aes => [0x31, 0xC1, 0xF2, 0xE6, 0xBF, 0x71, 0x43, 0x50, 0xBE, 0x58, 0x05, 0x21, 0x6A, 0xFC, 0x5A, 0xFF];

    private static ReadOnlySpan<byte> ChaCha => [0xD6, 0x03, 0x8A, 0x2B, 0x8B, 0x6F, 0x4C, 0xB5, 0xA5, 0x24, 0x33, 0x9A, 0x31, 0xDB, 0xB5, 0x9A];

    private static ReadOnlySpan<byte> AesKdf => [0xC9, 0xD9, 0xF3, 0x9A, 0x62, 0x8A, 0x44, 0x60, 0xBF, 0x74, 0x0D, 0x08, 0xC1, 0x8A, 0x4F, 0xEA];

    private static ReadOnlySpan<byte> AesKdfKdbx4 => [0x7C, 0x02, 0xBB, 0x82, 0x79, 0xA7, 0x4A, 0xC0, 0x92, 0x7D, 0x11, 0x4A, 0x00, 0x64, 0x82, 0x38];

    /// <summary>Reads the vault at <paramref name="path"/>.</summary>
    /// <exception cref="KdbxException">The file is not a vault this reads, the key is wrong, or it was changed.</exception>
    public static KdbxVault Read(string path, Secret passphrase, string? keyFile = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(passphrase);

        byte[] file;

        try
        {
            file = File.ReadAllBytes(path);
        }
        catch (Exception unread) when (unread is IOException or UnauthorizedAccessException)
        {
            throw new KdbxException($"{path} could not be read: {unread.Message}");
        }

        if (file.Length < 12 || BinaryPrimitives.ReadUInt32LittleEndian(file) != 0x9AA2D903
            || BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(4)) != 0xB54BFB67)
        {
            throw new KdbxException($"{path} is not a KeePass database.");
        }

        int major = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(10));

        try
        {
            using Secret composite = KdbxWriter.Composite(passphrase, keyFile);

            return major switch
            {
                4 => Four(path, file, composite),
                3 => Three(path, file, composite),
                _ => throw new KdbxException($"{path} is a KeePass database of version {major}, which this client does not read: only KDBX 3.1 and 4 are."),
            };
        }
        catch (Exception broken) when (broken is ArgumentOutOfRangeException or IndexOutOfRangeException or EndOfStreamException
                                           or InvalidDataException or XmlException or FormatException)
        {
            throw new KdbxException($"{path} is damaged: its contents end or break where a KeePass database's would not. Nothing was read from it.");
        }
    }

    private static KdbxVault Four(string path, byte[] file, Secret composite)
    {
        int at = 12;
        Dictionary<byte, byte[]> fields = [];

        while (true)
        {
            byte id = file[at];
            int length = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(at + 1));

            fields[id] = file.AsSpan(at + 5, length).ToArray();
            at += 5 + length;

            if (id == 0)
            {
                break;
            }
        }

        ReadOnlySpan<byte> header = file.AsSpan(0, at);

        if (!SHA256.HashData(header).AsSpan().SequenceEqual(file.AsSpan(at, 32)))
        {
            throw new KdbxException($"{path} is damaged: its header does not match the hash written after it. Nothing was read from it.");
        }

        byte[] masterSeed = fields[4];
        Dictionary<string, (byte Type, byte[] Value)> kdf = Dictionary(fields[11]);

        using Secret transformed = Transform(path, composite, kdf);

        byte[] cipherKey = SHA256.HashData([.. masterSeed, .. transformed.Bytes]);
        byte[] hmacBase = SHA512.HashData([.. masterSeed, .. transformed.Bytes, 0x01]);

        try
        {
            if (!HMACSHA256.HashData(BlockKey(hmacBase, ulong.MaxValue), header).AsSpan().SequenceEqual(file.AsSpan(at + 32, 32)))
            {
                throw new KdbxException($"{path} did not open: the passphrase or the key file is wrong, or the file was changed after it was written.");
            }

            at += 64;

            using MemoryStream cipher = new();

            for (ulong index = 0; ; index++)
            {
                ReadOnlySpan<byte> mac = file.AsSpan(at, 32);
                int length = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(at + 32));
                byte[] signed = new byte[12 + length];

                BinaryPrimitives.WriteUInt64LittleEndian(signed, index);
                file.AsSpan(at + 32, 4 + length).CopyTo(signed.AsSpan(8));

                if (!HMACSHA256.HashData(BlockKey(hmacBase, index), signed).AsSpan().SequenceEqual(mac))
                {
                    throw new KdbxException($"{path} was changed or damaged after it was written: block {index} does not authenticate. Nothing was read from it.");
                }

                at += 36 + length;

                if (length == 0)
                {
                    break;
                }

                cipher.Write(signed.AsSpan(12));
            }

            byte[] payload = Decrypt(path, fields[2], cipherKey, fields[7], cipher.ToArray());

            try
            {
                payload = Decompressed(payload, BinaryPrimitives.ReadUInt32LittleEndian(fields[3]));

                int inner = 0;
                uint streamId = 0;
                byte[] streamKey = [];

                while (true)
                {
                    byte id = payload[inner];
                    int length = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(inner + 1));
                    ReadOnlySpan<byte> value = payload.AsSpan(inner + 5, length);

                    if (id == 1)
                    {
                        streamId = BinaryPrimitives.ReadUInt32LittleEndian(value);
                    }
                    else if (id == 2)
                    {
                        streamKey = value.ToArray();
                    }

                    inner += 5 + length;

                    if (id == 0)
                    {
                        break;
                    }
                }

                return Document(path, payload.AsMemory(inner), Stream(path, streamId, streamKey), header: null);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(payload);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(cipherKey);
            CryptographicOperations.ZeroMemory(hmacBase);
        }
    }

    private static KdbxVault Three(string path, byte[] file, Secret composite)
    {
        if (BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(8)) < 1)
        {
            throw new KdbxException($"{path} is a KDBX 3.0 database, which this client does not read: only KDBX 3.1 and 4 are.");
        }

        int at = 12;
        Dictionary<byte, byte[]> fields = [];

        while (true)
        {
            byte id = file[at];
            int length = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(at + 1));

            fields[id] = file.AsSpan(at + 3, length).ToArray();
            at += 3 + length;

            if (id == 0)
            {
                break;
            }
        }

        byte[] header = file[..at];

        using Secret transformed = AesTransform(composite, fields[5], BinaryPrimitives.ReadUInt64LittleEndian(fields[6]));

        byte[] cipherKey = SHA256.HashData([.. fields[4], .. transformed.Bytes]);
        byte[] plain;

        try
        {
            plain = Decrypt(path, fields[2], cipherKey, fields[7], file[at..], wrongKey: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(cipherKey);
        }

        try
        {
            if (plain.Length < 32 || !plain.AsSpan(0, 32).SequenceEqual(fields[9]))
            {
                throw new KdbxException($"{path} did not open: the passphrase or the key file is wrong, or the file was changed after it was written.");
            }

            using MemoryStream content = new();
            int block = 32;

            while (true)
            {
                int length = BinaryPrimitives.ReadInt32LittleEndian(plain.AsSpan(block + 36));
                ReadOnlySpan<byte> hash = plain.AsSpan(block + 4, 32);

                if (length == 0)
                {
                    break;
                }

                ReadOnlySpan<byte> data = plain.AsSpan(block + 40, length);

                if (!SHA256.HashData(data).AsSpan().SequenceEqual(hash))
                {
                    throw new KdbxException($"{path} was changed or damaged after it was written: block {BinaryPrimitives.ReadUInt32LittleEndian(plain.AsSpan(block))} does not match its hash. Nothing was read from it.");
                }

                content.Write(data);
                block += 40 + length;
            }

            byte[] xml = Decompressed(content.ToArray(), BinaryPrimitives.ReadUInt32LittleEndian(fields[3]));

            try
            {
                return Document(path, xml, Stream(path, BinaryPrimitives.ReadUInt32LittleEndian(fields[10]), fields[8]), header);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(xml);
                CryptographicOperations.ZeroMemory(content.GetBuffer());
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    /// <summary>The KDBX 4 key derivation the file names.</summary>
    private static Secret Transform(string path, Secret composite, Dictionary<string, (byte Type, byte[] Value)> kdf)
    {
        ReadOnlySpan<byte> uuid = kdf["$UUID"].Value;

        if (uuid.SequenceEqual(AesKdfKdbx4) || uuid.SequenceEqual(AesKdf))
        {
            return AesTransform(composite, kdf["S"].Value, BinaryPrimitives.ReadUInt64LittleEndian(kdf["R"].Value));
        }

        bool argon2d = uuid.SequenceEqual(KdbxWriter.Argon2dKdf);

        if (!argon2d && !uuid.SequenceEqual(KdbxWriter.Argon2idKdf))
        {
            throw new KdbxException($"{path} derives its key with a function this client does not know.");
        }

        ulong memory = BinaryPrimitives.ReadUInt64LittleEndian(kdf["M"].Value) / 1024;
        ulong passes = BinaryPrimitives.ReadUInt64LittleEndian(kdf["I"].Value);
        uint lanes = BinaryPrimitives.ReadUInt32LittleEndian(kdf["P"].Value);

        // A file states its own cost, and one that asks for more than a machine has is refused rather
        // than allowed to take the process down.
        if (memory > 4UL * 1024 * 1024 || passes > 10_000 || lanes is 0 or > 256)
        {
            throw new KdbxException($"{path} asks for {memory / 1024} MiB over {passes} passes to open, which is more than this client will spend.");
        }

        return KdbxWriter.Argon(composite, kdf["S"].Value, new KdbxKdf((int)memory, (int)passes, (int)lanes, argon2d),
                                kdf.TryGetValue("K", out (byte, byte[] Value) secret) ? secret.Value : default,
                                kdf.TryGetValue("A", out (byte, byte[] Value) associated) ? associated.Value : default);
    }

    /// <summary>AES-KDF: the composite key encrypted with AES-ECB under the seed, round after round, then hashed.</summary>
    internal static Secret AesTransform(Secret composite, byte[] seed, ulong rounds)
    {
        byte[] key = composite.Bytes.ToArray();

        try
        {
            using Aes aes = System.Security.Cryptography.Aes.Create();

            aes.Key = seed;
            aes.Mode = CipherMode.ECB;
            aes.Padding = PaddingMode.None;

            using ICryptoTransform ecb = aes.CreateEncryptor();

            for (ulong round = 0; round < rounds; round++)
            {
                ecb.TransformBlock(key, 0, 32, key, 0);
            }

            return Secret.From(SHA256.HashData(key));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private static byte[] Decrypt(string path, byte[] cipherId, byte[] key, byte[] iv, byte[] cipher, bool wrongKey = false)
    {
        if (cipherId.AsSpan().SequenceEqual(Aes))
        {
            using Aes aes = System.Security.Cryptography.Aes.Create();

            aes.Key = key;

            try
            {
                return aes.DecryptCbc(cipher, iv, PaddingMode.PKCS7);
            }
            catch (CryptographicException) when (wrongKey)
            {
                // KDBX 3.1 has no HMAC, so a wrong key first shows as padding that does not decrypt.
                throw new KdbxException($"{path} did not open: the passphrase or the key file is wrong, or the file was changed after it was written.");
            }
        }

        if (cipherId.AsSpan().SequenceEqual(ChaCha))
        {
            byte[] plain = (byte[])cipher.Clone();

            using ChaCha20 stream = new(key, iv);

            stream.Xor(plain);

            return plain;
        }

        throw new KdbxException($"{path} is encrypted with a cipher this client does not read: only AES-256 and ChaCha20 are.");
    }

    private static byte[] Decompressed(byte[] payload, uint compression)
    {
        if (compression == 0)
        {
            return payload;
        }

        using GZipStream unzip = new(new MemoryStream(payload), CompressionMode.Decompress);
        using MemoryStream plain = new();

        unzip.CopyTo(plain);

        CryptographicOperations.ZeroMemory(payload);

        byte[] result = plain.ToArray();

        CryptographicOperations.ZeroMemory(plain.GetBuffer());

        return result;
    }

    /// <summary>The inner stream that unmasks protected values: ChaCha20 for KDBX 4, Salsa20 or ChaCha20 for 3.1.</summary>
    private static Func<byte[], byte[]> Stream(string path, uint id, byte[] key)
    {
        if (id == 3)
        {
            byte[] hash = SHA512.HashData(key);
            ChaCha20 chacha = new(hash.AsSpan(0, 32), hash.AsSpan(32, 12));

            CryptographicOperations.ZeroMemory(hash);

            return masked =>
            {
                chacha.Xor(masked);

                return masked;
            };
        }

        if (id == 2)
        {
            Salsa20 salsa = new(SHA256.HashData(key), [0xE8, 0x30, 0x09, 0x4B, 0x97, 0x20, 0x5D, 0x2A]);

            return masked =>
            {
                salsa.Xor(masked);

                return masked;
            };
        }

        throw new KdbxException($"{path} protects its passwords with a stream this client does not read.");
    }

    /// <summary>The XML document: every protected value unmasked in document order, then the tree outside the recycle bin.</summary>
    private static KdbxVault Document(string path, ReadOnlyMemory<byte> xml, Func<byte[], byte[]> unmask, byte[]? header)
    {
        XmlDocument document = new() { XmlResolver = null };

        using (MemoryStream stream = new(xml.ToArray(), writable: false))
        using (XmlReader reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null }))
        {
            document.Load(reader);
        }

        // KDBX 3.1 has no header HMAC; KeePass writes the header's hash into the document instead.
        if (header is not null && document.SelectSingleNode("/KeePassFile/Meta/HeaderHash")?.InnerText is { Length: > 0 } stated
            && !Convert.FromBase64String(stated).AsSpan().SequenceEqual(SHA256.HashData(header)))
        {
            throw new KdbxException($"{path} was changed after it was written: its header does not match the hash inside it. Nothing was read from it.");
        }

        List<Secret> held = [];
        Dictionary<XmlNode, Secret> unmasked = [];

        // In document order, history included: the stream is consumed by every protected value there is.
        foreach (XmlElement value in document.SelectNodes("//Value[@Protected='True']")!.OfType<XmlElement>())
        {
            byte[] masked = Convert.FromBase64String(value.InnerText);
            Secret secret = Secret.From(unmask(masked));

            CryptographicOperations.ZeroMemory(masked);
            held.Add(secret);
            unmasked[value] = secret;
        }

        // A password a file left unprotected is held the same way, so every one is erased with the vault.
        foreach (XmlElement value in document.SelectNodes("//String[Key='Password']/Value[not(@Protected='True')]")!.OfType<XmlElement>())
        {
            Secret secret = Secret.From(Encoding.UTF8.GetBytes(value.InnerText));

            held.Add(secret);
            unmasked[value] = secret;
        }

        string recycleBin = document.SelectSingleNode("/KeePassFile/Meta/RecycleBinUUID")?.InnerText ?? string.Empty;

        XmlNode root = document.SelectSingleNode("/KeePassFile/Root/Group")
            ?? throw new KdbxException($"{path} holds no groups.");

        return new KdbxVault(Group(root, unmasked, recycleBin), held);
    }

    private static KdbxGroup Group(XmlNode group, Dictionary<XmlNode, Secret> unmasked, string recycleBin)
    {
        return new KdbxGroup
        {
            Name = group.SelectSingleNode("Name")?.InnerText ?? string.Empty,
            Entries = [.. group.SelectNodes("Entry")!.OfType<XmlNode>().Select(entry => Entry(entry, unmasked))],
            Groups = [.. group.SelectNodes("Group")!.OfType<XmlNode>()
                              .Where(child => recycleBin.Length == 0 || child.SelectSingleNode("UUID")?.InnerText != recycleBin)
                              .Select(child => Group(child, unmasked, recycleBin))],
        };
    }

    private static KdbxEntry Entry(XmlNode entry, Dictionary<XmlNode, Secret> unmasked)
    {
        string Text(string key) =>
            entry.SelectSingleNode($"String[Key='{key}']/Value") is XmlElement value && !unmasked.ContainsKey(value)
                ? value.InnerText
                : string.Empty;

        Secret? password = entry.SelectSingleNode("String[Key='Password']/Value") is XmlElement given
                           && unmasked.TryGetValue(given, out Secret? found)
            ? found
            : null;

        return new KdbxEntry
        {
            Title = Text("Title"),
            UserName = Text("UserName"),
            Url = Text("URL"),
            Notes = Text("Notes"),
            Password = password,
        };
    }

    /// <summary>A KDBX 4 variant dictionary: a version, then typed and named values until a zero.</summary>
    private static Dictionary<string, (byte Type, byte[] Value)> Dictionary(byte[] bytes)
    {
        Dictionary<string, (byte, byte[])> values = new(StringComparer.Ordinal);
        int at = 2;

        while (bytes[at] != 0)
        {
            byte type = bytes[at];
            int nameLength = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(at + 1));
            string name = Encoding.UTF8.GetString(bytes, at + 5, nameLength);
            int valueLength = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(at + 5 + nameLength));

            values[name] = (type, bytes.AsSpan(at + 9 + nameLength, valueLength).ToArray());
            at += 9 + nameLength + valueLength;
        }

        return values;
    }

    private static byte[] BlockKey(byte[] hmacBase, ulong index)
    {
        byte[] input = new byte[8 + hmacBase.Length];

        BinaryPrimitives.WriteUInt64LittleEndian(input, index);
        hmacBase.CopyTo(input, 8);

        return SHA512.HashData(input);
    }
}

/// <summary>Salsa20/20, which KDBX 3.1 masks protected values with: 64-bit nonce, 64-bit counter from zero.</summary>
internal sealed class Salsa20
{
    private readonly uint[] _state = new uint[16];
    private readonly uint[] _working = new uint[16];
    private readonly byte[] _block = new byte[64];
    private int _used = 64;

    public Salsa20(ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce)
    {
        _state[0] = 0x61707865;
        _state[5] = 0x3320646E;
        _state[10] = 0x79622D32;
        _state[15] = 0x6B206574;

        for (int word = 0; word < 4; word++)
        {
            _state[1 + word] = BinaryPrimitives.ReadUInt32LittleEndian(key[(word * 4)..]);
            _state[11 + word] = BinaryPrimitives.ReadUInt32LittleEndian(key[(16 + (word * 4))..]);
        }

        _state[6] = BinaryPrimitives.ReadUInt32LittleEndian(nonce);
        _state[7] = BinaryPrimitives.ReadUInt32LittleEndian(nonce[4..]);
    }

    public void Xor(Span<byte> data)
    {
        for (int at = 0; at < data.Length; at++)
        {
            if (_used == 64)
            {
                NextBlock();
            }

            data[at] ^= _block[_used++];
        }
    }

    private void NextBlock()
    {
        _state.CopyTo(_working, 0);

        uint[] x = _working;

        for (int round = 0; round < 10; round++)
        {
            x[4] ^= uint.RotateLeft(x[0] + x[12], 7); x[8] ^= uint.RotateLeft(x[4] + x[0], 9);
            x[12] ^= uint.RotateLeft(x[8] + x[4], 13); x[0] ^= uint.RotateLeft(x[12] + x[8], 18);
            x[9] ^= uint.RotateLeft(x[5] + x[1], 7); x[13] ^= uint.RotateLeft(x[9] + x[5], 9);
            x[1] ^= uint.RotateLeft(x[13] + x[9], 13); x[5] ^= uint.RotateLeft(x[1] + x[13], 18);
            x[14] ^= uint.RotateLeft(x[10] + x[6], 7); x[2] ^= uint.RotateLeft(x[14] + x[10], 9);
            x[6] ^= uint.RotateLeft(x[2] + x[14], 13); x[10] ^= uint.RotateLeft(x[6] + x[2], 18);
            x[3] ^= uint.RotateLeft(x[15] + x[11], 7); x[7] ^= uint.RotateLeft(x[3] + x[15], 9);
            x[11] ^= uint.RotateLeft(x[7] + x[3], 13); x[15] ^= uint.RotateLeft(x[11] + x[7], 18);

            x[1] ^= uint.RotateLeft(x[0] + x[3], 7); x[2] ^= uint.RotateLeft(x[1] + x[0], 9);
            x[3] ^= uint.RotateLeft(x[2] + x[1], 13); x[0] ^= uint.RotateLeft(x[3] + x[2], 18);
            x[6] ^= uint.RotateLeft(x[5] + x[4], 7); x[7] ^= uint.RotateLeft(x[6] + x[5], 9);
            x[4] ^= uint.RotateLeft(x[7] + x[6], 13); x[5] ^= uint.RotateLeft(x[4] + x[7], 18);
            x[11] ^= uint.RotateLeft(x[10] + x[9], 7); x[8] ^= uint.RotateLeft(x[11] + x[10], 9);
            x[9] ^= uint.RotateLeft(x[8] + x[11], 13); x[10] ^= uint.RotateLeft(x[9] + x[8], 18);
            x[12] ^= uint.RotateLeft(x[15] + x[14], 7); x[13] ^= uint.RotateLeft(x[12] + x[15], 9);
            x[14] ^= uint.RotateLeft(x[13] + x[12], 13); x[15] ^= uint.RotateLeft(x[14] + x[13], 18);
        }

        for (int word = 0; word < 16; word++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(_block.AsSpan(word * 4), x[word] + _state[word]);
        }

        if (++_state[8] == 0)
        {
            _state[9]++;
        }

        _used = 0;
    }
}
