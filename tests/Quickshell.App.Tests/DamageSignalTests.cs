using Xunit;

namespace Quickshell.App.Tests;

/// <summary>The latch the render stage waits on.</summary>
public sealed class DamageSignalTests
{
    [Fact]
    public async Task AWaitAfterASetReturnsAtOnce()
    {
        DamageSignal signal = new();
        signal.Set();

        await signal.WaitAsync(TestContext.Current.CancellationToken);

        Assert.False(signal.IsSet);
    }

    /// <summary>
    /// Two changes between two waits are one wake-up. The waiter wants the current state, not a list
    /// of the states it missed — which is the same reason the parser drains before it sets.
    /// </summary>
    [Fact]
    public async Task TwoSetsBetweenTwoWaitsAreOneWakeUp()
    {
        DamageSignal signal = new();

        signal.Set();
        signal.Set();
        signal.Set();

        await signal.WaitAsync(TestContext.Current.CancellationToken);

        Assert.False(signal.IsSet);
        Assert.Equal(3, signal.Sets);
    }

    [Fact]
    public async Task AWaitBeforeASetIsWokenByIt()
    {
        DamageSignal signal = new();
        Task waiting = signal.WaitAsync(TestContext.Current.CancellationToken);

        Assert.False(waiting.IsCompleted);

        signal.Set();

        await waiting;
    }

    [Fact]
    public async Task AWaitOnNothingIsCancellable()
    {
        DamageSignal signal = new();
        using CancellationTokenSource stopping = new(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => signal.WaitAsync(stopping.Token));
    }

    /// <summary>A cancelled wait leaves the signal usable, because a render loop that was stopped and
    /// restarted is an ordinary thing.</summary>
    [Fact]
    public async Task ACancelledWaitDoesNotBreakTheNextOne()
    {
        DamageSignal signal = new();

        using (CancellationTokenSource stopping = new(TimeSpan.FromMilliseconds(50)))
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => signal.WaitAsync(stopping.Token));
        }

        signal.Set();

        await signal.WaitAsync(TestContext.Current.CancellationToken);
    }
}
