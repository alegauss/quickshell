using System.Text;
using Quickshell.Terminal;
using Xunit;

namespace Quickshell.Terminal.Tests;

/// <summary>
/// The mouse: four modes, two encodings, and the coordinate the older one cannot spell.
/// </summary>
public sealed class MouseTests
{
    // Spelled as characters rather than escapes, for the same reason Emulator.Replies.cs is: an
    // escape in a literal is one careless edit away from a raw control byte nothing can see. QS100.
    private const char Escape = (char)0x1B;
    private static readonly string Csi = new([Escape, '[']);

    // ---- The falsification ----

    /// <summary>
    /// The design's own falsification: <em>falsified when a click past column 223 reports a column
    /// the host disagrees with</em>.
    ///
    /// <para>The legacy encoding packs a one-based coordinate plus 32 into a byte, so column 224 is
    /// where it runs out of room. A terminal that sent the wrapped byte anyway would tell the host a
    /// cell near the left edge was clicked, and the host would act on it — which is the silent
    /// misreport this task exists to refuse. Nothing is sent, and the drop is counted.</para>
    /// </summary>
    [Fact]
    public void AClickPastTheLegacyCeilingIsDroppedRatherThanMisreported()
    {
        Emulator emulator = Tracking(1000, columns: 300);
        int before = emulator.Unhandled;

        Assert.Equal(
            MouseDisposition.BeyondLegacyReach,
            emulator.ReportMouse(MouseButton.Left, MouseAction.Press, column: 250, row: 0));

        Assert.Empty(emulator.Reply.ToArray());
        Assert.True(emulator.Unhandled > before);
    }

    /// <summary>The last coordinate that does fit, so the ceiling is off by nothing.</summary>
    [Fact]
    public void TheColumnJustBelowTheCeilingStillReports()
    {
        Emulator emulator = Tracking(1000, columns: 300);

        Assert.Equal(
            MouseDisposition.Reported,
            emulator.ReportMouse(MouseButton.Left, MouseAction.Press, Emulator.LegacyMouseLimit - 1, 0));

        Assert.Equal(Csi + "M" + Bytes(32, 255, 33), Sent(emulator));
    }

    /// <summary>
    /// And with SGR enabled the same click reports the column it actually was, which is the reason
    /// this client prefers that encoding whenever a program offers it.
    /// </summary>
    [Fact]
    public void SgrHasNoCeiling()
    {
        Emulator emulator = Tracking(1000, columns: 300);
        emulator.Feed(Encoding.UTF8.GetBytes(Csi + "?1006h"));

        Assert.Equal(
            MouseDisposition.Reported,
            emulator.ReportMouse(MouseButton.Left, MouseAction.Press, column: 250, row: 0));

        Assert.Equal(Csi + "<0;251;1M", Sent(emulator));
    }

    // ---- The escape hatch ----

    /// <summary>
    /// Shift held is never a report, whatever the mode. Without it a full-screen program that has
    /// taken the mouse makes copying text out of it impossible.
    /// </summary>
    [Theory]
    [InlineData(9)]
    [InlineData(1000)]
    [InlineData(1002)]
    [InlineData(1003)]
    public void ShiftIsReservedForLocalSelection(int mode)
    {
        Emulator emulator = Tracking(mode);

        Assert.Equal(
            MouseDisposition.HeldForSelection,
            emulator.ReportMouse(MouseButton.Left, MouseAction.Press, 5, 5, MouseModifiers.Shift));

        Assert.Empty(emulator.Reply.ToArray());
    }

    [Fact]
    public void ShiftWithAnotherModifierIsStillLocal()
    {
        Emulator emulator = Tracking(1000);

        Assert.Equal(
            MouseDisposition.HeldForSelection,
            emulator.ReportMouse(
                MouseButton.Left,
                MouseAction.Press,
                5,
                5,
                MouseModifiers.Shift | MouseModifiers.Control));
    }

    // ---- Nobody asked ----

    [Fact]
    public void WithNoModeSetTheClickBelongsToTheWindow()
    {
        Emulator emulator = new(80, 24);

        Assert.Equal(MouseTracking.Off, emulator.MouseReporting);
        Assert.Equal(
            MouseDisposition.NotAsked,
            emulator.ReportMouse(MouseButton.Left, MouseAction.Press, 0, 0));

        Assert.Empty(emulator.Reply.ToArray());
    }

