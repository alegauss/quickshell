using System.Globalization;
using System.Runtime.InteropServices;

namespace Quickshell.Idle;

/// <summary>
/// How long the GPU's engines spent on a set of processes, from Windows' own <c>GPU Engine</c>
/// counters (QS137).
///
/// <para><b>Zero draw calls bounds submission, not residency.</b> A render loop that presents
/// nothing can still keep an engine busy, and the draw-call test cannot see that. The counter can:
/// <c>Running Time</c> is each engine's cumulative busy time per process, in 100 ns units, so the
/// difference across an interval is what that process cost the GPU over it.</para>
///
/// <para>Added by its English name, because <c>Get-Counter</c> and <c>typeperf</c> take the
/// localised one and the reference desk is not in English.</para>
/// </summary>
internal sealed class GpuTime : IDisposable
{
    private const uint Success = 0;
    private const uint MoreData = 0x800007D2;

    private readonly nint _query;
    private readonly nint _counter;

    private GpuTime(nint query, nint counter)
    {
        _query = query;
        _counter = counter;
    }

    /// <summary>Opens the counter, or null where this desk has none to read.</summary>
    public static GpuTime? TryOpen()
    {
        if (PdhOpenQuery(null, 0, out nint query) != Success)
        {
            return null;
        }

        if (PdhAddEnglishCounter(query, @"\GPU Engine(*)\Running Time", 0, out nint counter) != Success)
        {
            _ = PdhCloseQuery(query);

            return null;
        }

        return new GpuTime(query, counter);
    }

    /// <summary>The busy time of every engine, summed, for the processes given.</summary>
    public TimeSpan Read(IEnumerable<int> processes)
    {
        HashSet<string> prefixes = [.. processes.Select(pid => string.Create(CultureInfo.InvariantCulture, $"pid_{pid}_"))];

        if (PdhCollectQueryData(_query) != Success)
        {
            return TimeSpan.Zero;
        }

        uint size = 0;
        uint count = 0;

        if (PdhGetRawCounterArray(_counter, ref size, out count, 0) != MoreData || size == 0)
        {
            return TimeSpan.Zero;
        }

        nint buffer = Marshal.AllocHGlobal((int)size);

        try
        {
            if (PdhGetRawCounterArray(_counter, ref size, out count, buffer) != Success)
            {
                return TimeSpan.Zero;
            }

            long ticks = 0;
            int stride = Marshal.SizeOf<RawItem>();

            for (int at = 0; at < count; at++)
            {
                RawItem item = Marshal.PtrToStructure<RawItem>(buffer + (at * stride));
                string name = Marshal.PtrToStringUni(item.Name) ?? string.Empty;

                if (prefixes.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal)))
                {
                    ticks += item.Value.FirstValue;
                }
            }

            return TimeSpan.FromTicks(ticks);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <inheritdoc/>
    public void Dispose() => _ = PdhCloseQuery(_query);

    [StructLayout(LayoutKind.Sequential)]
    private struct RawCounter
    {
        public uint CStatus;
        public long TimeStamp;
        public long FirstValue;
        public long SecondValue;
        public uint MultiCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawItem
    {
        public nint Name;
        public RawCounter Value;
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode, EntryPoint = "PdhOpenQueryW")]
    private static extern uint PdhOpenQuery(string? source, nint userData, out nint query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode, EntryPoint = "PdhAddEnglishCounterW")]
    private static extern uint PdhAddEnglishCounter(nint query, string path, nint userData, out nint counter);

    [DllImport("pdh.dll")]
    private static extern uint PdhCollectQueryData(nint query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode, EntryPoint = "PdhGetRawCounterArrayW")]
    private static extern uint PdhGetRawCounterArray(nint counter, ref uint bufferSize, out uint itemCount, nint items);

    [DllImport("pdh.dll")]
    private static extern uint PdhCloseQuery(nint query);
}
