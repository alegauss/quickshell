using System.Diagnostics;
using System.IO;
using Xunit;

namespace Quickshell.App.Tests;

/// <summary>
/// Runs the crash probe: a second process that arms the client's own guard and then genuinely dies.
///
/// <para>Built by the same solution build the suite starts with, so there is nothing to compile
/// here — only a path to find and a process to wait for.</para>
/// </summary>
internal static class Crashing
{
    /// <summary>Runs the probe against a folder and gives back its exit code.</summary>
    public static int Run(string folder)
    {
        using Process probe = new()
        {
            StartInfo = new ProcessStartInfo(Probe(), [folder])
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
            },
        };

        Assert.True(probe.Start(), "the probe did not start");

        // Read before waiting: a pipe that fills is a process that blocks forever, and this one is
        // meant to die on its own terms rather than on a full buffer.
        string said = probe.StandardError.ReadToEnd();

        Assert.True(probe.WaitForExit(TimeSpan.FromSeconds(60)),
                    $"the probe was still running after a minute. It said: {said}");

        return probe.ExitCode;
    }

    /// <summary>
    /// The probe beside this test run, in the same configuration.
    ///
    /// <para>Derived from where these tests are running rather than assumed, so a Release run does
    /// not read a Debug binary somebody built last week.</para>
    /// </summary>
    private static string Probe()
    {
        DirectoryInfo here = new(AppContext.BaseDirectory);

        // ...\tests\Quickshell.App.Tests\bin\<Configuration>\net10.0-windows
        string framework = here.Name;
        string configuration = here.Parent?.Name ?? "Debug";

        string path = Path.Combine(Root(), "tools", "Quickshell.CrashProbe", "bin", configuration,
                                   framework, "Quickshell.CrashProbe.exe");

        Assert.True(File.Exists(path), $"{path} is not there — the solution build should make it");

        return path;
    }

    private static string Root()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Quickshell.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);

        return directory.FullName;
    }
}
