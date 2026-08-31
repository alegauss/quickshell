using System.Globalization;
using System.IO;
using System.Text;

namespace Quickshell.App;

/// <summary>What ended the process, because the two are not the same failure and must not read
/// alike.</summary>
public enum CrashKind
{
    /// <summary>A defect in this client. The report is worth sending.</summary>
    Defect,

    /// <summary>
    /// The graphics device went away — a driver update, a reset, a GPU that hung.
    ///
    /// <para>Recoverable by construction: <c>GraphicsDevice.Recover</c> exists for exactly this and
    /// walks the adapter chain again. One that reached this far is one nothing recovered, which is
    /// still worth a report — but it is a report about a machine and not about a bug, and filing it
    /// as a defect is how a maintainer spends a week on somebody's driver update.</para>
    /// </summary>
    DeviceLost,
}

/// <summary>
/// What the client was doing when it stopped. Gathered at the moment of the failure rather than
/// held, because a field kept up to date all day is a field that is wrong in the one second it is
/// read.
/// </summary>
/// <param name="Version">Which build this was.</param>
/// <param name="Windows">The OS, since a graphics failure is usually about the machine.</param>
/// <param name="Adapter">The adapter chain's own account of what it opened on, or why there is none.</param>
/// <param name="Recoveries">How many device losses were already recovered from before this one.</param>
/// <param name="Sessions">How many connections were open. Four is a different report from none.</param>
/// <param name="Running">How long the process had been up.</param>
/// <param name="LogFiles">The session log's files, newest first, for the tail this report carries.</param>
public sealed record CrashContext(
    string Version,
    string Windows,
    string Adapter,
    int Recoveries,
    int Sessions,
    TimeSpan Running,
    IReadOnlyList<string> LogFiles)
{
    /// <summary>What can be known without asking anything that might itself be broken.</summary>
    public static CrashContext Minimal() =>
        new(Build(), Environment.OSVersion.VersionString, "no device was opened", 0, 0,
            TimeSpan.Zero, []);

    /// <summary>This build, as a string a report can be filed against.</summary>
    public static string Build() =>
        typeof(CrashContext).Assembly.GetName().Version?.ToString() ?? "unknown";
}

/// <summary>What the user is told, and where they can go to read it themselves.</summary>
/// <param name="Kind">Defect or device loss.</param>
/// <param name="Path">The report on disk, or empty where even writing it failed.</param>
/// <param name="Sentence">What to say, in the words a person reads at the worst moment.</param>
public sealed record CrashNotice(CrashKind Kind, string Path, string Sentence);

/// <summary>
/// The last second, and what is kept from it.
///
/// <para><b>A client that vanishes silently is worse than one that fails visibly</b>, and the whole
/// difference is a file and a sentence. This composes the file. Nothing here sends anything: the
/// report is written locally, the user is told where it is, and every step after that is an act they
/// perform themselves. That is the telemetry non-goal applied at the exact moment it is most
/// tempting to break — a crash is when a maintainer most wants the data and when a user is least
/// able to refuse.</para>
///
/// <para><b>Composition is separate from telling</b> on purpose: the text of a report can be
/// asserted, and a message box cannot. What follows is pure — an exception and a context in, a
/// string out — and <see cref="CrashGuard"/> is the part that touches the process.</para>
///
/// <para><b>On secrets.</b> The log tail this carries comes from <c>SessionLog</c> files, which
/// cannot contain a secret because nothing can hand one to that type. The exception's own message
/// and stack are written as they were thrown: this client composes none of them from a credential,
/// and a report that silently dropped part of a stack would be worse at the job it exists
/// for.</para>
/// </summary>
public static class CrashReport
{
    /// <summary>How many lines of the session log ride along.</summary>
    private const int TailLines = 200;

    /// <summary>How many reports are kept before the oldest goes.</summary>
    private const int Keep = 10;

    /// <summary>
    /// The DXGI results that mean the device went away rather than the client being wrong.
    ///
    /// <para>Matched on <see cref="Exception.HResult"/> rather than on the graphics library's own
    /// exception type, which keeps this assembly from naming it: the numbers are DXGI's and they
    /// outlive whichever wrapper threw. Removed, hung, reset, and the driver's own internal
    /// error.</para>
    /// </summary>
    private static readonly int[] DeviceGone =
    [
        unchecked((int)0x887A0005), unchecked((int)0x887A0006),
        unchecked((int)0x887A0007), unchecked((int)0x887A0020),
    ];

