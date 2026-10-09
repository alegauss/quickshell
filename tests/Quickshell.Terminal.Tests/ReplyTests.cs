using System.Text;
using Quickshell.Terminal;
using Xunit;

namespace Quickshell.Terminal.Tests;

/// <summary>
/// The two sequences that reach back out of the terminal, and the rule that keeps every other reply
/// safe: no byte this client sends back may be a byte the host supplied.
/// </summary>
public sealed class ReplyTests
{
    private const string E = "\u001b";

    /// <summary>Every question this terminal answers, and the two it refuses to.</summary>
    private const string EveryQuestion =
        E + "[c" + E + "[>c" + E + "[5n" + E + "[6n" + E + "[?6n"
        + E + "[18t" + E + "[20t" + E + "[21t" + E + "[7;1;1;1;24;80*y" + E + "[?7$p" + E + "[4$p"
        + E + "]4;1;?\a" + E + "]5;0;?" + E + "\\" + E + "]10;?" + E + "\\"
        + E + "P+q436F" + E + "\\" + E + "P+qMARKERa1b2c3" + E + "\\";

    // ---- The falsification ----

    /// <summary>
    /// The design's own falsification: <em>falsified when any reply the terminal sends back contains
    /// host-chosen text</em>.
    ///
    /// <para>The host plants a marker everywhere it is allowed to — the title, the icon name, the
    /// working directory, a hyperlink, a colour, and the screen itself — and then asks every question
    /// this terminal answers. If any of it comes back, the terminal is a way to type at the user's
    /// shell.</para>
    /// </summary>
    [Fact]
    public void NoReplyContainsTextTheHostSupplied()
    {
        const string marker = "MARKERa1b2c3";

        Emulator emulator = Fed(
            E + "]2;" + marker + "\a"
            + E + "]0;" + marker + "\a"
            + E + "]7;" + marker + "\a"
            + E + "]8;;https://" + marker + "\a"
            + E + "]4;1;" + marker + "\a"
            + E + "]52;c;" + marker + "\a"
            + marker
            + EveryQuestion);

        Assert.DoesNotContain(marker, Sent(emulator), StringComparison.Ordinal);
    }

    /// <summary>
    /// The same rule stated the other way round, which is the one that survives someone adding a
    /// sequence: a reply is built from a constant and some numbers, so there is no byte in it outside
    /// this alphabet.
    /// </summary>
    [Fact]
    public void EveryReplyByteComesFromTheClosedAlphabet()
    {
        Emulator emulator = Fed(E + "]2;a title\a" + EveryQuestion);
        // P, ! and ~ frame DECRQCRA's answer, A to F are its hex digits and the backslash ends it.
        // and $ and y close DECRQM's. A colour report is rgb:, lower-case hex and slashes, closed by
        // BEL where it was asked with one; + and = frame XTGETTCAP's (QS234).
        const string allowed = "\u001b[]?>;0123456789cnRtP!~ABCDEF\\$yrgb:/abcdef\a+=";

        foreach (byte sent in emulator.Reply)
        {
            Assert.True(allowed.Contains((char)sent), $"the reply carried 0x{sent:x2}");
        }
    }

    // ---- The title, which is set and never reported ----

    [Theory]
    [InlineData(20)]
    [InlineData(21)]
    public void TheTitleAndIconReportsAreRefusedRatherThanAnswered(int operation)
    {
        Emulator emulator = Fed(E + "]2;planted\a" + E + "[" + operation + "t");

        Assert.Equal("planted", emulator.Title);
        Assert.Empty(emulator.Reply.ToArray());
        Assert.True(emulator.Unhandled > 0);
    }

    // ---- What is answered, and with what ----

    [Fact]
    public void TheTerminalSaysWhatKindOfTerminalItIs()
    {
        Assert.Equal(E + "[?62;22c", Sent(Fed(E + "[c")));
        Assert.Equal(E + "[?62;22c", Sent(Fed(E + "[0c")));
    }

    [Fact]
    public void TheSecondaryAttributesAreTheirOwnQuestion()
    {
        Assert.Equal(E + "[>1;0;0c", Sent(Fed(E + "[>c")));
    }

