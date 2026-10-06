using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;

namespace Quickshell.Transport.Tests;

/// <summary>
/// Pageant's window, answering over shared memory the way Pageant before 0.78 does — and the way
/// every Pageant still does beside its pipe (QS114).
///
/// <para><b>The carrier is real and the agent is <see cref="FakeAgent"/>.</b> A hidden top-level
/// window whose class and title are <c>Pageant</c>, a <c>WM_COPYDATA</c> carrying PuTTY's
/// identifier and a mapping name, a mapping opened by that name, and the answer written back into
/// it before the message returns. The bytes it answers with come from the same
/// <see cref="FakeAgent.Answer"/> the pipe serves, so a difference between the two carriers is the
/// carrier's.</para>
///
/// <para><b>It refuses what Pageant refuses.</b> A mapping whose owner is not this user is ignored,
/// as Pageant ignores it — which is the one rule that differs by machine, since an administrator's
/// default owner is the Administrators group.</para>
///
/// <para>One at a time: the class name is global, so the tests that use this live in one class and
/// run in sequence, and each skips where a real Pageant is already open.</para>
/// </summary>
internal sealed partial class FakePageant : IDisposable
{
    private const uint CopyData = 0x004A;
    private const uint Close = 0x0010;
    private const uint Destroy = 0x0002;
    private const nuint CopyDataId = 0x804e50ba;
    private const uint FileMapAllAccess = 0x000F001F;
    private const int KernelObject = 6;
    private const uint OwnerInformation = 0x00000001;

    private readonly Func<byte[], byte[]> _answer;
    private readonly Thread _pump;
    private readonly ManualResetEventSlim _ready = new();
    private readonly WindowProcedure _procedure;
    private nint _window;

    internal FakePageant(Func<byte[], byte[]> answer)
    {
        _answer = answer;
        _procedure = Procedure;
        _pump = new Thread(Pump) { IsBackground = true, Name = "fake pageant" };
        _pump.SetApartmentState(ApartmentState.STA);
        _pump.Start();

        if (!_ready.Wait(TimeSpan.FromSeconds(5)) || _window == 0)
        {
            throw new InvalidOperationException("the fake Pageant window never appeared");
        }
    }

    private delegate nint WindowProcedure(nint window, uint message, nint wParam, nint lParam);

    /// <summary>How many requests arrived, refused ones included.</summary>
    internal int Requests { get; private set; }

    /// <summary>How many were refused for the mapping's owner.</summary>
    internal int RefusedForOwner { get; private set; }

    /// <summary>Whether a Pageant window — a real one — is already open, which a test must not answer for.</summary>
    internal static bool AnotherIsOpen => FindWindowW("Pageant", "Pageant") != 0;

    public void Dispose()
    {
        if (_window != 0)
        {
            PostMessageW(_window, Close, 0, 0);
        }

        _pump.Join(TimeSpan.FromSeconds(5));
        _ready.Dispose();
    }

    private unsafe void Pump()
    {
        nint module = GetModuleHandleW(null);

        WindowClass registration = new()
        {
            Size = (uint)sizeof(WindowClass),
            Procedure = Marshal.GetFunctionPointerForDelegate(_procedure),
            Instance = module,
        };

        fixed (char* name = "Pageant")
        {
            registration.ClassName = (nint)name;

            ushort atom = RegisterClassExW(ref registration);

            if (atom != 0)
            {
                _window = CreateWindowExW(0, "Pageant", "Pageant", 0, 0, 0, 0, 0, 0, 0, module, 0);
            }

            _ready.Set();

            if (_window == 0)
            {
                return;
            }

            while (GetMessageW(out Message message, 0, 0, 0) > 0)
            {
                TranslateMessage(ref message);
                DispatchMessageW(ref message);
            }

            UnregisterClassW("Pageant", module);
        }
    }

    private nint Procedure(nint window, uint message, nint wParam, nint lParam)
    {
        if (message == CopyData)
        {
            return Serve(lParam);
        }

        if (message == Destroy)
        {
            PostQuitMessage(0);

            return 0;
        }

        return DefWindowProcW(window, message, wParam, lParam);
    }

