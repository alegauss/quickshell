using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Quickshell.Transport;

/// <summary>Where a saved secret rests, which is a decision with quite different consequences.</summary>
public enum SecretVault
{
    /// <summary>
    /// Windows Credential Manager. Preferred for a per-host secret, because the user can see what is
    /// stored and revoke it with a tool they already have and already trust — which nothing this
    /// client could build would match.
    /// </summary>
    CredentialManager,

    /// <summary>
    /// A file this client writes, encrypted with DPAPI against the current user. The floor: a copied
    /// file is useless on another machine. Used where there is no Credential Manager to use.
    /// </summary>
    ProtectedFile,
}

/// <summary>
/// Saved passwords, and the honest account of what saving one does and does not protect.
///
/// <para><b>Off by default.</b> Nothing is stored until a user asks for it, because a client that
/// stores credentials badly is worse than one that stores none — the second at least does not make
/// a promise.</para>
///
/// <para><b>What DPAPI is, exactly.</b> Ciphertext bound to the Windows account, so a file copied to
/// another machine will not open. That is the whole of it: it is <em>no defence at all</em> against
/// anything already running as the user, which can simply ask DPAPI to decrypt as the user asked.
/// <see cref="WhatThisDoesNotProtect"/> is that sentence in the words a settings surface must show,
/// and it is here rather than in a window so that every surface says the same thing.</para>
///
/// <para><b>A master password is what changes that</b>, and it is a real key derivation feeding an
/// authenticated cipher rather than a hash and a comparison — see <see cref="MasterKey"/>. Portable
/// mode <em>requires</em> one, because DPAPI binds to a machine's user and a portable install has no
/// business assuming it will be run by the same one.</para>
/// </summary>
public sealed class SecretStore
{
    /// <summary>What the user must be told about DPAPI, in the words they should read.</summary>
    public const string WhatThisDoesNotProtect =
        "A saved password is encrypted against this Windows account, so a copy of the file will not "
        + "open on another machine. It is not protected from anything already running as you on this "
        + "one. Set a master password if you need it to survive a stolen laptop.";

    /// <summary>How this client names itself in Credential Manager, so a user can find its entries.</summary>
    private const string Prefix = "quickshell:";

    private readonly string? _directory;
    private readonly Secret? _master;

    private SecretStore(SecretVault vault, string? directory, Secret? master, bool portable)
    {
        Vault = vault;
        Portable = portable;
        _directory = directory;
        _master = master;
    }

    /// <summary>Where secrets are kept.</summary>
    public SecretVault Vault { get; }

    /// <summary>Whether this install carries its own settings and cannot rely on a Windows account.</summary>
    public bool Portable { get; }

    /// <summary>Whether a master password is in force.</summary>
    public bool HasMasterPassword => _master is not null;

    /// <summary>
    /// The ordinary store: Credential Manager, with a master password if the user set one.
    /// </summary>
    public static SecretStore Installed(Secret? master = null) =>
        new(SecretVault.CredentialManager, null, master, portable: false);

    /// <summary>
    /// A store in a directory of this client's own, for a portable install or a test.
    /// </summary>
    /// <param name="directory">Where the files go.</param>
    /// <param name="master">
    /// The master password. <b>Required when <paramref name="portable"/> is set</b>, because DPAPI
    /// binds to a machine's user and a portable install is by definition one that may be carried to
    /// a different one — where the ciphertext would not open and, worse, where the user would have
    /// believed it was protected by something.
    /// </param>
    /// <param name="portable">Whether this is a portable install.</param>
    public static SecretStore In(string directory, Secret? master = null, bool portable = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        if (portable && master is null)
        {
            throw new SshException(
                SshFailureKind.Unrecognised,
                "A portable install needs a master password before it can save anything.",
                "DPAPI binds ciphertext to one Windows account, and a portable install is one that "
                + "may be run by another — so there is nothing here to protect a saved password "
                + "with unless the user supplies it.",
                "Set a master password, or do not save passwords in portable mode.");
        }

        return new SecretStore(SecretVault.ProtectedFile, directory, master, portable);
    }

    /// <summary>Saves a secret for one account on one host, replacing whatever was there.</summary>
    public void Save(SshEndpoint endpoint, Secret secret)
    {
        ArgumentNullException.ThrowIfNull(secret);

        byte[] sealedUp = Seal(secret.Bytes, Target(endpoint));

        try
        {
            if (Vault == SecretVault.CredentialManager)
            {
                CredentialManager.Write(Target(endpoint), sealedUp);

                return;
            }

            Directory.CreateDirectory(_directory!);
            File.WriteAllBytes(FileFor(endpoint), sealedUp);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(sealedUp);
        }
    }

