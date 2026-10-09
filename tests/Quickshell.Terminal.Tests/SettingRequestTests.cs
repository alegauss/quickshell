using System.Text;
using Quickshell.Terminal;
using Xunit;

namespace Quickshell.Terminal.Tests;

/// <summary>
/// DECRQSS: a host asking for one setting back in its own syntax, and the half that matters most —
/// what happens when it asks for one this client does not report.
/// </summary>
public sealed class SettingRequestTests
{
    // Spelled as characters rather than escapes, for the same reason Emulator.Replies.cs is: an
    // escape in a literal is one careless edit away from a raw control byte nothing can see. QS100.
    private const char Escape = (char)0x1B;
    private const char Quote = '"';
    private static readonly string Csi = new([Escape, '[']);
    private static readonly string Dcs = new([Escape, 'P']);
    private static readonly string St = new([Escape, (char)0x5C]);

    // ---- The falsification ----

    /// <summary>
    /// The design's own falsification: <em>falsified when a request this client does not implement is
    /// swallowed with no reply at all</em>.
    ///
    /// <para>The asker is blocked on an answer. Silence leaves it there until whatever timeout it
    /// has runs out, which is how a shell prompt comes up a second late every time it starts.</para>
    /// </summary>
    [Fact]
    public void ASettingThisClientDoesNotReportIsRefusedRatherThanIgnored()
    {
        Assert.Equal(Dcs + "0$r" + St, Sent(Fed(Request("Z"))));
    }

    [Theory]
    [InlineData("u")]
    [InlineData("$t")]
    [InlineData("|")]
    [InlineData("")]
    public void EveryUnreportedSettingGetsTheSameRefusal(string name)
    {
        Emulator emulator = Fed(Request(name));

        Assert.Equal(Dcs + "0$r" + St, Sent(emulator));
        Assert.True(emulator.Unhandled > 0);
    }

    /// <summary>A name too long to be one is refused, rather than matched against what fitted.</summary>
    [Fact]
    public void AnOverlongNameIsRefusedAndNotTruncatedIntoAMatch()
    {
        Assert.Equal(
            Dcs + "0$r" + St,
            Sent(Fed(Request("m" + new string('m', Emulator.MaximumDcsLength + 10)))));
    }

    // ---- What is reported ----

    /// <summary>The design's falsifier (QS243): <em>falsified when DECRQSS for DECSCUSR answers invalid</em>.</summary>
    [Theory]
    [InlineData("", "0")]
    [InlineData("4", "4")]
    [InlineData("6", "6")]
    public void TheCursorStyleIsReportedAsItWasSet(string style, string reported)
    {
        Assert.Equal(Dcs + "1$r" + reported + " q" + St, Sent(Fed(Csi + style + " q" + Request(" q"))));
    }

    [Fact]
    public void AStyleOutsideTheSixIsIgnored()
    {
        Emulator emulator = Fed(Csi + "5 q" + Csi + "9 q");

        Assert.Equal(5, emulator.CursorStyle);
        Assert.True(emulator.Unhandled > 0);
    }

    [Theory]
    [InlineData(0, CursorShape.None)]
    [InlineData(1, CursorShape.Block)]
    [InlineData(2, CursorShape.Block)]
    [InlineData(3, CursorShape.Underline)]
    [InlineData(4, CursorShape.Underline)]
    [InlineData(5, CursorShape.Bar)]
    [InlineData(6, CursorShape.Bar)]
    public void EachStyleNamesAShape(int style, CursorShape shape)
    {
        Assert.Equal(shape, Emulator.ShapeFor(style));
    }

    [Fact]
    public void AStyleChangeIsDamageAndAResetForgetsIt()
    {
        Emulator emulator = Fed(Csi + "6 q");

        Assert.Equal(6, emulator.Damage.CursorStyle);

        emulator.Feed(Encoding.ASCII.GetBytes(Escape + "c"));

        Assert.Equal(0, emulator.Damage.CursorStyle);
    }

    /// <summary>The page, screen and status-line settings, answered from what this client has (QS243).</summary>
    [Theory]
    [InlineData("t", "1$r24t")]
    [InlineData("*|", "1$r24*|")]
    [InlineData("$}", "1$r0$}")]
    [InlineData("$~", "1$r0$~")]
    public void TheScreenSettingsAreReportedFromThePane(string name, string reply)
    {
        Assert.Equal(Dcs + reply + St, Sent(Fed(Request(name))));
    }

    [Fact]
    public void DecidAnswersAsDaDoes()
    {
        Assert.Equal(Sent(Fed(Csi + "c")), Sent(Fed(Escape + "Z")));
    }

