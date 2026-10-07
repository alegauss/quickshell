using System.IO;
using System.Text.RegularExpressions;
using System.Windows.Controls;
using System.Windows.Input;
using Quickshell.App;
using Xunit;

namespace Quickshell.App.Tests;

/// <summary>
/// QS170: a window over the settings file, with the file still the setting.
/// </summary>
public sealed partial class SettingsWindowTests : IDisposable
{
    private readonly string _here = Directory.CreateTempSubdirectory("qs170-").FullName;

    public void Dispose() => Directory.Delete(_here, recursive: true);

    /// <summary>
    /// Every key the file documents has a control, and what each control says it means is the
    /// reference's own sentence — so the window and the page cannot describe a setting two ways.
    /// </summary>
    [Fact]
    public void EveryDocumentedKeyHasAControlInTheReferencesWords()
    {
        string[] keys = [.. SettingsFile.Known.Where(key => key != "schema")];

        Assert.Equal(keys.Order(), SettingsWindow.Keys.Select(entry => entry.Key).Order());

        string reference = Flat(File.ReadAllText(Path.Combine(Repository.Root, "docs", "SETTINGS.md")));

        foreach ((string key, _, string means) in SettingsWindow.Keys)
        {
            Assert.True(reference.Contains(Flat(means), StringComparison.Ordinal),
                        $"what the window says {key} means is not a sentence of docs/SETTINGS.md: {means}");
        }
    }

    /// <summary>
    /// The falsification turned round: a change made in the window is in the file — written as the
    /// control changes, with the note the user wrote beside the value still there.
    /// </summary>
    [Fact]
    public void AChangeInTheWindowIsAChangeInTheFileAndTheNotesSurvive()
    {
        string path = Path.Combine(_here, "settings.json");

        File.WriteAllText(path, "{\n  \"schema\": 1,\n  // I like it still\n  \"cursorBlink\": true,\n  \"fontSize\": 12\n}\n");

        Sta.Run(() =>
        {
            SettingsWindow window = new(path);

            ((CheckBox)window.ControlFor("cursorBlink")).IsChecked = false;

            TextBox size = (TextBox)window.ControlFor("fontSize");

            size.Text = "15.5";
            Leave(size);

            // Nonsense is kept in the field for fixing and not written.
            size.Text = "big";
            Leave(size);

            window.Close();
        });

        string written = File.ReadAllText(path);

        Assert.Contains("// I like it still", written, StringComparison.Ordinal);
        Assert.False(SettingsFile.ReadFrom(path).CursorBlink);
        Assert.Equal(15.5, SettingsFile.ReadFrom(path).FontSize);
    }

    /// <summary>
    /// A file that does not parse is somebody's half-finished edit, and the window writes nothing
    /// over it — every control is off, and asking for a change changes nothing.
    /// </summary>
    [Fact]
    public void AFileThatDoesNotParseIsNotWrittenOver()
    {
        string path = Path.Combine(_here, "settings.json");
        const string HalfDone = "{ \"fontSize\": 14, \"theme\": ";

        File.WriteAllText(path, HalfDone);

        (bool enabled, bool changed) = Sta.Run(() =>
        {
            SettingsWindow window = new(path);

            bool on = window.ControlFor("cursorBlink").IsEnabled;
            bool wrote = window.Change(now => now with { CursorBlink = false });

            window.Close();

            return (on, wrote);
        });

        Assert.False(enabled);
        Assert.False(changed);
        Assert.Equal(HalfDone, File.ReadAllText(path));
    }

    /// <summary>
    /// QS174's falsification: every value this client could not use leaves a line where somebody
    /// looking at their settings will look — a misspelt key, a value of the wrong kind, a scheme path
    /// that leads nowhere — and a file it used whole says nothing.
    /// </summary>
    [Fact]
    public void EveryValueTheClientCouldNotUseIsSaidInTheWindow()
    {
        string path = Path.Combine(_here, "settings.json");

        File.WriteAllText(path, "{\n  \"fontsize\": 14,\n  \"scrollback\": \"lots\",\n  \"theme\": \"Purple\",\n"
                                + "  \"colourScheme\": \"schemes/missing.itermcolors\"\n}\n");

        (string said, string afterFixing) = Sta.Run(() =>
        {
            SettingsWindow window = new(path);
            string first = window.Unused;

            File.WriteAllText(path, "{ \"fontSize\": 14 }");
            window.Reload();

            string second = window.Unused;

            window.Close();

            return (first, second);
        });

        Assert.Contains("\"fontsize\" is not a setting this build knows", said, StringComparison.Ordinal);
        Assert.Contains("\"scrollback\" is \"lots\"", said, StringComparison.Ordinal);
        Assert.Contains("\"theme\" is \"Purple\"", said, StringComparison.Ordinal);
        Assert.Contains("missing.itermcolors, which is not there", said, StringComparison.Ordinal);

        Assert.Equal(string.Empty, afterFixing);
    }

    // ---- plumbing ----

    /// <summary>The markup and the line breaks taken out, which is all that separates the two texts.</summary>
    private static string Flat(string text) =>
        Spaces().Replace(text.Replace("**", string.Empty, StringComparison.Ordinal)
                             .Replace("`", string.Empty, StringComparison.Ordinal), " ");

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    /// <summary>The field loses the keyboard, which is what commits it.</summary>
    private static void Leave(TextBox box) =>
        box.RaiseEvent(new KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice, 0, box, null)
        {
            RoutedEvent = Keyboard.LostKeyboardFocusEvent,
        });
}
