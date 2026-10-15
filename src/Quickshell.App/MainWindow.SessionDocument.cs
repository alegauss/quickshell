using System.IO;

using Quickshell.Transport;

namespace Quickshell.App;

/// <summary>
/// The PDF of chosen sessions (QS315), from the sidebar's menu and from the palette.
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>
    /// Asks where the document goes, given the file to offer and how many sessions it holds; null
    /// where the person declined. A save dialog, unless a caller says otherwise.
    /// </summary>
    public Func<string, int, string?>? AsksDocumentFile { get; set; }

    /// <summary>
    /// Writes a PDF of the sessions selected in the sidebar, a folder standing for every session
    /// under it, or of every saved session where none is selected.
    /// </summary>
    /// <returns>The file written, or null where nothing was.</returns>
    public string? DocumentSessions()
    {
        IReadOnlyList<ResolvedSession> chosen = SelectedSessions();

        return DocumentSessions(chosen.Count > 0 ? chosen : (_store ?? SessionTree.Empty()).Sessions());
    }

    /// <summary>Writes a PDF of the session at <paramref name="path"/>, or of every session in the folder there.</summary>
    /// <returns>The file written, or null where nothing was.</returns>
    public string? DocumentSessionsAt(string path)
    {
        SessionTree store = _store ?? SessionTree.Empty();

        return DocumentSessions(store.Session(path) is { } one ? [one] : store.Group(path));
    }

    private string? DocumentSessions(IReadOnlyList<ResolvedSession> sessions)
    {
        const string caption = "Document sessions";

        if (sessions.Count == 0)
        {
            Say("There are no saved sessions, so there is nothing to document.", caption);

            return null;
        }

        string offered = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "quickshell-sessions.pdf");

        if ((AsksDocumentFile ?? AskedDocumentFile)(offered, sessions.Count) is not { Length: > 0 } file)
        {
            return null;
        }

        SecretStore? secrets = Locations.Current.Portable && OpensSecrets is null ? null : Secrets();

        try
        {
            SessionDocument.Write(sessions, file, DateTimeOffset.Now, CrashContext.Build(), named => Saved(secrets, named));
        }
        catch (Exception failed) when (failed is IOException or UnauthorizedAccessException)
        {
            Say($"The document could not be written to {file}: {failed.Message}", caption);

            return null;
        }

        Say($"{sessions.Count} {(sessions.Count == 1 ? "session is" : "sessions are")} documented in {file}. "
            + "It names keys and saved credentials but holds no password, passphrase or private key.", caption);

        return file;
    }

    /// <summary>Whether a password is saved under a credential name, the secret dropped as soon as it is found.</summary>
    private static bool Saved(SecretStore? secrets, string named)
    {
        using Secret? found = secrets?.LoadNamed(named);

        return found is not null;
    }

    private string? AskedDocumentFile(string offered, int sessions)
    {
        Microsoft.Win32.SaveFileDialog choose = new()
        {
            Title = $"Document {sessions} {(sessions == 1 ? "session" : "sessions")} as PDF",
            FileName = Path.GetFileName(offered),
            InitialDirectory = Path.GetDirectoryName(offered),
            DefaultExt = ".pdf",
            Filter = "PDF document (*.pdf)|*.pdf",
            AddExtension = true,
            OverwritePrompt = true,
        };

        return choose.ShowDialog(this) == true ? choose.FileName : null;
    }

    /// <summary>The document of the selected sessions, from the palette and from no key (QS315).</summary>
    private sealed class DocumentingSessions(MainWindow window) : Doing(window)
    {
        /// <inheritdoc/>
        public override string Name => "Document sessions as PDF";

        /// <inheritdoc/>
        public override void Execute(object? parameter) => Window.DocumentSessions();
    }
}