    // ---- Which mode wants which event ----

    [Fact]
    public void PressOnlyReportsNoRelease()
    {
        Emulator emulator = Tracking(9);

        Assert.Equal(MouseTracking.PressOnly, emulator.MouseReporting);
        Assert.Equal(
            MouseDisposition.Reported,
            emulator.ReportMouse(MouseButton.Left, MouseAction.Press, 0, 0));
        Assert.Equal(
            MouseDisposition.NotAsked,
            emulator.ReportMouse(MouseButton.Left, MouseAction.Release, 0, 0));

        Assert.Equal(Csi + "M" + Bytes(32, 33, 33), Sent(emulator));
    }

    /// <summary>X10 predates the modifier bits, and a program asking for it would read them as a
    /// different button.</summary>
    [Fact]
    public void PressOnlyCarriesNoModifierBits()
    {
        Emulator emulator = Tracking(9);
        emulator.ReportMouse(MouseButton.Left, MouseAction.Press, 0, 0, MouseModifiers.Control);

        Assert.Equal(Csi + "M" + Bytes(32, 33, 33), Sent(emulator));
    }

    [Fact]
    public void PressReleaseReportsBothAndNoMotion()
    {
        Emulator emulator = Tracking(1000);

        Assert.Equal(
            MouseDisposition.Reported,
            emulator.ReportMouse(MouseButton.Left, MouseAction.Release, 0, 0));
        Assert.Equal(
            MouseDisposition.NotAsked,
            emulator.ReportMouse(MouseButton.Left, MouseAction.Move, 1, 0));
    }

    /// <summary>1002 is what makes dragging a <c>tmux</c> pane divider work, and only that.</summary>
    [Fact]
    public void ButtonMotionReportsMotionOnlyWhileAButtonIsHeld()
    {
        Emulator emulator = Tracking(1002);

        Assert.Equal(
            MouseDisposition.Reported,
            emulator.ReportMouse(MouseButton.Left, MouseAction.Move, 1, 0));
        Assert.Equal(
            MouseDisposition.NotAsked,
            emulator.ReportMouse(MouseButton.None, MouseAction.Move, 2, 0));
    }

    [Fact]
    public void AnyMotionReportsMotionWithNothingHeld()
    {
        Emulator emulator = Tracking(1003);

        Assert.Equal(
            MouseDisposition.Reported,
            emulator.ReportMouse(MouseButton.None, MouseAction.Move, 4, 2));

        // Button code three, plus the motion bit: 35, plus the encoding's 32.
        Assert.Equal(Csi + "M" + Bytes(67, 37, 35), Sent(emulator));
    }

    /// <summary>
    /// A program that asked for 1002 and then switched 1000 off has not stopped wanting motion. A
    /// full-screen program on the way out sends a reset for every mode it might have set.
    /// </summary>
    [Fact]
    public void ResettingAModeThatIsNotLiveLeavesTrackingAlone()
    {
        Emulator emulator = Tracking(1002);
        emulator.Feed(Encoding.UTF8.GetBytes(Csi + "?1000l"));

        Assert.Equal(MouseTracking.ButtonMotion, emulator.MouseReporting);

        emulator.Feed(Encoding.UTF8.GetBytes(Csi + "?1002l"));

        Assert.Equal(MouseTracking.Off, emulator.MouseReporting);
    }

    /// <summary>Setting one replaces the request rather than adding to it.</summary>
    [Fact]
    public void TheLastModeSetIsTheOneThatIsLive()
    {
        Emulator emulator = Tracking(1003);
        emulator.Feed(Encoding.UTF8.GetBytes(Csi + "?1000h"));

        Assert.Equal(MouseTracking.PressRelease, emulator.MouseReporting);
        Assert.Equal(
            MouseDisposition.NotAsked,
            emulator.ReportMouse(MouseButton.None, MouseAction.Move, 3, 3));
    }

    // ---- The encodings ----

    [Fact]
    public void TheLegacyEncodingCannotSayWhichButtonWasReleased()
    {
        Emulator emulator = Tracking(1000);
        emulator.ReportMouse(MouseButton.Right, MouseAction.Release, 0, 0);

        // Code three, whichever button it was.
        Assert.Equal(Csi + "M" + Bytes(35, 33, 33), Sent(emulator));
    }

