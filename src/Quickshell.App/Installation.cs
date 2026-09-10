using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using Microsoft.Win32;

namespace Quickshell.App;

/// <summary>
/// An installed copy of this client, and the three things that make it one: a folder of program
/// files, a shortcut in the Start menu, and the entry that lists it among the installed apps.
///
/// <para><b>An installer that does the least.</b> Nothing here registers a service, schedules a
/// task, claims a file type or writes a setting. Installing is copying a folder and writing two
/// small pointers at it, and uninstalling is taking those three away — which is why it is this
/// client's own code rather than an installer framework: there is nothing a framework would add
/// except a second program to trust.</para>
///
/// <para><b>For one person by default, with no administrator prompt</b>: under
/// <c>%LocalAppData%\Programs</c>, in that person's Start menu and under
/// <c>HKEY_CURRENT_USER</c>, which is where Windows puts an app installed for one user and all of
/// which is writable without elevation. The people this client is for are often at a machine they
/// cannot elevate on. <see cref="ForEveryone"/> is the same three things in the machine's places,
/// for a managed deployment that runs elevated anyway.</para>
///
/// <para><b>Settings are not part of it.</b> They live where <see cref="Locations"/> puts them, which
/// is never under the program folder, so an uninstall that removes every program file leaves them
/// as they were and a later install finds them.</para>
/// </summary>
public sealed class Installation
{
    /// <summary>The program's file name, in every copy.</summary>
    public const string ProgramName = "quickshell.exe";

    /// <summary>Where an installed app's entry lives, under either hive.</summary>
    public const string Entries = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\quickshell";

    /// <summary>What the Start menu shows when a pointer rests on the shortcut.</summary>
    private const string Description = "An SSH client for Windows";

    /// <summary>How many times a folder is renamed or removed before a refusal is believed.</summary>
    private const int Attempts = 50;

    /// <summary>How long between those tries: fifty of them are five seconds.</summary>
    private static readonly TimeSpan Pause = TimeSpan.FromMilliseconds(100);

    private readonly string _shortcuts;
    private readonly RegistryKey _hive;
    private readonly string _entry;

    /// <summary>
    /// An installation in the places given. The two factories are the real ones; a test gives its
    /// own, because an entry written under the real key is an app in somebody's list.
    /// </summary>
    public Installation(string folder, string shortcuts, RegistryKey hive, string entry, bool everyone)
    {
        ArgumentException.ThrowIfNullOrEmpty(folder);
        ArgumentException.ThrowIfNullOrEmpty(shortcuts);
        ArgumentNullException.ThrowIfNull(hive);
        ArgumentException.ThrowIfNullOrEmpty(entry);

        Folder = Full(folder);
        _shortcuts = shortcuts;
        _hive = hive;
        _entry = entry;
        Everyone = everyone;
    }

