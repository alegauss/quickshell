using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Quickshell.App;

/// <summary>
/// The public half of a private key file a session names, as <c>ssh-keygen -l</c> prints it: the
/// algorithm and <c>SHA256:</c> the digest (QS315).
///
/// <para><b>Read from what is public, and nothing else.</b> The <c>.pub</c> beside the key first; then
/// the OpenSSH format's header, which carries the public key unencrypted ahead of the private half;
/// then a PuTTY file's <c>Public-Lines</c>. A passphrase is never asked for and the private half is
/// never decoded, so a document of the sessions can name which key each one uses without that key
/// being opened.</para>
/// </summary>
public static class KeyFingerprint
{
    private const string OpenSshArmour = "-----BEGIN OPENSSH PRIVATE KEY-----";

    private static readonly byte[] OpenSshMagic = Encoding.ASCII.GetBytes("openssh-key-v1\0");

    /// <summary>The fingerprint of the key at <paramref name="privateKey"/>, or the reason there is none.</summary>
    /// <param name="privateKey">The key file, as the session names it after expansion.</param>
    /// <returns><c>ssh-ed25519 SHA256:…</c>, or null with a sentence saying why it could not be read.</returns>
    public static (string? Fingerprint, string Why) Of(string privateKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(privateKey);

        try
        {
            if (File.Exists(privateKey + ".pub") && FromPublicLine(File.ReadAllText(privateKey + ".pub")) is { } beside)
            {
                return (beside, string.Empty);
            }

            if (!File.Exists(privateKey))
            {
                return (null, "the key file is not there");
            }

            string text = File.ReadAllText(privateKey);

            if (text.Contains(OpenSshArmour, StringComparison.Ordinal))
            {
                return FromOpenSsh(text) is { } header
                    ? (header, string.Empty)
                    : (null, "the key file is not one OpenSSH wrote");
            }

            if (text.StartsWith("PuTTY-User-Key-File-", StringComparison.Ordinal))
            {
                return FromPutty(text) is { } putty
                    ? (putty, string.Empty)
                    : (null, "the PuTTY key file has no public lines");
            }

            return (null, "the key is in a format whose public half cannot be read without its passphrase, and no .pub is beside it");
        }
        catch (Exception unreadable) when (unreadable is IOException or UnauthorizedAccessException)
        {
            return (null, $"the key file could not be read: {unreadable.Message}");
        }
    }

    /// <summary>A blob's algorithm and digest, as <c>ssh-keygen -l</c> prints them.</summary>
    public static string? Print(ReadOnlySpan<byte> blob)
    {
        if (blob.Length < 4 || BinaryPrimitives.ReadUInt32BigEndian(blob) is var length && length > blob.Length - 4)
        {
            return null;
        }

        string algorithm = Encoding.ASCII.GetString(blob.Slice(4, (int)length));
        string digest = Convert.ToBase64String(SHA256.HashData(blob)).TrimEnd('=');

        return $"{algorithm} SHA256:{digest}";
    }

    private static string? FromPublicLine(string line)
    {
        string[] parts = line.Trim().Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);

        return parts.Length >= 2 && Decoded(parts[1]) is { } blob ? Print(blob) : null;
    }

    private static string? FromOpenSsh(string text)
    {
        int start = text.IndexOf(OpenSshArmour, StringComparison.Ordinal) + OpenSshArmour.Length;
        int end = text.IndexOf("-----END", start, StringComparison.Ordinal);

        if (end < 0 || Decoded(string.Concat(text[start..end].Where(each => !char.IsWhiteSpace(each)))) is not { } body
            || !body.AsSpan().StartsWith(OpenSshMagic))
        {
            return null;
        }

        // The cipher, the key derivation and its options, the number of keys, then the first public key.
        ReadOnlySpan<byte> rest = body.AsSpan(OpenSshMagic.Length);

        for (int skipped = 0; skipped < 3; skipped++)
        {
            if (Next(ref rest).IsEmpty && rest.IsEmpty)
            {
                return null;
            }
        }

        if (rest.Length < 4)
        {
            return null;
        }

        rest = rest[4..];

        ReadOnlySpan<byte> blob = Next(ref rest);

        return blob.IsEmpty ? null : Print(blob);
    }

    private static string? FromPutty(string text)
    {
        string[] lines = text.Split('\n').Select(each => each.TrimEnd('\r')).ToArray();
        int at = Array.FindIndex(lines, each => each.StartsWith("Public-Lines:", StringComparison.Ordinal));

        if (at < 0 || !int.TryParse(lines[at]["Public-Lines:".Length..].Trim(), out int count) || at + count >= lines.Length)
        {
            return null;
        }

        return Decoded(string.Concat(lines.Skip(at + 1).Take(count))) is { } blob ? Print(blob) : null;
    }

    /// <summary>One SSH string off the front of <paramref name="rest"/>; empty where there is none.</summary>
    private static ReadOnlySpan<byte> Next(ref ReadOnlySpan<byte> rest)
    {
        if (rest.Length < 4 || BinaryPrimitives.ReadUInt32BigEndian(rest) is var length && length > rest.Length - 4)
        {
            rest = [];

            return [];
        }

        ReadOnlySpan<byte> taken = rest.Slice(4, (int)length);

        rest = rest[(4 + (int)length)..];

        return taken;
    }

    private static byte[]? Decoded(string base64)
    {
        try
        {
            return Convert.FromBase64String(base64);
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
