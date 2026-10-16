using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

using Quickshell.Transport;

using Xunit;

namespace Quickshell.Transport.Tests;

/// <summary>QS356: an argument vector joined for a Windows server, so each word reaches the program as given.</summary>
public sealed partial class ArgvTests
{
    /// <summary>
    /// The arguments Windows reads back out of the line are the ones given, every awkward character included:
    /// read back by <c>CommandLineToArgvW</c> itself, which is the parse the program will make.
    /// </summary>
    [Theory]
    [InlineData("plain")]
    [InlineData("two words")]
    [InlineData("a \"quoted\" word")]
    [InlineData("100% & echo pwned ^ | <in >out")]
    [InlineData("it's")]
    [InlineData(@"C:\path with space\")]
    [InlineData(@"ends in a backslash\")]
    [InlineData(@"\\server\share\""odd""\")]
    [InlineData("")]
    public void EachArgumentIsReadBackAsItWasGiven(string word)
    {
        string[] words = ["prog.exe", word, "next"];

        Assert.Equal(words[1..], CommandLineToArgv("prog.exe " + Arguments(Argv.ForWindows(words))));
    }

    /// <summary>
    /// The line cmd sees holds nothing it acts on: one PowerShell switch set and base64. The program and the
    /// directory are PowerShell literals, a quote in them doubled.
    /// </summary>
    [Fact]
    public void TheLineIsPowerShellWithNothingForCmdToRead()
    {
        string line = Argv.ForWindows(["it's.exe", "a&b"], @"C:\it's here");

        Assert.Matches(@"^powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand [A-Za-z0-9+/=]+$", line);

        string script = Script(line);

        Assert.Contains("$s.FileName='it''s.exe';", script, StringComparison.Ordinal);
        Assert.Contains(@"Set-Location -LiteralPath 'C:\it''s here';", script, StringComparison.Ordinal);
        Assert.Contains("exit 127", script, StringComparison.Ordinal);
    }

    [Fact]
    public void AVectorNeedsAProgram() => Assert.Throws<ArgumentException>(() => Argv.ForWindows([]));

    private static string Script(string line) =>
        Encoding.Unicode.GetString(Convert.FromBase64String(line[(line.LastIndexOf(' ') + 1)..]));

    /// <summary>The arguments string the script hands ProcessStartInfo, as PowerShell reads its literal.</summary>
    private static string Arguments(string line) =>
        ArgumentsLiteral().Match(Script(line)).Groups[1].Value.Replace("''", "'", StringComparison.Ordinal);

    [GeneratedRegex(@"\$s\.Arguments='((?:[^']|'')*)';")]
    private static partial Regex ArgumentsLiteral();

    /// <summary>Windows' own parse of a command line, the program's name dropped.</summary>
    private static string[] CommandLineToArgv(string line)
    {
        nint parsed = CommandLineToArgvW(line, out int count);

        try
        {
            return [.. Enumerable.Range(1, count - 1).Select(at => Marshal.PtrToStringUni(Marshal.ReadIntPtr(parsed, at * nint.Size))!)];
        }
        finally
        {
            LocalFree(parsed);
        }
    }

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial nint CommandLineToArgvW(string line, out int count);

    [LibraryImport("kernel32.dll")]
    private static partial nint LocalFree(nint memory);
}
