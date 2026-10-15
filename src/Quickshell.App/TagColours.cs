namespace Quickshell.App;

/// <summary>
/// The `tagColours` setting (QS351): which tags a tab shows, and in which of the theme's own brushes.
///
/// <para><b>Named brushes, never a hex.</b> A tag is given <c>critical</c>, <c>caution</c>,
/// <c>success</c> or <c>accent</c>, which are the theme's, so a scheme or the dark theme can never make
/// a tag unreadable, and Brand.cs stays the one place a colour is written. <c>prod</c> is critical
/// without being named; naming it is how it is given another.</para>
///
/// <para>Written as <c>staging=caution, db=accent</c>: one string, which the settings file splices like
/// every other value, rather than an object only a whole rewrite could carry.</para>
/// </summary>
public static class TagColours
{
    /// <summary>The names a tag may be given, and the theme brush each one is.</summary>
    public static readonly IReadOnlyDictionary<string, string> Brushes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["critical"] = "SystemFillColorCriticalBrush",
        ["caution"] = "SystemFillColorCautionBrush",
        ["success"] = "SystemFillColorSuccessBrush",
        ["accent"] = "AccentFillColorDefaultBrush",
    };

    /// <summary>
    /// Each tag and the brush resource it is drawn in, prod included, from the setting's text; entries
    /// that do not read are left out and named in <paramref name="refused"/>.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Parse(string text, List<string>? refused = null)
    {
        Dictionary<string, string> colours = new(StringComparer.OrdinalIgnoreCase) { ["prod"] = Brushes["critical"] };

        foreach (string entry in (text ?? string.Empty).Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string[] parts = entry.Split('=', 2, StringSplitOptions.TrimEntries);

            if (parts.Length == 2 && parts[0].Length > 0 && Brushes.TryGetValue(parts[1], out string? brush))
            {
                colours[parts[0]] = brush;
            }
            else
            {
                refused?.Add(entry);
            }
        }

        return colours;
    }
}
