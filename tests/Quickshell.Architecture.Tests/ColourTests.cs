// System.IO is not in a WPF project's implicit usings, which is why this file names it.
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace Quickshell.Architecture.Tests;

/// <summary>
/// QS83's falsification, as a test: <em>falsified when two windows in this repository declare the
/// same colour</em>.
///
/// <para><b>Stricter than the sentence, because the sentence alone would have passed.</b> Before QS83
/// no two windows declared a colour at all — the chrome declares none — while the terminal's default
/// text, ground and cursor were each written three or four times across three assemblies. So the rule
/// read here is where every colour is written: in <c>Brand.cs</c>, where this client's own colours
/// are declared, or in <c>Rgb.cs</c>, where the type keeps black and white. Anything else refers to
/// them.</para>
///
/// <para><b>The client's sources, not the tests'.</b> A golden scene or a benchmark fixes its own
/// colours on purpose, so an image does not move when the brand does.</para>
///
/// <para><b>The matcher is a pure function</b> and is run against text written to break it, so the
/// guard is known to fire before it is trusted to report nothing.</para>
/// </summary>
public sealed partial class ColourTests
{
    /// <summary>The two files a colour may be written in.</summary>
    private static readonly string[] Homes = ["Brand.cs", "Rgb.cs"];

    /// <summary>
    /// An <c>Rgb</c> of three literal bytes: <c>new Rgb(1, 2, 3)</c>, and the target-typed
    /// <c>new(1, 2, 3)</c> on a line that names the type.
    /// </summary>
    [GeneratedRegex(@"\bRgb\b.*\bnew\s*(?:Rgb\s*)?\(\s*(\d{1,3})\s*,\s*(\d{1,3})\s*,\s*(\d{1,3})\s*\)")]
    private static partial Regex RgbLiteral { get; }

    /// <summary>WPF's own constructors given literal channels, which is how chrome would drift first.</summary>
    [GeneratedRegex(@"\bColor\.From(?:Rgb|Argb)\s*\(\s*(?:0x[0-9A-Fa-f]{1,2}|\d{1,3})")]
    private static partial Regex WpfLiteral { get; }

    /// <summary>A hex colour in a string or an attribute: <c>"#1E1E1E"</c>, <c>"#FF1E1E1E"</c>.</summary>
    [GeneratedRegex(@"""#(?:[0-9A-Fa-f]{8}|[0-9A-Fa-f]{6}|[0-9A-Fa-f]{3})""")]
    private static partial Regex HexLiteral { get; }

    /// <summary>Every colour in the client's sources is written in one of the two homes.</summary>
    [Fact]
    public void EveryColourIsWrittenInOnePlace()
    {
        List<string> strays = [];

        foreach (string path in Sources())
        {
            if (Homes.Contains(Path.GetFileName(path), StringComparer.Ordinal))
            {
                continue;
            }

            strays.AddRange(Declared(File.ReadAllText(path)).Select(found => $"{Relative(path)}: {found}"));
        }

        Assert.True(strays.Count == 0,
                    "a colour is written outside Brand.cs, where every other surface would have to find it "
                    + "to change it. Refer to Brand instead: " + string.Join("; ", strays));
    }

    /// <summary>Inside the home, no colour is written twice either — two names for one value is two decisions to keep in step.</summary>
    [Fact]
    public void NoColourIsWrittenTwiceInTheBrand()
    {
        string brand = Sources().Single(path => Path.GetFileName(path) == "Brand.cs");

        string[] declared = [.. Declared(File.ReadAllText(brand))];

        Assert.NotEmpty(declared);
        Assert.Equal(declared.Length, declared.Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>The matcher fires on every spelling a colour arrives in, and on nothing that only looks like one.</summary>
    [Fact]
    public void TheMatcherFindsEverySpellingAndNothingElse()
    {
        string written =
            """
            public Rgb Foreground { get; set; } = new(214, 219, 228);
            Rgb ground = new Rgb(16, 18, 24);
            Brush accent = new SolidColorBrush(Color.FromRgb(0xD9, 0x77, 0x57));
            Color band = Color.FromArgb(61, 217, 119, 87);
            string toast = "#E89072";
            """;

        string innocent =
            """
            Emulator emulator = new(200, 50, scrollback: 2_000);
            public Rgb Foreground { get; set; } = Brand.Ink;
            _entries[232 + index] = new Rgb(level, level, level);
            string heading = "#Performance";
            """;

        string[] caught = [.. Declared(written)];
        string[] mistaken = [.. Declared(innocent)];

        Assert.True(caught.Length == 5, $"expected five colours, found {caught.Length}: {string.Join(" | ", caught)}");
        Assert.True(mistaken.Length == 0, $"read a colour into text that has none: {string.Join(" | ", mistaken)}");
    }

    /// <summary>What a text declares, one entry per colour, as the value it declares.</summary>
    private static IEnumerable<string> Declared(string text)
    {
        foreach (string line in text.Split('\n'))
        {
            // A documentation comment may quote a colour to explain one; it declares nothing.
            if (line.TrimStart().StartsWith("//", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (Match rgb in RgbLiteral.Matches(line))
            {
                yield return $"rgb({rgb.Groups[1].Value}, {rgb.Groups[2].Value}, {rgb.Groups[3].Value})";
            }

            foreach (Match wpf in WpfLiteral.Matches(line))
            {
                yield return wpf.Value;
            }

            foreach (Match hex in HexLiteral.Matches(line))
            {
                yield return hex.Value;
            }
        }
    }

    /// <summary>The client's own sources, markup included, less build output.</summary>
    private static IEnumerable<string> Sources() =>
        Directory.EnumerateFiles(Path.Combine(RepositoryRoot(), "src"), "*.*", SearchOption.AllDirectories)
            .Where(path => Path.GetExtension(path) is ".cs" or ".xaml")
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                           && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    private static string Relative(string path) => Path.GetRelativePath(RepositoryRoot(), path);

    private static string RepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Quickshell.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory.FullName;
    }
}
