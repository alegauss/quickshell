using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Xml.Linq;
using Quickshell.Terminal;

namespace Quickshell.App;

/// <summary>
/// Reads a colour scheme out of one of the two formats that already circulate.
///
/// <para><b>QS51: reading them is a small piece of work with a disproportionate effect.</b> Between
/// them the iTerm2 property list and the Windows Terminal JSON fragment cover very nearly everything
/// published, so a user arrives on the first evening with the scheme they already use instead of
/// approximating it.</para>
///
/// <para><b>The format is told by what is in the file, not by its extension.</b> Windows Terminal
/// fragments are pasted out of a settings file and saved under whatever name somebody typed, and a
/// reader that insisted on <c>.json</c> would refuse the commonest way one arrives.</para>
///
/// <para><b>An omission is derived by a stated rule, never guessed.</b> Both formats routinely leave
/// out the cursor, and the rule is that it becomes the foreground — the one answer that is legible
/// against the background by construction, since the foreground has to be. The rule is in
/// <c>docs/SETTINGS.md</c> because a rule a user cannot read is indistinguishable from a guess.
/// </para>
///
/// <para><b>A file this cannot read is null and never an exception.</b> The caller is start-up, and
/// a scheme somebody mistyped the path to is not a reason for a terminal not to open.</para>
/// </summary>
public static class SchemeFile
{
    /// <summary>
    /// Reads a scheme from a file, or null where there is nothing here this can read.
    /// </summary>
    /// <param name="path">The file. A missing one is null rather than an error.</param>
    public static ColourScheme? ReadFrom(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        try
        {
            return File.Exists(path)
                       ? Parse(File.ReadAllText(path), Path.GetFileNameWithoutExtension(path))
                       : null;
        }
        catch (Exception)
        {
            // Unreadable, locked, or not what it looked like. The caller keeps the scheme it has.
            return null;
        }
    }

    /// <summary>
    /// Reads a scheme out of text in either format, or null where it is neither.
    /// </summary>
    /// <param name="text">The file's contents.</param>
    /// <param name="named">What to call the scheme where the file does not name itself.</param>
    public static ColourScheme? Parse(string text, string named = "imported")
    {
        ArgumentNullException.ThrowIfNull(text);

        string trimmed = text.TrimStart('﻿', ' ', '\t', '\r', '\n');

        if (trimmed.StartsWith('<'))
        {
            return FromPropertyList(trimmed, named);
        }

        return trimmed.StartsWith('{') ? FromWindowsTerminal(trimmed, named) : null;
    }

    // ---- iTerm2: a property list of dictionaries of floating-point components ----

    /// <summary>
    /// The iTerm2 format: one dictionary per colour, each holding sRGB components from 0 to 1.
    ///
    /// <para>The components are floating point and the file states its colour space. Only sRGB is
    /// read; a scheme in a wide-gamut space would need converting, and converting it wrong is worse
    /// than declining it, because the user would see colours nobody chose and have nothing to blame.
    /// </para>
    /// </summary>
    private static ColourScheme? FromPropertyList(string text, string named)
    {
        XElement? dictionary = XDocument.Parse(text).Root?.Element("dict");

        if (dictionary is null)
        {
            return null;
        }

        Dictionary<string, Rgb> colours = new(StringComparer.OrdinalIgnoreCase);

        // <key>name</key> then the <dict> that follows it, which is how a property list pairs things.
        foreach (XElement key in dictionary.Elements("key"))
        {
            if (key.ElementsAfterSelf().FirstOrDefault() is { Name.LocalName: "dict" } value
                && Components(value) is { } colour)
            {
                colours[key.Value] = colour;
            }
        }

        if (colours.Count == 0)
        {
            return null;
        }

        Rgb[] entries = [.. ColourScheme.Default.Palette];

        for (int index = 0; index < ColourScheme.Entries; index++)
        {
            if (colours.TryGetValue(
                    $"Ansi {index.ToString(CultureInfo.InvariantCulture)} Color", out Rgb entry))
            {
                entries[index] = entry;
            }
        }

        Rgb foreground = colours.TryGetValue("Foreground Color", out Rgb ink)
                             ? ink
                             : ColourScheme.Default.Foreground;

        return new ColourScheme
        {
            Name = named,
            Palette = entries,
            Foreground = foreground,
            Background = colours.TryGetValue("Background Color", out Rgb ground)
                             ? ground
                             : ColourScheme.Default.Background,

            // The stated rule for an omission: the cursor is the foreground, which is legible
            // against the background by construction.
            Cursor = colours.TryGetValue("Cursor Color", out Rgb caret) ? caret : foreground,
        };
    }

