using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using NSec.Cryptography;
using IncrementalHash = System.Security.Cryptography.IncrementalHash;

namespace Quickshell.Transport;

/// <summary>One entry of a KeePass vault: what a person looks up when a password is forgotten.</summary>
public sealed class KdbxEntry
{
    /// <summary>The name the entry is listed under.</summary>
    public required string Title { get; init; }

    /// <summary>The login, or empty.</summary>
    public string UserName { get; init; } = string.Empty;

    /// <summary>Where it is used, e.g. <c>ssh://user@host:22</c>, or empty.</summary>
    public string Url { get; init; } = string.Empty;

    /// <summary>Free text, or empty.</summary>
    public string Notes { get; init; } = string.Empty;

    /// <summary>
    /// The password, or null where none is kept. Written as a protected value: XORed with the inner
    /// stream before it is encoded, so it never appears in the document a reader decrypts first.
    /// The caller owns it and erases it.
    /// </summary>
    public Secret? Password { get; init; }

    /// <summary>Files carried in the entry, which a reader shows as its attachments.</summary>
    public IReadOnlyList<KdbxAttachment> Attachments { get; init; } = [];
}

/// <summary>A file attached to an entry: its name as a reader lists it, and its bytes.</summary>
/// <param name="Name">The name, unique within its entry.</param>
/// <param name="Content">The bytes, carried inside the encrypted payload and nowhere else.</param>
public sealed record KdbxAttachment(string Name, ReadOnlyMemory<byte> Content);

/// <summary>A folder of a KeePass vault, holding entries and further folders.</summary>
public sealed class KdbxGroup
{
    /// <summary>The name the group is listed under; the root's is the database's own name.</summary>
    public required string Name { get; init; }

    /// <summary>The entries directly in this group.</summary>
    public IReadOnlyList<KdbxEntry> Entries { get; init; } = [];

    /// <summary>The groups directly in this group.</summary>
    public IReadOnlyList<KdbxGroup> Groups { get; init; } = [];
}

/// <summary>
/// How expensive the vault's key is to derive: Argon2's memory, passes, lanes and variant.
///
/// <para><b>This client writes Argon2id over one lane</b>, which libsodium computes; the other shapes
/// are what a vault from KeePassXC carries (QS308), read through the managed <see cref="Argon2"/>.</para>
/// </summary>
/// <param name="MemoryKibibytes">Memory per derivation, in KiB.</param>
/// <param name="Passes">Passes over that memory.</param>
/// <param name="Lanes">The degree of parallelism the file declares.</param>
/// <param name="Argon2d">Argon2d rather than Argon2id, which is KeePassXC's default.</param>
public sealed record KdbxKdf(int MemoryKibibytes, int Passes, int Lanes = 1, bool Argon2d = false)
{
    /// <summary>
    /// 256 MiB and three passes. A vault is a file that leaves this machine, so it is the offline
    /// target <see cref="MasterKey"/>'s 64 MiB was not sized for: four times the memory per guess,
    /// at well under a second for the one derivation a person opening it waits for.
    /// </summary>
    public static KdbxKdf Default { get; } = new(256 * 1024, 3);
}