    /// <summary>What was saved for this account, or null where nothing was.</summary>
    public Secret? Load(SshEndpoint endpoint)
    {
        byte[]? sealedUp = Vault == SecretVault.CredentialManager
            ? CredentialManager.Read(Target(endpoint))
            : File.Exists(FileFor(endpoint)) ? File.ReadAllBytes(FileFor(endpoint)) : null;

        if (sealedUp is null)
        {
            return null;
        }

        try
        {
            return Open(sealedUp, Target(endpoint));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(sealedUp);
        }
    }

    /// <summary>Forgets what was saved for this account.</summary>
    /// <returns>Whether there was anything to forget.</returns>
    public bool Forget(SshEndpoint endpoint)
    {
        if (Vault == SecretVault.CredentialManager)
        {
            return CredentialManager.Delete(Target(endpoint));
        }

        if (!File.Exists(FileFor(endpoint)))
        {
            return false;
        }

        File.Delete(FileFor(endpoint));

        return true;
    }

    /// <summary>
    /// Encrypts, under the master password where there is one and under DPAPI where there is not.
    ///
    /// <para>The target string is bound in as additional data either way, so ciphertext moved between
    /// two entries does not open — a saved password for one host cannot be replayed as another's by
    /// anybody who can write to the store.</para>
    /// </summary>
    private byte[] Seal(ReadOnlySpan<byte> plain, string target)
    {
        if (_master is null)
        {
            return ProtectedData.Protect(plain.ToArray(), Encoding.UTF8.GetBytes(target),
                                         DataProtectionScope.CurrentUser);
        }

        byte[] salt = RandomNumberGenerator.GetBytes(MasterKey.SaltBytes);
        byte[] nonce = RandomNumberGenerator.GetBytes(AesGcm.NonceByteSizes.MaxSize);
        byte[] cipher = new byte[plain.Length];
        byte[] tag = new byte[AesGcm.TagByteSizes.MaxSize];

        using Secret key = MasterKey.Derive(_master, salt);
        using AesGcm cipherSuite = new(key.Bytes, tag.Length);

        cipherSuite.Encrypt(nonce, plain, cipher, tag, Encoding.UTF8.GetBytes(target));

        return [.. salt, .. nonce, .. tag, .. cipher];
    }

    /// <summary>Decrypts, or answers null where the master password is wrong or the bytes are not ours.</summary>
    private Secret? Open(byte[] sealedUp, string target)
    {
        try
        {
            if (_master is null)
            {
                byte[] plain = ProtectedData.Unprotect(sealedUp, Encoding.UTF8.GetBytes(target),
                                                       DataProtectionScope.CurrentUser);

                try
                {
                    return Secret.From(plain);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(plain);
                }
            }

            int nonceLength = AesGcm.NonceByteSizes.MaxSize;
            int tagLength = AesGcm.TagByteSizes.MaxSize;

            if (sealedUp.Length < MasterKey.SaltBytes + nonceLength + tagLength)
            {
                return null;
            }

            ReadOnlySpan<byte> all = sealedUp;
            ReadOnlySpan<byte> salt = all[..MasterKey.SaltBytes];
            ReadOnlySpan<byte> nonce = all.Slice(MasterKey.SaltBytes, nonceLength);
            ReadOnlySpan<byte> tag = all.Slice(MasterKey.SaltBytes + nonceLength, tagLength);
            ReadOnlySpan<byte> cipher = all[(MasterKey.SaltBytes + nonceLength + tagLength)..];

            byte[] plainBytes = new byte[cipher.Length];

            using Secret key = MasterKey.Derive(_master, salt);
            using AesGcm cipherSuite = new(key.Bytes, tagLength);

            try
            {
                cipherSuite.Decrypt(nonce, cipher, tag, plainBytes, Encoding.UTF8.GetBytes(target));

                return Secret.From(plainBytes);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plainBytes);
            }
        }
        catch (Exception failure) when (failure is CryptographicException or ArgumentException)
        {
            // A wrong master password, a file from another account, or bytes somebody edited. All
            // three mean the same thing to a caller: there is no secret here to be had.
            return null;
        }
    }

    /// <summary>How an entry is named, which is also what is bound into the ciphertext.</summary>
    private static string Target(SshEndpoint endpoint) => $"{Prefix}{endpoint}";

    private string FileFor(SshEndpoint endpoint) =>
        Path.Combine(_directory!,
                     Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Target(endpoint)))));
}
