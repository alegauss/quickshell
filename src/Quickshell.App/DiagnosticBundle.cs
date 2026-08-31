using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using Quickshell.Render;

namespace Quickshell.App;

/// <summary>Where the things a bundle gathers live.</summary>
/// <param name="Config">Session files and settings.</param>
/// <param name="Logs">The session log's folder.</param>
/// <param name="Crashes">Where <see cref="CrashReport"/> writes.</param>
/// <param name="Recordings">Where <see cref="SessionRecording"/> writes.</param>
public sealed record DiagnosticSources(string Config, string Logs, string Crashes, string Recordings)
{
    /// <summary>The four folders this client actually uses.</summary>
    public static DiagnosticSources Default()
    {
        string data = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "quickshell");

        return new DiagnosticSources(data, Path.Combine(data, "logs"),
                                     Path.Combine(data, "crashes"),
                                     Path.Combine(data, "recordings"));
    }
}

/// <summary>
/// One action that produces everything a maintainer will ask for.
///
/// <para><b>A defect report costs several round trips because the first message never carries what
/// is needed</b> — the version, the machine, the graphics adapter, the server, the settings, the
/// log. Each of those is a day. This collects them into one file.</para>
///
/// <para><b>One file, and it is a text file on purpose.</b> The user is meant to read it before
/// deciding to send it, and an archive is something people send unread. That is the same reasoning
/// as the crash path and the reason this is not a button that uploads: nothing here is automatic and
/// nothing is sent.</para>
///
/// <para><b>Configuration is included by being rewritten, never by being copied.</b> Every value is
/// dropped whose property name is one a secret could hide behind — a post-login command most of all,
/// since it is free text a user may have typed a <c>sudo</c> password into. A file that cannot be
/// parsed is named and left out entirely, which is the safe direction to fail in: a redaction that
/// silently did not apply is worse than a section that is missing.</para>
///
/// <para><b>It names the graphics adapter, which a crash report cannot.</b> This runs while the
/// client is healthy, so it can walk the adapter chain and ask — where the crash path has to write
/// what it already knows and nothing more.</para>
/// </summary>
public static class DiagnosticBundle
{
    /// <summary>How many lines of the session log ride along.</summary>
    private const int TailLines = 500;

    /// <summary>Property names whose values never appear in a bundle.</summary>
    private static readonly string[] Sensitive =
    [
        "postlogin", "password", "passphrase", "secret", "token", "credentialvalue",
    ];

