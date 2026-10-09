using System.Security.Cryptography;
using NSec.Cryptography;

namespace Quickshell.Transport;

/// <summary>
/// Turns a master password into a key, slowly and expensively on purpose.
///
/// <para><b>Argon2id, through libsodium</b> (QS115). The design asked for a memory-hard function,
/// and .NET 10 ships none, so the owner accepted a dependency for it on 2026-10-09: NSec, which
/// calls libsodium's audited implementation rather than carrying its own. Memory-hard is the whole
/// point. A graphics card that tries billions of PBKDF2 guesses a second gets a small fraction of
/// that against a function that needs <see cref="MemoryKibibytes"/> of its own memory per guess.</para>
///
/// <para><b>The old derivation stays readable.</b> Everything sealed before QS115 used
/// PBKDF2-HMAC-SHA512 at <see cref="LegacyIterations"/> iterations, and its format carries no
/// version. <see cref="DeriveLegacy"/> is kept so those secrets still open, and the store seals
/// them again in the new format the first time it opens one.</para>
///
/// <para>The parameters are here rather than at the call site because they are part of the format:
/// changing one makes every stored secret unreadable, so it is a decision with a version behind it
/// and not a number to tune.</para>
/// </summary>
internal static class MasterKey
{
    /// <summary>
    /// Argon2id's memory per derivation: 64 MiB, the second profile RFC 9106 recommends and well
    /// above OWASP's minimum. A store is opened once, so a user does not notice it.
    /// </summary>
    public const int MemoryKibibytes = 64 * 1024;

    /// <summary>Argon2id's passes over that memory, as RFC 9106's same profile has it.</summary>
    public const int Passes = 3;

    /// <summary>The iterations the pre-QS115 PBKDF2-HMAC-SHA512 derivation used, kept to read what it sealed.</summary>
    public const int LegacyIterations = 600_000;

    /// <summary>How much salt each stored secret carries, which is its own and never reused. Argon2id takes exactly this.</summary>
    public const int SaltBytes = 16;

    /// <summary>A 256-bit key, which is what the cipher above this wants.</summary>
    private const int KeyBytes = 32;

    private static readonly Argon2id Argon = PasswordBasedKeyDerivationAlgorithm.Argon2id(new Argon2Parameters
    {
        DegreeOfParallelism = 1,
        MemorySize = MemoryKibibytes,
        NumberOfPasses = Passes,
    });

    /// <summary>Derives the key for one stored secret with Argon2id, into a buffer that erases itself.</summary>
    public static Secret Derive(Secret master, ReadOnlySpan<byte> salt)
    {
        byte[] derived = new byte[KeyBytes];

        try
        {
            Argon.DeriveBytes(master.Bytes, salt, derived);

            return Secret.From(derived);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(derived);
        }
    }

    /// <summary>The pre-QS115 derivation, PBKDF2-HMAC-SHA512, used only to open what it sealed.</summary>
    public static Secret DeriveLegacy(Secret master, ReadOnlySpan<byte> salt)
    {
        byte[] derived = Rfc2898DeriveBytes.Pbkdf2(master.Bytes, salt, LegacyIterations,
                                                   HashAlgorithmName.SHA512, KeyBytes);

        try
        {
            return Secret.From(derived);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(derived);
        }
    }
}
