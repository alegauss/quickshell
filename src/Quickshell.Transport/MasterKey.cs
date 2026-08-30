using System.Security.Cryptography;

namespace Quickshell.Transport;

/// <summary>
/// Turns a master password into a key, slowly and expensively on purpose.
///
/// <para><b>The design asks for Argon2id or an equivalent memory-hard function, and this is not
/// one.</b> .NET 10 ships no memory-hard derivation, so the choice was between a third-party
/// cryptographic dependency in the one path where a mistake is unrecoverable, and the strongest
/// thing the framework does have. This is the second: PBKDF2-HMAC-SHA512 at
/// <see cref="Iterations"/> iterations, which is well above OWASP's current figure for that
/// construction.</para>
///
/// <para><b>What that costs, stated rather than glossed.</b> PBKDF2 is compute-hard and not
/// memory-hard, so it is far cheaper to attack with a GPU than Argon2id would be — the gap is
/// roughly two orders of magnitude against an attacker with hardware. It is a real derivation and it
/// is honest work; it is not what the design asked for, and QS115 is where that is closed.</para>
///
/// <para>The parameters are here rather than at the call site because they are part of the format:
/// changing one makes every stored secret unreadable, so it is a decision with a version behind it
/// and not a number to tune.</para>
/// </summary>
internal static class MasterKey
{
    /// <summary>
    /// How many iterations. OWASP's 2023 figure for PBKDF2-HMAC-SHA512 is 210,000; this is more,
    /// because the operation happens once when a store is opened and a user will not notice it.
    /// </summary>
    public const int Iterations = 600_000;

    /// <summary>How much salt each stored secret carries, which is its own and never reused.</summary>
    public const int SaltBytes = 16;

    /// <summary>A 256-bit key, which is what the cipher above this wants.</summary>
    private const int KeyBytes = 32;

    /// <summary>Derives the key for one stored secret, into a buffer that erases itself.</summary>
    public static Secret Derive(Secret master, ReadOnlySpan<byte> salt)
    {
        byte[] derived = Rfc2898DeriveBytes.Pbkdf2(master.Bytes, salt, Iterations,
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