    [Fact]
    public void TheStatusReportSaysNothingIsWrong()
    {
        Assert.Equal(E + "[0n", Sent(Fed(E + "[5n")));
    }

    [Fact]
    public void TheCursorReportIsOneBasedAndInThatOrder()
    {
        Assert.Equal(E + "[3;5R", Sent(Fed(E + "[3;5H" + E + "[6n")));
    }

    [Fact]
    public void ThePrivateCursorReportCarriesThePageNumber()
    {
        Assert.Equal(E + "[?3;5;1R", Sent(Fed(E + "[3;5H" + E + "[?6n")));
    }

    /// <summary>
    /// Under DECOM the host is in the region's coordinates, so it must be answered in them — a report
    /// in screen rows is a number the host will send straight back to the wrong row.
    /// </summary>
    [Fact]
    public void TheCursorReportUsesTheCoordinatesTheHostAskedFor()
    {
        Assert.Equal(E + "[1;1R", Sent(Fed(E + "[5;10r" + E + "[?6h" + E + "[1;1H" + E + "[6n")));
    }

    [Fact]
    public void TheWindowReportsTheSizeItActuallyHas()
    {
        Assert.Equal(E + "[8;24;80t", Sent(Fed(E + "[18t")));
    }

    [Fact]
    public void AWindowOperationNothingHereAnswersIsCounted()
    {
        Emulator emulator = Fed(E + "[7t");

        Assert.Empty(emulator.Reply.ToArray());
        Assert.True(emulator.Unhandled > 0);
    }

    // ---- The reply is drained, and bounded ----

    [Fact]
    public void WhoeverWritesTheReplyBackClearsIt()
    {
        Emulator emulator = Fed(E + "[5n");
        Assert.NotEmpty(emulator.Reply.ToArray());

        emulator.ClearReply();

        Assert.Empty(emulator.Reply.ToArray());
    }

    /// <summary>
    /// A host can ask faster than anything drains the answers, and unbounded is how a remote machine
    /// decides how much memory this process holds.
    /// </summary>
    [Fact]
    public void AHostAskingFasterThanTheReplyDrainsHitsACeiling()
    {
        Emulator emulator = Fed(string.Concat(Enumerable.Repeat(E + "[6n", 2000)));

        Assert.True(emulator.Reply.Length <= Emulator.MaximumReplyLength + 32);
        Assert.True(emulator.Unhandled > 0);
    }

    // ---- The clipboard ----

    [Fact]
    public void TheClipboardIsNotWritableUntilTheSessionSaysSo()
    {
        Emulator emulator = Fed(E + "]52;c;aGVsbG8=\a");

        Assert.False(emulator.ClipboardWriteEnabled);
        Assert.Equal(string.Empty, emulator.ClipboardWrite);
        Assert.True(emulator.Unhandled > 0);
    }

    [Fact]
    public void AnEnabledSessionLetsTheHostWriteIt()
    {
        Emulator emulator = new(80, 24) { ClipboardWriteEnabled = true };
        emulator.Feed(Encoding.UTF8.GetBytes(E + "]52;c;aGVsbG8=\a"));

        Assert.Equal("hello", emulator.ClipboardWrite);
    }

    /// <summary>
    /// The read direction has no setting. It tells a remote machine what the user last copied, and
    /// there is no session in which that is needed.
    /// </summary>
    [Fact]
    public void TheReadDirectionIsRefusedEvenWhenWritingIsAllowed()
    {
        Emulator emulator = new(80, 24) { ClipboardWriteEnabled = true };
        emulator.Feed(Encoding.UTF8.GetBytes(E + "]52;c;?\a"));

        Assert.Empty(emulator.Reply.ToArray());
        Assert.True(emulator.Unhandled > 0);
    }

    [Fact]
    public void SomethingThatIsNotBase64IsCountedRatherThanPasted()
    {
        Emulator emulator = new(80, 24) { ClipboardWriteEnabled = true };
        emulator.Feed(Encoding.UTF8.GetBytes(E + "]52;c;not base64 at all\a"));

        Assert.Equal(string.Empty, emulator.ClipboardWrite);
        Assert.True(emulator.Unhandled > 0);
    }

    // ---- DECRQM, whether a mode is set (QS104) ----