    /// <summary>One request, read from the mapping it names and answered into the same mapping.</summary>
    private unsafe nint Serve(nint lParam)
    {
        CopyDataStruct* copied = (CopyDataStruct*)lParam;

        if (copied->Data != CopyDataId || copied->Length <= 0)
        {
            return 0;
        }

        Requests++;

        string name = Encoding.ASCII.GetString((byte*)copied->Pointer, copied->Length).TrimEnd('\0');
        nint mapping = OpenFileMappingW(FileMapAllAccess, 0, name);

        if (mapping == 0)
        {
            return 0;
        }

        try
        {
            if (!OwnedByThisUser(mapping))
            {
                RefusedForOwner++;

                return 0;
            }

            nint view = MapViewOfFile(mapping, FileMapAllAccess, 0, 0, 0);

            if (view == 0)
            {
                return 0;
            }

            try
            {
                Span<byte> shared = new((void*)view, PageantMemoryBytes);
                uint length = BinaryPrimitives.ReadUInt32BigEndian(shared);

                if (length > PageantMemoryBytes - 4)
                {
                    return 0;
                }

                byte[] answer = _answer(shared.Slice(4, (int)length).ToArray());

                BinaryPrimitives.WriteUInt32BigEndian(shared, (uint)answer.Length);
                answer.CopyTo(shared[4..]);

                return 1;
            }
            finally
            {
                UnmapViewOfFile(view);
            }
        }
        finally
        {
            CloseHandle(mapping);
        }
    }

    /// <summary>Pageant's own check, which is what makes a default-owned mapping fail on an admin's machine.</summary>
    private static bool OwnedByThisUser(nint mapping)
    {
        if (GetSecurityInfo(mapping, KernelObject, OwnerInformation, out nint owner, 0, 0, 0, out nint descriptor) != 0)
        {
            return false;
        }

        try
        {
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();

            return new SecurityIdentifier(owner) == identity.User;
        }
        finally
        {
            LocalFree(descriptor);
        }
    }

    /// <summary>The size the client maps, which is what a request and its answer must fit.</summary>
    private const int PageantMemoryBytes = 8192;

    [StructLayout(LayoutKind.Sequential)]
    private struct CopyDataStruct
    {
        public nuint Data;
        public int Length;
        public nint Pointer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowClass
    {
        public uint Size;
        public uint Style;
        public nint Procedure;
        public int ClassExtra;
        public int WindowExtra;
        public nint Instance;
        public nint Icon;
        public nint Cursor;
        public nint Background;
        public nint MenuName;
        public nint ClassName;
        public nint SmallIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Message
    {
        public nint Window;
        public uint Id;
        public nint WParam;
        public nint LParam;
        public uint Time;
        public int X;
        public int Y;
        public uint Private;
    }

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint FindWindowW(string className, string windowName);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial ushort RegisterClassExW(ref WindowClass registration);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnregisterClassW(string className, nint instance);

    [LibraryImport("user32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateWindowExW(uint extendedStyle, string className, string windowName, uint style,
                                                int x, int y, int width, int height, nint parent, nint menu,
                                                nint instance, nint parameter);

    [LibraryImport("user32.dll")]
    private static partial int GetMessageW(out Message message, nint window, uint first, uint last);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TranslateMessage(ref Message message);

    [LibraryImport("user32.dll")]
    private static partial nint DispatchMessageW(ref Message message);

    [LibraryImport("user32.dll")]
    private static partial nint DefWindowProcW(nint window, uint message, nint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostMessageW(nint window, uint message, nint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    private static partial void PostQuitMessage(int code);

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint GetModuleHandleW(string? name);

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint OpenFileMappingW(uint access, int inherit, string name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint MapViewOfFile(nint mapping, uint access, uint offsetHigh, uint offsetLow, nuint bytes);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnmapViewOfFile(nint view);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

    [LibraryImport("kernel32.dll")]
    private static partial nint LocalFree(nint memory);

    [LibraryImport("advapi32.dll")]
    private static partial uint GetSecurityInfo(nint handle, int objectType, uint information, out nint owner,
                                                nint group, nint dacl, nint sacl, out nint descriptor);
}
