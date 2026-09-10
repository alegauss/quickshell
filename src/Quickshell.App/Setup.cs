using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Windows;

namespace Quickshell.App;

/// <summary>
/// Installing and uninstalling as a person or a deployment asks for them: <c>--install</c>,
/// <c>--install --all-users</c> and <c>--uninstall</c>, each with <c>--quiet</c> for a run nobody is
/// watching, and the palette's install for somebody who started a copy by double-clicking it.
///
/// <para><b>The same executable, and not a second one called setup.</b> The archive this client
/// ships in is already a working copy, so installing it is something that copy does to itself, and
/// the list of installed apps runs <c>--uninstall</c> on the installed copy — which puts removing it
/// through the same code that put it there.</para>
///
/// <para><b>Every answer is a dialog, or an exit code under <c>--quiet</c></b>: this is a windowed
/// program, and a line written to a console would reach nobody. Zero is done, 740 is the Windows code
/// for needing an administrator, and one is anything else that did not happen.</para>
/// </summary>
public static class Setup
{
    /// <summary>ERROR_ELEVATION_REQUIRED, which deployment tools already know how to read.</summary>
    public const int NeedsAdministrator = 740;

    private const string Caption = "quickshell";

    /// <summary>Whether the palette's install is copying. Read and written on the window's thread alone.</summary>
    private static bool _installing;

    /// <summary>This build's version as the list of installed apps shows it: three numbers.</summary>
    public static string Version =>
        typeof(Setup).Assembly.GetName().Version is { } version
            ? $"{version.Major}.{version.Minor}.{version.Build}"
            : "0.0.0";

    /// <summary>Whether a command line asks for setup rather than a window.</summary>
    public static bool Asked(string[] arguments) =>
        arguments.Contains("--install", StringComparer.Ordinal)
        || arguments.Contains("--uninstall", StringComparer.Ordinal);

    /// <summary>
    /// Does what the command line asked, and says how it went.
    /// </summary>
    /// <returns>The process's exit code.</returns>
    public static int Run(string[] arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        bool quiet = arguments.Contains("--quiet", StringComparer.Ordinal);
        bool everyone = arguments.Contains("--all-users", StringComparer.Ordinal);
        bool uninstalling = arguments.Contains("--uninstall", StringComparer.Ordinal);

        // An installed copy uninstalls itself, which is what the list of installed apps asks of it.
        // Any other copy uninstalls the installation the flags name.
        Installation target = (uninstalling ? Installation.Of(AppContext.BaseDirectory) : null)
                              ?? (everyone ? Installation.ForEveryone() : Installation.ForUser());

        if (target.Everyone && !Elevated())
        {
            return quiet ? NeedsAdministrator : Elevate(uninstalling, quiet);
        }

        try
        {
            return uninstalling ? Uninstall(target, quiet) : Install(target, quiet);
        }
        catch (Exception failure) when (failure is SetupException or IOException
                                            or UnauthorizedAccessException or COMException)
        {
            Tell(quiet, Failed(uninstalling, failure), MessageBoxImage.Warning);

            return 1;
        }
    }

    /// <summary>
    /// Installs the running copy for the person using it, from the palette, and says how it went.
    ///
    /// <para>The copying happens off the window's thread — a copy is a hundred and sixty megabytes,
    /// and the window it was asked from goes on drawing while it happens.</para>
    /// </summary>
    public static async Task InstallForUserAsync(Window owner)
    {
        ArgumentNullException.ThrowIfNull(owner);

        // One at a time. Nothing is on screen while the copy runs, so a second choice from the palette
        // is somebody who thinks the first did nothing — and two installs into one staging folder
        // would each delete what the other was writing.
        if (_installing)
        {
            return;
        }

        _installing = true;

        Installation target = Installation.ForUser();
        string said;
        MessageBoxImage image;

        try
        {
            long bytes = await Task.Run(() => target.Install(AppContext.BaseDirectory, Version)).ConfigureAwait(true);

            said = Installed(target, bytes);
            image = MessageBoxImage.Information;
        }
        catch (Exception failure) when (failure is SetupException or IOException
                                            or UnauthorizedAccessException or COMException)
        {
            said = Failed(uninstalling: false, failure);
            image = MessageBoxImage.Warning;
        }
        finally
        {
            _installing = false;
        }

        MessageBox.Show(owner, said, Caption, MessageBoxButton.OK, image);
    }

