using System.IO;

namespace Quickshell.App;

/// <summary>
/// MobaXterm's own placeholders at the start of a path, as it writes them in a session's key field
/// (QS276): <c>_ProfileDir_\OneDrive\keys\eztec.ppk</c>.
///
/// <para>Expanded in two places: on import, so a new store holds a real path, and where a session's
/// key is offered, so a store imported before this existed works without being imported again.</para>
/// </summary>
public static class MobaXtermPaths
{
    /// <summary>
    /// The path with a leading placeholder replaced, case-insensitively. A path without one, or with
    /// an underscore word this does not know, comes back as it was.
    /// </summary>
    public static string Expand(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        foreach ((string placeholder, Func<string> folder) in Known)
        {
            if (!path.StartsWith(placeholder, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string rest = path[placeholder.Length..];

            // MobaXterm writes the drive as "_CurrentDrive_:\…", so the colon is already there.
            if (placeholder == CurrentDrive)
            {
                string letter = folder();

                return rest.StartsWith(':') ? letter + rest : $"{letter}:{rest}";
            }

            return folder() + rest;
        }

        return path;
    }

    private const string CurrentDrive = "_CurrentDrive_";

    /// <summary>
    /// The placeholders and where each points. The drive MobaXterm ran from is not knowable here, so
    /// it is the system drive, which is where a default install puts it.
    /// </summary>
    private static readonly (string Placeholder, Func<string> Folder)[] Known =
    [
        ("_ProfileDir_", () => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)),
        ("_MyDocuments_", () => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)),
        ("_AppDataDir_", () => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)),
        (CurrentDrive, () => (Path.GetPathRoot(Environment.SystemDirectory) is { Length: > 0 } root ? root : "C:")[..1]),
    ];
}
