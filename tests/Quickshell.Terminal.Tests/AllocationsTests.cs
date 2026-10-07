using Quickshell.Tests;
using Xunit;

namespace Quickshell.Terminal.Tests;

/// <summary>
/// QS167: the least of several passes forgives the runtime a one-off and forgives the code nothing.
/// </summary>
public sealed class AllocationsTests
{
    /// <summary>A pass that allocates every time is caught, whichever pass is the least.</summary>
    [Fact]
    public void AnAllocationEveryPassIsStillCaught()
    {
        long least = Allocations.Least(() => GC.KeepAlive(new byte[100]));

        Assert.True(least >= 100, $"a hundred bytes a pass measured as {least}");
    }

    /// <summary>A one-off on the first pass is the runtime's, and the next pass says so.</summary>
    [Fact]
    public void AOneOffIsNotTheCodes()
    {
        int pass = 0;

        long least = Allocations.Least(() =>
        {
            if (pass++ == 0)
            {
                GC.KeepAlive(new byte[7288]);
            }
        });

        Assert.Equal(0, least);
        Assert.Equal(2, pass);
    }
}
