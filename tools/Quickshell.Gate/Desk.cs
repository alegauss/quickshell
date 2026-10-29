using System.ComponentModel;
using System.Diagnostics;

namespace Quickshell.Gate;

/// <summary>
/// Whether this desk is quiet enough to measure on, asked before anything is built (QS374).
///
/// <para><b>A guest left running is the noise.</b> run-tests-vm, run-startup-vm and run-app-vm leave
/// the VM up when they finish, and a sitting beside one comes out with its rounds spread past what
/// the gate can judge: start 516 ms against 501, allowed 11.7 %, on 2026-10-22. That answer came
/// after a publish of both sides and four rounds. Asking vmrun first costs a second.</para>
///
/// <para><b>Listing needs no password.</b> <c>vmrun list</c> names every running VM, encrypted or
/// not, so this reads no credentials. Suspending an encrypted one does need them, which is why the
/// remedy is <c>suspend-vm.cmd</c>, going through vm-guest.ps1, and not a bare <c>vmrun suspend</c>.</para>
/// </summary>
public static class Desk
{
    /// <summary>Where VMware installs vmrun, in the order vm-guest.ps1 looks.</summary>
    private static readonly string[] VmRunCandidates =
    [
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "VMware", "VMware Workstation", "vmrun.exe"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "VMware", "VMware Workstation", "vmrun.exe"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "VMware", "VMware VIX", "vmrun.exe"),
    ];

    /// <summary>
    /// Refuses when a VM is running here. A desk without VMware, or a vmrun that will not answer,
    /// has nothing this can say about it, and the sitting's own spread is still the judge.
    /// </summary>
    public static void RefuseARunningGuest()
    {
        string? vmrun = VmRunCandidates.FirstOrDefault(File.Exists);

        if (vmrun is null)
        {
            return;
        }

        string? listed = Listed(vmrun);

        if (listed is null)
        {
            Console.WriteLine("  vmrun did not answer, so whether a guest is running here is not known");

            return;
        }

        IReadOnlyList<string> running = Running(listed);

        if (running.Count > 0)
        {
            throw new GateRefusal($"{Named(running)} running on this desk, and a sitting beside a guest comes out too "
                                  + "noisy to judge. Suspend it with suspend-vm.cmd and run the gate again.");
        }
    }

    /// <summary>The .vmx of every VM that <c>vmrun list</c> names, after its count line.</summary>
    /// <param name="listed">What <c>vmrun -T ws list</c> printed.</param>
    public static IReadOnlyList<string> Running(string listed)
    {
        ArgumentNullException.ThrowIfNull(listed);

        return [.. listed.Split('\n')
                         .Select(line => line.Trim())
                         .Where(line => line.Length > 0 && !line.StartsWith("Total running VMs", StringComparison.OrdinalIgnoreCase))];
    }

    private static string Named(IReadOnlyList<string> running) => running.Count == 1
        ? $"the VM {Path.GetFileName(running[0])} is"
        : $"{running.Count} VMs are ({string.Join(", ", running.Select(Path.GetFileName))})";

    private static string? Listed(string vmrun)
    {
        ProcessStartInfo start = new(vmrun)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            ArgumentList = { "-T", "ws", "list" },
        };

        Process? process;

        try
        {
            process = Process.Start(start);
        }
        catch (Win32Exception)
        {
            // Not this gate's to judge: a vmrun that cannot start is a desk this check knows nothing about.
            return null;
        }

        if (process is null)
        {
            return null;
        }

        using Process started = process;

        Task<string> error = process.StandardError.ReadToEndAsync();
        string output = process.StandardOutput.ReadToEnd();

        process.WaitForExit();
        _ = error.GetAwaiter().GetResult();

        return process.ExitCode == 0 ? output : null;
    }
}
