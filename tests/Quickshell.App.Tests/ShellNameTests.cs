using Quickshell.App;
using Xunit;

namespace Quickshell.App.Tests;

/// <summary>
/// A shell that writes its own executable's path as its title is named as the shell (QS282), and
/// nothing else a host writes is touched.
/// </summary>
public sealed class ShellNameTests
{
    /// <summary>The one device, atlas and render loop these tabs draw with. See QS49.</summary>
    private static readonly TerminalShare Shared = new();

    [Theory]
    [InlineData(@"C:\WINDOWS\system32\cmd.exe", "Command Prompt")]
    [InlineData(@"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe", "Windows PowerShell")]
    [InlineData(@"C:\Program Files\PowerShell\7\pwsh.exe", "PowerShell")]
    [InlineData(@"C:\WINDOWS\system32\wsl.exe", "WSL")]
    [InlineData(@"C:\WINDOWS\system32\bash.exe", "WSL")]
    [InlineData(@"D:\tools\nu.exe", "nu")]
    public void AnExecutablesPathIsNamedAsItsProgram(string written, string shown) =>
        Assert.Equal(shown, ShellName.Of(written));

    /// <summary>A directory, a command line, an elevated prefix or a remote's title is shown as written.</summary>
    [Theory]
    [InlineData(@"C:\WINDOWS\system32\cmd.exe /k build")]
    [InlineData(@"Administrator: C:\WINDOWS\system32\cmd.exe")]
    [InlineData(@"C:\Users\alex")]
    [InlineData("vim notes.txt")]
    [InlineData("cmd.exe")]
    [InlineData("deploy@db1: ~")]
    [InlineData("")]
    public void AnythingElseIsShownAsWritten(string written) =>
        Assert.Equal(written, ShellName.Of(written));

    /// <summary>A local tab whose title is cmd's path is called Command Prompt, and a name the user gave still outranks it.</summary>
    [Fact]
    public void ALocalTabIsNamedAsItsShellAndTheUsersNameStillWins()
    {
        (string shown, string named) = Sta.Run(() =>
        {
            TerminalTab tab = TerminalTab.Open(Settings.Default, Shared, @"C:\WINDOWS\system32\cmd.exe");
            string before = tab.Title;

            tab.Named = "build box";

            return (before, tab.Title);
        });

        Assert.Equal("Command Prompt", shown);
        Assert.Equal("build box", named);
    }
}
