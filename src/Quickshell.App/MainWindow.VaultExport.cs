using System.IO;
using System.Windows;
using System.Windows.Interop;
using Quickshell.Transport;

namespace Quickshell.App;

/// <summary>
/// Exporting the saved passwords to a KeePass vault (QS306): the file a person consults when a
/// password is forgotten, which today only this client on this machine can read.
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>
    /// Asks where the vault goes and what passphrase opens it, given the file to offer and how many
    /// sessions there are; null where the person declined. The dialog, unless a caller says otherwise.
    /// </summary>
    public Func<string, int, VaultExportAnswer?>? AsksVaultExport { get; set; }

    /// <summary>
    /// Confirms the person at the desk before every password is read. Windows Hello, unless a caller
    /// says otherwise — which is how a test answers it, since a desk nobody is at has no face to show.
    /// </summary>
    public Func<string, Task<HelloVerdict>>? Verifies { get; set; }

    /// <summary>The store the passwords are read from: Credential Manager, unless a caller says otherwise.</summary>
    public Func<SecretStore?>? OpensSecrets { get; set; }

    /// <summary>
    /// Asks for a file and a passphrase, confirms the person with Windows Hello, then writes every
    /// saved session and its password to a KDBX 4 vault (QS306).
    /// </summary>
    /// <returns>The file written, or null where nothing was.</returns>
    public async Task<string?> ExportVaultAsync()
    {
        if (Locations.Current.Portable && OpensSecrets is null)
        {
            Say("A portable copy saves no passwords, so there are none to export.", "Export saved passwords");

            return null;
        }

        SessionTree store;

        try
        {
            store = SessionTree.ReadFrom(StoreFile);
        }
        catch (SessionStoreException unreadable)
        {
            Say($"{unreadable.Message}\n\n{unreadable.Means}\n\n{unreadable.Remedy}", "Export saved passwords");

            return null;
        }

        int sessions = store.Sessions().Count;

        if (sessions == 0)
        {
            Say("There are no saved sessions, so there is nothing to export.", "Export saved passwords");

            return null;
        }

        string offered = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "quickshell-sessions.kdbx");

        if ((AsksVaultExport ?? AskedVaultExport)(offered, sessions) is not { } answer)
        {
            return null;
        }

        HelloVerdict verdict = await (Verifies ?? (message => WindowsHello.VerifyAsync(new WindowInteropHelper(this).Handle, message)))(
            "Confirm it is you before every saved password is written to a vault.");

        if (verdict == HelloVerdict.Unavailable)
        {
            Say("Windows Hello is not set up for this account, so there is no way to confirm it is you before every "
                + "password is read, and nothing was exported. Set up a PIN in Settings, Accounts, Sign-in options, then try again.",
                "Export saved passwords");

            return null;
        }

        if (verdict != HelloVerdict.Verified)
        {
            return null;
        }

        using KdbxKey key = DeriveVaultKey(answer.Passphrase);
        using VaultExport export = VaultExport.Gather(store, Secrets(), answer.Keys);

        try
        {
            export.Write(answer.Path, key);

            if (answer.Keep)
            {
                VaultKeeper.Keep(VaultKeyFileOrDefault, answer.Path, key, key.Fingerprint(export.Root), answer.Keys);
                KeepVaultAt(Path.GetFullPath(answer.Path));
            }
        }
        catch (Exception failed) when (failed is IOException or UnauthorizedAccessException)
        {
            Say($"The vault could not be written to {answer.Path}: {failed.Message}", "Export saved passwords");

            return null;
        }

        Say($"{export.Sessions} sessions, {export.WithPassword} of them with a saved password, are in {answer.Path}. "
            + "Open it in KeePassXC or any KeePass app with the passphrase."
            + (answer.Keys ? $" {export.KeyFiles} key files are attached." : string.Empty)
            + (export.MissingKeys.Count > 0 ? $" These key files were not there to attach: {string.Join(", ", export.MissingKeys)}." : string.Empty)
            + (answer.Keep ? " It is written again whenever a saved password or a session changes." : string.Empty),
            "Export saved passwords");

        return answer.Path;
    }

    /// <summary>Where the kept vault's sealed key is: <see cref="Locations.VaultKey"/> unless a caller says otherwise.</summary>
    public string? VaultKeyFile { get; set; }

    /// <summary>
    /// How strong a vault's key derivation is. <see cref="KdbxKdf.Default"/> unless a caller says
    /// otherwise, which a test does so that many exports do not cost a quarter of a gigabyte each.
    /// </summary>
    public KdbxKdf? VaultKdf { get; set; }

    private string VaultKeyFileOrDefault => VaultKeyFile ?? Locations.Current.VaultKey;

    private KdbxKey DeriveVaultKey(string passphrase)
    {
        using Secret secret = Secret.From(passphrase.AsSpan());

        return KdbxKey.Derive(secret, kdf: VaultKdf);
    }

    private SecretStore? Secrets() => (OpensSecrets ?? (() => SecretStore.Installed()))();

    /// <summary>The sentence last said about keeping the vault, so a refusal is said once and not at every change.</summary>
    private string _vaultSaid = string.Empty;

    private bool _vaultWatched;

    /// <summary>
    /// Writes the kept vault again where the store's content changed, off the window's thread, and
    /// says once why where it could not (QS307). Nothing at all while no vault is kept.
    /// </summary>
    /// <returns>What it came to, which a test waits on.</returns>
    public async Task<VaultKeeping> KeepVaultAsync()
    {
        string configured = Settings.VaultFile;
        string stateFile = VaultKeyFileOrDefault;

        Watch(configured.Length > 0);

        if (configured.Length == 0 && !File.Exists(stateFile))
        {
            return new VaultKeeping(VaultKept.Off, string.Empty);
        }

        string storeFile = StoreFile;
        SecretStore? secrets = Secrets();

        VaultKeeping kept = await Task.Run(() =>
        {
            try
            {
                return VaultKeeper.Refresh(stateFile, configured, SessionTree.ReadFrom(storeFile), secrets);
            }
            catch (SessionStoreException unreadable)
            {
                return new VaultKeeping(VaultKept.Failed, $"The vault was not written again: {unreadable.Message}");
            }
        }).ConfigureAwait(false);

        // Back on the window's own thread to say anything, whichever thread the write finished on.
        await Dispatcher.InvokeAsync(() =>
        {
            if (kept.Sentence.Length > 0 && kept.Sentence != _vaultSaid)
            {
                Say(kept.Sentence, "Vault");
            }

            _vaultSaid = kept.Sentence;
        });

        return kept;
    }

    /// <summary>Names the vault to keep in the settings file and this window, and starts keeping it.</summary>
    private void KeepVaultAt(string vault)
    {
        string path = SettingsPath ?? Locations.Current.Settings;

        if (SettingsFile.Readable(path))
        {
            SettingsFile.WriteTo(path, SettingsFile.ReadFrom(path) with { VaultFile = vault });
        }

        Settings = Settings with { VaultFile = vault };
        _vaultSaid = string.Empty;

        Watch(true);
    }

    /// <summary>Listens for saved passwords changing only while a vault is kept, so a window keeping none pays nothing.</summary>
    private void Watch(bool on)
    {
        if (on == _vaultWatched)
        {
            return;
        }

        _vaultWatched = on;

        if (on)
        {
            SecretStore.Changed += SecretChanged;
            Closed += Unwatch;
        }
        else
        {
            SecretStore.Changed -= SecretChanged;
            Closed -= Unwatch;
        }
    }

    private void Unwatch(object? sender, EventArgs closed) => Watch(false);

    /// <summary>A password saved or forgotten anywhere in the process, which may be on a connection's thread.</summary>
    private void SecretChanged(object? sender, EventArgs changed) =>
        Dispatcher.BeginInvoke(() => _ = KeepVaultAsync());

    private VaultExportAnswer? AskedVaultExport(string offered, int sessions)
    {
        VaultExportDialog dialog = new(offered, sessions)
        {
            Owner = this,
            ThemeMode = ThemeMode,
        };

        dialog.ShowDialog();

        return dialog.Answered;
    }

    /// <summary>Exporting the saved passwords, from the palette and from no key (QS306).</summary>
    private sealed class ExportingVault(MainWindow window) : Doing(window)
    {
        /// <inheritdoc/>
        public override string Name => "Export saved passwords to a KeePass vault";

        /// <inheritdoc/>
        public override void Execute(object? parameter) => _ = Window.ExportVaultAsync();
    }
}
