using System.Text;
using Quickshell.Terminal;
using Xunit;

namespace Quickshell.Terminal.Tests;

/// <summary>DECSCNM, reverse screen (QS246): the visual bell's flash, modelled as a lookup and not a rewrite.</summary>
public sealed class ReverseScreenTests
{
    private const string E = "\u001b";

    [Fact]
    public void ModeFiveSwapsTheDefaultsWithoutTouchingACell()
    {
        Emulator emulator = Fed("ab" + E + "[?5h");
        Cell before = emulator.Buffer.Screen(0)[0];

        Assert.True(emulator.Palette.Reversed);
        Assert.Equal(emulator.Palette.Foreground, emulator.Palette.Resolve(Colour.Default, background: true));
        Assert.Equal(emulator.Palette.Background, emulator.Palette.Resolve(Colour.Default));

        emulator.Feed(Encoding.ASCII.GetBytes(E + "[?5l"));

        Assert.False(emulator.Palette.Reversed);
        Assert.Equal(before, emulator.Buffer.Screen(0)[0]);
        Assert.Equal(emulator.Palette.Background, emulator.Palette.Resolve(Colour.Default, background: true));
    }

    [Fact]
    public void AnIndexedColourIsNotSwapped()
    {
        Emulator emulator = Fed(E + "[?5h");

        Assert.Equal(emulator.Palette[1], emulator.Palette.Resolve(Colour.Indexed(1)));
    }

    /// <summary>The flip is damage of its own, so the frame after each edge of a visual bell draws it.</summary>
    [Fact]
    public void TheFlipIsDamage()
    {
        Emulator emulator = Fed(string.Empty);
        Damage before = emulator.Damage;

        emulator.Feed(Encoding.ASCII.GetBytes(E + "[?5h"));

        Assert.NotEqual(before, emulator.Damage);
        Assert.True(emulator.Damage.ReverseScreen);
    }

    [Fact]
    public void AResetPutsTheScreenTheRightWayRound()
    {
        Assert.False(Fed(E + "[?5h" + E + "c").Palette.Reversed);
    }

    private static Emulator Fed(string stream)
    {
        Emulator emulator = new(20, 4);
        emulator.Feed(Encoding.ASCII.GetBytes(stream));

        return emulator;
    }
}
