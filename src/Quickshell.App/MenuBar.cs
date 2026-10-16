namespace Quickshell.App;

/// <summary>
/// Where an action goes in the menu bar (QS354), worked out from its name.
///
/// <para><b>No action is written into the menu.</b> The bar lists what the palette lists, read off the
/// same bindings (<see cref="Commands.From"/>); what this decides is only which heading an action sits
/// under. That is a rule over the action's own words, first match winning, and an action no rule names
/// goes under Terminal, so a new action is in the menu the moment it is bound, under some heading, and a
/// test keeps the headings honest.</para>
/// </summary>
public static class MenuBar
{
    /// <summary>The headings, in the order the bar shows them, each with its access key.</summary>
    public static readonly IReadOnlyList<string> Headings =
        ["_Session", "_Edit", "_View", "_Terminal", "Tab and _Pane", "T_ransfer", "_Forward", "_Agents", "_Help"];

    /// <summary>Words that put an action under a heading, checked in this order; the first that occurs wins.</summary>
    private static readonly (string Heading, string[] Words)[] Rules =
    [
        ("_Agents", ["agent", "claude"]),
        ("_Help", ["help", "diagnostic", "log folder", "install quickshell", "show all commands"]),
        ("_Forward", ["forward"]),
        ("T_ransfer", ["browse", "send file", "upload", "download"]),
        ("Tab and _Pane", ["tab", "pane", "divider", "split", "broadcast"]),
        ("_Edit", ["copy", "paste", "find", "select"]),
        ("_Session", ["session", "connect", "record", "trace", "vault", "password", "import"]),
        ("_View", ["home", "settings", "zoom", "font", "theme", "sidebar", "menu bar"]),
        ("_Terminal", ["scroll", "clear", "reset"]),
    ];

    /// <summary>The heading an action named <paramref name="name"/> goes under.</summary>
    public static string HeadingOf(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        foreach ((string heading, string[] words) in Rules)
        {
            if (words.Any(word => name.Contains(word, StringComparison.OrdinalIgnoreCase)))
            {
                return heading;
            }
        }

        return "_Terminal";
    }

    /// <summary>
    /// The actions under each heading, in the bar's order, each heading's actions in the order they were
    /// bound; a heading with none is left out.
    /// </summary>
    public static IReadOnlyList<(string Heading, IReadOnlyList<Command> Actions)> Grouped(IReadOnlyList<Command> actions)
    {
        ArgumentNullException.ThrowIfNull(actions);

        return [.. Headings.Select(heading => (heading, (IReadOnlyList<Command>)[.. actions.Where(action => HeadingOf(action.Name) == heading)]))
                           .Where(group => group.Item2.Count > 0)];
    }
}