    /// <summary>Which is the whole reason 1006 exists: it keeps the button and moves the
    /// distinction into the final byte.</summary>
    [Fact]
    public void SgrKeepsTheButtonAndSaysReleaseInTheFinalByte()
    {
        Emulator emulator = Tracking(1000);
        emulator.Feed(Encoding.UTF8.GetBytes(Csi + "?1006h"));

        Assert.Equal(MouseEncoding.Sgr, emulator.MouseReportEncoding);

        emulator.ReportMouse(MouseButton.Right, MouseAction.Press, 3, 1);
        emulator.ReportMouse(MouseButton.Right, MouseAction.Release, 3, 1);

        Assert.Equal(Csi + "<2;4;2M" + Csi + "<2;4;2m", Sent(emulator));
    }

    [Fact]
    public void SgrSwitchesBackOffAgain()
    {
        Emulator emulator = Tracking(1000);
        emulator.Feed(Encoding.UTF8.GetBytes(Csi + "?1006h" + Csi + "?1006l"));

        Assert.Equal(MouseEncoding.Legacy, emulator.MouseReportEncoding);
    }

    /// <summary>Meta is bit eight and control is bit sixteen, on top of the button.</summary>
    [Fact]
    public void ModifiersRideOnTheButtonCode()
    {
        Emulator emulator = Tracking(1000);
        emulator.Feed(Encoding.UTF8.GetBytes(Csi + "?1006h"));

        emulator.ReportMouse(MouseButton.Middle, MouseAction.Press, 0, 0, MouseModifiers.Meta);
        emulator.ReportMouse(MouseButton.Middle, MouseAction.Press, 0, 0, MouseModifiers.Control);

        Assert.Equal(Csi + "<9;1;1M" + Csi + "<17;1;1M", Sent(emulator));
    }

    /// <summary>The wheel arrives as buttons four and five, in the high range.</summary>
    [Fact]
    public void TheWheelIsAButton()
    {
        Emulator emulator = Tracking(1000);
        emulator.Feed(Encoding.UTF8.GetBytes(Csi + "?1006h"));

        emulator.ReportMouse(MouseButton.WheelUp, MouseAction.Press, 0, 0);
        emulator.ReportMouse(MouseButton.WheelDown, MouseAction.Press, 0, 0);

        Assert.Equal(Csi + "<64;1;1M" + Csi + "<65;1;1M", Sent(emulator));
    }

    /// <summary>A wheel notch has no release to wait for, so a release for one is not an event.</summary>
    [Fact]
    public void TheWheelHasNoRelease()
    {
        Emulator emulator = Tracking(1000);

        Assert.Equal(
            MouseDisposition.NotAsked,
            emulator.ReportMouse(MouseButton.WheelUp, MouseAction.Release, 0, 0));
    }

    /// <summary>The wheel reports under press-only tracking too, or scrolling looks broken.</summary>
    [Fact]
    public void TheWheelReportsUnderPressOnlyTracking()
    {
        Emulator emulator = Tracking(9);

        Assert.Equal(
            MouseDisposition.Reported,
            emulator.ReportMouse(MouseButton.WheelUp, MouseAction.Press, 0, 0));
    }

    // ---- Traffic ----

    /// <summary>
    /// Motion inside one cell is not news. A pixel stream turned into a report each time is what
    /// makes mode 1003 saturate a link that is otherwise doing nothing.
    /// </summary>
    [Fact]
    public void MotionThatStaysInTheSameCellIsNotReported()
    {
        Emulator emulator = Tracking(1003);

        Assert.Equal(
            MouseDisposition.Reported,
            emulator.ReportMouse(MouseButton.None, MouseAction.Move, 4, 4));
        Assert.Equal(
            MouseDisposition.SameCell,
            emulator.ReportMouse(MouseButton.None, MouseAction.Move, 4, 4));
        Assert.Equal(
            MouseDisposition.Reported,
            emulator.ReportMouse(MouseButton.None, MouseAction.Move, 5, 4));
    }

