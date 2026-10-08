using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Quickshell.Transport;

/// <summary>
/// Which program is listening on a local port, for the sentence that says a forward could not open
/// it (QS69).
///
/// <para><b>A port in use is the commonest way a forward fails, and the answer is usually this
/// client.</b> A window still open from earlier, or one that did not close cleanly, holds the port
/// the new forward wants, and "something is listening on it" sends a person to reboot. Naming the
/// program — and saying when it is another quickshell — is what saves that.</para>
///
/// <para>Read from Windows' own TCP table with the owning process of each listener, IPv4 and IPv6,
/// because .NET has no way to ask which process holds a socket. Anything that cannot be read —
/// a process gone, a table that will not come back — is an answer of null, and the sentence falls
/// back to what it said before.</para>
/// </summary>
public static class PortHolder
{
    private const int AfInet = 2;
    private const int AfInet6 = 23;
    private const int TcpTableOwnerPidListener = 3;

    /// <summary>
    /// A clause naming what listens on <paramref name="port"/> — "quickshell (another window, process
    /// 4120)" or "node (process 9000)" — or null where nothing can be named.
    /// </summary>
    public static string? Describe(int port)
    {
        if (port <= 0 || !OperatingSystem.IsWindows())
        {
            return null;
        }

        foreach (int process in Listeners(port))
        {
            try
            {
                using Process holding = Process.GetProcessById(process);

                if (process == Environment.ProcessId)
                {
                    return $"this quickshell window (process {process}), from another session's forward";
                }

                return holding.ProcessName.Equals("quickshell", StringComparison.OrdinalIgnoreCase)
                    ? $"another quickshell window (process {process})"
                    : $"{holding.ProcessName} (process {process})";
            }
            catch (ArgumentException)
            {
                // Gone between the table and the question.
            }
            catch (InvalidOperationException)
            {
                // Gone, or not ours to name.
            }
        }

        return null;
    }

    /// <summary>The processes listening on a port, from both address families' tables.</summary>
    private static IEnumerable<int> Listeners(int port) =>
        Table(AfInet, rowSize: 24, portOffset: 8, pidOffset: 20, port)
            .Concat(Table(AfInet6, rowSize: 56, portOffset: 20, pidOffset: 52, port))
            .Distinct();

    /// <summary>
    /// One family's listener table, read as MIB_TCPROW_OWNER_PID (IPv4) or MIB_TCP6ROW_OWNER_PID
    /// (IPv6): a row count, then fixed-size rows whose local port is in network order.
    /// </summary>
    private static List<int> Table(int family, int rowSize, int portOffset, int pidOffset, int port)
    {
        List<int> found = [];
        int size = 0;

        _ = GetExtendedTcpTable(IntPtr.Zero, ref size, false, family, TcpTableOwnerPidListener, 0);

        if (size <= 0)
        {
            return found;
        }

        IntPtr table = Marshal.AllocHGlobal(size);

        try
        {
            if (GetExtendedTcpTable(table, ref size, false, family, TcpTableOwnerPidListener, 0) != 0)
            {
                return found;
            }

            int rows = Marshal.ReadInt32(table);

            for (int row = 0; row < rows; row++)
            {
                IntPtr at = table + 4 + (row * rowSize);

                // The port is the low two bytes of a DWORD, in network order.
                ushort local = BinaryPrimitives.ReverseEndianness((ushort)Marshal.ReadInt16(at + portOffset));

                if (local == port)
                {
                    found.Add(Marshal.ReadInt32(at + pidOffset));
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(table);
        }

        return found;
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool sort, int family,
                                                   int tableClass, uint reserved);
}