/// <summary>
/// A KeePass key file: the second factor KDBX defines beside the passphrase (QS305).
///
/// <para><b>Read the way KeePass reads one</b>, so a key file another tool made opens here and one
/// made here opens there: the XML formats 1.0 and 2.0, then 32 raw bytes, then 64 hexadecimal
/// characters, and anything else is hashed whole.</para>
/// </summary>
public static class KdbxKeyFile
{
    /// <summary>Writes a new random key file in KeePass's 2.0 XML format, which every reader opens.</summary>
    public static void Create(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        byte[] key = RandomNumberGenerator.GetBytes(32);

        try
        {
            string hex = Convert.ToHexString(key);
            string hash = Convert.ToHexString(SHA256.HashData(key).AsSpan(0, 4));
            StringBuilder data = new();

            for (int at = 0; at < hex.Length; at += 8)
            {
                data.Append(at % 32 == 0 ? "\n\t\t\t" : " ").Append(hex, at, 8);
            }

            File.WriteAllText(path,
                "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<KeyFile>\n\t<Meta>\n\t\t<Version>2.0</Version>\n\t</Meta>\n"
                + $"\t<Key>\n\t\t<Data Hash=\"{hash}\">{data}\n\t\t</Data>\n\t</Key>\n</KeyFile>\n",
                new UTF8Encoding(false));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    /// <summary>The 32 bytes a key file contributes to the composite key.</summary>
    internal static Secret Read(string path)
    {
        byte[] content = File.ReadAllBytes(path);

        try
        {
            if (FromXml(content) is { } xml)
            {
                return xml;
            }

            if (content.Length == 32)
            {
                return Secret.From(content);
            }

            if (content.Length == 64 && content.All(octet => char.IsAsciiHexDigit((char)octet)))
            {
                byte[] decoded = Convert.FromHexString(Encoding.ASCII.GetString(content));

                try
                {
                    return Secret.From(decoded);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(decoded);
                }
            }

            return Secret.From(SHA256.HashData(content));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(content);
        }
    }

    /// <summary>The key of an XML key file, or null where the bytes are not one.</summary>
    private static Secret? FromXml(byte[] content)
    {
        XmlDocument document = new() { XmlResolver = null };

        try
        {
            using MemoryStream stream = new(content, writable: false);
            using XmlReader reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit });

            document.Load(reader);
        }
        catch (XmlException)
        {
            return null;
        }

        string? version = document.SelectSingleNode("/KeyFile/Meta/Version")?.InnerText.Trim();

        if (document.SelectSingleNode("/KeyFile/Key/Data") is not XmlElement data)
        {
            return null;
        }

        byte[] key = version is not null && version.StartsWith("2.", StringComparison.Ordinal)
            ? Convert.FromHexString(string.Concat(data.InnerText.Where(Uri.IsHexDigit)))
            : Convert.FromBase64String(data.InnerText.Trim());

        try
        {
            string hash = data.GetAttribute("Hash");

            if (hash.Length > 0 && !hash.Equals(Convert.ToHexString(SHA256.HashData(key).AsSpan(0, 4)), StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The key file's Hash does not match its key, so it was edited or damaged.");
            }

            return Secret.From(key);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }
}

/// <summary>
/// A vault's key once derived: the Argon2id output and the salt and parameters it came from (QS307).
///
/// <para><b>Kept so a vault can be written again without its passphrase.</b> The passphrase itself is
/// never kept: this is the transformed key, which opens this vault and no other, sealed under DPAPI
/// for this Windows account by <see cref="Seal"/>. Anything running as the user can unseal it, which
/// is DPAPI's whole bargain and the same one <see cref="SecretStore"/> makes for the passwords
/// themselves.</para>
/// </summary>
public sealed class KdbxKey : IDisposable
{
    private const byte Format = 1;

    private static ReadOnlySpan<byte> Purpose => "quickshell vault key"u8;

    private KdbxKey(byte[] salt, KdbxKdf kdf, Secret transformed)
    {
        Salt = salt;
        Kdf = kdf;
        Transformed = transformed;
    }

    /// <summary>The Argon2id salt the key was derived with, which every file written under it carries.</summary>
    internal byte[] Salt { get; }

    /// <summary>The Argon2id parameters.</summary>
    internal KdbxKdf Kdf { get; }

    /// <summary>The transformed key.</summary>
    internal Secret Transformed { get; }

    /// <summary>Derives the key from a passphrase, and a key file where one is given, under a fresh salt.</summary>
    public static KdbxKey Derive(Secret passphrase, string? keyFile = null, KdbxKdf? kdf = null)
    {
        ArgumentNullException.ThrowIfNull(passphrase);

        kdf ??= KdbxKdf.Default;

        byte[] salt = RandomNumberGenerator.GetBytes(16);

        return new KdbxKey(salt, kdf, KdbxWriter.Transform(passphrase, keyFile, salt, kdf));
    }

    /// <summary>The key sealed under DPAPI for this Windows account, to be kept on disk.</summary>
    public byte[] Seal()
    {
        if (Kdf.Lanes != 1 || Kdf.Argon2d)
        {
            throw new InvalidOperationException("Only a key derived as this client derives one, Argon2id over one lane, is kept.");
        }

        byte[] plain = new byte[1 + 8 + Salt.Length + Transformed.Length];

        try
        {
            plain[0] = Format;
            BinaryPrimitives.WriteInt32LittleEndian(plain.AsSpan(1), Kdf.MemoryKibibytes);
            BinaryPrimitives.WriteInt32LittleEndian(plain.AsSpan(5), Kdf.Passes);
            Salt.CopyTo(plain, 9);
            Transformed.Bytes.CopyTo(plain.AsSpan(9 + Salt.Length));

            return ProtectedData.Protect(plain, Purpose.ToArray(), DataProtectionScope.CurrentUser);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    /// <summary>A key <see cref="Seal"/> wrote, or null where it is not one, or was sealed for another account.</summary>
    public static KdbxKey? Unseal(byte[] sealedUp)
    {
        ArgumentNullException.ThrowIfNull(sealedUp);

        byte[] plain;

        try
        {
            plain = ProtectedData.Unprotect(sealedUp, Purpose.ToArray(), DataProtectionScope.CurrentUser);
        }
        catch (CryptographicException)
        {
            return null;
        }

        try
        {
            if (plain.Length != 1 + 8 + 16 + 32 || plain[0] != Format)
            {
                return null;
            }

            KdbxKdf kdf = new(BinaryPrimitives.ReadInt32LittleEndian(plain.AsSpan(1)), BinaryPrimitives.ReadInt32LittleEndian(plain.AsSpan(5)));

            return new KdbxKey(plain[9..25], kdf, Secret.From(plain.AsSpan(25)));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    /// <summary>
    /// What a vault written from <paramref name="root"/> would hold, as an HMAC under this key: equal
    /// for equal content, so a rewrite that would change nothing is skipped, and meaningless to
    /// anybody without the key, so it can rest beside the vault.
    /// </summary>
    public byte[] Fingerprint(KdbxGroup root)
    {
        ArgumentNullException.ThrowIfNull(root);

        using IncrementalHash hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, Transformed.Bytes);

        Feed(hmac, root);

        return hmac.GetHashAndReset();
    }

    /// <summary>Erases the transformed key.</summary>
    public void Dispose() => Transformed.Dispose();

    private static void Feed(IncrementalHash hmac, KdbxGroup group)
    {
        Field(hmac, "group"u8);
        Field(hmac, Encoding.UTF8.GetBytes(group.Name));

        foreach (KdbxEntry entry in group.Entries)
        {
            Field(hmac, "entry"u8);

            foreach (string text in new[] { entry.Title, entry.UserName, entry.Url, entry.Notes })
            {
                Field(hmac, Encoding.UTF8.GetBytes(text));
            }

            Field(hmac, entry.Password is { } password ? password.Bytes : []);

            foreach (KdbxAttachment attachment in entry.Attachments)
            {
                Field(hmac, Encoding.UTF8.GetBytes(attachment.Name));
                Field(hmac, attachment.Content.Span);
            }
        }

        foreach (KdbxGroup child in group.Groups)
        {
            Feed(hmac, child);
        }

        Field(hmac, "end"u8);
    }

    /// <summary>One field, its length first, so no two different contents feed the same bytes.</summary>
    private static void Field(IncrementalHash hmac, ReadOnlySpan<byte> value)
    {
        Span<byte> length = stackalloc byte[4];

        BinaryPrimitives.WriteInt32LittleEndian(length, value.Length);
        hmac.AppendData(length);
        hmac.AppendData(value);
    }
}

/// <summary>
/// Writes a KeePass KDBX 4 database (QS305): the open format for an offline vault, which
/// KeePassXC, KeePass, Strongbox and KeePassDX all open, so a file written here outlives this
/// client and this machine.
///
/// <para><b>What it is made of.</b> The composite key is SHA-256 over the passphrase's hash and the
/// key file's 32 bytes; Argon2id (libsodium, through NSec, as <see cref="MasterKey"/> uses) turns it
/// into the transformed key; AES-256-CBC carries the payload; every block of it, and the header,
/// carries an HMAC-SHA-256, so an edited file refuses to open; and each password is XORed again with
/// a ChaCha20 stream inside the payload. The writer is this repository's own because KeePassLib is
/// GPL.</para>
///
/// <para><b>No plain text reaches the disk.</b> The document is built and encrypted in memory, the
/// cipher text is written to a temporary name beside the target and renamed over it, and every
/// buffer that held the decrypted payload or a key is zeroed, as <see cref="SecretStore"/> does.</para>
/// </summary>
public static class KdbxWriter
{
    private const int BlockBytes = 1024 * 1024;

    private static ReadOnlySpan<byte> AesCipher =>
        [0x31, 0xC1, 0xF2, 0xE6, 0xBF, 0x71, 0x43, 0x50, 0xBE, 0x58, 0x05, 0x21, 0x6A, 0xFC, 0x5A, 0xFF];

    internal static ReadOnlySpan<byte> Argon2dKdf =>
        [0xEF, 0x63, 0x6D, 0xDF, 0x8C, 0x29, 0x44, 0x4B, 0x91, 0xF7, 0xA9, 0xA4, 0x03, 0xE3, 0x0A, 0x0C];

    internal static ReadOnlySpan<byte> Argon2idKdf =>
        [0x9E, 0x29, 0x8B, 0x19, 0x56, 0xDB, 0x47, 0x73, 0xB2, 0x3D, 0xFC, 0x3E, 0xC6, 0xF0, 0xA1, 0xE6];

    /// <summary>
    /// Writes the database at <paramref name="path"/>, replacing what is there only once the whole
    /// file is written.
    /// </summary>
    /// <param name="path">Where the vault goes.</param>
    /// <param name="root">The root group, whose name is the database's.</param>
    /// <param name="passphrase">What opens it. The caller owns it and erases it.</param>
    /// <param name="keyFile">A key file also needed to open it, or null for the passphrase alone.</param>
    /// <param name="kdf">How expensive its key is; <see cref="KdbxKdf.Default"/> where null.</param>
    public static void Write(string path, KdbxGroup root, Secret passphrase, string? keyFile = null, KdbxKdf? kdf = null)
    {
        ArgumentNullException.ThrowIfNull(passphrase);

        using KdbxKey key = KdbxKey.Derive(passphrase, keyFile, kdf);

        Write(path, root, key);
    }

    /// <summary>
    /// Writes the database under a key already derived (QS307): what keeps a vault current without
    /// asking for its passphrase again. Each write still takes a fresh master seed and IV, so no two
    /// files share a cipher key; only the Argon2id derivation, and its salt, are reused.
    /// </summary>
    public static void Write(string path, KdbxGroup root, KdbxKey key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(key);

        byte[] masterSeed = RandomNumberGenerator.GetBytes(32);
        byte[] iv = RandomNumberGenerator.GetBytes(16);

        byte[] header = Header(masterSeed, iv, key.Salt, key.Kdf);

        Secret transformed = key.Transformed;

        byte[] cipherKey = SHA256.HashData([.. masterSeed, .. transformed.Bytes]);
        byte[] hmacBase = SHA512.HashData([.. masterSeed, .. transformed.Bytes, 0x01]);
        byte[] payload = Payload(root);
        byte[] cipher = [];

        try
        {
            using (Aes aes = Aes.Create())
            {
                aes.Key = cipherKey;
                cipher = aes.EncryptCbc(payload, iv, PaddingMode.PKCS7);
            }

            string directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
            string temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4))}.tmp");

            try
            {
                using (FileStream file = new(temporary, FileMode.CreateNew, FileAccess.Write))
                {
                    file.Write(header);
                    file.Write(SHA256.HashData(header));
                    file.Write(HMACSHA256.HashData(BlockKey(hmacBase, ulong.MaxValue), header));
                    WriteBlocks(file, cipher, hmacBase);
                }

                File.Move(temporary, path, overwrite: true);
            }
            catch
            {
                File.Delete(temporary);

                throw;
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(cipherKey);
            CryptographicOperations.ZeroMemory(hmacBase);
            CryptographicOperations.ZeroMemory(payload);
            CryptographicOperations.ZeroMemory(masterSeed);
        }
    }

    /// <summary>The file's SHA-256, as lowercase hex: what tells a vault written here from one edited since.</summary>
    public static string Hash(string path)
    {
        using FileStream file = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

        return Convert.ToHexStringLower(SHA256.HashData(file));
    }

    /// <summary>The outer header: version, cipher, seed, IV and the KDF's parameters, ending in its end field.</summary>
    private static byte[] Header(byte[] masterSeed, byte[] iv, byte[] salt, KdbxKdf kdf)
    {
        using MemoryStream header = new();

        Span<byte> number = stackalloc byte[8];

        BinaryPrimitives.WriteUInt32LittleEndian(number, 0x9AA2D903);
        header.Write(number[..4]);
        BinaryPrimitives.WriteUInt32LittleEndian(number, 0xB54BFB67);
        header.Write(number[..4]);
        BinaryPrimitives.WriteUInt32LittleEndian(number, 0x0004_0000);
        header.Write(number[..4]);

        BinaryPrimitives.WriteUInt32LittleEndian(number, 0);

        OuterField(header, 2, AesCipher);
        OuterField(header, 3, number[..4]);
        OuterField(header, 4, masterSeed);
        OuterField(header, 7, iv);
        OuterField(header, 11, KdfParameters(salt, kdf));
        OuterField(header, 0, "\r\n\r\n"u8);

        return header.ToArray();
    }

    /// <summary>One outer header field: its id, its length in four bytes, then its value.</summary>
    private static void OuterField(Stream header, byte id, ReadOnlySpan<byte> value)
    {
        Span<byte> length = stackalloc byte[4];

        BinaryPrimitives.WriteInt32LittleEndian(length, value.Length);
        header.WriteByte(id);
        header.Write(length);
        header.Write(value);
    }

    /// <summary>Argon2id's parameters as KDBX's variant dictionary spells them.</summary>
    private static byte[] KdfParameters(byte[] salt, KdbxKdf kdf)
    {
        using MemoryStream dictionary = new();

        dictionary.Write([0x00, 0x01]);
        Variant(dictionary, 0x42, "$UUID", kdf.Argon2d ? Argon2dKdf : Argon2idKdf);
        Variant(dictionary, 0x42, "S", salt);
        Variant(dictionary, 0x04, "P", BitConverter.GetBytes((uint)kdf.Lanes));
        Variant(dictionary, 0x05, "M", BitConverter.GetBytes((ulong)kdf.MemoryKibibytes * 1024));
        Variant(dictionary, 0x05, "I", BitConverter.GetBytes((ulong)kdf.Passes));
        Variant(dictionary, 0x04, "V", BitConverter.GetBytes(0x13u));
        dictionary.WriteByte(0);

        return dictionary.ToArray();
    }

    private static void Variant(Stream dictionary, byte type, string name, ReadOnlySpan<byte> value)
    {
        byte[] key = Encoding.UTF8.GetBytes(name);

        dictionary.WriteByte(type);
        dictionary.Write(BitConverter.GetBytes(key.Length));
        dictionary.Write(key);
        dictionary.Write(BitConverter.GetBytes(value.Length));
        dictionary.Write(value);
    }

    /// <summary>The composite key through Argon2: the transformed key everything else is derived from.</summary>
    internal static Secret Transform(Secret passphrase, string? keyFile, byte[] salt, KdbxKdf kdf)
    {
        using Secret composite = Composite(passphrase, keyFile);

        return Argon(composite, salt, kdf);
    }

    /// <summary>
    /// KDBX's composite key: SHA-256 over the passphrase's SHA-256, then the key file's 32 bytes where
    /// there is one. Every KDBX version and every KDF starts from it.
    /// </summary>
    internal static Secret Composite(Secret passphrase, string? keyFile)
    {
        byte[] composite = new byte[64];
        byte[] compositeHash = new byte[32];

        try
        {
            SHA256.HashData(passphrase.Bytes, composite.AsSpan(0, 32));

            int length = 32;

            if (keyFile is not null)
            {
                using Secret key = KdbxKeyFile.Read(keyFile);

                key.Bytes.CopyTo(composite.AsSpan(32));
                length = 64;
            }

            SHA256.HashData(composite.AsSpan(0, length), compositeHash);

            return Secret.From(compositeHash);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(composite);
            CryptographicOperations.ZeroMemory(compositeHash);
        }
    }

    /// <summary>
    /// Argon2 over the composite key: libsodium where it can (Argon2id, one lane, a 16-byte salt, what
    /// this client writes), the managed <see cref="Argon2"/> for everything else KDBX allows.
    /// </summary>
    internal static Secret Argon(Secret composite, ReadOnlySpan<byte> salt, KdbxKdf kdf,
                                 ReadOnlySpan<byte> secret = default, ReadOnlySpan<byte> associated = default)
    {
        byte[] derived = new byte[32];

        try
        {
            if (!kdf.Argon2d && kdf.Lanes == 1 && salt.Length == 16 && secret.IsEmpty && associated.IsEmpty)
            {
                PasswordBasedKeyDerivationAlgorithm.Argon2id(new Argon2Parameters
                {
                    DegreeOfParallelism = 1,
                    MemorySize = kdf.MemoryKibibytes,
                    NumberOfPasses = kdf.Passes,
                }).DeriveBytes(composite.Bytes, salt, derived);
            }
            else
            {
                Argon2.Derive(kdf.Argon2d ? Argon2.Variant.D : Argon2.Variant.Id, composite.Bytes, salt, kdf.Lanes,
                              kdf.MemoryKibibytes, kdf.Passes, derived, secret, associated);
            }

            return Secret.From(derived);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(derived);
        }
    }

    /// <summary>The key one block's HMAC is computed with, the header's being block <see cref="ulong.MaxValue"/>.</summary>
    private static byte[] BlockKey(byte[] hmacBase, ulong index)
    {
        byte[] input = new byte[8 + hmacBase.Length];

        BinaryPrimitives.WriteUInt64LittleEndian(input, index);
        hmacBase.CopyTo(input, 8);

        try
        {
            return SHA512.HashData(input);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(input);
        }
    }

    /// <summary>The cipher text as HMAC blocks, ending in the empty block a reader stops at.</summary>
    private static void WriteBlocks(Stream file, byte[] cipher, byte[] hmacBase)
    {
        ulong index = 0;

        for (int at = 0; at < cipher.Length; at += BlockBytes)
        {
            WriteBlock(file, index++, cipher.AsSpan(at, Math.Min(BlockBytes, cipher.Length - at)), hmacBase);
        }

        WriteBlock(file, index, [], hmacBase);
    }

    /// <summary>One block: its HMAC over its index, length and bytes, then the length and the bytes.</summary>
    private static void WriteBlock(Stream file, ulong index, ReadOnlySpan<byte> block, byte[] hmacBase)
    {
        byte[] signed = new byte[12 + block.Length];

        BinaryPrimitives.WriteUInt64LittleEndian(signed, index);
        BinaryPrimitives.WriteInt32LittleEndian(signed.AsSpan(8), block.Length);
        block.CopyTo(signed.AsSpan(12));

        byte[] key = BlockKey(hmacBase, index);

        try
        {
            file.Write(HMACSHA256.HashData(key, signed));
            file.Write(signed.AsSpan(8));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    /// <summary>The decrypted payload: the inner header, with the stream key and the attachments, then the document.</summary>
    private static byte[] Payload(KdbxGroup root)
    {
        byte[] streamKey = RandomNumberGenerator.GetBytes(64);
        byte[] streamHash = SHA512.HashData(streamKey);
        List<KdbxAttachment> attachments = [];
        MemoryStream payload = new();

        try
        {
            using ChaCha20 stream = new(streamHash.AsSpan(0, 32), streamHash.AsSpan(32, 12));

            InnerField(payload, 1, BitConverter.GetBytes(3u));
            InnerField(payload, 2, streamKey);

            Collect(root, attachments);

            foreach (KdbxAttachment attachment in attachments)
            {
                InnerField(payload, 3, [0x00, .. attachment.Content.Span]);
            }

            InnerField(payload, 0, []);

            XmlWriterSettings settings = new() { Encoding = new UTF8Encoding(false), Indent = true, CloseOutput = false };

            using (XmlWriter xml = XmlWriter.Create(payload, settings))
            {
                Document(xml, root, stream);
            }

            return payload.ToArray();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload.GetBuffer());
            CryptographicOperations.ZeroMemory(streamKey);
            CryptographicOperations.ZeroMemory(streamHash);
            payload.Dispose();
        }
    }

    private static void InnerField(Stream payload, byte id, ReadOnlySpan<byte> value)
    {
        payload.WriteByte(id);
        payload.Write(BitConverter.GetBytes(value.Length));
        payload.Write(value);
    }

    /// <summary>Every attachment, in the order the document refers to them by.</summary>
    private static void Collect(KdbxGroup group, List<KdbxAttachment> attachments)
    {
        foreach (KdbxEntry entry in group.Entries)
        {
            attachments.AddRange(entry.Attachments);
        }

        foreach (KdbxGroup child in group.Groups)
        {
            Collect(child, attachments);
        }
    }

    private static void Document(XmlWriter xml, KdbxGroup root, ChaCha20 stream)
    {
        string now = Time(DateTime.UtcNow);

        xml.WriteStartDocument(standalone: true);
        xml.WriteStartElement("KeePassFile");

        xml.WriteStartElement("Meta");
        xml.WriteElementString("Generator", "Quickshell");
        xml.WriteElementString("DatabaseName", root.Name);
        xml.WriteElementString("DatabaseNameChanged", now);
        xml.WriteStartElement("MemoryProtection");
        xml.WriteElementString("ProtectTitle", "False");
        xml.WriteElementString("ProtectUserName", "False");
        xml.WriteElementString("ProtectPassword", "True");
        xml.WriteElementString("ProtectURL", "False");
        xml.WriteElementString("ProtectNotes", "False");
        xml.WriteEndElement();
        xml.WriteElementString("RecycleBinEnabled", "False");
        xml.WriteEndElement();

        xml.WriteStartElement("Root");
        int reference = 0;
        Group(xml, root, stream, now, ref reference);
        xml.WriteStartElement("DeletedObjects");
        xml.WriteEndElement();
        xml.WriteEndElement();

        xml.WriteEndElement();
        xml.WriteEndDocument();
    }

    private static void Group(XmlWriter xml, KdbxGroup group, ChaCha20 stream, string now, ref int reference)
    {
        xml.WriteStartElement("Group");
        xml.WriteElementString("UUID", Uuid());
        xml.WriteElementString("Name", group.Name);
        Times(xml, now);
        xml.WriteElementString("IsExpanded", "True");

        foreach (KdbxEntry entry in group.Entries)
        {
            Entry(xml, entry, stream, now, ref reference);
        }

        foreach (KdbxGroup child in group.Groups)
        {
            Group(xml, child, stream, now, ref reference);
        }

        xml.WriteEndElement();
    }

    private static void Entry(XmlWriter xml, KdbxEntry entry, ChaCha20 stream, string now, ref int reference)
    {
        xml.WriteStartElement("Entry");
        xml.WriteElementString("UUID", Uuid());
        Times(xml, now);

        Field(xml, "Title", entry.Title);
        Field(xml, "UserName", entry.UserName);
        Field(xml, "URL", entry.Url);
        Field(xml, "Notes", entry.Notes);

        xml.WriteStartElement("String");
        xml.WriteElementString("Key", "Password");
        xml.WriteStartElement("Value");
        xml.WriteAttributeString("Protected", "True");

        byte[] masked = entry.Password?.Bytes.ToArray() ?? [];

        try
        {
            stream.Xor(masked);
            xml.WriteString(Convert.ToBase64String(masked));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(masked);
        }

        xml.WriteEndElement();
        xml.WriteEndElement();

        foreach (KdbxAttachment attachment in entry.Attachments)
        {
            xml.WriteStartElement("Binary");
            xml.WriteElementString("Key", attachment.Name);
            xml.WriteStartElement("Value");
            xml.WriteAttributeString("Ref", (reference++).ToString(System.Globalization.CultureInfo.InvariantCulture));
            xml.WriteEndElement();
            xml.WriteEndElement();
        }

        xml.WriteEndElement();
    }

    private static void Field(XmlWriter xml, string key, string value)
    {
        xml.WriteStartElement("String");
        xml.WriteElementString("Key", key);
        xml.WriteElementString("Value", value);
        xml.WriteEndElement();
    }

    private static void Times(XmlWriter xml, string now)
    {
        xml.WriteStartElement("Times");
        xml.WriteElementString("CreationTime", now);
        xml.WriteElementString("LastModificationTime", now);
        xml.WriteElementString("LastAccessTime", now);
        xml.WriteElementString("ExpiryTime", now);
        xml.WriteElementString("Expires", "False");
        xml.WriteElementString("UsageCount", "0");
        xml.WriteElementString("LocationChanged", now);
        xml.WriteEndElement();
    }

    /// <summary>A time as KDBX 4 writes one: seconds since 0001-01-01 UTC, eight bytes little-endian, in base64.</summary>
    private static string Time(DateTime utc) =>
        Convert.ToBase64String(BitConverter.GetBytes(utc.Ticks / TimeSpan.TicksPerSecond));

    private static string Uuid() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
}

/// <summary>
/// The ChaCha20 key stream of RFC 8439, which KDBX 4 masks protected values with. .NET ships it only
/// inside an AEAD, and the inner stream wants the bare cipher with its counter starting at zero.
/// </summary>
internal sealed class ChaCha20 : IDisposable
{
    private readonly uint[] _state = new uint[16];
    private readonly uint[] _working = new uint[16];
    private readonly byte[] _block = new byte[64];
    private int _used = 64;

    public ChaCha20(ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce)
    {
        _state[0] = 0x61707865;
        _state[1] = 0x3320646E;
        _state[2] = 0x79622D32;
        _state[3] = 0x6B206574;

        for (int word = 0; word < 8; word++)
        {
            _state[4 + word] = BinaryPrimitives.ReadUInt32LittleEndian(key[(word * 4)..]);
        }

        _state[12] = 0;

        for (int word = 0; word < 3; word++)
        {
            _state[13 + word] = BinaryPrimitives.ReadUInt32LittleEndian(nonce[(word * 4)..]);
        }
    }

    /// <summary>XORs the next bytes of the stream into <paramref name="data"/>, carrying on where the last call stopped.</summary>
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

        for (int round = 0; round < 10; round++)
        {
            Quarter(0, 4, 8, 12);
            Quarter(1, 5, 9, 13);
            Quarter(2, 6, 10, 14);
            Quarter(3, 7, 11, 15);
            Quarter(0, 5, 10, 15);
            Quarter(1, 6, 11, 12);
            Quarter(2, 7, 8, 13);
            Quarter(3, 4, 9, 14);
        }

        for (int word = 0; word < 16; word++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(_block.AsSpan(word * 4), _working[word] + _state[word]);
        }

        _state[12]++;
        _used = 0;
    }

    private void Quarter(int a, int b, int c, int d)
    {
        uint[] x = _working;

        x[a] += x[b]; x[d] = uint.RotateLeft(x[d] ^ x[a], 16);
        x[c] += x[d]; x[b] = uint.RotateLeft(x[b] ^ x[c], 12);
        x[a] += x[b]; x[d] = uint.RotateLeft(x[d] ^ x[a], 8);
        x[c] += x[d]; x[b] = uint.RotateLeft(x[b] ^ x[c], 7);
    }

    public void Dispose()
    {
        CryptographicOperations.ZeroMemory(System.Runtime.InteropServices.MemoryMarshal.AsBytes(_state.AsSpan()));
        CryptographicOperations.ZeroMemory(System.Runtime.InteropServices.MemoryMarshal.AsBytes(_working.AsSpan()));
        CryptographicOperations.ZeroMemory(_block);
    }
}
