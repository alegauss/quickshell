using System.IO;
using System.Reflection;

namespace Quickshell.App;

/// <summary>
/// Where every file this client owns lives, and which of the two layouts it is using.
///
/// <para><b>Portable mode is a marker file and nothing else.</b> A file called
/// <see cref="Marker"/> beside the executable moves the whole layout into <c>data\</c> next to it —
/// which is what the footprint argument means in practice: this client on a USB stick, leaving
/// nothing in a user profile it was never installed into. There is no setting for it, because a
/// setting for it would have to live somewhere, and where it lives is the question being
/// answered.</para>
///
/// <para><b>Otherwise it follows the platform.</b> <c>%AppData%\quickshell</c>, which is where a
/// Windows program's data goes and where a user's own backup already looks.</para>
///
/// <para><b>Discovered once and cached</b>, because the answer cannot change while the process runs
/// — a marker file created underneath a running client is not a layout change, it is a layout change
/// next time. The check is one <c>File.Exists</c> and it happens the first time somebody needs a
/// path, which is deliberately not during start-up: nothing on the way to the first paint touches a
/// disk.</para>
/// </summary>
public sealed class Locations
{
    /// <summary>The file whose presence beside the executable chooses portable mode.</summary>
    public const string Marker = "quickshell.portable";

    private static Locations? _current;

    private Locations(bool portable, string root, string means)
    {
        Portable = portable;
        Root = root;
        Means = means;
    }

    /// <summary>This process's layout, worked out the first time it is asked for.</summary>
    public static Locations Current => _current ??= Discover();

    /// <summary>Whether the portable layout is in use.</summary>
    public bool Portable { get; }

    /// <summary>The folder everything else is under.</summary>
    public string Root { get; }

    /// <summary>
    /// One sentence saying which layout this is and what chose it. Written into the diagnostics
    /// bundle, because "where are my settings" is a support question that should cost one line.
    /// </summary>
    public string Means { get; }

    /// <summary>The settings file.</summary>
    public string Settings => Path.Combine(Root, "settings.json");

    /// <summary>The saved sessions.</summary>
    public string Sessions => Path.Combine(Root, "sessions.json");

    /// <summary>Where the window was last time, per arrangement of screens.</summary>
    public string Windows => Path.Combine(Root, "windows.json");

    /// <summary>
    /// The marker that says this user asked not to be warned about closing open sessions.
    ///
    /// <para>Its presence is the whole answer, which is why it is a file rather than a settings key:
    /// a key lives in a file a user may hand-edit into something unreadable, and
    /// <see cref="SettingsFile"/> answers that by falling back to the defaults. Falling back to the
    /// default here would bring back a dialog somebody switched off, which is the one outcome
    /// <see cref="CloseGuard"/> exists to prevent.</para>
    /// </summary>
    public string CloseSilently => Path.Combine(Root, "close-without-asking");

    /// <summary>The session log's folder.</summary>
    public string Logs => Path.Combine(Root, "logs");

    /// <summary>Where crash reports go.</summary>
    public string Crashes => Path.Combine(Root, "crashes");

    /// <summary>Where session recordings go.</summary>
    public string Recordings => Path.Combine(Root, "recordings");

    /// <summary>Where diagnostic bundles go.</summary>
    public string Diagnostics => Path.Combine(Root, "diagnostics");

    /// <summary>
    /// Works out the layout.
    /// </summary>
    /// <param name="beside">
    /// Where to look for the marker. The executable's own folder unless a test says otherwise —
    /// and a test has to be able to, since a marker file cannot be dropped beside a test runner
    /// without changing what every later test sees.
    /// </param>
    public static Locations Discover(string? beside = null)
    {
        string here = beside ?? Beside();

        if (File.Exists(Path.Combine(here, Marker)))
        {
            return new Locations(portable: true, Path.Combine(here, "data"),
                                 $"portable: {Marker} is beside the executable, so everything is "
                                 + "under data\\ next to it and nothing is written to your profile");
        }

        return new Locations(
            portable: false,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                         "quickshell"),
            $"installed: there is no {Marker} beside the executable, so files follow the platform "
            + "into %AppData%\\quickshell");
    }

    /// <summary>
    /// The folder the executable is in.
    ///
    /// <para><see cref="AppContext.BaseDirectory"/> and not the assembly's own location, because a
    /// single-file build has no file to ask about — and that is exactly the build somebody puts on
    /// a USB stick.</para>
    /// </summary>
    private static string Beside() =>
        AppContext.BaseDirectory is { Length: > 0 } directory
            ? directory
            : Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? ".";
}
