using Quickshell.App;

namespace Quickshell.CrashProbe;

/// <summary>
/// Arms the client's crash guard and then dies for real.
///
/// <para><b>Why this is a second process.</b> An exception that is genuinely unhandled ends the
/// process it is thrown in, so a test cannot raise one inside its own run — it would take the run
/// with it. A handler asserted only by calling its own method is a handler that has never actually
/// been hooked to anything, which is the failure this program exists to rule out: it arms
/// <see cref="CrashGuard"/> exactly as the client does, throws where nothing is watching, and leaves
/// the file behind for the test to read.</para>
///
/// <para>It exits non-zero by construction, which is why it is here and not under <c>tests\</c>.</para>
/// </summary>
public static class Probe
{
    /// <summary>Takes the folder to write the report into, and does not return.</summary>
    public static int Main(string[] arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        if (arguments.Length < 1)
        {
            Console.Error.WriteLine("usage: Quickshell.CrashProbe <folder for the report>");

            return 2;
        }

        string folder = arguments[0];

        // With --dialog the client's own telling is used instead of this one, which is how the
        // dialog a user actually sees gets captured. It is modal and nobody is there to click it,
        // so that mode is for a person with a screenshot tool and never for the suite.
        bool dialog = arguments.Contains("--dialog", StringComparer.Ordinal);

        // No Application: this has no window, and the process-wide hook is the one under test.
        using CrashGuard guard = CrashGuard.Arm(
            application: null,
            gather: () => new CrashContext(CrashContext.Build(),
                                           Environment.OSVersion.VersionString,
                                           "no device was opened", 0, 2,
                                           TimeSpan.FromSeconds(1), []),
            folder: folder,
            tell: dialog ? null : notice => Console.Error.WriteLine(notice.Sentence));

        // On the thread pool, where nothing catches and the runtime ends the process.
        ThreadPool.QueueUserWorkItem(
            _ => throw new InvalidOperationException("thrown off a thread nobody was watching"));

        // Long enough for that to happen, and bounded so a probe that somehow survives is a failure
        // the test sees rather than a process left running on somebody's machine.
        Thread.Sleep(TimeSpan.FromSeconds(30));

        Console.Error.WriteLine("the probe was still alive after 30s, which means nothing crashed");

        return 3;
    }
}
