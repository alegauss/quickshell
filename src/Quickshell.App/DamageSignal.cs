namespace Quickshell.App;

/// <summary>
/// "Something changed", for a thread that would otherwise be asking.
///
/// <para><b>It latches, and it does not count.</b> Two changes between two waits are one wake-up,
/// because a terminal is a state machine and the waiter wants the current state rather than a list
/// of the states it missed. That is the same reason the parser drains its whole queue before setting
/// this: the intermediate screens are not frames anyone is owed.</para>
///
/// <para>A replaceable completion source rather than an event with a wait handle, so a render loop
/// can await it alongside the swapchain's own latency wait without a thread parked on either.</para>
/// </summary>
public sealed class DamageSignal
{
    private readonly Lock _gate = new();

    private TaskCompletionSource _pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _set;

    /// <summary>How many times this has been set, which is how many frames a loop was offered.</summary>
    public long Sets { get; private set; }

    /// <summary>Whether a change is waiting to be noticed.</summary>
    public bool IsSet
    {
        get
        {
            lock (_gate)
            {
                return _set;
            }
        }
    }

    /// <summary>Says something changed, and wakes whoever is waiting.</summary>
    public void Set()
    {
        TaskCompletionSource woken;

        lock (_gate)
        {
            Sets++;

            if (_set)
            {
                return;
            }

            _set = true;
            woken = _pending;
        }

        woken.TrySetResult();
    }

    /// <summary>
    /// Waits for the next change and takes it, so the next wait blocks again.
    /// </summary>
    public async Task WaitAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            Task pending;

            lock (_gate)
            {
                if (_set)
                {
                    _set = false;
                    _pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    return;
                }

                pending = _pending.Task;
            }

            await pending.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