    [Theory]
    [InlineData("", "[?7$p", "[?7;1$y")]            // autowrap, on by default
    [InlineData("[?7l", "[?7$p", "[?7;2$y")]        // and off
    [InlineData("[?2004h", "[?2004$p", "[?2004;1$y")]
    [InlineData("[?69h", "[?69$p", "[?69;1$y")]          // left and right margins (QS233)
    [InlineData("", "[20$p", "[20;2$y")]                 // LNM, off by default (QS232)
    [InlineData("[?1049h", "[?1049$p", "[?1049;1$y")]
    [InlineData("[?1002h", "[?1000$p", "[?1000;2$y")] // a different tracking mode is live
    [InlineData("[?1002h", "[?1002$p", "[?1002;1$y")]
    [InlineData("", "[?1005$p", "[?1005;4$y")]      // refused on purpose: permanently off
    [InlineData("", "[?80$p", "[?80;4$y")]          // sixel, a non-goal
    [InlineData("[?3h", "[?3$p", "[?3;4$y")]        // 132 columns, a non-goal even once asked for (QS208)
    [InlineData("", "[?12345$p", "[?12345;0$y")]    // never heard of
    [InlineData("", "[4$p", "[4;2$y")]              // insert mode, an ANSI mode, off by default
    [InlineData("[4h", "[4$p", "[4;1$y")]           // and on (QS207)
    [InlineData("", "[12345$p", "[12345;0$y")]      // an ANSI mode never heard of
    public void AModeIsReportedWithOneOfItsFiveAnswers(string before, string asked, string answer)
    {
        string setup = before.Length == 0 ? string.Empty : E + before;

        Assert.Equal(E + answer, Sent(Fed(setup + E + asked)));
    }

    /// <summary>The design's falsifier: a mode refused on purpose is never reported as merely off.</summary>
    [Fact]
    public void ARefusedModeIsNotReportedAsOffEvenAfterAHostTriesToSetIt() =>
        Assert.Equal(E + "[?1005;4$y", Sent(Fed(E + "[?1005h" + E + "[?1005$p")));

    // ---- DECRQCRA, the checksum a suite reads the screen through (QS103) ----

    /// <summary>
    /// One cell, as esctest asks for it: the answer is the character negated in sixteen bits, so
    /// 0x10000 minus it — esctest's own reading — is the character back.
    /// </summary>
    [Fact]
    public void OneCellsChecksumIsItsCharacterNegated()
    {
        string sent = Sent(Fed("a" + E + "[1;1;1;1;1;1*y"));

        Assert.Equal(E + "P1!~FF9F" + E + "\\", sent);
        Assert.Equal('a', 0x10000 - Convert.ToInt32(sent[5..9], 16));
    }

    /// <summary>An empty cell is a space, which esctest reads back as empty.</summary>
    [Fact]
    public void AnEmptyCellCountsAsASpace() =>
        Assert.Equal(E + "P2!~FFE0" + E + "\\", Sent(Fed(E + "[2;1;5;5;5;5*y")));

    /// <summary>No rectangle is the whole screen: 80 by 24 spaces, 61,440, negated to 0x1000.</summary>
    [Fact]
    public void NoRectangleIsTheWholeScreen() =>
        Assert.Equal(E + "P3!~1000" + E + "\\", Sent(Fed(E + "[3*y")));

    /// <summary>A bold, underlined, inverse a is still an a to a suite asking which character it is.</summary>
    [Fact]
    public void AttributesAreNotInTheSum() =>
        Assert.Equal(Sent(Fed("a" + E + "[4;1;1;1;1;1*y")),
                     Sent(Fed(E + "[1;4;7ma" + E + "[0m" + E + "[4;1;1;1;1;1*y")));

    /// <summary>Under origin mode the rectangle is counted from the top of the scrolling region.</summary>
    [Fact]
    public void UnderOriginModeTheRectangleIsRelativeToTheRegion()
    {
        Emulator emulator = Fed(E + "[5;10r" + E + "[?6h" + E + "[1;1Hz" + E + "[5;1;1;1;1;1*y");

        Assert.Equal('z', emulator.Buffer.Screen(4)[0].Codepoint);
        Assert.Equal(0x10000 - 'z', Convert.ToInt32(Sent(emulator)[5..9], 16));
    }