    /// <summary>Where bundles go.</summary>
    public static string Folder() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                     "quickshell", "diagnostics");

    /// <summary>
    /// Gathers everything and writes one file, returning where it went.
    /// </summary>
    /// <param name="folder">Where the bundle goes.</param>
    /// <param name="from">Where to gather from.</param>
    /// <param name="when">The moment, for the file's name and its first line.</param>
    /// <param name="adapter">
    /// What the graphics chain says. Null asks DXGI, which is what a running client wants; a test
    /// passes its own so the answer does not depend on the machine.
    /// </param>
    public static string WriteTo(string folder, DiagnosticSources from, DateTimeOffset when,
                                 string? adapter = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        ArgumentNullException.ThrowIfNull(from);

        Directory.CreateDirectory(folder);

        string path = Path.Combine(
            folder,
            $"quickshell-diagnostics-{when.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}.txt");

        File.WriteAllText(path, Compose(from, when, adapter ?? Graphics()));

        return path;
    }

    /// <summary>The bundle's text, which is the whole of what it is.</summary>
    public static string Compose(DiagnosticSources from, DateTimeOffset when, string adapter)
    {
        ArgumentNullException.ThrowIfNull(from);

        StringBuilder bundle = new();

        bundle.AppendLine("quickshell diagnostics")
              .AppendLine()
              .AppendLine("Read this before sending it anywhere. It was written by your own client,")
              .AppendLine("it has not been sent to anybody, and nothing here will send it.")
              .AppendLine()
              .AppendLine("Passwords, passphrases and key material are not in it: the session log")
              .AppendLine("cannot hold one, and every settings value that could hide one has been")
              .AppendLine("removed rather than copied.")
              .AppendLine();

        Heading(bundle, "the machine and the build");

        bundle.AppendLine(Field("when", when.ToString("yyyy-MM-dd HH:mm:ss 'UTC'",
                                                      CultureInfo.InvariantCulture)))
              .AppendLine(Field("version", CrashContext.Build()))
              .AppendLine(Field("windows", Environment.OSVersion.VersionString))
              .AppendLine(Field("64-bit process", Environment.Is64BitProcess.ToString()))
              .AppendLine(Field("processors", Environment.ProcessorCount.ToString(CultureInfo.InvariantCulture)))
              // Named rather than left blank: the invariant culture has an empty name, and a blank
              // field reads as "nobody looked" where this means "the process has no locale".
              .AppendLine(Field("culture", CultureInfo.CurrentCulture.Name is { Length: > 0 } named
                                               ? named
                                               : "invariant"))
              .AppendLine(Field("graphics", adapter))
              .AppendLine();

        Heading(bundle, "settings, with everything that could hide a secret removed");

        bundle.AppendLine(Configuration(from.Config)).AppendLine();

        Heading(bundle, "crash reports");

        bundle.AppendLine(Crashes(from.Crashes)).AppendLine();

        Heading(bundle, "session recordings");

        bundle.AppendLine(Recordings(from.Recordings)).AppendLine();

        Heading(bundle, $"the session log, last {TailLines.ToString(CultureInfo.InvariantCulture)} lines");

        bundle.AppendLine(Log(from.Logs));

        return bundle.ToString();
    }

    /// <summary>What the adapter chain would choose, asked without opening a device.</summary>
    private static string Graphics()
    {
        try
        {
            return AdapterChain.Choose(new DxgiAdapterProbe(), nint.Zero).ToString();
        }
        catch (Exception failure)
        {
            // A machine whose DXGI will not answer is itself worth reporting, and it is not a reason
            // to lose the rest of the bundle.
            return $"could not be asked: {failure.Message}";
        }
    }

    private static void Heading(StringBuilder bundle, string what) =>
        bundle.AppendLine($"---- {what} ----").AppendLine();

    private static string Field(string name, string value) => $"{name}: {value}";

    /// <summary>
    /// Every JSON file in the settings folder, rewritten with the sensitive values dropped.
    /// </summary>
    private static string Configuration(string folder)
    {
        if (!Directory.Exists(folder))
        {
            return "no settings folder.";
        }

        StringBuilder settings = new();
        bool any = false;

        foreach (string file in Directory.EnumerateFiles(folder, "*.json")
                                         .OrderBy(file => file, StringComparer.Ordinal))
        {
            any = true;

            settings.AppendLine($"# {Path.GetFileName(file)}");

            try
            {
                using JsonDocument document = JsonDocument.Parse(File.ReadAllText(file));

                settings.AppendLine(Redacted(document.RootElement));
            }
            catch (Exception failure)
            {
                // Named, and left out. A file this could not parse is a file it could not redact,
                // and copying it anyway is exactly the mistake this section exists to avoid.
                settings.AppendLine($"  left out — it could not be read as settings: {failure.Message}");
            }

            settings.AppendLine();
        }

        return any ? settings.ToString().TrimEnd() : "no settings files.";
    }

    /// <summary>
    /// The same document with every sensitive value replaced, written back as JSON.
    ///
    /// <para>Rewritten rather than filtered with a search: a search finds what somebody remembered
    /// to look for, and a rewrite decides about every property there is.</para>
    /// </summary>
    private static string Redacted(JsonElement element)
    {
        using MemoryStream into = new();

        using (Utf8JsonWriter writer = new(into, new JsonWriterOptions { Indented = true }))
        {
            Write(writer, element, name: null);
        }

        return Encoding.UTF8.GetString(into.ToArray());
    }

    private static void Write(Utf8JsonWriter writer, JsonElement element, string? name)
    {
        if (name is not null && Sensitive.Contains(name.ToLowerInvariant()))
        {
            writer.WriteString(name, "(removed)");

            return;
        }

        if (name is not null)
        {
            writer.WritePropertyName(name);
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();

                foreach (JsonProperty property in element.EnumerateObject())
                {
                    Write(writer, property.Value, property.Name);
                }

                writer.WriteEndObject();

                break;

            case JsonValueKind.Array:
                writer.WriteStartArray();

                foreach (JsonElement item in element.EnumerateArray())
                {
                    Write(writer, item, name: null);
                }

                writer.WriteEndArray();

                break;

            default:
                element.WriteTo(writer);

                break;
        }
    }

    /// <summary>Every crash report, and the newest one whole.</summary>
    private static string Crashes(string folder)
    {
        if (!Directory.Exists(folder))
        {
            return "none.";
        }

        string[] reports = [.. Directory.EnumerateFiles(folder, "crash-*.txt")
                                        .OrderBy(file => file, StringComparer.Ordinal)];

        if (reports.Length == 0)
        {
            return "none.";
        }

        StringBuilder said = new();

        foreach (string report in reports)
        {
            said.AppendLine($"  {Path.GetFileName(report)}  "
                            + $"{new FileInfo(report).Length.ToString(CultureInfo.InvariantCulture)} bytes");
        }

        said.AppendLine().AppendLine($"# {Path.GetFileName(reports[^1])}, whole:").AppendLine();

        try
        {
            said.AppendLine(File.ReadAllText(reports[^1]));
        }
        catch (Exception failure)
        {
            said.AppendLine($"  it could not be read: {failure.Message}");
        }

        return said.ToString().TrimEnd();
    }

    /// <summary>
    /// The recordings, listed and never inlined: they are compressed bytes, and pasting one into a
    /// text file would make the bundle unreadable and the recording unusable at the same time.
    /// </summary>
    private static string Recordings(string folder)
    {
        if (!Directory.Exists(folder))
        {
            return "none.";
        }

        string[] recordings = [.. Directory.EnumerateFiles(folder, "*.raw.gz")
                                           .OrderBy(file => file, StringComparer.Ordinal)];

        if (recordings.Length == 0)
        {
            return "none.";
        }

        StringBuilder said = new();

        foreach (string recording in recordings)
        {
            said.AppendLine($"  {recording}  "
                            + $"{new FileInfo(recording).Length.ToString(CultureInfo.InvariantCulture)} bytes");
        }

        said.AppendLine()
            .AppendLine("These are session output as it arrived, gzipped. Send one only if the")
            .AppendLine("defect is something a maintainer has to see happen. They are the same")
            .AppendLine("shape as benchmarks/corpus/streams, so one becomes a regression test by")
            .AppendLine("being moved there.");

        return said.ToString().TrimEnd();
    }

    /// <summary>The end of the newest log file.</summary>
    private static string Log(string folder)
    {
        if (!Directory.Exists(folder))
        {
            return "no session log.";
        }

        string[] logs = [.. Directory.EnumerateFiles(folder, "*.log")
                                     .OrderBy(file => file, StringComparer.Ordinal)];

        if (logs.Length == 0)
        {
            return "no session log.";
        }

        try
        {
            // Shared, because the client that is writing it is the client asking for this.
            using FileStream reading = new(logs[^1], FileMode.Open, FileAccess.Read,
                                           FileShare.ReadWrite);
            using StreamReader text = new(reading);

            string[] lines = text.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);

            return string.Join('\n', lines.TakeLast(TailLines)).TrimEnd();
        }
        catch (Exception failure)
        {
            return $"it could not be read: {failure.Message}";
        }
    }
}
