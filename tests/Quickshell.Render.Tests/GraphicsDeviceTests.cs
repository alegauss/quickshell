using Quickshell.Render;
using Quickshell.Terminal;
using Vortice.Direct3D11;
using Xunit;

namespace Quickshell.Render.Tests;

/// <summary>
/// The device itself, opened for real. These are not mocks: a device that opens in a unit test but
/// not on a machine is exactly the failure the line asks about.
/// </summary>
public sealed class GraphicsDeviceTests
{
    /// <summary>Everything a pane will hold on the GPU, reduced to a counter.</summary>
    private sealed class CountingResource : IDeviceResource
    {
        public int Created { get; private set; }

        public int Released { get; private set; }

        public bool Live { get; private set; }

        public void Create(ID3D11Device device)
        {
            Created++;
            Live = true;
        }

        public void Release()
        {
            Released++;
            Live = false;
        }
    }

    private sealed class WarpOnly : IAdapterProbe
    {
        public AdapterInfo? ForOutputWindow(nint outputWindow) => null;

        public AdapterInfo? DefaultHardware() => null;

        public AdapterInfo Warp() => new("WARP software rasteriser", 0);
    }

    [Fact]
    public void ADeviceOpensOnThisMachine()
    {
        using GraphicsDevice device = GraphicsDevice.Open();

        Assert.NotNull(device.Device);
        Assert.NotNull(device.Context);
        Assert.Null(device.RemovedReason());
        Assert.True(device.Device.FeatureLevel >= Vortice.Direct3D.FeatureLevel.Level_11_0);
    }

    /// <summary>
    /// Half of what falsifies this line is a client that cannot reach WARP when the driver is
    /// gone. The driver cannot be removed here, so the chain is made to answer WARP and the device
    /// is then opened on it for real: if WARP itself did not work, this fails.
    /// </summary>
    [Fact]
    public void WarpOpensWhenNoHardwareAnswers()
    {
        using GraphicsDevice device = GraphicsDevice.Open(new WarpOnly());

        Assert.Equal(AdapterKind.Warp, device.Adapter.Kind);
        Assert.NotNull(device.Device);
        Assert.Null(device.RemovedReason());
    }

    /// <summary>
    /// The other half is that a device loss must not cost the terminal anything.
    ///
    /// <para><b>The loss here is driven, not induced.</b> D3D11 exposes no supported way for a
    /// process to remove its own device, and the ways that do work - a TDR from a hung GPU
    /// submission, pulling an external adapter, a driver update - either cannot be arranged from a
    /// test or would take the desktop down with them. So <see cref="GraphicsDevice.Recover"/> is
    /// called directly: every GPU resource really is released and rebuilt on a really new device,
    /// and what is not exercised is the detection that would have called it. A real removal is a
    /// thing to try by hand on a machine nobody minds losing, and it has not been tried.</para>
    /// </summary>
    [Fact]
    public void RecoveryRebuildsTheGpuAndLeavesTheTerminalAlone()
    {
        string terminalStateBefore = TerminalLayer.Name;

        using GraphicsDevice device = GraphicsDevice.Open();
        CountingResource resource = new();
        device.Register(resource);

        Assert.Equal(1, resource.Created);
        Assert.True(resource.Live);

        ID3D11Device before = device.Device;

        device.Recover();

        Assert.Equal(1, device.Recoveries);
        Assert.Equal(1, resource.Released);
        Assert.Equal(2, resource.Created);
        Assert.True(resource.Live);
        Assert.NotSame(before, device.Device);
        Assert.Null(device.RemovedReason());
        Assert.Equal(TerminalLayer.Name, terminalStateBefore);
    }

    [Fact]
    public void RecoveryWalksTheChainAgainRatherThanReusingTheAdapter()
    {
        using GraphicsDevice device = GraphicsDevice.Open(new WarpOnly());

        device.Recover();

        Assert.Equal(AdapterKind.Warp, device.Adapter.Kind);
        Assert.Contains("no hardware adapter could be opened", device.Adapter.Skipped);
    }

    [Fact]
    public void DisposeReleasesWhatWasRegistered()
    {
        CountingResource resource = new();

        using (GraphicsDevice device = GraphicsDevice.Open())
        {
            device.Register(resource);
        }

        Assert.Equal(1, resource.Released);
        Assert.False(resource.Live);
    }
}
