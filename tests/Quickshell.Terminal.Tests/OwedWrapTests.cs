using System.Text;
using Quickshell.Terminal;
using Xunit;

namespace Quickshell.Terminal.Tests;

/// <summary>
/// QS248: an owed wrap at the right edge survives what moves nothing - SGR, a mode, a report, REP -
/// and is cancelled by what moves the cursor or changes the grid under it, as xterm's ResetWrap is.
/// </summary>
public sealed class OwedWrapTests
{
    private const string E = "\u001b";

    /// <summary>
    /// The design's own falsifier: <em><c>\e[1;80H</c> + <c>ab</c> + <c>\e[31m</c> + <c>c</c> on an
    /// 80-column screen leaves <c>c</c> in row 1</em>. Here the screen is ten wide.
    /// </summary>
    [Fact]
    public void AColourChangeAtTheEdgeKeepsTheWrapOwed()
    {
        Emulator emulator = Fed(E + "[1;9Hab" + E + "[31mc");

        Assert.Equal('b', emulator.Buffer.Screen(0)[9].Codepoint);
        Assert.Equal('c', emulator.Buffer.Screen(1)[0].Codepoint);
        Assert.Equal(Colour.Indexed(1), emulator.Buffer.Screen(1)[0].Foreground);
    }

    [Theory]
    [InlineData("[0m")]        // SGR reset
    [InlineData("[?25l")]      // a mode that moves nothing
    [InlineData("[4l")]        // IRM
    [InlineData("[g")]         // a tab stop cleared
    [InlineData("[6n")]        // a report
    [InlineData("[c")]
    [InlineData("[1 q")]       // DECSCUSR
    [InlineData("[s")]         // SCOSC, which saves and moves nothing
    public void WhatMovesNothingLeavesTheWrapOwed(string sequence)
    {
        Emulator emulator = Fed(E + "[1;9Hab" + E + sequence + "c");

        Assert.Equal('b', emulator.Buffer.Screen(0)[9].Codepoint);
        Assert.Equal('c', emulator.Buffer.Screen(1)[0].Codepoint);
    }

    [Theory]
    [InlineData("[C")]         // CUF, which stops at the edge it is already on
    [InlineData("[1;10H")]     // CUP to the very cell
    [InlineData("[10G")]       // CHA to it
    [InlineData("[K")]         // EL from the cursor
    public void WhatMovesOrErasesCancelsIt(string sequence)
    {
        Emulator emulator = Fed(E + "[1;9Hab" + E + sequence + "c");

        Assert.Equal('c', emulator.Buffer.Screen(0)[9].Codepoint);
        Assert.Equal(' ', emulator.Buffer.Screen(1)[0].Codepoint);
    }

    private static Emulator Fed(string stream)
    {
        Emulator emulator = new(10, 4, scrollback: 0);
        emulator.Feed(Encoding.ASCII.GetBytes(stream));

        return emulator;
    }
}
