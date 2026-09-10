using System.IO;
using Quickshell.Transport;

namespace Quickshell.App;

/// <summary>The three ways the shells this client meets read a quoted word.</summary>
public enum ShellKind
{
    /// <summary>Windows' command processor: double quotes, which no Windows name can contain.</summary>
    Cmd,

    /// <summary>PowerShell: single quotes, inside which a doubled quote is one quote.</summary>
    PowerShell,

    /// <summary>A POSIX shell on the far side: single quotes, closed and reopened around a quote.</summary>
    Posix,
}

/// <summary>
/// A path turned into a word the shell in a pane reads as that path and nothing else — QS64's
/// falsification, <em>falsified when a path typed into a terminal by a drop is not quoted for the
/// shell</em>.
///
/// <para><b>Always quoted, never only when it looks necessary.</b> The same argument
/// <see cref="ShellWord.Quote"/> makes for the far side holds here: quoting only the paths that
/// contain something suspicious is a safety that depends on a list of suspicious characters being
/// complete.</para>
/// </summary>
public static class ShellQuoting
{
    /// <summary>
    /// Which shell a program is, by its file name. What is not Windows' two is taken to be POSIX,
    /// which is what every host a session connects to runs.
    /// </summary>
    public static ShellKind Of(string program)
    {
        ArgumentNullException.ThrowIfNull(program);

        string name = Path.GetFileName(program);

        return name.ToUpperInvariant() switch
        {
            "CMD.EXE" or "CMD" => ShellKind.Cmd,
            "POWERSHELL.EXE" or "PWSH.EXE" or "POWERSHELL" or "PWSH" => ShellKind.PowerShell,
            _ => ShellKind.Posix,
        };
    }

    /// <summary>
    /// One path as one word.
    ///
    /// <para><b>cmd is the one with a gap, and it is named rather than hidden.</b> Inside double
    /// quotes cmd still expands <c>%NAME%</c>, and interactively there is no escape for it — so a
    /// file literally named with a variable between percent signs is typed as the variable's value.
    /// No other character a Windows name may hold means anything inside the quotes.</para>
    /// </summary>
    public static string Quote(string path, ShellKind kind)
    {
        ArgumentNullException.ThrowIfNull(path);

        return kind switch
        {
            ShellKind.Cmd => "\"" + path + "\"",
            ShellKind.PowerShell => "'" + path.Replace("'", "''", StringComparison.Ordinal) + "'",
            _ => ShellWord.Quote(path),
        };
    }

    /// <summary>
    /// Several paths as the words of one command line, with a space after the last — so a user who
    /// dropped a file onto a prompt can go on typing the next argument without adding one.
    /// </summary>
    public static string Line(IEnumerable<string> paths, ShellKind kind)
    {
        ArgumentNullException.ThrowIfNull(paths);

        return string.Join(' ', paths.Select(path => Quote(path, kind))) + " ";
    }
}
