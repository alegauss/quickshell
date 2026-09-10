using System.Collections.Concurrent;
using System.Diagnostics;
using Xunit;

namespace Quickshell.App.Tests;

/// <summary>
/// A pane's thread, as a queue drained by hand — which is all a dispatcher is — and the longest any
/// one piece of work held it.
///
/// <para>What <see cref="Quickshell.App.DirectoryPane"/> and <see cref="Quickshell.App.BrowserActions"/>
/// post lands here, and runs only when a test drains it: so a test can say exactly what a window
/// would have shown at each moment, and how long each step would have held its thread.</para>
/// </summary>
internal sealed class PanePump
{
    private readonly ConcurrentQueue<Action> _queue = new();

    /// <summary>The longest one piece of work held the thread.</summary>
    public TimeSpan Longest { get; private set; }

    /// <summary>What a pane is given as its post.</summary>
    public void Post(Action work) => _queue.Enqueue(work);

    /// <summary>Runs everything queued so far, and anything it queues in turn.</summary>
    public void Drain()
    {
        while (_queue.TryDequeue(out Action? work))
        {
            long began = Stopwatch.GetTimestamp();

            work();

            TimeSpan took = Stopwatch.GetElapsedTime(began);

            if (took > Longest)
            {
                Longest = took;
            }
        }
    }

    /// <summary>Drains until a condition holds, failing the test if it never does.</summary>
    public async Task DrainUntil(Func<bool> done, TimeSpan patience)
    {
        Stopwatch waited = Stopwatch.StartNew();

        while (true)
        {
            Drain();

            if (done())
            {
                return;
            }

            Assert.True(waited.Elapsed < patience, "the pane never reached the state waited for");

            await Task.Delay(5, TestContext.Current.CancellationToken);
        }
    }
}
