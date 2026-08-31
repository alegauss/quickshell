using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Quickshell.App;
using Quickshell.Terminal;
using Xunit;

namespace Quickshell.App.Tests;

/// <summary>
/// One action that produces what a maintainer asks for, and a recording that does not contain what
/// the user typed.
///
/// <para>The line's own falsification is the second one, so it is asked of a real pipeline: a
/// password is typed through the same call the keyboard uses, the host sends its own bytes, and the
/// recording is then decompressed and searched. The rest asks what the bundle carries and what it
/// refuses to carry.</para>
/// </summary>
public sealed class DiagnosticsTests : IDisposable
{
    private readonly string _here =
        Path.Combine(Path.GetTempPath(), $"quickshell-diag-{Guid.NewGuid():N}");

    private static CancellationToken Stop => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        if (Directory.Exists(_here))
        {
            Directory.Delete(_here, recursive: true);
        }
    }

    // ---- The falsification ----

    /// <summary>
    /// A recording keeps what the host sent and not one byte of what the user typed.
    ///
    /// <para>Driven through <see cref="SessionPipeline"/> itself rather than by calling the recorder,
    /// because what is being asserted is a property of the arrangement: the keystroke path writes
    /// straight to the channel and shares nothing with the stage that feeds a recording. A test that
    /// only called <c>HostSent</c> would prove nothing about where <c>TypeAsync</c> goes.</para>
    /// </summary>
    [Fact]
    public async Task ARecordingKeepsWhatTheHostSentAndNotWhatWasTyped()
    {
        const string Typed = "hunter2-the-password-nobody-should-see";

        await using PtyStub host = new();

        Emulator emulator = new(80, 25);

        await using SessionRecording recording = SessionRecording.Start(_here, "a-session");

        await using (SessionPipeline pipeline =
                     SessionPipeline.Start(host, emulator, recording: recording))
        {
            host.Produce(Encoding.ASCII.GetBytes("Password: "));

            // The same call a keypress makes. Nothing else is done with it.
            await pipeline.TypeAsync(Encoding.ASCII.GetBytes(Typed), Stop);

            host.Produce(Encoding.ASCII.GetBytes("\r\nLogin incorrect\r\n"));
            host.Finish();

            await pipeline.Completed.WaitAsync(TimeSpan.FromSeconds(10), Stop);
        }

        await recording.DisposeAsync();

        string kept = Decompressed(recording.Path);

        // The host's side is all there.
        Assert.Contains("Password: ", kept, StringComparison.Ordinal);
        Assert.Contains("Login incorrect", kept, StringComparison.Ordinal);

        // And the user's side is not, which is the whole of this line.
        Assert.DoesNotContain(Typed, kept, StringComparison.Ordinal);

        // The typing really did happen, so the absence above is not the absence of a keystroke.
        Assert.Contains(host.Written, written =>
            Encoding.ASCII.GetString(written).Contains(Typed, StringComparison.Ordinal));
    }

    /// <summary>
    /// And the recording is the corpus's own shape, so a defect found this way becomes a regression
    /// test by moving one file rather than by writing one.
    /// </summary>
    [Fact]
    public async Task ARecordingIsACorpusEntry()
    {
        await using SessionRecording recording = SessionRecording.Start(_here, "vim-scroll");

        // Escapes and not the bytes themselves: a raw control byte in a source file is invisible in
        // a diff, which SourceHygieneTests refuses for exactly that reason.
        const string Screen = "\u001b[2J\u001b[Hhello";

        recording.HostSent(Encoding.ASCII.GetBytes(Screen));

        await recording.DisposeAsync();

        Assert.EndsWith(".raw.gz", recording.Path, StringComparison.Ordinal);
        Assert.Equal("vim-scroll.raw.gz", Path.GetFileName(recording.Path));

        // Readable the way the corpus loader reads one: gzip, and raw bytes under it.
        Assert.Equal(Screen, Decompressed(recording.Path));
    }

    /// <summary>Nothing records on its own: a session with no recording writes no file.</summary>
    [Fact]
    public async Task NothingRecordsUnlessSomebodyAsked()
    {
        await using PtyStub host = new();

        await using (SessionPipeline pipeline = SessionPipeline.Start(host, new Emulator(80, 25)))
        {
            host.Produce(Encoding.ASCII.GetBytes("nobody is watching this"));
            host.Finish();

            await pipeline.Completed.WaitAsync(TimeSpan.FromSeconds(10), Stop);
        }

        Assert.False(Directory.Exists(_here), $"{_here} should not have been made");
    }

    // ---- What the bundle carries ----

    /// <summary>
    /// The bundle carries the machine, the build, the graphics, the settings, the crash reports and
    /// the log — which is the list of things a first message never has.
    /// </summary>
    [Fact]
    public void TheBundleCarriesWhatAMaintainerWouldOtherwiseAskFor()
    {
        DiagnosticSources from = Made(config: "{\"Name\":\"a fleet\"}",
                                      log: "2026-01-01 00:00:00.000 connecting where=me@example.test:22\n",
                                      crash: "quickshell stopped: a defect in the client\n");

        string bundle = DiagnosticBundle.Compose(from, DateTimeOffset.UnixEpoch,
                                                 "DefaultHardware: a video card");

        Assert.Contains("graphics: DefaultHardware: a video card", bundle, StringComparison.Ordinal);
        Assert.Contains("windows:", bundle, StringComparison.Ordinal);
        Assert.Contains("version:", bundle, StringComparison.Ordinal);
        Assert.Contains("a fleet", bundle, StringComparison.Ordinal);
        Assert.Contains("connecting where=me@example.test:22", bundle, StringComparison.Ordinal);
        Assert.Contains("quickshell stopped: a defect in the client", bundle,
                        StringComparison.Ordinal);

        // And it says what it is, to somebody about to send it.
        Assert.Contains("has not been sent to anybody", bundle, StringComparison.Ordinal);
    }

    /// <summary>
    /// A post-login command is free text a user may have typed a password into, so its value never
    /// leaves the machine inside a bundle.
    /// </summary>
    [Fact]
    public void EveryValueASecretCouldHideBehindIsRemoved()
    {
        DiagnosticSources from = Made(config: """
            {
              "Name": "a fleet",
              "Settings": { "User": "somebody", "Credential": "work-laptop" },
              "PostLogin": "echo sudo-password-in-here | sudo -S systemctl restart thing",
              "Children": [
                { "Name": "a host", "Password": "another one", "Settings": { "Port": 2222 } }
              ]
            }
            """);

        string bundle = DiagnosticBundle.Compose(from, DateTimeOffset.UnixEpoch, "none");

        Assert.DoesNotContain("sudo-password-in-here", bundle, StringComparison.Ordinal);
        Assert.DoesNotContain("another one", bundle, StringComparison.Ordinal);
        Assert.Contains("(removed)", bundle, StringComparison.Ordinal);

        // What is not a secret is still there, or the section would be useless.
        Assert.Contains("somebody", bundle, StringComparison.Ordinal);
        Assert.Contains("work-laptop", bundle, StringComparison.Ordinal);
        Assert.Contains("2222", bundle, StringComparison.Ordinal);
    }

    /// <summary>
    /// A settings file that cannot be parsed is named and left out whole — the safe direction to
    /// fail in, since a file it could not parse is a file it could not redact.
    /// </summary>
    [Fact]
    public void SettingsThatCannotBeParsedAreLeftOutRatherThanCopied()
    {
        DiagnosticSources from = Made(config: "{ this is not json, and here is a secret-word");

        string bundle = DiagnosticBundle.Compose(from, DateTimeOffset.UnixEpoch, "none");

        Assert.DoesNotContain("secret-word", bundle, StringComparison.Ordinal);
        Assert.Contains("left out", bundle, StringComparison.Ordinal);
        Assert.Contains("settings.json", bundle, StringComparison.Ordinal);
    }

    /// <summary>
    /// Recordings are listed and never pasted in: they are compressed bytes, and inlining one would
    /// ruin the bundle and the recording at once.
    /// </summary>
    [Fact]
    public async Task RecordingsAreListedAndNotInlined()
    {
        DiagnosticSources from = Made();

        await using (SessionRecording recording = SessionRecording.Start(from.Recordings, "htop"))
        {
            recording.HostSent(Encoding.ASCII.GetBytes("some session output"));
        }

        string bundle = DiagnosticBundle.Compose(from, DateTimeOffset.UnixEpoch, "none");

        Assert.Contains("htop.raw.gz", bundle, StringComparison.Ordinal);
        Assert.Contains("benchmarks/corpus/streams", bundle, StringComparison.Ordinal);
        Assert.DoesNotContain("some session output", bundle, StringComparison.Ordinal);
    }

    /// <summary>Nothing to gather is a bundle that says so, not a failure.</summary>
    [Fact]
    public void AFreshInstallStillProducesABundle()
    {
        string path = DiagnosticBundle.WriteTo(
            Path.Combine(_here, "out"),
            new DiagnosticSources(Path.Combine(_here, "nothing"), Path.Combine(_here, "nothing"),
                                  Path.Combine(_here, "nothing"), Path.Combine(_here, "nothing")),
            DateTimeOffset.UnixEpoch, adapter: "none");

        string bundle = File.ReadAllText(path);

        Assert.Contains("no settings folder.", bundle, StringComparison.Ordinal);
        Assert.Contains("no session log.", bundle, StringComparison.Ordinal);
    }

    /// <summary>
    /// It asks the real machine, which is the one thing a crash report cannot do — a bundle runs
    /// while the client is healthy.
    /// </summary>
    [Fact]
    public void ItNamesTheGraphicsAdapterOnThisMachine()
    {
        string path = DiagnosticBundle.WriteTo(Path.Combine(_here, "out"), Made(),
                                               DateTimeOffset.UnixEpoch);

        string bundle = File.ReadAllText(path);

        // Whatever this machine has, the line is answered rather than left blank.
        Assert.Matches(@"graphics: \S", bundle);
        Assert.DoesNotContain("graphics: \n", bundle, StringComparison.Ordinal);
    }

    // ---- plumbing ----

    private DiagnosticSources Made(string? config = null, string? log = null, string? crash = null)
    {
        DiagnosticSources from = new(Path.Combine(_here, "config"), Path.Combine(_here, "logs"),
                                     Path.Combine(_here, "crashes"),
                                     Path.Combine(_here, "recordings"));

        Directory.CreateDirectory(from.Config);
        Directory.CreateDirectory(from.Logs);
        Directory.CreateDirectory(from.Crashes);
        Directory.CreateDirectory(from.Recordings);

        if (config is not null)
        {
            File.WriteAllText(Path.Combine(from.Config, "settings.json"), config);
        }

        if (log is not null)
        {
            File.WriteAllText(Path.Combine(from.Logs, "quickshell.log"), log);
        }

        if (crash is not null)
        {
            File.WriteAllText(Path.Combine(from.Crashes, "crash-20260101-000000-000.txt"), crash);
        }

        return from;
    }

    private static string Decompressed(string path)
    {
        using FileStream file = File.OpenRead(path);
        using GZipStream expanding = new(file, CompressionMode.Decompress);
        using StreamReader text = new(expanding);

        return text.ReadToEnd();
    }
}
