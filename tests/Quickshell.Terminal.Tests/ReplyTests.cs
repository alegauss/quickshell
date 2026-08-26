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
        + E + "[18t" + E + "[20t" + E + "[21t";

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
        const string allowed = "\u001b[]?>;0123456789cnRt";

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

    private static Emulator Fed(string stream)
    {
        Emulator emulator = new(80, 24);
        emulator.Feed(Encoding.UTF8.GetBytes(stream));

        return emulator;
    }

    private static string Sent(Emulator emulator) => Encoding.ASCII.GetString(emulator.Reply);
}
