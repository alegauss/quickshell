using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace Quickshell.App;

/// <summary>
/// A shortcut file, written and read back through the shell's own object for it.
///
/// <para>A <c>.lnk</c> is a binary format the shell owns and extends, and a shortcut composed by
/// hand is how a Start menu ends up showing a blank icon that opens nothing. <c>ShellLink</c> is the
/// object every installer on this platform goes through, and two of its interfaces are all this
/// needs: one to say what the shortcut points at, one to put it on disk.</para>
///
/// <para>Source-generated COM rather than the runtime's built-in kind, for the reason the rest of
/// this client's interop is source-generated: what crosses the boundary is code a reader can
/// open.</para>
/// </summary>
public static partial class ShellLinks
{
    /// <summary>CLSID_ShellLink.</summary>
    private static readonly Guid ShellLink = new("00021401-0000-0000-C000-000000000046");

    /// <summary>CLSCTX_INPROC_SERVER: the shell's own DLL, in this process.</summary>
    private const uint InProcess = 1;

    /// <summary>MAX_PATH, which is what the shell's own buffers for these fields are.</summary>
    private const int Longest = 260;

    private static readonly StrategyBasedComWrappers Wrappers = new();

    /// <summary>
    /// Writes a shortcut to a program.
    /// </summary>
    /// <param name="shortcut">The <c>.lnk</c> file to write, replaced where it exists.</param>
    /// <param name="target">The program it starts.</param>
    /// <param name="startIn">
    /// Where the program starts. Environment variables are the shell's to expand when it is used,
    /// which is what lets one shortcut serve every user of a machine.
    /// </param>
    /// <param name="description">What the Start menu shows when a pointer rests on it.</param>
    public static void Write(string shortcut, string target, string startIn, string description)
    {
        IShellLinkW link = Create();

        link.SetPath(target);
        link.SetWorkingDirectory(startIn);
        link.SetDescription(description);
        link.SetIconLocation(target, 0);

        ((IPersistFile)link).Save(shortcut, remember: true);
    }

    /// <summary>
    /// The program a shortcut starts, as the shell reads it, or null where the file is not a
    /// shortcut the shell can open.
    /// </summary>
    public static unsafe string? Target(string shortcut)
    {
        IShellLinkW link = Create();

        try
        {
            ((IPersistFile)link).Load(shortcut, 0);
        }
        catch (COMException)
        {
            return null;
        }

        char* path = stackalloc char[Longest];

        link.GetPath(path, Longest, 0, 0);

        return new string(path);
    }

    private static IShellLinkW Create()
    {
        Guid iid = typeof(IShellLinkW).GUID;

        Marshal.ThrowExceptionForHR(CoCreateInstance(ShellLink, 0, InProcess, iid, out nint instance));

        try
        {
            return (IShellLinkW)Wrappers.GetOrCreateObjectForComInstance(instance, CreateObjectFlags.UniqueInstance);
        }
        finally
        {
            Marshal.Release(instance);
        }
    }

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(in Guid clsid, nint outer, uint context, in Guid iid,
                                                out nint instance);
}

/// <summary>
/// IShellLinkW, every method in the order its vtable has them — the ones this client never calls
/// included, because a slot left out moves every slot after it.
/// </summary>
[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("000214F9-0000-0000-C000-000000000046")]
internal unsafe partial interface IShellLinkW
{
    void GetPath(char* file, int length, nint findData, uint flags);

    void GetIDList(out nint list);

    void SetIDList(nint list);

    void GetDescription(char* name, int length);

    void SetDescription(string name);

    void GetWorkingDirectory(char* directory, int length);

    void SetWorkingDirectory(string directory);

    void GetArguments(char* arguments, int length);

    void SetArguments(string arguments);

    void GetHotkey(out ushort hotkey);

    void SetHotkey(ushort hotkey);

    void GetShowCmd(out int show);

    void SetShowCmd(int show);

    void GetIconLocation(char* path, int length, out int icon);

    void SetIconLocation(string path, int icon);

    void SetRelativePath(string path, uint reserved);

    void Resolve(nint window, uint flags);

    void SetPath(string file);
}

/// <summary>IPersistFile, with IPersist's one method first where the vtable has it.</summary>
[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("0000010B-0000-0000-C000-000000000046")]
internal partial interface IPersistFile
{
    void GetClassID(out Guid classId);

    [PreserveSig]
    int IsDirty();

    void Load(string file, uint mode);

    void Save(string file, [MarshalAs(UnmanagedType.Bool)] bool remember);

    void SaveCompleted(string file);

    void GetCurFile(out nint file);
}
