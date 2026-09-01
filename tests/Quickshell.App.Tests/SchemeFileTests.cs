using System.IO;
using Quickshell.App;
using Quickshell.Terminal;
using Xunit;

namespace Quickshell.App.Tests;

/// <summary>
/// Reading the two formats that already circulate, which is the whole of QS51's argument: nobody
/// types nineteen colours, so the client that reads the file somebody already has is the one they
/// keep.
///
/// <para><b>The samples here are real fragments and not invented ones.</b> A reader tested against
/// a file its own author wrote is a reader tested against its own assumptions — the two cases that
/// actually bite are a property list whose keys are in a different order and a Windows Terminal
/// fragment that spells magenta the other way.</para>
/// </summary>
public sealed class SchemeFileTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"quickshell-scheme-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    // ---- iTerm2 ----

    /// <summary>An iTerm2 property list, with its components as sRGB floats.</summary>
    private const string ITerm = """
        <?xml version="1.0" encoding="UTF-8"?>
        <plist version="1.0">
        <dict>
          <key>Ansi 0 Color</key>
          <dict>
            <key>Blue Component</key><real>0.0</real>
            <key>Green Component</key><real>0.0</real>
            <key>Red Component</key><real>0.0</real>
          </dict>
          <key>Ansi 1 Color</key>
          <dict>
            <key>Color Space</key><string>sRGB</string>
            <key>Red Component</key><real>1.0</real>
            <key>Green Component</key><real>0.0</real>
            <key>Blue Component</key><real>0.0</real>
          </dict>
          <key>Background Color</key>
          <dict>
            <key>Red Component</key><real>0.0</real>
            <key>Green Component</key><real>0.16862745285034180</real>
            <key>Blue Component</key><real>0.21176470816135406</real>
          </dict>
          <key>Foreground Color</key>
          <dict>
            <key>Red Component</key><real>0.51372551918029785</real>
            <key>Green Component</key><real>0.58039218187332153</real>
            <key>Blue Component</key><real>0.58823531866073608</real>
          </dict>
        </dict>
        </plist>
        """;

    /// <summary>The components are read whatever order the file happens to list them in.</summary>
    [Fact]
    public void AnITermPropertyListIsRead()
    {
        ColourScheme scheme = Assert.IsType<ColourScheme>(SchemeFile.Parse(ITerm, "solarized"));

        Assert.Equal("solarized", scheme.Name);
        Assert.Equal(new Rgb(0, 0, 0), scheme.Palette[0]);
        Assert.Equal(new Rgb(255, 0, 0), scheme.Palette[1]);
        Assert.Equal(new Rgb(0, 43, 54), scheme.Background);
        Assert.Equal(new Rgb(131, 148, 150), scheme.Foreground);
    }

    /// <summary>
    /// A file with no cursor in it gets the foreground, which is the rule and not a guess.
    ///
    /// <para>The foreground has to be legible against the background, so a cursor taking it is
    /// legible too. Picking anything else would be inventing a colour nobody chose.</para>
    /// </summary>
    [Fact]
    public void AnOmittedCursorBecomesTheForeground()
    {
        ColourScheme scheme = Assert.IsType<ColourScheme>(SchemeFile.Parse(ITerm));

        Assert.Equal(scheme.Foreground, scheme.Cursor);
    }

    /// <summary>
    /// A colour in a space this does not convert from is left at the default rather than misread.
    ///
    /// <para>Reading wide-gamut components as sRGB would show the user colours nobody chose, with
    /// nothing to blame — worse than declining the entry.</para>
    /// </summary>
    [Fact]
    public void AColourInASpaceThisCannotConvertIsNotGuessedAt()
    {
        string wide = ITerm.Replace("<string>sRGB</string>",
                                    "<string>Display P3</string>", StringComparison.Ordinal);

        ColourScheme scheme = Assert.IsType<ColourScheme>(SchemeFile.Parse(wide));

        // Entry 1 was the P3 one; entry 0 beside it was not, and is still read.
        Assert.Equal(ColourScheme.Default.Palette[1], scheme.Palette[1]);
        Assert.Equal(new Rgb(0, 0, 0), scheme.Palette[0]);
    }

    // ---- Windows Terminal ----

    /// <summary>A Windows Terminal fragment, of the shape its settings file holds.</summary>
    private const string Windows = """
        {
          "name": "Campbell",
          "background": "#0C0C0C",
          "foreground": "#CCCCCC",
          "black": "#0C0C0C",
          "red": "#C50F1F",
          "green": "#13A10E",
          "yellow": "#C19C00",
          "blue": "#0037DA",
          "purple": "#881798",
          "cyan": "#3A96DD",
          "white": "#CCCCCC",
          "brightBlack": "#767676",
          "brightRed": "#E74856",
          "brightGreen": "#16C60C",
          "brightYellow": "#F9F1A5",
          "brightBlue": "#3B78FF",
          "brightPurple": "#B4009E",
          "brightCyan": "#61D6D6",
          "brightWhite": "#F2F2F2",
          "cursorColor": "#FFFFFF",
          "selectionBackground": "#FFFFFF"
        }
        """;

    /// <summary>The fragment is read, and the file's own name outranks the file's name.</summary>
    [Fact]
    public void AWindowsTerminalFragmentIsRead()
    {
        ColourScheme scheme = Assert.IsType<ColourScheme>(SchemeFile.Parse(Windows, "whatever"));

        Assert.Equal("Campbell", scheme.Name);
        Assert.Equal(new Rgb(0x0C, 0x0C, 0x0C), scheme.Background);
        Assert.Equal(new Rgb(0xCC, 0xCC, 0xCC), scheme.Foreground);
        Assert.Equal(new Rgb(0xC5, 0x0F, 0x1F), scheme.Palette[1]);
        Assert.Equal(new Rgb(0x88, 0x17, 0x98), scheme.Palette[5]);
        Assert.Equal(new Rgb(0xB4, 0x00, 0x9E), scheme.Palette[13]);
        Assert.Equal(Rgb.White, scheme.Cursor);
    }

    /// <summary>
    /// Magenta is read where Microsoft's own schemes say purple.
    ///
    /// <para>Every terminal other than Windows Terminal calls it magenta, so a scheme converted from
    /// anywhere else uses that name — and refusing it would be refusing the file for being right.
    /// </para>
    /// </summary>
    [Fact]
    public void MagentaIsTheSameColourAsPurple()
    {
        string other = Windows.Replace("\"purple\"", "\"magenta\"", StringComparison.Ordinal)
                              .Replace("\"brightPurple\"", "\"brightMagenta\"",
                                       StringComparison.Ordinal);

        ColourScheme scheme = Assert.IsType<ColourScheme>(SchemeFile.Parse(other));

        Assert.Equal(new Rgb(0x88, 0x17, 0x98), scheme.Palette[5]);
        Assert.Equal(new Rgb(0xB4, 0x00, 0x9E), scheme.Palette[13]);
    }

    /// <summary>The selection colour both formats carry is not read, because nothing paints it.</summary>
    [Fact]
    public void TheSelectionColourIsNotSomethingThisClientTakes()
    {
        ColourScheme scheme = Assert.IsType<ColourScheme>(SchemeFile.Parse(Windows));

        // Nothing in the scheme is the file's white selection except the cursor, which the file set
        // separately. A selected cell has its two colours swapped instead.
        Assert.Equal(new Rgb(0x0C, 0x0C, 0x0C), scheme.Background);
        Assert.Equal(new Rgb(0xCC, 0xCC, 0xCC), scheme.Foreground);
    }

    /// <summary>Three-digit hex, which hand-written fragments use.</summary>
    [Fact]
    public void ShortHexIsRead()
    {
        ColourScheme scheme = Assert.IsType<ColourScheme>(
            SchemeFile.Parse("""{ "background": "#036", "foreground": "#FFF" }"""));

        Assert.Equal(new Rgb(0x00, 0x33, 0x66), scheme.Background);
        Assert.Equal(Rgb.White, scheme.Foreground);
    }

    // ---- Neither, and nothing ----

    /// <summary>Valid JSON with no colour in it is not a scheme, and is not read as the defaults.</summary>
    [Fact]
    public void JsonThatIsNotASchemeIsNotOne()
    {
        Assert.Null(SchemeFile.Parse("""{ "fontSize": 12 }"""));
    }

    /// <summary>Anything else is null rather than an exception, because the caller is start-up.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("not a file")]
    [InlineData("<plist><dict></dict></plist>")]
    public void SomethingThatIsNeitherFormatIsNull(string text)
    {
        Assert.Null(SchemeFile.Parse(text));
    }

    /// <summary>A file that will not parse is null, and the caller keeps the scheme it has.</summary>
    [Fact]
    public void AFileThatWillNotParseIsNull()
    {
        Directory.CreateDirectory(_directory);

        string path = Path.Combine(_directory, "broken.json");

        File.WriteAllText(path, "{ \"background\": ");

        Assert.Null(SchemeFile.ReadFrom(path));
    }

    /// <summary>A path that leads nowhere is null and never an error.</summary>
    [Fact]
    public void AMissingFileIsNull()
    {
        Assert.Null(SchemeFile.ReadFrom(Path.Combine(_directory, "nothing.itermcolors")));
    }

    /// <summary>The file names the scheme where the fragment does not name itself.</summary>
    [Fact]
    public void AFileWithoutANameIsCalledAfterItself()
    {
        Directory.CreateDirectory(_directory);

        string path = Path.Combine(_directory, "Gruvbox Dark.itermcolors");

        File.WriteAllText(path, ITerm);

        ColourScheme scheme = Assert.IsType<ColourScheme>(SchemeFile.ReadFrom(path));

        Assert.Equal("Gruvbox Dark", scheme.Name);
    }

    /// <summary>
    /// A fragment saved under any name at all is read, because the format is told by its contents.
    ///
    /// <para>Windows Terminal fragments are pasted out of a settings file and saved under whatever
    /// somebody typed, which is the commonest way one arrives.</para>
    /// </summary>
    [Fact]
    public void TheFormatIsToldByTheContentsAndNotTheExtension()
    {
        Directory.CreateDirectory(_directory);

        string path = Path.Combine(_directory, "campbell.itermcolors");

        File.WriteAllText(path, Windows);

        Assert.Equal("Campbell", Assert.IsType<ColourScheme>(SchemeFile.ReadFrom(path)).Name);
    }

    // ---- Through the settings file, which is how a user reaches it ----

    /// <summary>
    /// The settings key names a scheme beside the settings file, and a relative path is relative to
    /// it.
    ///
    /// <para>That is the arrangement that survives being cloned onto another machine, which is the
    /// point of the settings file being text somebody can commit.</para>
    /// </summary>
    [Fact]
    public void TheSettingsKeyReadsASchemeBesideTheSettingsFile()
    {
        Directory.CreateDirectory(_directory);

        File.WriteAllText(Path.Combine(_directory, "campbell.json"), Windows);
        File.WriteAllText(Path.Combine(_directory, "settings.json"),
                          """{ "colourScheme": "campbell.json" }""");

        Settings read = SettingsFile.ReadFrom(Path.Combine(_directory, "settings.json"));

        Assert.Equal("campbell.json", read.Scheme);
        Assert.Equal("Campbell", read.Colours.Name);
        Assert.Equal(new Rgb(0x0C, 0x0C, 0x0C), read.Colours.Background);
    }

    /// <summary>A path that leads nowhere is the built-in scheme and never a refusal to start.</summary>
    [Fact]
    public void ASchemeThatIsNotThereIsTheBuiltInOne()
    {
        Directory.CreateDirectory(_directory);

        File.WriteAllText(Path.Combine(_directory, "settings.json"),
                          """{ "colourScheme": "typo.json" }""");

        Settings read = SettingsFile.ReadFrom(Path.Combine(_directory, "settings.json"));

        // The path is kept as the user wrote it, so saving the file back does not silently correct
        // their typo into nothing.
        Assert.Equal("typo.json", read.Scheme);
        Assert.Equal(ColourScheme.Default.Background, read.Colours.Background);
    }

    /// <summary>The path survives a write, along with everything else in the file.</summary>
    [Fact]
    public void TheSchemePathIsWrittenBack()
    {
        Directory.CreateDirectory(_directory);

        string path = Path.Combine(_directory, "settings.json");

        File.WriteAllText(path, """
            {
              // Mine, and I want it kept.
              "colourScheme": "schemes/gruvbox.itermcolors"
            }
            """);

        Settings read = SettingsFile.ReadFrom(path);

        Assert.Equal("schemes/gruvbox.itermcolors", read.Scheme);

        SettingsFile.WriteTo(path, read with { Scheme = "schemes/nord.json" });

        string after = File.ReadAllText(path);

        Assert.Contains("\"colourScheme\": \"schemes/nord.json\"", after, StringComparison.Ordinal);
        Assert.Contains("// Mine, and I want it kept.", after, StringComparison.Ordinal);
    }
}