    /// <summary>One colour's three components, or null where this is not an sRGB colour.</summary>
    private static Rgb? Components(XElement dictionary)
    {
        double? red = null;
        double? green = null;
        double? blue = null;
        bool wide = false;

        foreach (XElement key in dictionary.Elements("key"))
        {
            if (key.ElementsAfterSelf().FirstOrDefault() is not { } value)
            {
                continue;
            }

            switch (key.Value)
            {
                case "Red Component":
                    red = Real(value);
                    break;

                case "Green Component":
                    green = Real(value);
                    break;

                case "Blue Component":
                    blue = Real(value);
                    break;

                case "Color Space":
                    // Absent means sRGB, which is what every scheme published before iTerm2 grew
                    // the key is. Anything else stated is a space this does not convert from.
                    wide = !value.Value.Equals("sRGB", StringComparison.OrdinalIgnoreCase)
                           && !value.Value.Equals("Calibrated", StringComparison.OrdinalIgnoreCase);
                    break;

                default:
                    break;
            }
        }

        return !wide && red is { } r && green is { } g && blue is { } b
                   ? new Rgb(Channel(r), Channel(g), Channel(b))
                   : null;
    }

    /// <summary>A property list real, which is written in the invariant culture whatever the machine.</summary>
    private static double? Real(XElement element) =>
        double.TryParse(element.Value, NumberStyles.Float, CultureInfo.InvariantCulture,
                        out double value)
            ? value
            : null;

    /// <summary>Nought to one, as nought to 255, clamped because a file may say anything.</summary>
    private static byte Channel(double value) =>
        (byte)Math.Clamp(Math.Round(value * 255.0), 0, 255);

    // ---- Windows Terminal: a JSON fragment of hex strings ----

    /// <summary>
    /// The Windows Terminal format: flat JSON, one hex string per colour.
    ///
    /// <para><c>purple</c> is what Microsoft's own schemes call it and <c>magenta</c> is what every
    /// other terminal does; both are read, because a scheme copied from anywhere else uses the
    /// second and refusing it would be refusing the file for being right.</para>
    /// </summary>
    private static ColourScheme? FromWindowsTerminal(string text, string named)
    {
        using JsonDocument document = JsonDocument.Parse(
            text,
            new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });

        JsonElement root = document.RootElement;

        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        string[][] slots =
        [
            ["black"], ["red"], ["green"], ["yellow"], ["blue"], ["purple", "magenta"], ["cyan"],
            ["white"], ["brightBlack"], ["brightRed"], ["brightGreen"], ["brightYellow"],
            ["brightBlue"], ["brightPurple", "brightMagenta"], ["brightCyan"], ["brightWhite"],
        ];

        Rgb[] entries = [.. ColourScheme.Default.Palette];

        bool any = false;

        for (int index = 0; index < slots.Length; index++)
        {
            if (Hex(root, slots[index]) is { } entry)
            {
                entries[index] = entry;
                any = true;
            }
        }

        Rgb? foreground = Hex(root, ["foreground"]);
        Rgb? background = Hex(root, ["background"]);

        if (!any && foreground is null && background is null)
        {
            // Valid JSON with no colour in it is not a scheme. Reading it as one would hand the
            // user the defaults under the name of the file they chose.
            return null;
        }

        Rgb ink = foreground ?? ColourScheme.Default.Foreground;

        return new ColourScheme
        {
            Name = Text(root, "name") ?? named,
            Palette = entries,
            Foreground = ink,
            Background = background ?? ColourScheme.Default.Background,
            Cursor = Hex(root, ["cursorColor"]) ?? ink,
        };
    }

    /// <summary>The first of these properties that holds a colour this can read.</summary>
    private static Rgb? Hex(JsonElement root, string[] names)
    {
        foreach (string name in names)
        {
            if (Text(root, name) is { } written && Hex(written) is { } colour)
            {
                return colour;
            }
        }

        return null;
    }

    /// <summary><c>#RRGGBB</c>, or <c>#RGB</c>, which some hand-written fragments use.</summary>
    private static Rgb? Hex(string written)
    {
        ReadOnlySpan<char> digits = written.AsSpan().Trim().TrimStart('#');

        if (digits.Length == 3)
        {
            return Nibble(digits[0]) is { } r3 && Nibble(digits[1]) is { } g3
                   && Nibble(digits[2]) is { } b3
                       ? new Rgb((byte)(r3 * 17), (byte)(g3 * 17), (byte)(b3 * 17))
                       : null;
        }

        if (digits.Length != 6)
        {
            return null;
        }

        return byte.TryParse(digits[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture,
                             out byte red)
               && byte.TryParse(digits[2..4], NumberStyles.HexNumber, CultureInfo.InvariantCulture,
                                out byte green)
               && byte.TryParse(digits[4..], NumberStyles.HexNumber, CultureInfo.InvariantCulture,
                                out byte blue)
                   ? new Rgb(red, green, blue)
                   : null;
    }

    /// <summary>One hexadecimal digit's value, or null where it is not one.</summary>
    private static byte? Nibble(char digit) =>
        byte.TryParse([digit], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte value)
            ? value
            : null;

    private static string? Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
