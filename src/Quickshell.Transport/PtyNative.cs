using System.Runtime.InteropServices;

namespace Quickshell.Transport;

/// <summary>
/// The Windows calls a pseudo-console is made of.
///
/// <para>Declared here rather than beside the class that uses them, so the one file that has to be
/// read against the platform's own documentation is a file of nothing else. Every structure is
/// blittable and every layout is explicit: a field the runtime moved would be a failure that looks
/// like a Windows bug.</para>
/// </summary>
internal static partial class PtyNative
{
    /// <summary>The attribute that hands a process its pseudo-console.</summary>
    internal const nint AttributePseudoConsole = 0x00020016;

    /// <summary>Says the startup information is the extended kind, which is what carries the list.</summary>
    internal const uint ExtendedStartupInfoPresent = 0x00080000;

    /// <summary>
    /// STARTF_USESTDHANDLES. Says the three standard handles in the startup information are the
    /// child's — and with all three left null, that the child has none to inherit.
    /// </summary>
    internal const int UseStandardHandles = 0x00000100;

    /// <summary>A size in character cells, which is the only shape a console size comes in.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct Coord
    {
        public short X;
        public short Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct StartupInfo
    {
        public int Size;
        public nint Reserved;
        public nint Desktop;
        public nint Title;
        public int X;
        public int Y;
        public int XSize;
        public int YSize;
        public int XCountChars;
        public int YCountChars;
        public int FillAttribute;
        public int Flags;
        public short ShowWindow;
        public short Reserved2Length;
        public nint Reserved2;
        public nint StandardInput;
        public nint StandardOutput;
        public nint StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct StartupInfoEx
    {
        public StartupInfo StartupInfo;
        public nint AttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ProcessInformation
    {
        public nint Process;
        public nint Thread;
        public int ProcessId;
        public int ThreadId;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial int CreatePseudoConsole(Coord size, nint input, nint output, uint flags, out nint console);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial int ResizePseudoConsole(nint console, Coord size);

    /// <summary>
    /// Releases the pseudo-console, which is what tells the program on the other end that its console
    /// has gone. It blocks until the console host has flushed what it still owed.
    /// </summary>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial void ClosePseudoConsole(nint console);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool InitializeProcThreadAttributeList(
        nint list, int count, int flags, ref nint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool UpdateProcThreadAttribute(
        nint list, uint flags, nint attribute, nint value, nint size, nint previous, nint returned);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial void DeleteProcThreadAttributeList(nint list);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateProcessW", SetLastError = true,
                   StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CreateProcess(
        string? applicationName,
        ref char commandLine,
        nint processAttributes,
        nint threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint creationFlags,
        nint environment,
        string? currentDirectory,
        ref StartupInfoEx startupInfo,
        out ProcessInformation processInformation);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetExitCodeProcess(nint process, out int code);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool TerminateProcess(nint process, uint code);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CloseHandle(nint handle);
}
