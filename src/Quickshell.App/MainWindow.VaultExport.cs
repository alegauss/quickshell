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

        using Secret passphrase = Secret.From(answer.Passphrase.AsSpan());
        using VaultExport export = VaultExport.Gather(store, (OpensSecrets ?? (() => SecretStore.Installed()))());

        try
        {
            export.Write(answer.Path, passphrase);
        }
        catch (Exception failed) when (failed is IOException or UnauthorizedAccessException)
        {
            Say($"The vault could not be written to {answer.Path}: {failed.Message}", "Export saved passwords");

            return null;
        }

        Say($"{export.Sessions} sessions, {export.WithPassword} of them with a saved password, are in {answer.Path}. "
            + "Open it in KeePassXC or any KeePass app with the passphrase.", "Export saved passwords");

        return answer.Path;
    }

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
