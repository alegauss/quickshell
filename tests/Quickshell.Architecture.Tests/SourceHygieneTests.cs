// System.IO is not in a WPF project's implicit usings, which is why this file names it.
using System.IO;
using Xunit;

namespace Quickshell.Architecture.Tests;

/// <summary>
/// Things about this repository's own source that no compiler checks and no reviewer can see.
/// </summary>
public sealed class SourceHygieneTests
{
    /// <summary>
    /// QS98. A terminal sequence in a test is spelled with escapes, never a raw control byte.
    ///
    /// <para><b>This exists because the alternative cost a debugging cycle.</b> A test file was
    /// written with literal <c>ESC</c> bytes, the way two others already were, and this one arrived
    /// without them — <c>"ESC[?25l"</c> became five printable characters. It did not fail loudly:
    /// the emulator did the right thing with the text, and eight tests failed on assertions about
    /// modes that had never been set. Every one of them read as a defect in the code under test.</para>
    ///
    /// <para>A control byte in source is invisible in every diff, every review and every editor.
    /// Nothing showed the difference between the file that had them and the file that did not, and
    /// whether they survive at all is a property of whatever wrote the file rather than of the test.
    /// So the byte is banned outright and the escape is the only spelling.</para>
    ///
    /// <para>QS100 widened it from tests to <c>src</c> as well, having watched six raw <c>ESC</c>
    /// bytes go into a shipped source file with every check green. None of the reasoning above was
    /// ever about tests; that was only where the first instance happened. A raw <c>ESC</c> in
    /// <c>src</c> is in fact the quieter of the two, because a test that loses its escapes fails
    /// loudly and a source file that gains one compiles and works.</para>
    /// </summary>
    [Fact]
    public void NoSourceCarriesARawControlByte()
    {
        List<string> offenders = [];

        foreach (string file in Sources())
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                continue;
            }

            byte[] bytes = File.ReadAllBytes(file);
            int found = 0;

            foreach (byte value in bytes)
            {
                // Tab, carriage return and line feed are how a source file is laid out. Everything
                // else below 0x20 is a byte somebody meant to be an escape sequence.
                if (value < 0x20 && value is not (0x09 or 0x0A or 0x0D))
                {
                    found++;
                }
            }