    /// <summary>Where reports go, beside the user's other data.</summary>
    public static string Folder() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                     "quickshell", "crashes");

    /// <summary>
    /// Which failure this is. The inner exceptions are read too, since a device loss arrives wrapped
    /// in whatever the layer above it threw.
    /// </summary>
    public static CrashKind Classify(Exception? failure)
    {
        for (Exception? each = failure; each is not null; each = each.InnerException)
        {
            if (DeviceGone.Contains(each.HResult))
            {
                return CrashKind.DeviceLost;
            }

            if (each is AggregateException aggregate
                && aggregate.InnerExceptions.Any(inner => Classify(inner) == CrashKind.DeviceLost))
            {
                return CrashKind.DeviceLost;
            }
        }

        return CrashKind.Defect;
    }

    /// <summary>
    /// The report itself.
    /// </summary>
    /// <param name="kind">Defect or device loss.</param>
    /// <param name="failure">What was thrown, with every inner exception under it.</param>
    /// <param name="what">The client's own state.</param>
    /// <param name="when">The moment, in UTC, so two reports from two machines can be ordered.</param>
    public static string Compose(CrashKind kind, Exception? failure, CrashContext what,
                                 DateTimeOffset when)
    {
        ArgumentNullException.ThrowIfNull(what);

        StringBuilder report = new();

        report.AppendLine(kind == CrashKind.DeviceLost
                              ? "quickshell stopped: the graphics device went away"
                              : "quickshell stopped: a defect in the client")
              .AppendLine(Line("when", when.ToString("yyyy-MM-dd HH:mm:ss.fff 'UTC'",
                                                     CultureInfo.InvariantCulture)))
              .AppendLine(Line("version", what.Version))
              .AppendLine(Line("windows", what.Windows))
              .AppendLine(Line("adapter", what.Adapter))
              .AppendLine(Line("device recoveries", what.Recoveries.ToString(CultureInfo.InvariantCulture)))
              .AppendLine(Line("sessions open", what.Sessions.ToString(CultureInfo.InvariantCulture)))
              .AppendLine(Line("running for", $"{what.Running.TotalSeconds:F0}s"))
              .AppendLine();

        report.AppendLine("---- what was thrown ----").AppendLine();

        if (failure is null)
        {
            // The CLR can hand over an unhandled event with something that is not an Exception at
            // all. Saying so beats a report that looks empty for no stated reason.
            report.AppendLine("nothing that is an exception — the runtime reported a failure of "
                              + "another kind");
        }

        for (Exception? each = failure; each is not null; each = each.InnerException)
        {
            report.AppendLine($"{each.GetType().FullName}: {each.Message}")
                  .AppendLine(each.StackTrace ?? "  (no stack)")
                  .AppendLine();
        }

        report.AppendLine("---- the session log, last lines ----").AppendLine();

        report.AppendLine(Tail(what.LogFiles));

        return report.ToString();
    }

    /// <summary>
    /// Writes the report and returns where it went, keeping the last few and dropping the rest.
    ///
    /// <para>Returns an empty string where it could not be written — a full disk, a folder that is
    /// not writable. A crash handler that throws while handling a crash is how a client loses the
    /// message as well as the report.</para>
    /// </summary>
    public static string WriteTo(string folder, string report, DateTimeOffset when)
    {
        try
        {
            Directory.CreateDirectory(folder);

            string path = Path.Combine(
                folder,
                $"crash-{when.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture)}.txt");

            File.WriteAllText(path, report);

            Prune(folder);

            return path;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// The sentence the user reads: what happened, where the report is, and that nothing was sent.
    /// </summary>
    public static string Say(CrashKind kind, string path, CrashContext what)
    {
        ArgumentNullException.ThrowIfNull(what);

        StringBuilder said = new();

        said.Append(kind == CrashKind.DeviceLost
                        ? "quickshell has stopped because the graphics device went away, which is "
                          + "usually a driver update or a reset."
                        : "quickshell has stopped because of a defect in it.");

        if (what.Sessions > 0)
        {
            said.Append(what.Sessions == 1
                            ? " One session was open."
                            : $" {what.Sessions.ToString(CultureInfo.InvariantCulture)} sessions were open.");
        }

        said.Append(path.Length == 0
                        ? " A report could not be written — there was nowhere to put it."
                        : $" What happened is written to {path}.");

        said.Append(" It holds no passwords and no key material, and nothing has been sent anywhere.");

        return said.ToString();
    }

    private static string Line(string name, string value) => $"{name}: {value}";

    /// <summary>The end of the newest log file, which is what the client was doing.</summary>
    private static string Tail(IReadOnlyList<string> files)
    {
        if (files.Count == 0)
        {
            return "no session log was open.";
        }

        try
        {
            // Shared, because the log is still open by the process that is dying.
            using FileStream reading = new(files[^1], FileMode.Open, FileAccess.Read,
                                           FileShare.ReadWrite);
            using StreamReader text = new(reading);

            string[] lines = text.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);

            return string.Join('\n', lines.TakeLast(TailLines)).TrimEnd();
        }
        catch (Exception failure)
        {
            return $"the session log could not be read: {failure.Message}";
        }
    }

    /// <summary>Drops the oldest reports, so a client that crashes in a loop is not a disk that fills.</summary>
    private static void Prune(string folder)
    {
        string[] all = [.. Directory.EnumerateFiles(folder, "crash-*.txt")
                                    .OrderByDescending(file => file, StringComparer.Ordinal)];

        foreach (string old in all.Skip(Keep))
        {
            try
            {
                File.Delete(old);
            }
            catch (Exception)
            {
                // One that will not delete is not a reason to lose the one just written.
            }
        }
    }
}
