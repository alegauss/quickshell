using System.Globalization;
using System.IO;
using Quickshell.App;
using Xunit;

namespace Quickshell.App.Tests;

/// <summary>
/// The last second, and what survives it.
///
/// <para>The falsification is a crash that exits with no report and no message, so what is asked
/// here is: a report exists, it says what the client was doing, it tells the two failures apart, and
/// the sentence the user reads names the file. The dialog itself is not driven — a modal message box
/// has no assertion worth making and would hang a test run — so the guard is armed with a telling of
/// this test's own, which is the same call the dialog is made from.</para>
/// </summary>
public sealed class CrashReportTests : IDisposable
{
    private readonly string _here =
        Path.Combine(Path.GetTempPath(), $"quickshell-crash-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_here))
        {
            Directory.Delete(_here, recursive: true);
        }
    }

    // ---- The falsification ----

    /// <summary>
    /// An unhandled exception leaves a report on disk and a sentence naming it.
    ///
    /// <para>Raised through the guard's own path rather than by throwing off a thread: an exception
    /// that is genuinely unhandled ends the process running the tests, so what a test can ask is
    /// that the handler does its work — and <see cref="ACrashInARealProcessLeavesAReport"/> asks the
    /// other half against a process that really does die.</para>
    /// </summary>
    [Fact]
    public void ACrashLeavesAReportAndASentenceThatNamesIt()
    {
        CrashNotice? told = null;

        using CrashGuard guard = CrashGuard.Arm(
            application: null, gather: () => Doing(sessions: 3), folder: _here,
            tell: notice => told = notice);

        guard.Report(new InvalidOperationException("the terminal went sideways"));

        Assert.NotNull(guard.Last);

        CrashNotice notice = guard.Last;

        Assert.Equal(CrashKind.Defect, notice.Kind);
        Assert.True(File.Exists(notice.Path), $"{notice.Path} is not there");

        // The sentence a person reads: what happened, what was open, where it is, and that nothing
        // was sent.
        Assert.Contains("defect", notice.Sentence, StringComparison.Ordinal);
        Assert.Contains("3 sessions were open", notice.Sentence, StringComparison.Ordinal);
        Assert.Contains(notice.Path, notice.Sentence, StringComparison.Ordinal);
        Assert.Contains("nothing has been sent anywhere", notice.Sentence, StringComparison.Ordinal);

        string report = File.ReadAllText(notice.Path);

        Assert.Contains("the terminal went sideways", report, StringComparison.Ordinal);
        Assert.Contains("InvalidOperationException", report, StringComparison.Ordinal);
        Assert.Contains("sessions open: 3", report, StringComparison.Ordinal);

        // `told` is the guard's own call, which is the one the dialog is made from.
        Assert.Null(told);
    }

    /// <summary>
    /// The same question of a process that really does die: the exception is never caught, the
    /// runtime ends the process, and a file is on disk afterwards.
    ///
    /// <para>A handler asserted only through its own method is a handler that has never been hooked.
    /// This one runs the client's own guard in a second process, throws off the thread pool where
    /// nothing will catch it, and then reads the folder from here.</para>
    /// </summary>
    [Fact]
    public void ACrashInARealProcessLeavesAReport()
    {
        string report = Path.Combine(_here, "from-a-real-crash");

        Directory.CreateDirectory(report);

        int exit = Crashing.Run(report);

        // It died. A zero here would mean the exception was swallowed, which is the other way this
        // could be wrong.
        Assert.NotEqual(0, exit);

        string[] written = Directory.GetFiles(report, "crash-*.txt");

        Assert.Single(written);

        string text = File.ReadAllText(written[0]);

        Assert.Contains("a defect in the client", text, StringComparison.Ordinal);
        Assert.Contains("thrown off a thread nobody was watching", text, StringComparison.Ordinal);
    }

    // ---- Telling the two failures apart ----

    /// <summary>
    /// A lost device is not a defect. <c>GraphicsDevice.Recover</c> exists for it, and a report
    /// filed as a bug is how a maintainer spends a week on somebody's driver update.
    /// </summary>
    [Theory]
    [InlineData(unchecked((int)0x887A0005))] // removed
    [InlineData(unchecked((int)0x887A0006))] // hung
    [InlineData(unchecked((int)0x887A0007))] // reset
    [InlineData(unchecked((int)0x887A0020))] // the driver's own internal error
    public void ADeviceThatWentAwayIsNotReportedAsADefect(int result)
    {
        Exception lost = new("the device went away") { HResult = result };

        Assert.Equal(CrashKind.DeviceLost, CrashReport.Classify(lost));

        // And wrapped, which is how it actually arrives.
        Assert.Equal(CrashKind.DeviceLost,
                     CrashReport.Classify(new InvalidOperationException("while presenting", lost)));

        string said = CrashReport.Say(CrashKind.DeviceLost, "C:\\somewhere\\crash.txt",
                                      Doing(sessions: 0));

        Assert.Contains("graphics device went away", said, StringComparison.Ordinal);
        Assert.DoesNotContain("defect", said, StringComparison.Ordinal);
    }

