using System.IO;
using System.Text.Json;

namespace Quickshell.App;

/// <summary>
/// What the settings file holds, plus every key this build did not recognise.
///
/// <para><b>The unrecognised keys are the important field.</b> A user runs a newer build on their
/// desktop and an older one on a laptop from the same synced folder; the older build reads a file
/// with keys it has never heard of, and if it drops them on save it has just silently deleted the
/// newer machine's settings. Keeping them costs a dictionary and removes a whole class of complaint
/// nobody would ever diagnose.</para>
/// </summary>
public sealed record Settings
{
    /// <summary>The schema this build writes.</summary>
    public const int Schema = 1;

    /// <summary>What a build with no settings file uses.</summary>
    public static Settings Default { get; } = new();

    /// <summary>Which schema the file carried. Written back as <see cref="Schema"/>.</summary>
    public int SchemaVersion { get; init; } = Schema;

    /// <summary>Which way the chrome is painted.</summary>
    public ChromeTheme Theme { get; init; } = ChromeTheme.System;

    /// <summary>The terminal's typeface.</summary>
    public string FontFamily { get; init; } = "Cascadia Mono";

    /// <summary>Its point size.</summary>
    public double FontSize { get; init; } = 12;

    /// <summary>How many lines of scrollback a session keeps.</summary>
    public int Scrollback { get; init; } = 10_000;

    /// <summary>
    /// Every key this build did not recognise, kept exactly as it was read so it can be written back
    /// unchanged.
    /// </summary>
    public IReadOnlyDictionary<string, JsonElement> Unrecognised { get; init; } =
        new Dictionary<string, JsonElement>(StringComparer.Ordinal);
}

/// <summary>
/// The settings file: text, hand-editable, versioned from the first release, and forgiving of a
/// build that is not this one.
///
/// <para><b>A schema version from the first release, because there is no way to add one later.</b>
/// The alternative is discovering at version 1.4 that no key can be renamed without breaking every
/// installation in existence — a file with no version is a file whose format is frozen by the first
/// person who ran it.</para>
///
/// <para><b>Migration is forward-only and runs on load</b>, and a backup of the original is written
/// beside it before anything is touched. Forward-only because a client that could write an older
/// schema would have to know what a future one meant; a backup because the first migration this
/// project gets wrong will be discovered by a user, not by a test.</para>
///
/// <para><b>A file this cannot read is not a file this overwrites.</b> Unreadable settings load as
/// the defaults and the original is left where it is — saving over it would destroy the one copy of
/// whatever the user had typed, which for a hand-editable file is the likeliest thing to have
/// happened.</para>
/// </summary>
public static class SettingsFile
{
    /// <summary>The property the schema is written under.</summary>
    private const string Version = "schema";

    /// <summary>
    /// The keys this build knows. Everything else in the file is kept and written back untouched.
    /// </summary>
    private static readonly string[] Known =
    [
        Version, "theme", "fontFamily", "fontSize", "scrollback",
    ];

    /// <summary>
    /// Reads the file, migrating it forward where it is older than this build.
    /// </summary>
    /// <param name="path">The file. A missing one is the defaults and is not an error.</param>
    /// <returns>The settings, and whatever keys this build did not recognise.</returns>
    public static Settings ReadFrom(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!File.Exists(path))
        {
            return Settings.Default;
        }

        JsonElement root;

        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(path));

            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return Settings.Default;
            }

            root = document.RootElement.Clone();
        }
        catch (Exception)
        {
            // Left where it is. A file that will not parse is usually one somebody was editing, and
            // the worst possible response is to replace it with the defaults.
            return Settings.Default;
        }

        int was = root.TryGetProperty(Version, out JsonElement stamped)
                  && stamped.ValueKind == JsonValueKind.Number
                      ? stamped.GetInt32()
                      : 0;

        if (was < Settings.Schema)
        {
            Backup(path, was);
        }

        return Migrated(Read(root, was), was);
    }

    /// <summary>
    /// Writes the file, stamped with this build's schema and carrying back every key it did not
    /// recognise.
    /// </summary>
    public static void WriteTo(string path, Settings settings)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(settings);

        if (Path.GetDirectoryName(path) is { Length: > 0 } directory)
        {
            Directory.CreateDirectory(directory);
        }

        using MemoryStream into = new();

        using (Utf8JsonWriter writer = new(into, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();

            // First, so a person opening the file sees what it is before they see what is in it.
            writer.WriteNumber(Version, Settings.Schema);

            writer.WriteString("theme", settings.Theme.ToString());
            writer.WriteString("fontFamily", settings.FontFamily);
            writer.WriteNumber("fontSize", settings.FontSize);
            writer.WriteNumber("scrollback", settings.Scrollback);

            foreach ((string name, JsonElement value) in settings.Unrecognised)
            {
                // Written back exactly as it was read. This build has no idea what it means, which
                // is precisely why it is not this build's to discard.
                writer.WritePropertyName(name);
                value.WriteTo(writer);
            }

            writer.WriteEndObject();
        }

        File.WriteAllBytes(path, into.ToArray());
    }

    /// <summary>The file as this build reads it, with everything else set aside.</summary>
    private static Settings Read(JsonElement root, int schema)
    {
        Dictionary<string, JsonElement> unrecognised = new(StringComparer.Ordinal);

        foreach (JsonProperty property in root.EnumerateObject())
        {
            if (!Known.Contains(property.Name, StringComparer.Ordinal))
            {
                unrecognised[property.Name] = property.Value.Clone();
            }
        }

        return new Settings
        {
            SchemaVersion = schema,
            Theme = Theme(root),
            FontFamily = Text(root, "fontFamily") ?? Settings.Default.FontFamily,
            FontSize = Number(root, "fontSize") ?? Settings.Default.FontSize,
            Scrollback = (int?)Number(root, "scrollback") ?? Settings.Default.Scrollback,
            Unrecognised = unrecognised,
        };
    }

    /// <summary>
    /// Every step from the file's schema up to this build's, in order.
    ///
    /// <para><b>0 is a file written before there were versions</b>, which is the one migration this
    /// project already has: nothing in it moves, and it gains a stamp. The list is here so the
    /// second one has somewhere to go, and so the first person to need it does not also have to
    /// invent the backup and the ordering under time pressure.</para>
    /// </summary>
    private static Settings Migrated(Settings settings, int from)
    {
        Settings carried = settings;

        for (int step = from; step < Settings.Schema; step++)
        {
            carried = step switch
            {
                // 0 → 1: an unstamped file. Every key it had is already read above, and the only
                // change is that it now says which format it is.
                0 => carried with { SchemaVersion = 1 },

                _ => carried,
            };
        }

        return carried with { SchemaVersion = Settings.Schema };
    }

    /// <summary>
    /// A copy of the original, before a migration touches it.
    ///
    /// <para>Named for the schema it was, so a user who has to go back knows which file to take —
    /// and never overwritten, because the second run of a broken migration would otherwise back up
    /// the damage over the original.</para>
    /// </summary>
    private static void Backup(string path, int was)
    {
        try
        {
            string copy = $"{path}.v{was}.backup";

            if (!File.Exists(copy))
            {
                File.Copy(path, copy);
            }
        }
        catch (Exception)
        {
            // A backup that could not be written is not a reason to refuse to start. The migration
            // below is forward-only and additive, which is what makes this survivable.
        }
    }

    private static ChromeTheme Theme(JsonElement root) =>
        Text(root, "theme") is { } named && Enum.TryParse(named, ignoreCase: true, out ChromeTheme theme)
            ? theme
            : Settings.Default.Theme;

    private static string? Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static double? Number(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : null;
}
