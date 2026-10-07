using System.Runtime.ExceptionServices;
using Xunit;

namespace Quickshell.App.Tests;

/// <summary>
/// Runs work on an STA thread, which is the only kind a WPF window can be built on — the one copy
/// every class in this assembly calls (QS195).
///
/// <para><b>One copy because twenty-two of them had drifted.</b> Some shut the thread's dispatcher
/// down and some did not, some made the thread background and some did not, and the waits ran from
/// unbounded to thirty seconds. QS138 was what that costs: one fault in two copies, and nothing
/// that fixed one could reach the other.</para>
/// </summary>
internal static class Sta
{
    /// <summary>How long the work may take before the test says so instead of hanging.</summary>
    public static readonly TimeSpan Patience = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Runs the work on a new STA thread and hands back what it returned, or throws what it threw.
    ///
    /// <para>The test host's threads are not STA and xunit has no attribute for it here, so the
    /// thread is made rather than asked for. Everything comes back through the result or the
    /// exception, so a failure inside reads as a failure of the test rather than as a hang.</para>
    /// </summary>
    public static T Run<T>(Func<T> work)
    {
        ArgumentNullException.ThrowIfNull(work);

        T result = default!;
        ExceptionDispatchInfo? failed = null;

        Thread thread = new(() =>
        {
            try
            {
                result = work();
            }
            catch (Exception error)
            {
                failed = ExceptionDispatchInfo.Capture(error);
            }
            finally
            {
                // Building a window on a thread gives that thread a dispatcher, and a dispatcher that
                // was never told to stop keeps the frame it is on alive. The runner answers a thread
                // still running at the end with a FATAL and a non-zero exit on a suite where every
                // test passed - a red that says nothing about the code.
                System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });

        thread.SetApartmentState(ApartmentState.STA);

        // Background, so a dispatcher that outlives the work does not hold the test host open.
        thread.IsBackground = true;
        thread.Start();

        Assert.True(thread.Join(Patience), "the STA thread never finished");

        // Thrown as it was thrown, with its own stack: the assertion that failed inside is the
        // message a reader needs, and a wrapper's sentence in front of it was one more to read past.
        failed?.Throw();

        return result;
    }

    /// <summary>Runs work that answers nothing on a new STA thread, and throws what it threw.</summary>
    public static void Run(Action work)
    {
        ArgumentNullException.ThrowIfNull(work);

        Run(() =>
        {
            work();

            return true;
        });
    }
}
