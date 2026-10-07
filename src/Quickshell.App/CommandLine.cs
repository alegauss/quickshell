namespace Quickshell.App;

/// <summary>One flag the client acts on.</summary>
/// <param name="Name">The flag as typed.</param>
/// <param name="Takes">What follows it, or empty where nothing does.</param>
/// <param name="Means">What it does, as the reference says it.</param>
public readonly record struct Flag(string Name, string Takes, string Means);

/// <summary>
/// Every flag the client acts on, in one list (QS178).
///
/// <para><b>The command line is a real surface here and not a convenience.</b> The client has no menu
/// on purpose, so the command line is where a script, a shortcut or another program asks it for
/// anything — and a surface nobody documented is one nobody finds. The entry points read these names
/// rather than spelling their own, and a test holds <c>docs/COMMAND-LINE.md</c> to this list in both
/// directions, as it holds <c>docs/KEYS.md</c> to the bindings.</para>
///
/// <para>Not a help screen. This is a windowed program with no console to print one into, and a
/// <c>--help</c> that opened a dialog would be the client putting a window in front of a script.</para>
/// </summary>
public static class CommandLine
{
    /// <summary>Installs this copy.</summary>
    public const string Install = "--install";

    /// <summary>Removes an installed copy.</summary>
    public const string Uninstall = "--uninstall";

    /// <summary>For every user of the machine, with install or uninstall.</summary>
    public const string AllUsers = "--all-users";

    /// <summary>Asks nothing and shows nothing, with install or uninstall.</summary>
    public const string Quiet = "--quiet";

    /// <summary>Times the start into a file.</summary>
    public const string StartupReport = "--startup-report";

    /// <summary>Opens that many tabs.</summary>
    public const string Tabs = "--tabs";

    /// <summary>Splits the first tab that many ways.</summary>
    public const string Panes = "--panes";

    /// <summary>Types into every pane of the first tab.</summary>
    public const string Broadcast = "--broadcast";

    /// <summary>Opens the file browser.</summary>
    public const string Browse = "--browse";

    /// <summary>Previews an import, from a file or from MobaXterm's own place.</summary>
    public const string Import = "--import";

    /// <summary>Opens a saved session.</summary>
    public const string Session = "--session";

    /// <summary>Traces that session in a log of its own.</summary>
    public const string Trace = "--trace";

    /// <summary>Opens the palette.</summary>
    public const string Palette = "--palette";

    /// <summary>Every flag, in the order the reference lists them.</summary>
    public static readonly IReadOnlyList<Flag> All =
    [
        new(Tabs, "<n>", "Opens that many tabs, up to sixteen, as pressing Ctrl+Shift+T that many times would."),
        new(Panes, "<n>", "Splits the first tab that many ways, up to sixteen, side by side."),
        new(Broadcast, "", "Types into every pane of the first tab at once, after the split."),
        new(Session, "<path>", "Opens a saved session in a tab of its own, by its path in the session store. A folder's path opens every session in it as panes of one tab, typing into all of them."),
        new(Trace, "", "With --session: records that session's negotiation and channels in a log of its own, for this run only."),
        new(Import, "[file]", "Previews importing MobaXterm's sessions, from the file named or from where MobaXterm keeps them; nothing is written until you agree."),
        new(Browse, "", "Opens the file browser."),
        new(Palette, "", "Opens the palette."),
        new(StartupReport, "<file>", "Times this start and writes the milestones to the file once the shell is on screen."),
        new(Install, "", "Installs this copy for the current user, or for everyone with --all-users, and opens no window."),
        new(Uninstall, "", "Removes an installed copy, and asks whether to keep the settings unless --quiet."),
        new(AllUsers, "", "With --install or --uninstall: for every user of the machine, which needs an administrator."),
        new(Quiet, "", "With --install or --uninstall: asks nothing and shows nothing; the exit code is the answer."),
    ];
}