    /// <summary>And an ordinary failure is still a defect, so the test above is not vacuous.</summary>
    [Fact]
    public void AnOrdinaryFailureIsADefect()
    {
        Assert.Equal(CrashKind.Defect,
                     CrashReport.Classify(new InvalidOperationException("nothing to do with a GPU")));

        Assert.Equal(CrashKind.Defect, CrashReport.Classify(failure: null));
    }

    // ---- What the report carries ----

    /// <summary>
    /// The report says what the client was doing, which is the half of a bug report that otherwise
    /// costs days of correspondence.
    /// </summary>
    [Fact]
    public void TheReportSaysWhatTheClientWasDoing()
    {
        string log = Path.Combine(_here, "quickshell.log");

        Directory.CreateDirectory(_here);
        File.WriteAllText(log, "2026-01-01 00:00:00.000 connecting where=somebody@example.test:22\n");

        CrashContext what = new("1.2.3.4", "Windows 11", "DefaultHardware: a video card", 2, 1,
                                TimeSpan.FromSeconds(90), [log]);

        string report = CrashReport.Compose(CrashKind.Defect, new InvalidOperationException("bang"),
                                            what, DateTimeOffset.UnixEpoch);

        Assert.Contains("version: 1.2.3.4", report, StringComparison.Ordinal);
        Assert.Contains("adapter: DefaultHardware: a video card", report, StringComparison.Ordinal);
        Assert.Contains("device recoveries: 2", report, StringComparison.Ordinal);
        Assert.Contains("running for: 90s", report, StringComparison.Ordinal);

        // And the end of the log, which is what it was doing rather than what it was.
        Assert.Contains("connecting where=somebody@example.test:22", report,
                        StringComparison.Ordinal);
    }

    /// <summary>Every inner exception, because the outer one is rarely the interesting one.</summary>
    [Fact]
    public void EveryInnerExceptionIsWrittenDown()
    {
        Exception failure = new InvalidOperationException(
            "the outer one",
            new IOException("the one that actually happened"));

        string report = CrashReport.Compose(CrashKind.Defect, failure, Doing(sessions: 0),
                                            DateTimeOffset.UnixEpoch);

        Assert.Contains("the outer one", report, StringComparison.Ordinal);
        Assert.Contains("the one that actually happened", report, StringComparison.Ordinal);
    }

    /// <summary>
    /// A folder that cannot be written costs the file and not the message: the sentence says there
    /// was nowhere to put it, which is still more than a silent exit.
    /// </summary>
    [Fact]
    public void NowhereToWriteCostsTheFileAndNotTheMessage()
    {
        // A path under a file rather than under a directory: creating it cannot succeed.
        string blocked = Path.Combine(_here, "a-file", "crashes");

        Directory.CreateDirectory(_here);
        File.WriteAllText(Path.Combine(_here, "a-file"), "not a directory");

        using CrashGuard guard = CrashGuard.Arm(application: null, gather: () => Doing(sessions: 1),
                                                folder: blocked, tell: _ => { });

        CrashNotice notice = guard.Report(new InvalidOperationException("bang"));

        Assert.Equal(string.Empty, notice.Path);
        Assert.Contains("could not be written", notice.Sentence, StringComparison.Ordinal);
        Assert.Contains("nothing has been sent anywhere", notice.Sentence, StringComparison.Ordinal);
    }

    /// <summary>
    /// A client crashing in a loop is not a disk that fills: the last ten are kept and the rest go.
    /// </summary>
    [Fact]
    public void ReportsAreKeptToABoundedNumber()
    {
        using CrashGuard guard = CrashGuard.Arm(application: null, gather: () => Doing(sessions: 0),
                                                folder: _here, tell: _ => { });

        for (int crash = 0; crash < 25; crash++)
        {
            // The name carries milliseconds, so two in the same millisecond are one file. What is
            // asserted is the bound, which holds either way.
            guard.Report(new InvalidOperationException($"crash {crash.ToString(CultureInfo.InvariantCulture)}"));
        }

        Assert.InRange(Directory.GetFiles(_here, "crash-*.txt").Length, 1, 10);
    }

    /// <summary>
    /// Gathering the context reaches into a client that has just failed, so it is exactly the call
    /// that may fail again. Losing the report over it would be the worst trade available.
    /// </summary>
    [Fact]
    public void AContextThatThrowsStillLeavesAReport()
    {
        using CrashGuard guard = CrashGuard.Arm(
            application: null,
            gather: () => throw new InvalidOperationException("the state is gone too"),
            folder: _here, tell: _ => { });

        CrashNotice notice = guard.Report(new InvalidOperationException("bang"));

        Assert.True(File.Exists(notice.Path), $"{notice.Path} is not there");
        Assert.Contains("bang", File.ReadAllText(notice.Path), StringComparison.Ordinal);
    }

    /// <summary>Disposing unhooks, so one test's guard is not every later test's guard.</summary>
    [Fact]
    public void DisposingUnhooks()
    {
        CrashGuard guard = CrashGuard.Arm(application: null, gather: () => Doing(sessions: 0),
                                          folder: _here, tell: _ => { });

        guard.Dispose();

        // Twice is not an error either: the way out of a crash is not the place for a throw.
        guard.Dispose();
    }

    private static CrashContext Doing(int sessions) =>
        new("0.0.0-test", "Windows", "no device was opened", 0, sessions, TimeSpan.FromSeconds(5),
            []);
}
