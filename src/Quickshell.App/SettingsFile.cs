using System.Globalization;
using System.IO;
using System.Text.Json;
using Quickshell.Terminal;

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
    /// Whether the font's ligatures are formed.
    ///
    /// <para>On the list of things worth exposing for one reason and it is not taste in typography:
    /// users are sincerely divided about what <c>!=</c> should look like, and neither answer is
    /// wrong enough to decide for them.</para>
    /// </summary>
    public bool Ligatures { get; init; } = true;

    /// <summary>What the cursor is drawn as.</summary>
    public CursorShape Cursor { get; init; } = CursorShape.Block;

    /// <summary>Whether it blinks. Off is what a window with no clock in it sleeps on.</summary>
    public bool CursorBlink { get; init; } = true;

    /// <summary>
    /// Whether a paste carrying a newline is shown before it is sent.
    ///
    /// <para>Exposed because it is host-dependent rather than a matter of taste: a program that has
    /// turned bracketed paste on is deciding for itself and this never fires, and somebody who works
    /// entirely inside such programs is being asked a question that is already answered. Turning it
    /// off is a decision about the hosts you use, and the reference says what it costs.</para>
    /// </summary>
    public bool WarnOnPaste { get; init; } = true;

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
    ///
    /// <para><b>Public because it is what the reference is checked against.</b> QS50's falsification
    /// is that a setting exists which no reference documents, and the only way to hold that is for
    /// something to compare this list against <c>docs/SETTINGS.md</c> — which a test does, in both
    /// directions, so a key added here without a paragraph fails and a paragraph about a key that
    /// does not exist fails too.</para>
    /// </summary>
    public static readonly string[] Known =
    [
        Version, "theme", "fontFamily", "fontSize", "scrollback", "ligatures", "cursor",
        "cursorBlink", "warnOnPaste",
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
            // Comments and a trailing comma are allowed, because this is a file a person edits and
            // the reference invites them to write notes in it. A reader that refused them would
            // answer with the defaults for a file whose every value the user had set — silently,
            // which is the worst way for a settings file to be wrong.
            using JsonDocument document = JsonDocument.Parse(
                File.ReadAllBytes(path),
                new JsonDocumentOptions
                {
                    CommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true,
                });

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
    ///
    /// <para><b>An existing file is edited rather than rewritten, and QS50 is why.</b> The design's
    /// sentence is that a settings surface writes the file back preserving comments or is not worth
    /// having: a user who wrote a note beside a setting and lost it the first time they moved a
    /// slider has learnt that the file is the client's and not theirs. So the values this build
    /// knows are spliced into the bytes that are there, and every comment, every blank line and
    /// every choice of spacing survives untouched.</para>
    ///
    /// <para>A file that is not there, or is not something this can edit, is written whole. That is
    /// the only path that invents formatting, and there is nothing of the user's in it to lose.
    /// </para>
    /// </summary>
    public static void WriteTo(string path, Settings settings)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(settings);

        if (Path.GetDirectoryName(path) is { Length: > 0 } directory)
        {
            Directory.CreateDirectory(directory);
        }

        if (File.Exists(path) && Spliced(File.ReadAllText(path), settings) is { } edited)
        {
            File.WriteAllText(path, edited);

            return;
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
            writer.WriteBoolean("ligatures", settings.Ligatures);
            writer.WriteString("cursor", settings.Cursor.ToString());
            writer.WriteBoolean("cursorBlink", settings.CursorBlink);
            writer.WriteBoolean("warnOnPaste", settings.WarnOnPaste);

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

    /// <summary>
    /// The file with this build's values put back where they already were, or null where it could
    /// not be edited and has to be written whole.
    ///
    /// <para><b>Byte positions and not a rewrite.</b> <see cref="Utf8JsonReader"/> gives the span of
    /// every value it reads, so a value can be replaced where it sits and everything around it —
    /// comments, blank lines, the user's own spacing, and any key this build has never heard of —
    /// is carried through untouched because it is never looked at.</para>
    ///
    /// <para>Only keys already present are replaced. Adding one would mean inventing where it goes
    /// and how it is indented, which is the client deciding what somebody's file looks like; a
    /// setting the file does not mention is a setting at its default, and writing it down would say
    /// otherwise.</para>
    /// </summary>
    private static string? Spliced(string text, Settings settings)
    {
        Dictionary<string, string> writing = new(StringComparer.Ordinal)
        {
            [Version] = Settings.Schema.ToString(CultureInfo.InvariantCulture),
            ["theme"] = Quoted(settings.Theme.ToString()),
            ["fontFamily"] = Quoted(settings.FontFamily),
            ["fontSize"] = settings.FontSize.ToString(CultureInfo.InvariantCulture),
            ["scrollback"] = settings.Scrollback.ToString(CultureInfo.InvariantCulture),
            ["ligatures"] = settings.Ligatures ? "true" : "false",
            ["cursor"] = Quoted(settings.Cursor.ToString()),
            ["cursorBlink"] = settings.CursorBlink ? "true" : "false",
            ["warnOnPaste"] = settings.WarnOnPaste ? "true" : "false",
        };

        List<(int At, int Length, string Value)> edits = [];

        try
        {
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(text);
            Utf8JsonReader reader = new(bytes, new JsonReaderOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });

            string? name = null;
            int depth = 0;

            while (reader.Read())
            {
                switch (reader.TokenType)
                {
                    case JsonTokenType.StartObject or JsonTokenType.StartArray:
                        depth++;
                        break;

                    case JsonTokenType.EndObject or JsonTokenType.EndArray:
                        depth--;
                        break;

                    case JsonTokenType.PropertyName:
                        name = depth == 1 ? reader.GetString() : null;
                        break;

                    default:
                        if (name is not null && writing.Remove(name, out string? value))
                        {
                            // The span DOES include the quotes on a string, which is why the
                            // replacements above carry their own.
                            int at = (int)reader.TokenStartIndex;
                            int end = (int)(reader.BytesConsumed);

                            edits.Add((at, end - at, value));
                        }

                        name = null;
                        break;
                }
            }
        }
        catch (JsonException)
        {
            // Not something this can edit. The caller writes it whole, which is also what it does
            // for a file that was never there.
            return null;
        }

        if (edits.Count == 0)
        {
            return null;
        }

        // Backwards, so an earlier edit's positions are still the ones that were measured.
        System.Text.StringBuilder edited = new(text);

        foreach ((int at, int length, string value) in edits.OrderByDescending(one => one.At))
        {
            edited.Remove(at, length).Insert(at, value);
        }

        return edited.ToString();
    }

    /// <summary>A JSON string, with what a settings value can carry escaped.</summary>
    private static string Quoted(string value) => JsonSerializer.Serialize(value);

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
            Ligatures = Yes(root, "ligatures") ?? Settings.Default.Ligatures,
            Cursor = Shape(root) ?? Settings.Default.Cursor,
            CursorBlink = Yes(root, "cursorBlink") ?? Settings.Default.CursorBlink,
            WarnOnPaste = Yes(root, "warnOnPaste") ?? Settings.Default.WarnOnPaste,
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

    /// <summary>A flag, or null where the file did not carry one this build could read.</summary>
    private static bool? Yes(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement value)
        && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;

    /// <summary>The cursor's shape by name, or null where it is not one this build draws.</summary>
    private static CursorShape? Shape(JsonElement root) =>
        Text(root, "cursor") is { } named
        && Enum.TryParse(named, ignoreCase: true, out CursorShape shape)
        && shape != CursorShape.None
            ? shape
            : null;

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