    private static int Install(Installation target, bool quiet)
    {
        long bytes = target.Install(AppContext.BaseDirectory, Version);

        Tell(quiet, Installed(target, bytes), MessageBoxImage.Information);

        return 0;
    }

    /// <summary>
    /// Uninstalls, and only then asks about the settings — so a refusal is said before a question
    /// that would have been wasted on it.
    ///
    /// <para>Kept unless the answer is yes, and never asked under <c>--quiet</c>: a deployment that
    /// removes the program has no business deciding what happens to somebody's saved sessions.</para>
    /// </summary>
    private static int Uninstall(Installation target, bool quiet)
    {
        target.Uninstall();

        // The installed copy's own, which is never a portable copy's data folder — whichever copy
        // was asked to do the uninstalling.
        string settings = Locations.Discover(target.Folder).Root;

        if (quiet || !Directory.Exists(settings))
        {
            Tell(quiet, "quickshell is removed.", MessageBoxImage.Information);

            return 0;
        }

        MessageBoxResult answer = MessageBox.Show(
            $"quickshell is removed.\n\nYour settings and saved sessions are still in {settings}, and a "
            + "later install finds them there.\n\nRemove them as well?",
            Caption, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);

        if (answer == MessageBoxResult.Yes)
        {
            Directory.Delete(settings, recursive: true);
        }

        return 0;
    }

    /// <summary>What a finished install says.</summary>
    private static string Installed(Installation target, long bytes)
    {
        string whom = target.Everyone ? "for every user of this computer" : "for you";
        string portable = Locations.Current.Portable
            ? "\n\nThe copy you ran it from is unchanged and keeps its own settings in its data folder."
            : string.Empty;

        return $"quickshell {Version} is installed {whom}, in {target.Folder} ({bytes / (1024 * 1024)} MB), "
               + $"and it is in the Start menu. Installed apps, in Settings, is where it is removed.{portable}";
    }

    /// <summary>What an install or uninstall that did not happen says.</summary>
    private static string Failed(bool uninstalling, Exception failure)
    {
        if (failure is SetupException)
        {
            return failure.Message;
        }

        string doing = uninstalling ? "removed" : "installed";

        return $"quickshell could not be {doing}: {failure.Message}";
    }

    private static void Tell(bool quiet, string message, MessageBoxImage image)
    {
        if (!quiet)
        {
            MessageBox.Show(message, Caption, MessageBoxButton.OK, image);
        }
    }

    /// <summary>
    /// Whether this process holds an administrator's token — not whether its user could get one,
    /// which is the question User Account Control makes different.
    /// </summary>
    private static bool Elevated()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();

        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>
    /// Asks Windows to run the same request as an administrator, and waits for its answer.
    ///
    /// <para>This is how the list of installed apps removes a copy installed for every user: it runs
    /// the entry's command as whoever clicked, and the prompt is where that person becomes somebody
    /// allowed to.</para>
    /// </summary>
    private static int Elevate(bool uninstalling, bool quiet)
    {
        ProcessStartInfo asking = new(Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, Installation.ProgramName))
        {
            Arguments = (uninstalling ? "--uninstall" : "--install") + " --all-users" + (quiet ? " --quiet" : string.Empty),
            UseShellExecute = true,
            Verb = "runas",
        };

        try
        {
            using Process? elevated = Process.Start(asking);

            if (elevated is null)
            {
                return 1;
            }

            elevated.WaitForExit();

            return elevated.ExitCode;
        }
        catch (Win32Exception)
        {
            // The prompt was declined, which is an answer rather than a failure to report.
            return NeedsAdministrator;
        }
    }
}
