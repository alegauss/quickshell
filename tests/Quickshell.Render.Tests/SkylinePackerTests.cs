using Quickshell.Render;
using Xunit;

namespace Quickshell.Render.Tests;

/// <summary>
/// The allocator, on its own. Two rectangles sharing a pixel is the failure that shows up as one
/// character wearing a corner of another, which is the kind of bug a golden image catches long
/// after the commit that caused it.
/// </summary>
public sealed class SkylinePackerTests
{
    [Fact]
    public void NothingEverOverlaps()
    {
        const int Side = 64;

        SkylinePacker packer = new(Side, Side);
        bool[] taken = new bool[Side * Side];
        int placed = 0;

        // Deliberately uneven sizes: equal boxes tile by accident and prove nothing.
        for (int i = 0; i < 200; i++)
        {
            int width = 3 + (i % 7);
            int height = 2 + (i % 5);

            if (!packer.TryAllocate(width, height, out int x, out int y))
            {
                continue;
            }

            placed++;

            for (int row = y; row < y + height; row++)
            {
                for (int column = x; column < x + width; column++)
                {
                    Assert.False(taken[(row * Side) + column], $"({column},{row}) was handed out twice");
                    taken[(row * Side) + column] = true;
                }
            }
        }

        Assert.True(placed > 50, $"only {placed} rectangles fitted a 64-square page, which is not a packer");
        Assert.Equal(placed, packer.Count);
    }

    [Fact]
    public void NothingIsPlacedOffThePage()
    {
        SkylinePacker packer = new(32, 16);

        while (packer.TryAllocate(5, 3, out int x, out int y))
        {
            Assert.InRange(x, 0, packer.Width - 5);
            Assert.InRange(y, 0, packer.Height - 3);
        }

        Assert.True(packer.Count > 0);
    }

    [Fact]
    public void AFullPageRefusesRatherThanGrows()
    {
        SkylinePacker packer = new(16, 16);

        Assert.True(packer.TryAllocate(16, 16, out _, out _));
        Assert.False(packer.TryAllocate(1, 1, out _, out _));
        Assert.Equal(256, packer.Used);
    }

    [Fact]
    public void ResetHandsTheWholePageBack()
    {
        SkylinePacker packer = new(16, 16);
        Assert.True(packer.TryAllocate(16, 16, out _, out _));

        packer.Reset();

        Assert.Equal(0, packer.Used);
        Assert.Equal(0, packer.Count);
        Assert.True(packer.TryAllocate(16, 16, out int x, out int y));
        Assert.Equal(0, x);
        Assert.Equal(0, y);
    }

    [Fact]
    public void ARectangleWiderThanThePageIsRefused()
    {
        SkylinePacker packer = new(16, 16);

        Assert.False(packer.TryAllocate(17, 1, out _, out _));
        Assert.False(packer.TryAllocate(1, 17, out _, out _));
    }
}
