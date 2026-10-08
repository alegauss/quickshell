using System.Net;
using System.Net.Sockets;
using Quickshell.Transport;
using Xunit;

namespace Quickshell.Transport.Tests;

/// <summary>
/// QS69: a forward that cannot open its port says what is holding it, read from Windows' own table
/// of listeners and their processes.
/// </summary>
public sealed class PortHolderTests
{
    /// <summary>A port this process listens on is named as this process, by its id.</summary>
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    public void APortThisProcessHoldsIsNamedAsThisProcess(string address)
    {
        using TcpListener holding = new(IPAddress.Parse(address), 0);

        holding.Start();

        int port = ((IPEndPoint)holding.LocalEndpoint).Port;

        string? holder = PortHolder.Describe(port);

        Assert.NotNull(holder);
        Assert.Contains($"process {Environment.ProcessId}", holder, StringComparison.Ordinal);
    }

    /// <summary>A port nothing listens on names nothing, and the sentence falls back to what it said before.</summary>
    [Fact]
    public void AFreePortNamesNothing()
    {
        int port;

        using (TcpListener probe = new(IPAddress.Loopback, 0))
        {
            probe.Start();
            port = ((IPEndPoint)probe.LocalEndpoint).Port;
        }

        Assert.Null(PortHolder.Describe(port));
    }
}
