using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Quickshell.Replay;

/// <summary>
/// Keeps the scheduler out of the figures: the replay runs on the fastest core this CPU has, at high
/// priority (QS197).
///
/// <para><b>On a CPU with two kinds of core, where a pass runs decides how fast it is.</b> Seven
/// unpinned parse replays on the reference machine's i7-14700 split into two groups, about 1,000 MB/s
/// and about 670, with nothing about the parser different between them: the scheduler put some
/// passes on a performance core and some elsewhere.</para>
///
/// <para><b>Not the first performance core, and not any one by rule.</b> Pinned to logical processor
/// 0 a pass read slower than unpinned, since that processor takes most of the system's interrupts;
/// pinned to the last performance core it read slower still, because the cores of one class do not
/// run at one speed — the CPU boosts a favoured few highest. So each performance core is timed for
/// a moment on the same work, and the replay is held to the quickest.</para>
/// </summary>
internal static partial class Pinning
{
    /// <summary>How long each candidate core is timed for.</summary>
    private static readonly TimeSpan Trial = TimeSpan.FromMilliseconds(40);

    /// <summary>Pins this process to its fastest core at high priority, and says where, for the results file.</summary>
    public static string Apply()
    {
        using Process self = Process.GetCurrentProcess();

        self.PriorityClass = ProcessPriorityClass.High;

        int[] candidates = Candidates();

        if (candidates.Length == 0)
        {
            return "high priority, not pinned: the processor classes could not be read";
        }

        int core = candidates.MaxBy(Rate);

        // The thread that times the passes, and not the process. Held to one core with everything
        // else, the runtime's own threads queue behind a high-priority pass on it - the tiering
        // compiler among them, so the parser stayed in its first, slow compilation and every pass
        // read half what an unpinned one did.
        Thread.BeginThreadAffinity();
        SetThreadAffinityMask(GetCurrentThread(), (nint)(1L << core));

        return $"high priority, the timing thread pinned to logical processor {core}, the quickest of "
               + $"{candidates.Length} performance cores in a moment's trial";
    }

    /// <summary>
    /// One logical processor per core of the most capable class Windows reports, below 64 and never
    /// processor 0.
    /// </summary>
    private static unsafe int[] Candidates()
    {
        GetSystemCpuSetInformation(null, 0, out uint needed, 0, 0);

        if (needed == 0)
        {
            return [];
        }

        byte[] buffer = new byte[needed];
        List<(byte Index, byte Core, byte Class)> sets = [];

        fixed (byte* start = buffer)
        {
            if (!GetSystemCpuSetInformation(start, needed, out needed, 0, 0))
            {
                return [];
            }

            for (uint offset = 0; offset < needed;)
            {
                // SYSTEM_CPU_SET_INFORMATION: Size, Type, then the CpuSet member, whose
                // LogicalProcessorIndex is at 14, CoreIndex at 15 and EfficiencyClass at 18.
                uint size = *(uint*)(start + offset);

                sets.Add((*(start + offset + 14), *(start + offset + 15), *(start + offset + 18)));

                offset += size == 0 ? needed : size;
            }
        }

        if (sets.Count == 0)
        {
            return [];
        }

        byte best = sets.Max(set => set.Class);

        return [.. sets.Where(set => set.Class == best && set.Index is > 0 and < 64)
                       .GroupBy(set => set.Core)
                       .Select(core => (int)core.Min(set => set.Index))];
    }

    /// <summary>How much of a fixed piece of work one logical processor does in the trial.</summary>
    private static double Rate(int index)
    {
        Thread.BeginThreadAffinity();

        nint was = SetThreadAffinityMask(GetCurrentThread(), (nint)(1L << index));

        try
        {
            // Yield once so the thread is moved before the clock starts.
            Thread.Sleep(0);

            ulong state = 0x9E3779B97F4A7C15;
            long rounds = 0;
            Stopwatch clock = Stopwatch.StartNew();

            while (clock.Elapsed < Trial)
            {
                for (int step = 0; step < 4096; step++)
                {
                    state ^= state << 13;
                    state ^= state >> 7;
                    state ^= state << 17;
                }

                rounds++;
            }

            // Read, so the loop cannot be optimised away.
            return state == 0 ? 0 : rounds / clock.Elapsed.TotalSeconds;
        }
        finally
        {
            SetThreadAffinityMask(GetCurrentThread(), was);
            Thread.EndThreadAffinity();
        }
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool GetSystemCpuSetInformation(byte* information, uint length, out uint returned,
                                                                  nint process, uint flags);

    [LibraryImport("kernel32.dll")]
    private static partial nint SetThreadAffinityMask(nint thread, nint mask);

    [LibraryImport("kernel32.dll")]
    private static partial nint GetCurrentThread();
}
