using System.Diagnostics;
using System.IO;
using Microsoft.Win32;
using Quickshell.App;
using Xunit;

namespace Quickshell.App.Tests;

/// <summary>
/// QS77: a copy installed for one user without an administrator, and taken away again.
///
/// <para><b>Against the real things, in places of the test's own.</b> The folder is a real folder,
/// the shortcut is written and read back through the shell's own object, the entry is a real
/// registry key and a copy that is running is a real process. Only the places are the test's — a
/// temporary folder and a key under a name of its own — since an entry written under the real key
/// is an app in the list of whoever runs the suite.</para>
/// </summary>
public sealed class InstallationTests : IDisposable
{
    private const string Tests = @"Software\quickshell-tests";

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"qs77-{Guid.NewGuid():N}");
    private readonly string _entry = $@"{Tests}\{Guid.NewGuid():N}";

    /// <summary>
    /// A portable copy installs as an installed one: every program file is copied, its marker and
    /// its settings are not, and the Start menu and the list of installed apps point at the result.
    /// </summary>
    [Fact]
    public void AnInstallCopiesTheProgramAndPointsTheStartMenuAndTheAppListAtIt()
    {
        string download = Copy("download",
                               ("quickshell.exe", "program"),
                               (@"runtimes\library.dll", "library"),
                               (Locations.Marker, string.Empty),
                               (@"data\settings.json", "{}"));

        Installation installed = At();

        long bytes = installed.Install(download, "9.8.7");

        Assert.Equal("program", File.ReadAllText(installed.Program));
        Assert.Equal("library", File.ReadAllText(Path.Combine(installed.Folder, "runtimes", "library.dll")));
        Assert.Equal("program".Length + "library".Length, bytes);

        Assert.False(File.Exists(Path.Combine(installed.Folder, Locations.Marker)), "the installed copy was made portable");
        Assert.False(Directory.Exists(Path.Combine(installed.Folder, "data")), "a portable copy's settings were installed with it");
        Assert.True(File.Exists(Path.Combine(download, "data", "settings.json")), "the copy installed from lost its settings");

        Assert.Equal(installed.Program, ShellLinks.Target(installed.Shortcut), ignoreCase: true);

        using RegistryKey? entry = Registry.CurrentUser.OpenSubKey(_entry);

        Assert.NotNull(entry);
        Assert.Equal("quickshell", entry.GetValue("DisplayName"));
        Assert.Equal("9.8.7", entry.GetValue("DisplayVersion"));
        Assert.Equal(installed.Folder, entry.GetValue("InstallLocation"));
        Assert.Equal($"\"{installed.Program}\" --uninstall", entry.GetValue("UninstallString"));
        Assert.Equal($"\"{installed.Program}\" --uninstall --quiet", entry.GetValue("QuietUninstallString"));
        Assert.Equal(1, entry.GetValue("NoModify"));
        Assert.Equal((int)(bytes / 1024), entry.GetValue("EstimatedSize"));
    }

    /// <summary>
    /// Uninstalling takes away the three things installing wrote, and nothing it did not write.
    /// </summary>
    [Fact]
    public void AnUninstallTakesAwayTheFolderTheShortcutAndTheEntry()
    {
        string download = Copy("download", ("quickshell.exe", "program"), (@"data\sessions.json", "[]"));
        Installation installed = At();

        installed.Install(download, "1.0.0");

        Assert.True(installed.Uninstall(), "a copy this process was not running from was left to remove later");

        Assert.False(Directory.Exists(installed.Folder), "the program folder stayed");
        Assert.False(File.Exists(installed.Shortcut), "the Start menu still points at it");
        Assert.Null(Registry.CurrentUser.OpenSubKey(_entry));
        Assert.True(File.Exists(Path.Combine(download, "data", "sessions.json")), "the uninstall reached the copy it was installed from");
    }

    /// <summary>
    /// A newer copy replaces the installed one whole: a file only the old version had goes with it,
    /// and nothing of the swap is left beside the folder.
    /// </summary>
    [Fact]
    public void AnInstallOverAnOlderCopyReplacesItWhole()
    {
        Installation installed = At();

        installed.Install(Copy("first", ("quickshell.exe", "first"), ("retired.dll", "old")), "1.0.0");
        installed.Install(Copy("second", ("quickshell.exe", "second"), ("added.dll", "new")), "2.0.0");

        Assert.Equal("second", File.ReadAllText(installed.Program));
        Assert.False(File.Exists(Path.Combine(installed.Folder, "retired.dll")), "a file of the old version stayed");
        Assert.True(File.Exists(Path.Combine(installed.Folder, "added.dll")), "a file of the new version is missing");
        Assert.False(Directory.Exists(installed.Folder + ".new"), "the new copy's staging folder stayed");
        Assert.False(Directory.Exists(installed.Folder + ".old"), "the old copy stayed beside the new one");

        using RegistryKey? entry = Registry.CurrentUser.OpenSubKey(_entry);

        Assert.Equal("2.0.0", entry?.GetValue("DisplayVersion"));
    }

    /// <summary>
    /// Installing the copy that is already installed repairs its shortcut and its entry, and leaves
    /// its files alone.
    /// </summary>
    [Fact]
    public void InstallingTheInstalledCopyRepairsItsShortcutAndItsEntry()
    {
        Installation installed = At();

        installed.Install(Copy("download", ("quickshell.exe", "program")), "1.0.0");

        File.Delete(installed.Shortcut);
        Registry.CurrentUser.DeleteSubKeyTree(_entry);

        installed.Install(installed.Folder, "1.0.0");

        Assert.Equal("program", File.ReadAllText(installed.Program));
        Assert.Equal(installed.Program, ShellLinks.Target(installed.Shortcut), ignoreCase: true);
        Assert.NotNull(Registry.CurrentUser.OpenSubKey(_entry));
    }

    /// <summary>
    /// A copy that is running from the folder is named rather than half-replaced: install and
    /// uninstall both refuse, and a refused uninstall has removed nothing.
    ///
    /// <para>The running copy is a real process called <c>quickshell</c> — ping, renamed, so it has
    /// something to do for long enough to be found.</para>
    /// </summary>
    [Fact]
    public void ACopyRunningFromTheFolderIsNamedRatherThanHalfReplaced()
    {
        string download = Path.Combine(_root, "download");

        Directory.CreateDirectory(download);
        File.Copy(Path.Combine(Environment.SystemDirectory, "PING.EXE"), Path.Combine(download, "quickshell.exe"));

        Installation installed = At();

        installed.Install(download, "1.0.0");

        using Process running = Hold(installed.Program, seconds: 60);

        try
        {
            SetupException install = Assert.Throws<SetupException>(() => installed.Install(download, "1.0.1"));
            SetupException uninstall = Assert.Throws<SetupException>(() => installed.Uninstall());

            Assert.Equal($"quickshell is running from {installed.Folder}. Close it, and install again.", install.Message);
            Assert.Equal($"quickshell is running from {installed.Folder}. Close it, and uninstall again.", uninstall.Message);

            Assert.True(File.Exists(installed.Shortcut), "a refused uninstall removed the shortcut anyway");
            Assert.NotNull(Registry.CurrentUser.OpenSubKey(_entry));
        }
        finally
        {
            running.Kill();
            running.WaitForExit();
        }
    }

    /// <summary>
    /// The folder a copy uninstalls itself from goes once nothing holds it — here, the moment the
    /// program running from it exits, which it does after the call and not before.
    /// </summary>
    [Fact]
    public void AFolderHeldByARunningProgramIsRemovedOnceItExits()
    {
        string folder = Path.Combine(_root, "held");

        Directory.CreateDirectory(folder);
        File.Copy(Path.Combine(Environment.SystemDirectory, "PING.EXE"), Path.Combine(folder, "held.exe"));
        File.WriteAllText(Path.Combine(folder, "library.dll"), "library");

        using Process holding = Hold(Path.Combine(folder, "held.exe"), seconds: 3);

        Installation.RemoveWhenFree(folder);

        Assert.True(Directory.Exists(folder), "the folder went while a program was still running from it");

        Assert.True(holding.WaitForExit(TimeSpan.FromSeconds(30)), "the holding program never exited");

        Stopwatch waited = Stopwatch.StartNew();

        while (Directory.Exists(folder) && waited.Elapsed < TimeSpan.FromSeconds(15))
        {
            Thread.Sleep(100);
        }

        Assert.False(Directory.Exists(folder), "the folder outlived the program holding it by fifteen seconds");
    }

    /// <summary>A folder cmd would read as something other than a path is refused, not guessed at.</summary>
    [Fact]
    public void AFolderNamedWithAPercentIsLeftForAPerson() =>
        Assert.Throws<SetupException>(() => Installation.RemoveWhenFree(@"C:\nowhere\100%\quickshell"));

    /// <summary>
    /// The palette offers the install only where there is something to install from, and offering it
    /// is asking whoever composed the window.
    /// </summary>
    [Fact]
    public void ThePaletteOffersTheInstallOnlyWhereThereIsACopyToInstall()
    {
        const string Install = "Install quickshell for this user";

        (bool before, bool after, int asked) = OnStaThread(() =>
        {
            MainWindow window = new();

            bool before = window.Actions.Any(one => one.Name == Install);

            int asked = 0;

            window.Installs = () => asked++;

            Command offered = window.Actions.Single(one => one.Name == Install);

            offered.Run();

            return (before, true, asked);
        });

        Assert.False(before, "an installed copy offered to install itself");
        Assert.True(after);
        Assert.Equal(1, asked);
    }

    /// <summary>The two real installations are told apart by their folders, and nothing else is either.</summary>
    [Fact]
    public void AFolderIsTheInstallationItHolds()
    {
        Assert.False(Installation.Of(Installation.ForUser().Folder)?.Everyone ?? true);
        Assert.True(Installation.Of(Installation.ForEveryone().Folder + @"\")?.Everyone ?? false);
        Assert.Null(Installation.Of(_root));
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        Registry.CurrentUser.DeleteSubKeyTree(_entry, throwOnMissingSubKey: false);

        using (RegistryKey? tests = Registry.CurrentUser.OpenSubKey(Tests))
        {
            if (tests is { SubKeyCount: 0, ValueCount: 0 })
            {
                Registry.CurrentUser.DeleteSubKey(Tests, throwOnMissingSubKey: false);
            }
        }

        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    /// <summary>An installation of the test's own, under its temporary folder and its key.</summary>
    private Installation At() =>
        new(Path.Combine(_root, "Programs", "quickshell"), Path.Combine(_root, "Start Menu"),
            Registry.CurrentUser, _entry, everyone: false);

    /// <summary>A copy of the client, as a folder of the files named.</summary>
    private string Copy(string name, params (string Path, string Content)[] files)
    {
        string folder = Path.Combine(_root, name);

        foreach ((string path, string content) in files)
        {
            string full = Path.Combine(folder, path);

            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }

        return folder;
    }

    /// <summary>A program running from a file for about as many seconds as asked, holding it open.</summary>
    private static Process Hold(string program, int seconds) =>
        Process.Start(new ProcessStartInfo(program, $"-n {seconds} 127.0.0.1")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        }) ?? throw new InvalidOperationException($"{program} did not start");

    /// <summary>Runs work on an STA thread, which is the only kind a WPF window can be built on.</summary>
    private static T OnStaThread<T>(Func<T> work)
    {
        T result = default!;
        Exception? failed = null;

        Thread thread = new(() =>
        {
            try
            {
                result = work();
            }
            catch (Exception error)
            {
                failed = error;
            }
            finally
            {
                System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();

        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "the STA thread never finished");

        if (failed is not null)
        {
            throw new InvalidOperationException("the window could not be built", failed);
        }

        return result;
    }
}