    /// <summary>A wide character counts once: its trailing half adds nothing.</summary>
    [Fact]
    public void AWideCharacterCountsOnce()
    {
        string sent = Sent(Fed("中" + E + "[6;1;1;1;1;2*y"));

        Assert.Equal(-0x4E2D & 0xFFFF, Convert.ToInt32(sent[5..9], 16));
    }

    // ---- Colours asked for and reset (QS234) ----

    /// <summary>
    /// The line's falsification: OSC 4 ; 1 ; ? is answered, in xterm's sixteen-bit form and with the
    /// terminator it was asked with.
    /// </summary>
    [Fact]
    public void AColourAskedForIsAnsweredWithTheTerminatorItCameWith()
    {
        Assert.Equal(E + "]4;1;rgb:cdcd/0000/0000\a", Sent(Fed(E + "]4;1;?\a")));
        Assert.Equal(E + "]4;1;rgb:cdcd/0000/0000" + E + "\\", Sent(Fed(E + "]4;1;?" + E + "\\")));
    }

    /// <summary>X's # form is the high bits, so #fff reads back as f0f0 and #aaaabbbbcccc as aaaa.</summary>
    [Theory]
    [InlineData("#fff", "f0f0/f0f0/f0f0")]
    [InlineData("#808080", "8080/8080/8080")]
    [InlineData("#800800800", "8080/8080/8080")]
    [InlineData("#aaaabbbbcccc", "aaaa/bbbb/cccc")]
    [InlineData("rgb:f/f/f", "ffff/ffff/ffff")]
    public void ColourSpellingsReadBackAsXtermReadsThem(string spelling, string answer)
    {
        Assert.Equal(E + "]10;rgb:" + answer + "\a", Sent(Fed(E + "]10;" + spelling + "\a" + E + "]10;?\a")));
    }

    /// <summary>
    /// A reset returns to the session's own scheme, not to built-in defaults, and the special colours
    /// are the same five whether OSC 5 or OSC 4 past 255 reaches them.
    /// </summary>
    [Fact]
    public void AResetReturnsToTheSchemeAndSpecialColoursAreOneSetUnderTwoNumbers()
    {
        // A scheme whose colour 3 and background are not the built-in ones, so a reset that went to
        // the built-in palette would be seen.
        Rgb[] sixteen = [.. ColourScheme.Default.Palette];
        sixteen[3] = new Rgb(1, 2, 3);
        ColourScheme mine = new() { Palette = sixteen, Background = new Rgb(4, 5, 6) };

        Emulator emulator = new(80, 24);
        mine.ApplyTo(emulator.Palette);

        emulator.Feed(Encoding.UTF8.GetBytes(E + "]4;3;#aabbcc\a" + E + "]104;3\a"));
        Assert.Equal(new Rgb(1, 2, 3), emulator.Palette[3]);

        emulator.Feed(Encoding.UTF8.GetBytes(E + "]5;1;#123456\a" + E + "]4;257;?\a"));
        Assert.Equal(E + "]4;257;rgb:1212/3434/5656\a", Sent(emulator));

        emulator.ClearReply();
        emulator.Feed(Encoding.UTF8.GetBytes(E + "]11;#010203\a" + E + "]111\a"));
        Assert.Equal(new Rgb(4, 5, 6), emulator.Palette.Background);
    }

    /// <summary>XTGETTCAP answers Co with 256, and anything else as unknown with nothing echoed.</summary>
    [Fact]
    public void TheColourCountIsTheOneCapabilityAnswered()
    {
        Assert.Equal(E + "P1+r436F=323536" + E + "\\", Sent(Fed(E + "P+q436F" + E + "\\")));
        Assert.Equal(E + "P0+r" + E + "\\", Sent(Fed(E + "P+q544E" + E + "\\")));
    }

    private static Emulator Fed(string stream)
    {
        Emulator emulator = new(80, 24);
        emulator.Feed(Encoding.UTF8.GetBytes(stream));

        return emulator;
    }

    private static string Sent(Emulator emulator) => Encoding.ASCII.GetString(emulator.Reply);
}
