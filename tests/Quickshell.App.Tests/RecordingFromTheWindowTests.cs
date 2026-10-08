using System.IO;
using System.IO.Compression;
using System.Text;

using Quickshell.Terminal;

using Xunit;

namespace Quickshell.App.Tests;

/// <summary>
/// QS134: a recording is started from the window, as a session opens, and stopped from it, with
/// the file named when it stops.
/// </summary>
public sealed class RecordingFromTheWindowTests : IDisposable
{
    private static readonly TerminalShare Shared = new();

    private readonly string _here = Path.Combine(Path.GetTempPath(), "qs-recording-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_here))
        {
            Directory.Delete(_here, recursive: true);
        }
    }

    /// <summary>
    /// A new tab, recorded: the name is asked first, the title says it records while it does, and
    /// stopping says where the file is — a file holding what the shell sent.
    /// </summary>
    [Fact]
    public void ANewTabIsRecordedFromTheWindowAndStoppingSaysWhereTheFileIs()
    {
        List<string> said = [];

        (bool stopOfferedBefore, string? offered, string recordingTitle, bool stopOffered, string stoppedTitle) = Sta.Run(() =>
        {
            MainWindow window = new()
            {
                RecordingsFolder = _here,
                Tells = said.Add,
            };

            string? asked = null;

            window.AsksRecordingName = suggested =>
            {
                asked = suggested;

                return "a-defect";
            };

            // What the program does, with a shell that says one thing and waits.
            window.OpensRecorded = recording =>
            {
                TerminalTab tab = TerminalTab.Open(Settings.Default, Shared, "cmd.exe");

                window.Add(tab);
                tab.Focused.ConnectAsync("cmd.exe /d /k echo recorded-output", recording: recording)
                   .GetAwaiter().GetResult();
            };

            bool before = window.Actions.Any(one => one.Name == "Stop recording");

            window.Actions.Single(one => one.Name == "New tab, recorded").Run();

            SessionRecording running = window.Current!.Focused.Recording!;

            for (int wait = 0; wait < 200 && running.Bytes == 0; wait++)
            {
                Thread.Sleep(50);
            }

            string whileRecording = window.Title;
            bool offeredStop = window.Actions.Any(one => one.Name == "Stop recording");

            window.Actions.Single(one => one.Name == "Stop recording").Run();

            string afterwards = window.Title;

            // The shell is still running; the pane is closed so it is not left behind.
            window.Current!.Focused.DisposeAsync().AsTask().GetAwaiter().GetResult();

            return (before, asked, whileRecording, offeredStop, afterwards);
        });

        string file = Path.Combine(_here, "a-defect.raw.gz");

        Assert.False(stopOfferedBefore, "Stop recording was offered with nothing recording");
        Assert.StartsWith("local-", offered, StringComparison.Ordinal);
        Assert.StartsWith("● recording — ", recordingTitle, StringComparison.Ordinal);
        Assert.True(stopOffered, "a pane that was recording was not offered a stop");
        Assert.DoesNotContain("recording", stoppedTitle, StringComparison.Ordinal);
        Assert.Equal($"The recording is saved as {file}", Assert.Single(said));
        Assert.Contains("recorded-output", Unpacked(file), StringComparison.Ordinal);
    }

    /// <summary>Declining the name starts nothing: no tab, no file, no mark.</summary>
    [Fact]
    public void DecliningTheNameRecordsNothing()
    {
        (int tabs, string title) = Sta.Run(() =>
        {
            MainWindow window = new() { RecordingsFolder = _here, AsksRecordingName = _ => null };

            window.OpensRecorded = _ => throw new InvalidOperationException("opened a tab nobody agreed to record");

            window.Actions.Single(one => one.Name == "New tab, recorded").Run();

            return (window.Held.Count, window.Title);
        });

        Assert.Equal(0, tabs);
        Assert.DoesNotContain("recording", title, StringComparison.Ordinal);
        Assert.False(Directory.Exists(_here) && Directory.EnumerateFiles(_here).Any(), "a declined recording left a file");
    }

    /// <summary>The terms are said before it starts: what is kept, what never is, where, and the bound.</summary>
    [Fact]
    public void TheTermsSayWhatIsKeptWhereAndUpToWhatSize()
    {
        string terms = RecordingDialog.Terms(@"C:\recordings", SessionRecording.DefaultLimit);

        Assert.Contains("nothing you type is", terms, StringComparison.Ordinal);
        Assert.Contains(@"C:\recordings", terms, StringComparison.Ordinal);
        Assert.Contains("256 MB compressed", terms, StringComparison.Ordinal);
    }

    private static string Unpacked(string file)
    {
        using FileStream reading = File.OpenRead(file);
        using GZipStream unpacking = new(reading, CompressionMode.Decompress);
        using StreamReader text = new(unpacking, Encoding.UTF8);

        return text.ReadToEnd();
    }
}
