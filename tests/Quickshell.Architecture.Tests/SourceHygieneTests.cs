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
    /// </summary>
    [Fact]
    public void NoTestSourceCarriesARawControlByte()
    {
        string tests = Path.Combine(RepositoryRoot(), "tests");
        List<string> offenders = [];

        foreach (string file in Directory.EnumerateFiles(tests, "*.cs", SearchOption.AllDirectories))
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
            "these test sources carry raw control bytes, which are invisible in a diff and survive " +
            "or vanish depending on what wrote the file. Spell them as escapes instead - " +
            $"\\u001b for ESC: {string.Join(", ", offenders)}");
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
