using Quickshell.Transport;

namespace Quickshell.App;

/// <summary>
/// Importing saved passwords from a KeePass vault (QS308): the other half of QS306's export, for a new
/// machine or a reinstall, where every remembered password stayed with the old account.
/// </summary>
public sealed partial class MainWindow
{
    private const string ImportCaption = "Import saved passwords";

    /// <summary>Asks which vault and how to open it; null where the person declined. The dialog, unless a caller says otherwise.</summary>
    public Func<VaultImportAnswer?>? AsksVaultImport { get; set; }

    /// <summary>
    /// Shows what the import would do and answers which conflicts to replace, or null to import nothing.
    /// The preview, unless a caller says otherwise.
    /// </summary>
    public Func<VaultImport, string, IReadOnlySet<string>?>? ConfirmsVaultImport { get; set; }

    /// <summary>
    /// Reads a vault, matches its entries to the saved sessions, shows what would change, and saves the
    /// passwords the person agreed to (QS308).
    /// </summary>
    /// <returns>How many passwords were saved, or null where nothing was attempted.</returns>
    public int? ImportVault()
    {
        if (Locations.Current.Portable && OpensSecrets is null)
        {
            Say("A portable copy remembers no passwords, so there is nowhere to import them to.", ImportCaption);

            return null;
        }

        SessionTree store;

        try
        {
            store = SessionTree.ReadFrom(StoreFile);
        }
        catch (SessionStoreException unreadable)
        {
            Say($"{unreadable.Message}\n\n{unreadable.Means}\n\n{unreadable.Remedy}", ImportCaption);

            return null;
        }

        if ((AsksVaultImport ?? AskedVaultImport)() is not { } answer)
        {
            return null;
        }

        SecretStore secrets = Secrets() ?? SecretStore.Installed();
        KdbxVault vault;

        using (Secret passphrase = Secret.From(answer.Passphrase.AsSpan()))
        {
            try
            {
                vault = KdbxReader.Read(answer.Path, passphrase, answer.KeyFile);
            }
            catch (KdbxException refused)
            {
                Say(refused.Message, ImportCaption);

                return null;
            }
        }

        using (vault)
        {
            VaultImport plan = VaultImport.Plan(store, vault, secrets);

            if (plan.Items.Count == 0)
            {
                Say($"No entry in {answer.Path} has a password for a saved session, so nothing was imported."
                    + (plan.Unmatched.Count > 0 ? $" {plan.Unmatched.Count} entries match no saved session's host, port and login." : string.Empty),
                    ImportCaption);

                return 0;
            }

            if ((ConfirmsVaultImport ?? AskedVaultImportPreview)(plan, answer.Path) is not { } replace)
            {
                return null;
            }

            int saved = plan.Apply(secrets, replace);

            Say($"{saved} passwords were saved from {answer.Path}; the sessions they belong to will not ask for them.", ImportCaption);

            return saved;
        }
    }

    private VaultImportAnswer? AskedVaultImport()
    {
        VaultImportDialog dialog = new(Settings.VaultFile)
        {
            Owner = this,
            ThemeMode = ThemeMode,
        };

        dialog.ShowDialog();

        return dialog.Answered;
    }

    private IReadOnlySet<string>? AskedVaultImportPreview(VaultImport plan, string path)
    {
        VaultImportPreview preview = new(plan, path)
        {
            Owner = this,
            ThemeMode = ThemeMode,
        };

        preview.ShowDialog();

        return preview.Replace;
    }

    /// <summary>Importing saved passwords, from the palette and from no key (QS308).</summary>
    private sealed class ImportingVault(MainWindow window) : Doing(window)
    {
        /// <inheritdoc/>
        public override string Name => "Import saved passwords from a KeePass vault";

        /// <inheritdoc/>
        public override void Execute(object? parameter) => Window.ImportVault();
    }
}