            if (found > 0)
            {
                offenders.Add($"{Path.GetRelativePath(RepositoryRoot(), file)} ({found})");
            }
        }

        Assert.True(offenders.Count == 0,
            "these sources carry raw control bytes, which are invisible in a diff and survive " +
            "or vanish depending on what wrote the file. Spell them as escapes instead - " +
            $"\\u001b for ESC: {string.Join(", ", offenders)}");
    }

    /// <summary>
    /// QS149. No file carries text that went through the wrong codepage on its way back to disk.
    ///
    /// <para><b>The same failure as the one above, one codepage along.</b> A shell one-liner read a
    /// UTF-8 file as Windows-1252 and wrote it back as UTF-8, and every em dash in it became three
    /// characters of mojibake. It compiled and every test passed; a diff stat with far more lines
    /// than the edit was what caught it, and only because somebody looked.</para>
    ///
    /// <para><b>The shape is exact, which is what keeps real prose out of it.</b> A character of
    /// UTF-8 decoded as 1252 becomes a lead character — one whose 1252 byte opens a multibyte
    /// sequence — followed by exactly as many characters whose 1252 bytes continue one. "Não" and
    /// "ç" are a lead with an ordinary letter after it and are not that; an em dash gone wrong,
    /// U+00E2 U+20AC U+201D, is. Spelled as code points here, or this file would be its own
    /// offender.</para>
    /// </summary>
    [Fact]
    public void NoFileCarriesTextThatWentThroughTheWrongCodepage()
    {
        List<string> offenders = [];

        foreach (string file in Sources().Concat(Prose()))
        {
            if (Generated(file))
            {
                continue;
            }

            string[] lines = File.ReadAllLines(file);
            int[] mangled = [.. Enumerable.Range(0, lines.Length)
                                          .Where(index => Enumerable.Range(0, lines[index].Length)
                                                                    .Any(at => Mangled(lines[index], at)))];

            if (mangled.Length > 0)
            {
                offenders.Add($"{Path.GetRelativePath(RepositoryRoot(), file)}:{mangled[0] + 1} " +
                              $"({mangled.Length} lines)");
            }
        }

        Assert.True(offenders.Count == 0,
            "these files carry UTF-8 that was decoded as Windows-1252 and written back - an em " +
            "dash read as three characters is the usual sign. Restore them from git rather than " +
            $"by hand: {string.Join(", ", offenders)}");
    }

    /// <summary>
    /// And the shape is what it says: an em dash, a c-cedilla and an emoji gone through 1252 are
    /// caught, and the same characters written correctly — Portuguese included — are not.
    ///
    /// <para>Given as code points, so this file holds none of the text it is about and is not its
    /// own offender.</para>
    /// </summary>
    [Theory]
    [InlineData("61 20 E2 20AC 201D 20 62", true)]
    [InlineData("61 C3 A7 C3 A3 6F", true)]
    [InlineData("F0 178 2DC 20AC", true)]
    [InlineData("61 20 2014 20 62", false)]
    [InlineData("4E E3 6F 20 2014 20 61 E7 E3 6F 2C 20 63 61 66 E9 2026", false)]
    [InlineData("C2 20 A9", false)]
    public void TheWrongCodepageIsRecognisedAndRealProseIsNot(string codePoints, bool mangled)
    {
        string text = string.Concat(codePoints.Split(' ')
                                              .Select(hex => (char)int.Parse(hex, System.Globalization.NumberStyles.HexNumber,
                                                                             System.Globalization.CultureInfo.InvariantCulture)));

        Assert.Equal(mangled, Enumerable.Range(0, text.Length).Any(at => Mangled(text, at)));
    }

    /// <summary>
    /// Whether a re-encoded UTF-8 character starts here: a lead byte's 1252 character, then the
    /// exact number of continuation bytes' characters that lead promises.
    /// </summary>
    private static bool Mangled(string text, int at)
    {
        if (Windows1252(text[at]) is not { } lead || lead < 0xC2 || lead > 0xF4)
        {
            return false;
        }

        int follows = lead switch
        {
            >= 0xF0 => 3,
            >= 0xE0 => 2,
            _ => 1,
        };

        if (at + follows >= text.Length)
        {
            return false;
        }

        for (int next = 1; next <= follows; next++)
        {
            if (Windows1252(text[at + next]) is not { } continuing || continuing < 0x80 || continuing > 0xBF)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The byte Windows-1252 writes a character as, or null for one it cannot. Spelled out rather
    /// than asked of an encoding provider, because the twenty-seven characters 1252 puts where
    /// Latin-1 has controls are the whole of what this test is about — and spelled as numbers, so
    /// the table is not itself a file full of the characters it is looking for.
    /// </summary>
    private static int? Windows1252(char character) => character switch
    {
        >= (char)0xA0 and <= (char)0xFF => character,
        (char)0x20AC => 0x80, (char)0x201A => 0x82, (char)0x0192 => 0x83, (char)0x201E => 0x84,
        (char)0x2026 => 0x85, (char)0x2020 => 0x86, (char)0x2021 => 0x87, (char)0x02C6 => 0x88,
        (char)0x2030 => 0x89, (char)0x0160 => 0x8A, (char)0x2039 => 0x8B, (char)0x0152 => 0x8C,
        (char)0x017D => 0x8E, (char)0x2018 => 0x91, (char)0x2019 => 0x92, (char)0x201C => 0x93,
        (char)0x201D => 0x94, (char)0x2022 => 0x95, (char)0x2013 => 0x96, (char)0x2014 => 0x97,
        (char)0x02DC => 0x98, (char)0x2122 => 0x99, (char)0x0161 => 0x9A, (char)0x203A => 0x9B,
        (char)0x0153 => 0x9C, (char)0x017E => 0x9E, (char)0x0178 => 0x9F,
        _ => null,
    };

    /// <summary>
    /// The hand-written prose and scripts beside the code: the documents, the tools and the
    /// case files. A shell one-liner rewrites these as readily as it rewrites a source file.
    /// </summary>
    private static IEnumerable<string> Prose()
    {
        string root = RepositoryRoot();

        foreach ((string folder, string pattern, SearchOption depth) in
                 ((string, string, SearchOption)[])
                 [
                     (".", "*.md", SearchOption.TopDirectoryOnly),
                     (".", "*.cmd", SearchOption.TopDirectoryOnly),
                     ("docs", "*.md", SearchOption.AllDirectories),
                     ("tools", "*.ps1", SearchOption.AllDirectories),
                     ("cases", "*.json", SearchOption.AllDirectories),
                 ])
        {
            string under = Path.Combine(root, folder);

            if (!Directory.Exists(under))
            {
                continue;
            }

            foreach (string file in Directory.EnumerateFiles(under, pattern, depth))
            {
                yield return file;
            }
        }
    }

    private static bool Generated(string file) =>
        file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
        || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal);

    /// <summary>
    /// Every C# file this repository is written in, both halves of it. Build output is skipped
    /// because it is generated and a control byte in it says nothing about the source.
    /// </summary>
    private static IEnumerable<string> Sources()
    {
        string root = RepositoryRoot();

        foreach (string folder in (string[])["src", "tests"])
        {
            foreach (string file in Directory.EnumerateFiles(
                Path.Combine(root, folder), "*.cs", SearchOption.AllDirectories))
            {
                yield return file;
            }
        }
    }

    private static string RepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Quickshell.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory!.FullName;
    }
}
