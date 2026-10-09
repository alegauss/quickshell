using System.Text;
using Quickshell.Terminal;
using Xunit;

namespace Quickshell.Terminal.Tests;

/// <summary>
/// CSI t (QS236): every report answered with the pane's own geometry, the title stack, and the
/// manipulations refused.
/// </summary>
public sealed class WindowOperationTests
{
    private const string E = "\u001b";

    // ---- The falsification ----

    /// <summary>The design's own: <em>falsified when CSI 18 t goes unanswered</em>.</summary>
    [Fact]
    public void TheTextAreaSizeIsAnswered()
    {
        Assert.Equal(E + "[8;24;80t", Sent(Fed(E + "[18t")));
    }

    // ---- The reports ----

    [Fact]
    public void ThePaneIsTheScreenAHostCanSee()
    {
        Assert.Equal(E + "[9;24;80t", Sent(Fed(E + "[19t")));
    }

    [Fact]
    public void ThePaneIsNeverIconifiedAndSitsAtTheOrigin()
    {
        Assert.Equal(E + "[1t", Sent(Fed(E + "[11t")));
        Assert.Equal(E + "[3;0;0t", Sent(Fed(E + "[13t")));
    }

    [Fact]
    public void PixelReportsUseTheCellTheWindowDraws()
    {
        Emulator emulator = new(80, 24);
        emulator.UseCellPixels(9, 18);
        emulator.Feed(Encoding.ASCII.GetBytes(E + "[16t" + E + "[14t" + E + "[15t"));

        Assert.Equal(E + "[6;18;9t" + E + "[4;432;720t" + E + "[5;432;720t", Sent(emulator));
    }

    [Fact]
    public void AHeadlessEmulatorAnswersZeroPixelsRatherThanNothing()
    {
        Assert.Equal(E + "[6;0;0t", Sent(Fed(E + "[16t")));
    }

    [Fact]
    public void TheSizeFollowsAResize()
    {
        Emulator emulator = new(80, 24);
        emulator.Resize(100, 30);
        emulator.Feed(Encoding.ASCII.GetBytes(E + "[18t"));

        Assert.Equal(E + "[8;30;100t", Sent(emulator));
    }

    // ---- The manipulations ----

    [Theory]
    [InlineData("1")]
    [InlineData("2")]
    [InlineData("3;10;10")]
    [InlineData("4;400;600")]
    [InlineData("8;30;100")]
    [InlineData("9;1")]
    [InlineData("10;1")]
    [InlineData("24")]
    public void AManipulationIsRefusedAndChangesNothing(string operation)
    {
        Emulator emulator = Fed(E + "[" + operation + "t");

        Assert.Empty(emulator.Reply.ToArray());
        Assert.True(emulator.Unhandled > 0);
        Assert.Equal((80, 24), (emulator.Buffer.Columns, emulator.Buffer.Rows));
    }

    // ---- The title stack ----

    [Fact]
    public void PushingAndPoppingBothRestoresBoth()
    {
        Emulator emulator = Fed(Osc(0, "s") + E + "[22;0t" + Osc(2, "x") + Osc(1, "x") + E + "[23;0t");

        Assert.Equal("s", emulator.Title);
        Assert.Equal("s", emulator.IconTitle);
        Assert.Equal(0, emulator.TitleStackDepth);
    }

    [Fact]
    public void PoppingTheIconTakesTheWholeEntry()
    {
        Emulator emulator = Fed(Osc(0, "s") + E + "[22;0t" + Osc(2, "x") + Osc(1, "x")
                                + E + "[23;1t" + E + "[23;2t");

        Assert.Equal("x", emulator.Title);
        Assert.Equal("s", emulator.IconTitle);
    }

    [Fact]
    public void AnIconPushThenAWindowPushFillOneEntry()
    {
        Emulator emulator = Fed(Osc(2, "a") + Osc(1, "b") + E + "[22;1t" + E + "[22;2t"
                                + Osc(2, "y") + Osc(1, "z") + E + "[23;0t");

        Assert.Equal("a", emulator.Title);
        Assert.Equal("b", emulator.IconTitle);
    }

    [Fact]
    public void PushesOfOneKindStackInOrder()
    {
        Emulator emulator = Fed(Osc(2, "a") + E + "[22;2t" + Osc(2, "b") + E + "[22;2t" + Osc(2, "z")
                                + E + "[23;2t");

        Assert.Equal("b", emulator.Title);

        emulator.Feed(Encoding.ASCII.GetBytes(E + "[23;2t"));

        Assert.Equal("a", emulator.Title);
    }

    [Fact]
    public void TheStackIsBounded()
    {
        StringBuilder stream = new();

        for (int push = 0; push < Emulator.MaximumTitleStack + 5; push++)
        {
            stream.Append(E + "[22;0t");
        }

        Assert.Equal(Emulator.MaximumTitleStack, Fed(stream.ToString()).TitleStackDepth);
    }

    [Fact]
    public void PoppingAnEmptyStackChangesNothing()
    {
        Emulator emulator = Fed(Osc(2, "kept") + E + "[23;0t");

        Assert.Equal("kept", emulator.Title);
    }

    [Fact]
    public void ResetEmptiesTheStackAndTheTitles()
    {
        Emulator emulator = Fed(Osc(0, "s") + E + "[22;0t" + E + "c");

        Assert.Equal(string.Empty, emulator.Title);
        Assert.Equal(string.Empty, emulator.IconTitle);
        Assert.Equal(0, emulator.TitleStackDepth);
    }

    private static string Osc(int command, string text) => E + "]" + command + ";" + text + "\a";

    private static Emulator Fed(string stream)
    {
        Emulator emulator = new(80, 24);
        emulator.Feed(Encoding.ASCII.GetBytes(stream));

        return emulator;
    }

    private static string Sent(Emulator emulator) => Encoding.ASCII.GetString(emulator.Reply);
}
