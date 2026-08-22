using System.Diagnostics;

namespace HostProbe.Core;

/// <summary>QueryPerformanceCounter, in the one place every probe reads it from.</summary>
public static class Clock
{
    public static long Now => Stopwatch.GetTimestamp();

    public static double MillisecondsBetween(long from, long to) =>
        (to - from) * 1000.0 / Stopwatch.Frequency;
}