    /// <summary>For the person running this: no elevation, nothing outside their profile.</summary>
    public static Installation ForUser() => new(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                     "Programs", "quickshell"),
        Environment.GetFolderPath(Environment.SpecialFolder.Programs),
        Registry.CurrentUser, Entries, everyone: false);

    /// <summary>For every user of the machine, which only an administrator can write.</summary>
    public static Installation ForEveryone() => new(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "quickshell"),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
        Registry.LocalMachine, Entries, everyone: true);

    /// <summary>
    /// The installation a folder is, or null where it is not one — which is what a copy asks of
    /// its own folder to learn whether it is the installed copy or one run from somewhere else.
    /// </summary>
    public static Installation? Of(string folder) =>
        new[] { ForUser(), ForEveryone() }.FirstOrDefault(one => one.Holds(folder));

    /// <summary>The program files' folder.</summary>
    public string Folder { get; }

    /// <summary>The installed program.</summary>
    public string Program => Path.Combine(Folder, ProgramName);

    /// <summary>The Start menu's shortcut to it.</summary>
    public string Shortcut => Path.Combine(_shortcuts, "quickshell.lnk");

    /// <summary>Whether this is the machine-wide installation.</summary>
    public bool Everyone { get; }

    /// <summary>Whether a copy is installed here now.</summary>
    public bool Present => File.Exists(Program);

    /// <summary>Whether a folder is this installation's own.</summary>
    public bool Holds(string folder) => string.Equals(Full(folder), Folder, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Installs the copy in a folder, or replaces the one installed here with it.
    ///
    /// <para><b>The old copy is replaced whole or not at all.</b> The new files are written beside
    /// it first and swapped in by two renames, so an install that fails halfway — a full disk, a file
    /// somebody has open — leaves the copy that was there, rather than a folder of two versions that
    /// starts neither.</para>
    ///
    /// <para><b>A portable copy installs as an installed one</b>: its marker and its <c>data</c>
    /// folder stay where they are, so the copy it came from keeps working and the installed one keeps
    /// its settings where an installed copy does.</para>
    ///
    /// <para>Installing the copy that is already here writes nothing to the folder and rewrites the
    /// shortcut and the entry, which is what repairing either of those means.</para>
    /// </summary>
    /// <param name="from">The folder of the copy to install.</param>
    /// <param name="version">What the list of installed apps says this is.</param>
    /// <returns>The bytes the installed copy occupies.</returns>
    public long Install(string from, string version)
    {
        ArgumentException.ThrowIfNullOrEmpty(from);
        ArgumentException.ThrowIfNullOrEmpty(version);

        string source = Full(from);

        if (!File.Exists(Path.Combine(source, ProgramName)))
        {
            throw new SetupException($"There is no {ProgramName} in {source} to install.");
        }

        if (Folder.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new SetupException($"{source} contains {Folder}, so it cannot be installed there.");
        }

        long bytes;

        if (Holds(source))
        {
            bytes = Measure(Folder);
        }
        else
        {
            Refuse("install");

            bytes = Replace(source);
        }

        Directory.CreateDirectory(_shortcuts);

        // In the profile of whoever starts it and not in the program folder, which is where a shell
        // started in the installed copy's own directory would otherwise open.
        ShellLinks.Write(Shortcut, Program, "%USERPROFILE%", Description);

        using (RegistryKey entry = _hive.CreateSubKey(_entry, writable: true))
        {
            entry.SetValue("DisplayName", "quickshell");
            entry.SetValue("DisplayVersion", version);
            entry.SetValue("DisplayIcon", Program);
            entry.SetValue("InstallLocation", Folder);
            entry.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
            entry.SetValue("UninstallString", $"\"{Program}\" --uninstall");
            entry.SetValue("QuietUninstallString", $"\"{Program}\" --uninstall --quiet");
            entry.SetValue("URLInfoAbout", "https://github.com/alegauss/quickshell");
            entry.SetValue("EstimatedSize", (int)Math.Min(int.MaxValue, bytes / 1024), RegistryValueKind.DWord);
            entry.SetValue("NoModify", 1, RegistryValueKind.DWord);
            entry.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        }

        return bytes;
    }

    /// <summary>
    /// Removes the three things <see cref="Install"/> wrote, and nothing else.
    ///
    /// <para><b>A program cannot delete the file it is running from</b>, and the copy asked to
    /// uninstall itself is usually this one — it is what the list of installed apps runs. So where
    /// this process is the installed copy, the folder is handed to a process that outlives it and
    /// removes it once nothing holds it open.</para>
    /// </summary>
    /// <returns>True where the folder is gone now; false where it goes once this process ends.</returns>
    public bool Uninstall()
    {
        Refuse("uninstall");

        if (File.Exists(Shortcut))
        {
            File.Delete(Shortcut);
        }

        _hive.DeleteSubKeyTree(_entry, throwOnMissingSubKey: false);

        // What an install that stopped between its two renames can leave.
        Leave(Folder + ".new");
        Leave(Folder + ".old");

        if (!Directory.Exists(Folder))
        {
            return true;
        }

        if (Holds(AppContext.BaseDirectory))
        {
            RemoveWhenFree(Folder);

            return false;
        }

        Discard(Folder);

        return true;
    }

    /// <summary>
    /// Removes a folder once nothing holds it open, from a process that does not need this one.
    ///
    /// <para>cmd, because it is on every machine this runs on and needs nothing the folder holds:
    /// it tries once a second for two minutes, and stops the moment the folder is gone. Started in
    /// the system folder, since a process whose working directory is the folder being removed is
    /// itself one more thing holding it open.</para>
    /// </summary>
    public static void RemoveWhenFree(string folder)
    {
        // The one character cmd reads as its own inside quotes. A folder named with one is left for
        // a person to delete, rather than handed to a command that would read it as something else.
        if (folder.Contains('%', StringComparison.Ordinal))
        {
            throw new SetupException($"{folder} could not be removed, because its name has a % in it. Delete it once quickshell has closed.");
        }

        string quoted = $"\"{folder}\"";
        string gone = $"\"{folder}\\\"";

        ProcessStartInfo remover = new(
            Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            $"/d /q /v:off /c for /l %i in (1,1,120) do @(rd /s /q {quoted} 2>nul & "
            + $"if exist {gone} (ping -n 2 127.0.0.1 >nul) else (exit /b 0))")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Environment.SystemDirectory,
        };

        using Process? started = Process.Start(remover);
    }

    /// <summary>
    /// Refuses where another process is running the program from this folder.
    ///
    /// <para>Files a process has open cannot be replaced or removed, and finding that out halfway
    /// through is how an install leaves two versions in one folder. Asked first, it is one sentence
    /// saying what to close.</para>
    /// </summary>
    private void Refuse(string doing)
    {
        int running = Running();

        if (running > 0)
        {
            throw new SetupException(running == 1
                ? $"quickshell is running from {Folder}. Close it, and {doing} again."
                : $"{running} copies of quickshell are running from {Folder}. Close them, and {doing} again.");
        }
    }

    /// <summary>The processes other than this one running a program from this folder.</summary>
    private int Running()
    {
        int running = 0;

        foreach (Process process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(ProgramName)))
        {
            using (process)
            {
                try
                {
                    if (process.Id != Environment.ProcessId
                        && process.MainModule?.FileName is { } path
                        && path.StartsWith(Folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    {
                        running++;
                    }
                }
                catch (Win32Exception)
                {
                    // Another user's, which this process may not ask about. Its files are still in
                    // use, and the copy that meets them says so.
                }
                catch (InvalidOperationException)
                {
                    // Gone between the list and the question, which is the answer wanted anyway.
                }
            }
        }

        return running;
    }

    /// <summary>
    /// Writes the copy beside the installed one and swaps it in, putting the old one back where the
    /// swap fails.
    /// </summary>
    private long Replace(string source)
    {
        string staging = Folder + ".new";
        string previous = Folder + ".old";

        Discard(staging);

        long bytes = Copy(source, staging);

        try
        {
            Discard(previous);

            if (Directory.Exists(Folder))
            {
                Patiently(() => Directory.Move(Folder, previous), Folder, "replaced");
            }

            Patiently(() => Directory.Move(staging, Folder), Folder, "replaced");
        }
        catch (Exception)
        {
            if (!Directory.Exists(Folder) && Directory.Exists(previous))
            {
                Patiently(() => Directory.Move(previous, Folder), Folder, $"put back from {previous}");
            }

            Leave(staging);

            throw;
        }

        Leave(previous);

        return bytes;
    }

    /// <summary>Every file of a copy, less what makes a copy portable.</summary>
    private static long Copy(string source, string target)
    {
        long bytes = 0;

        Directory.CreateDirectory(target);

        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(source, file);

            if (Portable(relative))
            {
                continue;
            }

            string destination = Path.Combine(target, relative);

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);

            bytes += new FileInfo(destination).Length;
        }

        return bytes;
    }

    /// <summary>
    /// Whether a file belongs to a portable copy rather than to the program: the marker that makes it
    /// portable, and the data folder beside it.
    /// </summary>
    private static bool Portable(string relative) =>
        relative.Equals(Locations.Marker, StringComparison.OrdinalIgnoreCase)
        || relative.StartsWith("data" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static long Measure(string folder) =>
        Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                 .Sum(file => new FileInfo(file).Length);

    private static void Discard(string folder)
    {
        if (Directory.Exists(folder))
        {
            Patiently(() => Directory.Delete(folder, recursive: true), folder, "removed");
        }
    }

    /// <summary>
    /// Discards a folder nothing runs from, where it can. What stays is one the next install or
    /// uninstall removes, and not a reason to fail the one in progress.
    /// </summary>
    private static void Leave(string folder)
    {
        try
        {
            Discard(folder);
        }
        catch (SetupException)
        {
            // Something has kept a file in it open for the whole wait; the next install or
            // uninstall tries again.
        }
    }

    /// <summary>
    /// Renames or removes a folder, trying again for five seconds where something has a file in it
    /// open (QS194).
    ///
    /// <para><b>Something usually has.</b> Any handle open on a file inside a folder stops the folder
    /// being renamed, whatever its share mode, and a moment after a copy there nearly always is one:
    /// the virus scanner opens every executable it sees written, the indexer follows it, Explorer
    /// reads icons. All of them let go within a second or two, so a failure is believed only once it
    /// has lasted longer than that — and is then said as what it is, rather than as a denied path.</para>
    /// </summary>
    private static void Patiently(Action change, string folder, string doing)
    {
        Exception? held = null;

        for (int attempt = 0; attempt < Attempts; attempt++)
        {
            if (attempt > 0)
            {
                Thread.Sleep(Pause);
            }

            try
            {
                change();

                return;
            }
            catch (Exception busy) when (busy is IOException or UnauthorizedAccessException)
            {
                held = busy;
            }
        }

        throw new SetupException(
            $"{folder} could not be {doing}: something has kept a file in it open for "
            + $"{Attempts * Pause.TotalSeconds:0} seconds. Close whatever is using it, and try again.",
            held!);
    }

    private static string Full(string folder) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
}

/// <summary>
/// An install or uninstall that did not happen, and the sentence saying why — written for the
/// person who asked, since it is shown to them as it is.
/// </summary>
public sealed class SetupException : Exception
{
    /// <summary>A refusal saying why.</summary>
    public SetupException(string message)
        : base(message)
    {
    }

    /// <summary>A refusal saying why, with what caused it.</summary>
    public SetupException(string message, Exception inner)
        : base(message, inner)
    {
    }

    /// <summary>A refusal with no sentence, which the analyzers ask every exception to have.</summary>
    public SetupException()
    {
    }
}
