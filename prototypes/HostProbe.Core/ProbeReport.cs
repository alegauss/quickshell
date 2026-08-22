namespace HostProbe.Core;

/// <summary>One host's run. This is the file the decision record is written from.</summary>
public sealed class ProbeReport
{
    public string Host { get; set; } = "";
    public string MachineDisplayHz { get; set; } = "";
    public DateTimeOffset RanAt { get; set; }

    public double PresentedFpsClean { get; set; }
    public double PresentedFpsWithDropdown { get; set; }

    public int LatencyTrials { get; set; }
    public int LatencyTimeouts { get; set; }
    public double LatencyMedianMs { get; set; }
    public double LatencyP95Ms { get; set; }
    public double LatencyMinMs { get; set; }
    public double LatencyMaxMs { get; set; }
    public List<double> LatencySamplesMs { get; set; } = [];

    public bool DropdownOverlapped { get; set; }
    public bool ModalOverlapped { get; set; }
    public string DropdownShot { get; set; } = "";
    public string ModalShot { get; set; } = "";

    public List<string> Notes { get; set; } = [];
}
