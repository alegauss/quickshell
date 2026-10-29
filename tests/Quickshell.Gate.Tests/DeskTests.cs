using Quickshell.Gate;
using Xunit;

namespace Quickshell.Gate.Tests;

/// <summary>
/// QS374: what <c>vmrun list</c> printed, read as the VMs running on this desk. The count line is
/// vmrun's and not a VM, and an empty desk is one the gate measures on.
/// </summary>
public sealed class DeskTests
{
    [Fact]
    public void AnEmptyDeskHasNothingRunning() =>
        Assert.Empty(Desk.Running("Total running VMs: 0\r\n"));

    [Fact]
    public void EveryListedVmxIsRunningAndTheCountLineIsNot()
    {
        IReadOnlyList<string> running = Desk.Running(
            "Total running VMs: 2\r\nD:\\VMs\\win11\\win11.vmx\r\nE:\\Other VMs\\build\\build.vmx\r\n");

        Assert.Equal(["D:\\VMs\\win11\\win11.vmx", "E:\\Other VMs\\build\\build.vmx"], running);
    }

    [Fact]
    public void BlankLinesAroundTheListAreNotVms() =>
        Assert.Single(Desk.Running("\nTotal running VMs: 1\n\n  D:\\VMs\\win11\\win11.vmx  \n\n"));
}
