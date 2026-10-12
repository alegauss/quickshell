using System.IO;

namespace Quickshell.App;

/// <summary>
/// A shell named as the shell rather than as its executable's path (QS282).
///
/// <para>cmd writes its own full path through OSC the moment it starts, so a local tab, and the window
/// with it, read <c>C:\WINDOWS\system32\cmd.exe</c>. QS161 ranks a title the host wrote above the tab's
/// host on purpose, and that ranking stays; this is one narrow rule beneath it. A title that is exactly
/// a rooted path to an <c>.exe</c>, with nothing after it, is shown as the program's name. Anything else
/// a host writes, a directory or a running command included, is shown as written.</para>
/// </summary>
public static class ShellName
{
    private static readonly Dictionary<string, string> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        ["cmd"] = "Command Prompt",
        ["powershell"] = "Windows PowerShell",
        ["pwsh"] = "PowerShell",
        ["wsl"] = "WSL",
        ["bash"] = "WSL",
    };

    /// <summary>The title to show for one a host wrote: the shell's name where it wrote its path, otherwise what it wrote.</summary>
    public static string Of(string written)
    {
        ArgumentNullException.ThrowIfNull(written);

        string trimmed = written.Trim();

        if (!trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            || !Path.IsPathFullyQualified(trimmed)
            || trimmed.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
        {
            return written;
        }

        string program = Path.GetFileNameWithoutExtension(trimmed);

        return Known.TryGetValue(program, out string? named) ? named : program;
    }
}
