using System.Text;
using Quickshell.Terminal;
using Xunit;

namespace Quickshell.Terminal.Tests;

/// <summary>
/// DECSET 47, 1047 and 1049 as xterm has each (QS244), and mode 41, the tab that takes an owed wrap.
/// </summary>
public sealed class AlternateScreenTests
{
    private const string E = "\u001b";

    // ---- 47 and 1047 ----

    [Theory]
    [InlineData("47")]
    [InlineData("1047")]
    public void TheCursorStaysWhereItIsAcrossTheSwitch(string mode)
    {
        Emulator emulator = Fed("abc\r\nabc" + E + "[?" + mode + "h");

        Assert.Equal((1, 3), Cursor(emulator));

        emulator.Feed(Encoding.ASCII.GetBytes(E + "[4;6H" + E + "[?" + mode + "l"));

        Assert.Equal((3, 5), Cursor(emulator));
    }

    [Fact]
    public void FortySevenKeepsTheAlternateScreensContents()
    {
        Emulator emulator = Fed(E + "[?47h" + "def" + E + "[?47l" + E + "[?47h");

        Assert.Equal("def", Row(emulator, 0)[..3]);
    }

    [Fact]
    public void AResetClearsTheAlternateScreenTooSoFortySevenFindsItBlank()
    {
        Emulator emulator = Fed(E + "[?47h" + "def" + E + "c" + E + "[?47h");

        Assert.Equal("   ", Row(emulator, 0)[..3]);
    }

    [Fact]
    public void TenFortySevenClearsTheAlternateScreenOnTheWayOut()
    {
        Emulator emulator = Fed(E + "[?1047h" + "def" + E + "[?1047l" + E + "[?1047h");

        Assert.Equal("   ", Row(emulator, 0)[..3]);
    }

    [Fact]
    public void TenFortyNineStillRestoresTheMainScreensCursor()
    {
        Emulator emulator = Fed(E + "[3;4H" + E + "[?1049h" + E + "[9;9H" + E + "[?1049l");

        Assert.Equal((2, 3), Cursor(emulator));
    }

    // ---- Mode 41 ----

    [Fact]
    public void WithMoreFixATabTakesTheOwedWrap()
    {
        Emulator emulator = Fed(E + "[?41h" + new string('x', 20) + "\t1");

        Assert.Equal('1', emulator.Buffer.Screen(1)[8].Codepoint);
    }

    [Fact]
    public void WithoutItTheWrapIsStillOwedAfterTheTab()
    {
        Emulator emulator = Fed(new string('x', 20) + "\t2");

        Assert.Equal('x', emulator.Buffer.Screen(0)[19].Codepoint);
        Assert.Equal('2', emulator.Buffer.Screen(1)[0].Codepoint);
    }

    private static (int Row, int Column) Cursor(Emulator emulator) =>
        (emulator.Buffer.CursorRow, emulator.Buffer.CursorColumn);

    private static Emulator Fed(string stream)
    {
        Emulator emulator = new(20, 6, scrollback: 0);
        emulator.Feed(Encoding.ASCII.GetBytes(stream));

        return emulator;
    }

    private static string Row(Emulator emulator, int row)
    {
        StringBuilder text = new();

        foreach (Cell cell in emulator.Buffer.Screen(row))
        {
            text.Append((char)cell.Codepoint);
        }

        return text.ToString();
    }
}
