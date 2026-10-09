using System.Text;
using Quickshell.Terminal;
using Xunit;

namespace Quickshell.Terminal.Tests;

/// <summary>
/// Saved state (QS239): a saved cursor per screen, DECRC with nothing saved, DECSTR's resets, and
/// XTSAVE / XTRESTORE.
/// </summary>
public sealed class SavedStateTests
{
    private const string E = "\u001b";

    // ---- The falsification ----

    /// <summary>
    /// The design's own: <em>falsified when DECRC on the alternate screen restores what DECSC saved
    /// on the main one</em>.
    /// </summary>
    [Fact]
    public void EachScreenHasItsOwnSavedCursor()
    {
        Emulator emulator = Fed(E + "[3;2H" + E + "7" + E + "[?47h" + E + "[7;6H" + E + "7"
                                + E + "[?47l" + E + "8");

        Assert.Equal((2, 1), Cursor(emulator));

        emulator.Feed(Encoding.ASCII.GetBytes(E + "[?47h" + E + "8"));

        Assert.Equal((6, 5), Cursor(emulator));
    }

    [Fact]
    public void TheAlternateScreenRestoresNothingTheMainScreenSaved()
    {
        Emulator emulator = Fed(E + "[5;5H" + E + "7" + E + "[?47h" + E + "[9;9H" + E + "8");

        Assert.Equal((0, 0), Cursor(emulator));
    }

    // ---- DECRC ----

    [Fact]
    public void RestoringWithNothingSavedHomesAndResetsOriginMode()
    {
        Emulator emulator = Fed(E + "[5;7r" + E + "[?6h" + E + "[3;3H" + E + "8");

        Assert.False(emulator.OriginMode);
        Assert.Equal((0, 0), Cursor(emulator));
    }

    [Fact]
    public void OriginModeIsSavedAndRestored()
    {
        Emulator emulator = Fed(E + "[5;7r" + E + "[?6h" + E + "7" + E + "[?6l" + E + "8");

        Assert.True(emulator.OriginMode);
    }

    [Fact]
    public void TheMainScreensSavedCursorSurvivesAFullScreenProgram()
    {
        Emulator emulator = Fed(E + "[4;4H" + E + "[?1049h" + E + "[9;9H" + E + "7" + E + "[?1049l");

        Assert.Equal((3, 3), Cursor(emulator));
    }

    // ---- DECSTR ----

    [Fact]
    public void DecstrResetsTheSavedCursorToHome()
    {
        Emulator emulator = Fed(E + "[6;5H" + E + "7" + E + "[!p" + E + "8");

        Assert.Equal((0, 0), Cursor(emulator));
    }

    [Fact]
    public void DecstrLeavesTheCursorWhereItIs()
    {
        Assert.Equal((5, 4), Cursor(Fed(E + "[6;5H" + E + "[!p")));
    }

    [Fact]
    public void DecstrResetsWhatAProgramSetForItself()
    {
        Emulator emulator = Fed(E + "[3;4r" + E + "[?6h" + E + "[?45h" + E + "[4h" + E + "[?1h" + E + "="
                                + E + "[?69h" + E + "[5;6s" + E + "[?25l" + E + "[1m" + E + "(0" + E + "[!p");

        Assert.Equal((0, 23), (emulator.MarginTop, emulator.MarginBottom));
        Assert.False(emulator.OriginMode);
        Assert.False(emulator.ReverseWrap);
        Assert.False(emulator.InsertMode);
        Assert.False(emulator.ApplicationCursorKeys);
        Assert.False(emulator.ApplicationKeypad);
        Assert.False(emulator.LeftRightMarginMode);
        Assert.True(emulator.CursorVisible);
        Assert.Equal(CharacterSet.Ascii, emulator.ActiveCharacterSet);

        emulator.Feed(Encoding.ASCII.GetBytes("x"));

        Assert.Equal(CellFlags.None, emulator.Buffer.Screen(0)[0].Flags);
    }

    [Fact]
    public void DecstrLeavesAutowrapOnAsXtermDoes()
    {
        Assert.True(Fed(E + "[?7h" + E + "[!p").AutoWrap);
    }

    // ---- XTSAVE and XTRESTORE ----

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void XtrestoreSetsAModeBackAsXtsaveFoundIt(bool on)
    {
        string before = on ? "h" : "l";
        string after = on ? "l" : "h";
        Emulator emulator = Fed(E + "[?7" + before + E + "[?7s" + E + "[?7" + after + E + "[?7r");

        Assert.Equal(on, emulator.AutoWrap);
    }

    [Fact]
    public void SeveralModesAreSavedInOneSequence()
    {
        Emulator emulator = Fed(E + "[?2004h" + E + "[?1;2004s" + E + "[?1h" + E + "[?2004l" + E + "[?1;2004r");

        Assert.False(emulator.ApplicationCursorKeys);
        Assert.True(emulator.BracketedPaste);
    }

    [Fact]
    public void RestoringAModeNeverSavedLeavesItAlone()
    {
        Assert.True(Fed(E + "[?2004h" + E + "[?2004r").BracketedPaste);
    }

    [Fact]
    public void AModeWithNoStateIsNotSaved()
    {
        Emulator emulator = Fed(E + "[?12345s");

        Assert.True(emulator.Unhandled > 0);
    }

    private static (int Row, int Column) Cursor(Emulator emulator) =>
        (emulator.Buffer.CursorRow, emulator.Buffer.CursorColumn);

    private static Emulator Fed(string stream)
    {
        Emulator emulator = new(80, 24);
        emulator.Feed(Encoding.ASCII.GetBytes(stream));

        return emulator;
    }
}
