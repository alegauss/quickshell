using System.IO;
using System.Runtime.InteropServices;
using Quickshell.App;
using Xunit;

namespace Quickshell.App.Tests;

/// <summary>
/// The executable carries the client's own icon, at every size Windows asks for (QS283): the taskbar,
/// Alt+Tab, the Start menu shortcut and the uninstall entry all take it from there.
/// </summary>
public sealed class IconTests
{
    /// <summary>The sizes tools\make-icon.py writes, each drawn on its own grid.</summary>
    private static readonly int[] Sizes = [16, 20, 24, 32, 40, 48, 64, 256];

    [Fact]
    public void TheExecutableCarriesTheIconAtEverySize()
    {
        string exe = Path.ChangeExtension(typeof(MainWindow).Assembly.Location, ".exe");

        Assert.True(File.Exists(exe), $"no executable beside {typeof(MainWindow).Assembly.Location}");

        Assert.Equal(Sizes, IconSizes(exe).Order());
    }

    /// <summary>The sizes in the executable's first icon group, read from its resources without running it.</summary>
    private static int[] IconSizes(string exe)
    {
        nint module = LoadLibraryEx(exe, 0, LoadAsDataFile);

        Assert.NotEqual(0, module);

        try
        {
            nint group = 0;

            EnumResourceNames(module, GroupIcon, (_, _, name, _) =>
            {
                group = FindResource(module, name, GroupIcon);

                return false;
            }, 0);

            Assert.NotEqual(0, group);

            nint data = LockResource(LoadResource(module, group));
            int length = (int)SizeofResource(module, group);
            byte[] bytes = new byte[length];

            Marshal.Copy(data, bytes, 0, length);

            // GRPICONDIR: reserved, type, count; then 14 bytes per entry, the width first (0 is 256).
            int count = BitConverter.ToUInt16(bytes, 4);

            return [.. Enumerable.Range(0, count).Select(entry => bytes[6 + (14 * entry)] is 0 ? 256 : (int)bytes[6 + (14 * entry)])];
        }
        finally
        {
            FreeLibrary(module);
        }
    }

    private const uint LoadAsDataFile = 0x00000002 | 0x00000020;

    private static readonly nint GroupIcon = 14;

    private delegate bool Named(nint module, nint type, nint name, nint parameter);

    [DllImport("kernel32.dll", EntryPoint = "LoadLibraryExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint LoadLibraryEx(string path, nint file, uint flags);

    [DllImport("kernel32.dll")]
    private static extern bool FreeLibrary(nint module);

    [DllImport("kernel32.dll", EntryPoint = "EnumResourceNamesW")]
    private static extern bool EnumResourceNames(nint module, nint type, Named callback, nint parameter);

    [DllImport("kernel32.dll", EntryPoint = "FindResourceW")]
    private static extern nint FindResource(nint module, nint name, nint type);

    [DllImport("kernel32.dll")]
    private static extern nint LoadResource(nint module, nint resource);

    [DllImport("kernel32.dll")]
    private static extern nint LockResource(nint data);

    [DllImport("kernel32.dll")]
    private static extern uint SizeofResource(nint module, nint resource);
}
