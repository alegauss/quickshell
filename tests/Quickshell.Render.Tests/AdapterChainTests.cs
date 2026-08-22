using Quickshell.Render;
using Xunit;

namespace Quickshell.Render.Tests;

/// <summary>
/// The chain's order, tested without a machine whose driver is actually broken — which is the case
/// the chain exists for and the one that cannot be arranged on demand. A client that shows a black
/// window inside an RDP session has failed at the exact moment somebody needed it, so the order in
/// which it gives up is a rule and not a preference.
/// </summary>
public sealed class AdapterChainTests
{
    private sealed class Probe(AdapterInfo? window, AdapterInfo? hardware) : IAdapterProbe
    {
        public int WarpAsked { get; private set; }

        public AdapterInfo? ForOutputWindow(nint outputWindow) => window;

        public AdapterInfo? DefaultHardware() => hardware;

        public AdapterInfo Warp()
        {
            WarpAsked++;
            return new AdapterInfo("WARP software rasteriser", 0);
        }
    }

    [Fact]
    public void TheWindowsOwnAdapterWinsWhenThereIsOne()
    {
        Probe probe = new(new AdapterInfo("window's GPU", 0x10DE), new AdapterInfo("some other GPU", 0x8086));

        AdapterChoice choice = AdapterChain.Choose(probe, 0x1234);

        Assert.Equal(AdapterKind.OutputWindow, choice.Kind);
        Assert.Equal("window's GPU", choice.Adapter?.Description);
        Assert.Empty(choice.Skipped);
        Assert.Equal(0, probe.WarpAsked);
    }

    [Fact]
    public void TheDefaultHardwareAdapterIsNextAndTheSkipIsRecorded()
    {
        Probe probe = new(null, new AdapterInfo("default GPU", 0x8086));

        AdapterChoice choice = AdapterChain.Choose(probe, 0x1234);

        Assert.Equal(AdapterKind.DefaultHardware, choice.Kind);
        Assert.Equal("default GPU", choice.Adapter?.Description);
        Assert.Contains("no adapter owns the output window", choice.Skipped);
        Assert.Equal(0, probe.WarpAsked);
    }

    [Fact]
    public void WarpIsReachedWhenNoHardwareAnswers()
    {
        Probe probe = new(null, null);

        AdapterChoice choice = AdapterChain.Choose(probe, 0x1234);

        Assert.Equal(AdapterKind.Warp, choice.Kind);
        Assert.Equal(1, probe.WarpAsked);
        Assert.Contains("no hardware adapter could be opened", choice.Skipped);
    }

    [Fact]
    public void WithoutAnOutputWindowTheChainSaysSoRatherThanAskingForOne()
    {
        Probe probe = new(new AdapterInfo("never asked", 1), new AdapterInfo("default GPU", 0x8086));

        AdapterChoice choice = AdapterChain.Choose(probe, nint.Zero);

        Assert.Equal(AdapterKind.DefaultHardware, choice.Kind);
        Assert.Contains("no output window was named", choice.Skipped);
    }
}
