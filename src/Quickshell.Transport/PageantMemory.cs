using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace Quickshell.Transport;

/// <summary>
/// The carrier Pageant spoke before it had a named pipe, and still speaks beside it (QS114).
///
/// <para><b>The same agent requests, carried differently.</b> The request is written, with its
/// length in front, into a file mapping this process creates; the mapping's name is sent to the
/// hidden window whose class and title are both <c>Pageant</c>, as a <c>WM_COPYDATA</c> tagged with
/// PuTTY's own identifier; Pageant writes its answer into the same mapping before the message
/// returns. Nothing above <see cref="SshAgent"/>'s exchange knows the difference.</para>
///
/// <para><b>The mapping is owned by this user's SID, explicitly.</b> Pageant refuses a mapping whose
/// owner is not the user it runs as. Left to the default, the owner is the token's default owner,
/// which for a member of Administrators is the Administrators group and not the user — so the
/// request is ignored, and only on the machines of the people most likely to be running it.</para>
///
/// <para><b>Small messages.</b> Pageant before 0.78 read at most 8 KB from the mapping, so the
/// mapping is that size and a request or answer that does not fit is refused by name rather than
/// truncated. Listing identities and signing a login fit with room to spare.</para>
/// </summary>
internal static partial class PageantMemory
{
    /// <summary>What both the window's class and its title are.</summary>
    internal const string WindowName = "Pageant";

    /// <summary>The size of the mapping, which older Pageant reads no further than.</summary>
    internal const int MappingBytes = 8192;

    /// <summary>PuTTY's <c>AGENT_COPYDATA_ID</c>, which is how Pageant knows the message is a request.</summary>
    internal const nuint CopyDataId = 0x804e50ba;

    private const uint CopyData = 0x004A;
    private const uint PageReadWrite = 0x04;
    private const uint FileMapWrite = 0x0002;
    private const uint AbortIfHung = 0x0002;

    /// <summary>
    /// How long Pageant may take to answer. Long, because a key that asks for confirmation waits on
    /// a person, and abandoning that wait leaves a dialog nobody can now satisfy.
    /// </summary>
    private const uint AnswerWithinMilliseconds = 120_000;

    private static int _requests;

    /// <summary>Whether a Pageant window is there to be asked. Finding a window opens nothing.</summary>
    internal static bool IsRunning => FindWindowW(WindowName, WindowName) != 0;

    /// <summary>One framed request — length prefix included — and the framed answer's payload.</summary>
    /// <exception cref="IOException">No Pageant answered, or the answer did not fit.</exception>
    internal static unsafe byte[] Exchange(ReadOnlySpan<byte> framed)
    {
        nint window = FindWindowW(WindowName, WindowName);

        if (window == 0)
        {
            throw new IOException("no Pageant window is open");
        }

        if (framed.Length > MappingBytes)
        {
            throw new IOException($"a {framed.Length}-byte request does not fit Pageant's {MappingBytes}-byte mapping");
        }

        // Unique per request, not per thread as PuTTY names it: two sessions signing at once from
        // one thread pool must not write into each other's mapping.
        string name = $"PageantRequest{Environment.ProcessId:x8}{Interlocked.Increment(ref _requests):x8}";
        byte[] owner = OwnedByThisUser();

        fixed (byte* descriptor = owner)
        {
            SecurityAttributes attributes = new()
            {
                Length = sizeof(SecurityAttributes),
                Descriptor = (nint)descriptor,
                Inherit = 0,
            };

            using Microsoft.Win32.SafeHandles.SafeFileHandle mapping =
                CreateFileMappingW(-1, ref attributes, PageReadWrite, 0, MappingBytes, name);

            if (mapping.IsInvalid)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "the request mapping could not be created");
            }

            nint view = MapViewOfFile(mapping, FileMapWrite, 0, 0, MappingBytes);

            if (view == 0)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "the request mapping could not be mapped");
            }

            try
            {
                Span<byte> shared = new((void*)view, MappingBytes);

                framed.CopyTo(shared);

                byte[] ascii = Encoding.ASCII.GetBytes(name + "\0");

                fixed (byte* text = ascii)
                {
                    CopyDataStruct message = new() { Data = CopyDataId, Length = ascii.Length, Pointer = (nint)text };

                    nint sent = SendMessageTimeoutW(window, CopyData, 0, (nint)(&message), AbortIfHung,
                                                    AnswerWithinMilliseconds, out nint answered);

                    if (sent == 0 || answered == 0)
                    {
                        throw new IOException("Pageant did not take the request");
                    }
                }

                uint length = BinaryPrimitives.ReadUInt32BigEndian(shared);

                if (length == 0 || length > MappingBytes - 4)
                {
                    throw new IOException($"Pageant answered with a {length}-byte message");
                }

                return shared.Slice(4, (int)length).ToArray();
            }
            finally
            {
                UnmapViewOfFile(view);
            }
        }
    }

    /// <summary>A self-relative security descriptor whose owner is this process's user.</summary>
    internal static byte[] OwnedByThisUser()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();

        SecurityIdentifier user = identity.User
            ?? throw new IOException("this process has no user SID to own the request with");

        RawSecurityDescriptor descriptor = new(ControlFlags.None, user, null, null, null);
        byte[] binary = new byte[descriptor.BinaryLength];

        descriptor.GetBinaryForm(binary, 0);

        return binary;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Length;
        public nint Descriptor;
        public int Inherit;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CopyDataStruct
    {
        public nuint Data;
        public int Length;
        public nint Pointer;
    }

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint FindWindowW(string className, string windowName);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial nint SendMessageTimeoutW(nint window, uint message, nint wParam, nint lParam,
                                                    uint flags, uint timeout, out nint result);

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileMappingW(
        nint file, ref SecurityAttributes attributes, uint protect, uint sizeHigh, uint sizeLow, string name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint MapViewOfFile(Microsoft.Win32.SafeHandles.SafeFileHandle mapping, uint access,
                                              uint offsetHigh, uint offsetLow, nuint bytes);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnmapViewOfFile(nint view);
}