    [Fact]
    public void ThePenIsReportedAsTheSgrThatWouldSetIt()
    {
        Assert.Equal(Dcs + "1$r0m" + St, Sent(Fed(Request("m"))));
        Assert.Equal(Dcs + "1$r0;1;31m" + St, Sent(Fed(Csi + "1;31m" + Request("m"))));
        Assert.Equal(Dcs + "1$r0;7m" + St, Sent(Fed(Csi + "7m" + Request("m"))));
        Assert.Equal(Dcs + "1$r0;38;5;200m" + St, Sent(Fed(Csi + "38;5;200m" + Request("m"))));
        Assert.Equal(Dcs + "1$r0;4:3m" + St, Sent(Fed(Csi + "4:3m" + Request("m"))));
    }

    [Fact]
    public void ABrightColourIsReportedInTheRangeThatMeansBright()
    {
        Assert.Equal(Dcs + "1$r0;91;104m" + St, Sent(Fed(Csi + "91;104m" + Request("m"))));
    }

    [Fact]
    public void AStatedColourIsReportedByItsChannels()
    {
        Assert.Equal(
            Dcs + "1$r0;38;2;10;20;30m" + St,
            Sent(Fed(Csi + "38;2;10;20;30m" + Request("m"))));
    }

    /// <summary>
    /// A default colour is left out, not reported as whatever the theme currently is. They are
    /// different states, and reporting the concrete one is how a host pins the theme into the text.
    /// </summary>
    [Fact]
    public void ADefaultColourIsNotReportedAsAConcreteOne()
    {
        string report = Sent(Fed(Csi + "31m" + Csi + "39m" + Request("m")));

        Assert.Equal(Dcs + "1$r0m" + St, report);
    }

    /// <summary>
    /// The point of reporting in the setting's own syntax: the host reads the answer by sending it
    /// straight back. If a round trip does not land on the same pen, the report is decoration.
    /// </summary>
    [Fact]
    public void WhatIsReportedSetsTheSamePenWhenSentBack()
    {
        const string sgr = "1;3;4:3;7;9;38;5;200;48;2;1;2;3m";

        Emulator first = Fed(Csi + sgr + Request("m"));
        string reported = Sent(first);
        string parameters = reported[(Dcs + "1$r").Length..^(St.Length + 1)];

        Emulator second = Fed(Csi + parameters + "m");

        Assert.Equal(first.Pen, second.Pen);
    }

    [Fact]
    public void TheScrollRegionIsReportedOneBasedAndInclusive()
    {
        Assert.Equal(Dcs + "1$r1;24r" + St, Sent(Fed(Request("r"))));
        Assert.Equal(Dcs + "1$r5;10r" + St, Sent(Fed(Csi + "5;10r" + Request("r"))));
    }

    /// <summary>
    /// The conformance level is the number DA1 reports, because two different answers to what this
    /// terminal is would be a claim it cannot both keep.
    /// </summary>
    [Fact]
    public void TheConformanceLevelAgreesWithTheDeviceAttributes()
    {
        Assert.Equal(Dcs + "1$r62;1" + Quote + "p" + St, Sent(Fed(Request(Quote + "p"))));
        Assert.Contains("62", Sent(Fed(Csi + "c")), StringComparison.Ordinal);
    }

    // ---- Other device control strings ----

    /// <summary>
    /// A device control string that is not a request gets no reply, because nothing is waiting for
    /// one. It is counted, which is how an unimplemented sequence stays visible.
    /// </summary>
    [Fact]
    public void ADeviceControlStringThatIsNotARequestIsCountedAndNotAnswered()
    {
        Emulator emulator = Fed(Dcs + "0;1;0q#0;2;0;0;0" + St);

        Assert.Empty(emulator.Reply.ToArray());
        Assert.True(emulator.Unhandled > 0);
    }

    [Fact]
    public void ARequestDoesNotDisturbTheTextAroundIt()
    {
        Emulator emulator = Fed("before" + Request("m") + "after");

        Assert.Equal("beforeafter", Row(emulator, 0)[..11]);
    }

    private static string Request(string name) => Dcs + "$q" + name + St;

    private static Emulator Fed(string stream)
    {
        Emulator emulator = new(80, 24);
        emulator.Feed(Encoding.UTF8.GetBytes(stream));

        return emulator;
    }

    private static string Sent(Emulator emulator) => Encoding.ASCII.GetString(emulator.Reply);

    private static string Row(Emulator emulator, int row)
    {
        StringBuilder text = new();

        foreach (Cell cell in emulator.Buffer.Screen(row))
        {
            if (cell.Width != 0)
            {
                text.Append(emulator.Buffer.TextOf(cell));
            }
        }

        return text.ToString();
    }
}
