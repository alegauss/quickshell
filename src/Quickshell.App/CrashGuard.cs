using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;

namespace Quickshell.App;

/// <summary>
/// The part that touches the process: it hooks what the runtime raises on the way out, writes the
/// report and tells the user.
///
/// <para><b>Two hooks, and the third is left alone deliberately.</b> An exception that escapes the
/// dispatcher and one that escapes any other thread both end the process, and both are reported.
/// <c>TaskScheduler.UnobservedTaskException</c> is not hooked: it does not end anything — it fires
/// on a finalizer thread long after the fact, for a task nobody awaited — and reporting it would
/// fill a user's folder with files about failures the client already survived. That is the same
/// distinction <see cref="CrashKind.DeviceLost"/> draws, and drawing both is what keeps a crash
/// report worth opening.</para>
///
/// <para><b>Nothing is swallowed.</b> The dispatcher handler does not mark the exception handled, so
/// the process still ends. A client that carries on after an unhandled exception is a client whose
/// state nobody can reason about, and pretending otherwise would trade a visible failure for a
/// mysterious one — which is the symptom this exists to remove, not a cure for it.</para>
///
/// <para>Disposing unhooks, which is what lets a test arm one without arming every test after
/// it.</para>
/// </summary>
public sealed class CrashGuard : IDisposable
{
    private readonly Func<string> _folder;
    private readonly Func<CrashContext> _gather;
    private readonly Action<CrashNotice> _tell;
    private readonly Application? _application;

    private int _reporting;
    private bool _disposed;

    private CrashGuard(Func<string> folder, Func<CrashContext> gather, Action<CrashNotice> tell,
                       Application? application)
    {
        _folder = folder;
        _gather = gather;
        _tell = tell;
        _application = application;

        AppDomain.CurrentDomain.UnhandledException += OnProcess;

        if (_application is not null)
        {
            _application.DispatcherUnhandledException += OnDispatcher;
        }
    }

    /// <summary>The last report this guard wrote, or null. What a test reads instead of a dialog.</summary>
    public CrashNotice? Last { get; private set; }

    /// <summary>
    /// Arms the guard.
    /// </summary>
    /// <param name="application">The WPF application, so an exception off the UI thread is caught
    /// too. Null in a test that has no application.</param>
    /// <param name="gather">What the client was doing, asked for at the moment of the failure.</param>
    /// <param name="folder">
    /// Where reports go. Defaults to beside the user's other data — and resolved when a report is
    /// written rather than here, because working out which layout this client is running touches a
    /// disk, and arming happens on the way to the first paint where nothing may.
    /// </param>
    /// <param name="tell">How the user is told. Defaults to a dialog offering to open the file.</param>
    public static CrashGuard Arm(Application? application = null, Func<CrashContext>? gather = null,
                                 string? folder = null, Action<CrashNotice>? tell = null) =>
        new(folder is null ? CrashReport.Folder : () => folder, gather ?? CrashContext.Minimal,
            tell ?? Dialog, application);

    /// <summary>
    /// Writes the report for one failure and returns what the user would be told.
    ///
    /// <para>Public because it is the whole of the behaviour, and because raising a genuinely
    /// unhandled exception inside a test run ends the test run. What the hooks do is call
    /// this.</para>
    /// </summary>
    public CrashNotice Report(Exception? failure)
    {
        CrashKind kind = CrashReport.Classify(failure);
        CrashContext what = Gathered();
        DateTimeOffset when = DateTimeOffset.UtcNow;

        string path = CrashReport.WriteTo(Where(), CrashReport.Compose(kind, failure, what, when),
                                          when);

        CrashNotice notice = new(kind, path, CrashReport.Say(kind, path, what));

        Last = notice;

        return notice;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        AppDomain.CurrentDomain.UnhandledException -= OnProcess;

        if (_application is not null)
        {
            _application.DispatcherUnhandledException -= OnDispatcher;
        }
    }

    /// <summary>
    /// The default telling: a dialog that offers to open the report rather than one that sends it.
    ///
    /// <para>A report the user has not read is a report they should not be asked to send, so the
    /// only button here opens the file.</para>
    /// </summary>
    private static void Dialog(CrashNotice notice)
    {
        if (notice.Path.Length == 0)
        {
            MessageBox.Show(notice.Sentence, "quickshell has stopped", MessageBoxButton.OK,
                            MessageBoxImage.Error);

            return;
        }

        MessageBoxResult answer = MessageBox.Show(
            $"{notice.Sentence}\n\nOpen the report now?", "quickshell has stopped",
            MessageBoxButton.YesNo, MessageBoxImage.Error);

        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(notice.Path) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception)
        {
            // The report is still on disk and the sentence named it. Failing to open it is not
            // worth a second dialog on top of a crash.
        }
    }

    /// <summary>
    /// The context, or the little that can be known if gathering it throws too.
    ///
    /// <para>Whatever supplies this reaches into a client that has just failed, so it is exactly the
    /// call that may fail again — and losing the report because the header could not be filled in
    /// would be the worst trade in this file.</para>
    /// </summary>
    /// <summary>
    /// Where to write, asked at the moment of the crash.
    ///
    /// <para>A folder that cannot be worked out is not a reason to lose the report:
    /// <see cref="CrashReport.WriteTo"/> answers an empty path with an empty path, and the sentence
    /// the user reads then says there was nowhere to put it — which is still more than a silent
    /// exit.</para>
    /// </summary>
    private string Where()
    {
        try
        {
            return _folder();
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    private CrashContext Gathered()
    {
        try
        {
            return _gather();
        }
        catch (Exception)
        {
            return CrashContext.Minimal();
        }
    }

    private void OnProcess(object sender, UnhandledExceptionEventArgs raised) =>
        Handle(raised.ExceptionObject as Exception);

    private void OnDispatcher(object sender, DispatcherUnhandledExceptionEventArgs raised) =>
        Handle(raised.Exception);

    /// <summary>
    /// Reports once. A second failure while the first is being written is not a second report: the
    /// second is usually the first one's consequence, and two dialogs on the way out is worse than
    /// none.
    /// </summary>
    private void Handle(Exception? failure)
    {
        if (Interlocked.Exchange(ref _reporting, 1) == 1)
        {
            return;
        }

        try
        {
            _tell(Report(failure));
        }
        catch (Exception)
        {
            // Nothing above this catches, and throwing here replaces a message the user could act
            // on with a silent exit — which is the symptom, not a diagnosis of it.
        }
    }
}
