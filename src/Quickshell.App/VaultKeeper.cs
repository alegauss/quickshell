using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Quickshell.Transport;

namespace Quickshell.App;

/// <summary>What keeping the vault current came to.</summary>
public enum VaultKept
{
    /// <summary>No vault is kept.</summary>
    Off,

    /// <summary>The vault already holds what the store holds, so it was left as it is.</summary>
    Unchanged,

    /// <summary>The vault was written again.</summary>
    Written,

    /// <summary>The vault changed outside the client since it was last written, so it was not written over.</summary>
    Edited,

    /// <summary>A vault is named, but the client holds no key for it.</summary>
    NoKey,

    /// <summary>Writing it failed.</summary>
    Failed,
}

/// <summary>What keeping the vault came to, and the sentence the person is owed where it was not done.</summary>
public sealed record VaultKeeping(VaultKept Outcome, string Sentence);

/// <summary>
/// Keeps an exported vault current (QS307): written again from the store whenever a saved password or
/// a session changes, so the file consulted is the one in use.
///
/// <para><b>Without the passphrase.</b> The vault's derived key is kept sealed under DPAPI for this
/// account, beside the hash of the file as last written and an HMAC of its content. The HMAC is what
/// makes a rewrite cheap to decline: a change that leaves the vault's content as it was, a password
/// sealed again or a session's colour changed, writes nothing.</para>
///
/// <para><b>Never over somebody's edit.</b> A vault whose bytes are not the ones last written here was
/// changed outside the client, in KeePassXC perhaps, and writing over it would lose that change; a
/// merge is out of scope, so it is left and the person is told, naming the file.</para>
/// </summary>
public static class VaultKeeper
{
    private static readonly Lock Writing = new();

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>
    /// Records that the vault at <paramref name="vault"/> was just written under <paramref name="key"/>
    /// from content whose fingerprint is <paramref name="fingerprint"/>, so later changes keep it.
    /// </summary>
    public static void Keep(string stateFile, string vault, KdbxKey key, byte[] fingerprint, bool keys = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateFile);
        ArgumentNullException.ThrowIfNull(key);

        Save(stateFile, new State(Path.GetFullPath(vault), Convert.ToBase64String(key.Seal()),
                                  KdbxWriter.Hash(vault), Convert.ToBase64String(fingerprint), keys));
    }

    /// <summary>
    /// Writes the vault again if the store's content has changed, or says why it cannot; with no
    /// vault configured, forgets any key still kept.
    /// </summary>
    /// <param name="stateFile">Where the sealed key and the last write are kept.</param>
    /// <param name="configured">The vault the settings name, or empty.</param>
    /// <param name="store">The saved sessions as they are now.</param>
    /// <param name="secrets">Where their passwords are.</param>
    public static VaultKeeping Refresh(string stateFile, string configured, SessionTree store, SecretStore? secrets)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateFile);
        ArgumentNullException.ThrowIfNull(store);

        lock (Writing)
        {
            if (string.IsNullOrWhiteSpace(configured))
            {
                if (File.Exists(stateFile))
                {
                    File.Delete(stateFile);
                }

                return new VaultKeeping(VaultKept.Off, string.Empty);
            }

            string vault = Path.GetFullPath(configured);
            string noKey = $"The vault {vault} is not being kept up to date, because this client holds no key for it. "
                           + "Export to it once with Keep this file up to date ticked.";

            if (Load(stateFile) is not { } state || !string.Equals(state.Path, vault, StringComparison.OrdinalIgnoreCase))
            {
                return new VaultKeeping(VaultKept.NoKey, noKey);
            }

            using KdbxKey? key = KdbxKey.Unseal(Convert.FromBase64String(state.Key));

            if (key is null)
            {
                return new VaultKeeping(VaultKept.NoKey, noKey);
            }

            try
            {
                bool there = File.Exists(vault);

                if (there && KdbxWriter.Hash(vault) != state.File)
                {
                    return new VaultKeeping(VaultKept.Edited,
                        $"{vault} has changed since this client last wrote it, in KeePassXC perhaps, so it was not written over "
                        + "and is no longer kept up to date. Export to it again to replace it, or empty vaultFile in the settings.");
                }

                using VaultExport export = VaultExport.Gather(store, secrets, state.Keys);

                byte[] fingerprint = key.Fingerprint(export.Root);

                if (there && CryptographicOperations.FixedTimeEquals(fingerprint, Convert.FromBase64String(state.Fingerprint)))
                {
                    return new VaultKeeping(VaultKept.Unchanged, string.Empty);
                }

                export.Write(vault, key);
                Save(stateFile, state with { File = KdbxWriter.Hash(vault), Fingerprint = Convert.ToBase64String(fingerprint) });

                return new VaultKeeping(VaultKept.Written, string.Empty);
            }
            catch (Exception failed) when (failed is IOException or UnauthorizedAccessException)
            {
                return new VaultKeeping(VaultKept.Failed, $"The vault {vault} could not be written again: {failed.Message}");
            }
        }
    }

    private static State? Load(string stateFile)
    {
        try
        {
            return File.Exists(stateFile) ? JsonSerializer.Deserialize<State>(File.ReadAllBytes(stateFile)) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void Save(string stateFile, State state)
    {
        if (Path.GetDirectoryName(stateFile) is { Length: > 0 } folder)
        {
            Directory.CreateDirectory(folder);
        }

        File.WriteAllBytes(stateFile, JsonSerializer.SerializeToUtf8Bytes(state, Indented));
    }

    /// <summary>The vault kept, its key sealed under DPAPI, the hash and fingerprint of its last write, and whether it carries key files (QS345).</summary>
    private sealed record State(string Path, string Key, string File, string Fingerprint, bool Keys = false);
}
