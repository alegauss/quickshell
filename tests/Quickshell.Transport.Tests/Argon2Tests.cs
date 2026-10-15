using NSec.Cryptography;
using Quickshell.Transport;
using Xunit;

namespace Quickshell.Transport.Tests;

/// <summary>
/// The managed Argon2 (QS308) held to RFC 9106's own test vectors, and to libsodium where the two
/// can compute the same thing.
/// </summary>
public sealed class Argon2Tests
{
    /// <summary>RFC 9106 §5.1 and §5.3: four lanes, 32 KiB, three passes, with a secret and associated data.</summary>
    [Theory]
    [InlineData(Argon2.Variant.D, "512b391b6f1162975371d30919734294f868e3be3984f3c1a13a4db9fabe4acb")]
    [InlineData(Argon2.Variant.Id, "0d640df58d78766c08c037a34a8b53c9d01ef0452d75b65eb52520e96b01e659")]
    public void TheRfcVectorsAreReproduced(Argon2.Variant variant, string expected)
    {
        byte[] tag = new byte[32];

        Argon2.Derive(variant, Filled(32, 0x01), Filled(16, 0x02), lanes: 4, memoryKibibytes: 32, passes: 3, tag,
                      secret: Filled(8, 0x03), associated: Filled(12, 0x04));

        Assert.Equal(expected, Convert.ToHexStringLower(tag));
    }

    /// <summary>One lane and a 16-byte salt, which libsodium computes too: the two agree.</summary>
    [Fact]
    public void ArgonIdAgreesWithLibsodiumWhereBothApply()
    {
        byte[] password = "a composite key, say"u8.ToArray();
        byte[] salt = Filled(16, 0x5A);
        byte[] managed = new byte[32];
        byte[] native = new byte[32];

        Argon2.Derive(Argon2.Variant.Id, password, salt, lanes: 1, memoryKibibytes: 1024, passes: 2, managed);

        PasswordBasedKeyDerivationAlgorithm.Argon2id(new Argon2Parameters
        {
            DegreeOfParallelism = 1,
            MemorySize = 1024,
            NumberOfPasses = 2,
        }).DeriveBytes(password, salt, native);

        Assert.Equal(native, managed);
    }

    private static byte[] Filled(int length, byte value) => Enumerable.Repeat(value, length).ToArray();
}
