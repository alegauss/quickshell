using Quickshell.Transport;

using Xunit;

namespace Quickshell.App.Tests;

/// <summary>
/// QS327: the question that stopped an agent's command, named from what the command said. The texts
/// are the fixture's own, from an exec with no terminal and stdin closed.
/// </summary>
public sealed class CommandPromptsTests
{
    [Fact]
    public void SudoWithNoTerminalIsAPasswordPrompt() =>
        Assert.Equal("prompt.sudo_password", CommandPrompts.Recognise(new CommandResult(1, string.Empty,
            "sudo: a terminal is required to read the password; either use the -S option to read from standard input or configure an askpass helper\n"
            + "sudo: a password is required\n"))?.Name);

    [Fact]
    public void AQuestionAnsweredByNothingIsAConfirmation() =>
        Assert.Equal("prompt.confirmation", CommandPrompts.Recognise(new CommandResult(1, "Continue? [y/N] rc=1\n", string.Empty))?.Name);

    [Fact]
    public void APasswordAskedOfNobodyIsNamed() =>
        Assert.Equal("prompt.password", CommandPrompts.Recognise(new CommandResult(null, string.Empty, "probe@db's Password:", TimedOut: true))?.Name);

    [Fact]
    public void AnOrdinaryFailureNamesNoPrompt() =>
        Assert.Null(CommandPrompts.Recognise(new CommandResult(2, string.Empty, "ls: cannot access '/nope': No such file or directory\n")));

    [Fact]
    public void OnlyAWindowsServerIsTakenForOne()
    {
        Assert.True(CommandPrompts.IsWindows("SSH-2.0-OpenSSH_for_Windows_9.5"));
        Assert.False(CommandPrompts.IsWindows("SSH-2.0-OpenSSH_9.6p1 Ubuntu-3ubuntu13"));
        Assert.False(CommandPrompts.IsWindows(null));
    }
}