    /// <summary>A press in the cell the pointer is already in is still a press.</summary>
    [Fact]
    public void APressIsNeverSuppressedAsARepeat()
    {
        Emulator emulator = Tracking(1002);
        emulator.ReportMouse(MouseButton.Left, MouseAction.Move, 4, 4);

        Assert.Equal(
            MouseDisposition.Reported,
            emulator.ReportMouse(MouseButton.Left, MouseAction.Press, 4, 4));
        Assert.Equal(
            MouseDisposition.Reported,
            emulator.ReportMouse(MouseButton.Left, MouseAction.Release, 4, 4));
    }

    // ---- Edges ----

    /// <summary>
    /// A drag that leaves the window is ordinary, and the program wants to know the drag is still
    /// happening at the edge rather than to hear nothing.
    /// </summary>
    [Fact]
    public void ADragOffTheEdgeIsClampedIntoTheScreen()
    {
        Emulator emulator = Tracking(1002, columns: 80, rows: 24);
        emulator.Feed(Encoding.UTF8.GetBytes(Csi + "?1006h"));

        emulator.ReportMouse(MouseButton.Left, MouseAction.Move, 500, -3);

        Assert.Equal(Csi + "<32;80;1M", Sent(emulator));
    }

    /// <summary>
    /// The UTF-8 extension is a third encoding whose coordinate length programs disagree about. SGR
    /// does the same job unambiguously, so 1005 is refused — and counted, not merely ignored.
    /// </summary>
    [Fact]
    public void TheUtf8EncodingIsRefusedAndCounted()
    {
        Emulator emulator = Tracking(1000);
        int before = emulator.Unhandled;

        emulator.Feed(Encoding.UTF8.GetBytes(Csi + "?1005h"));

        Assert.True(emulator.Unhandled > before);
        Assert.Equal(MouseEncoding.Legacy, emulator.MouseReportEncoding);
    }

    /// <summary>A hard reset is nobody having asked for the mouse.</summary>
    [Fact]
    public void AResetPutsTheMouseBack()
    {
        Emulator emulator = Tracking(1003);
        emulator.Feed(Encoding.UTF8.GetBytes(Csi + "?1006h"));
        emulator.ClearReply();

        emulator.Feed([(byte)Escape, (byte)'c']);

        Assert.Equal(MouseTracking.Off, emulator.MouseReporting);
        Assert.Equal(MouseEncoding.Legacy, emulator.MouseReportEncoding);
    }

    /// <summary>Several modes in one sequence, which is how a program sends them.</summary>
    [Fact]
    public void ModesArriveTogetherInOneSequence()
    {
        Emulator emulator = new(80, 24);
        emulator.Feed(Encoding.UTF8.GetBytes(Csi + "?1002;1006h"));

        Assert.Equal(MouseTracking.ButtonMotion, emulator.MouseReporting);
        Assert.Equal(MouseEncoding.Sgr, emulator.MouseReportEncoding);
    }

    /// <summary>And a mouse mode alongside one that is not, which must not swallow the other.</summary>
    [Fact]
    public void AMouseModeDoesNotSwallowItsNeighbours()
    {
        Emulator emulator = new(80, 24);
        emulator.Feed(Encoding.UTF8.GetBytes(Csi + "?1000;25l"));

        Assert.Equal(MouseTracking.Off, emulator.MouseReporting);
        Assert.False(emulator.CursorVisible);
    }

    private static Emulator Tracking(int mode, int columns = 80, int rows = 24)
    {
        Emulator emulator = new(columns, rows);
        emulator.Feed(Encoding.UTF8.GetBytes(Csi + "?" + mode + "h"));
        emulator.ClearReply();

        return emulator;
    }

    private static string Sent(Emulator emulator) => Latin1(emulator.Reply);

    /// <summary>
    /// The reply as characters. Latin-1 and not ASCII, because the legacy encoding puts bytes above
    /// 127 on the wire and ASCII decoding would turn each of them into the same question mark —
    /// which is a decoder that passes whatever the coordinate was.
    /// </summary>
    private static string Latin1(ReadOnlySpan<byte> bytes)
    {
        char[] characters = new char[bytes.Length];

        for (int index = 0; index < bytes.Length; index++)
        {
            characters[index] = (char)bytes[index];
        }

        return new string(characters);
    }

    private static string Bytes(params int[] values)
    {
        char[] characters = new char[values.Length];

        for (int index = 0; index < values.Length; index++)
        {
            characters[index] = (char)values[index];
        }

        return new string(characters);
    }
}
