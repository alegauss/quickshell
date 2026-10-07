namespace Quickshell.Tests;

/// <summary>
/// What a steady-state pass allocates, measured so the runtime's own noise is not read as the
/// code's (QS167).
///
/// <para><b>Zero is the claim and it stays the claim.</b> An allocation assertion read
/// <c>GC.GetAllocatedBytesForCurrentThread</c> across one pass, and that counter counts whatever the
/// thread allocated — on a cold guest under a full suite, once in a while, something the runtime did
/// on the way through rather than anything the code under test asked for. The same tree went red in
/// the guest and green on the host and green on the guest's next run, which is a measurement being
/// read as an assertion.</para>
///
/// <para><b>So the pass is measured more than once, and the least of them is the answer.</b> A path
/// that allocates per call allocates on every pass, so its least is not zero and it still fails; a
/// one-off from the runtime lands on one pass and not the others. No ceiling is introduced — a
/// ceiling is how zero quietly becomes a budget.</para>
///
/// <para>Linked into every test project from <c>tests/Directory.Build.props</c>, beside
/// <see cref="Freshness"/>.</para>
/// </summary>
internal static class Allocations
{
    /// <summary>How many passes are measured at most. A pass that allocated nothing ends it early.</summary>
    internal const int Passes = 3;

    /// <summary>
    /// The fewest bytes one run of <paramref name="pass"/> allocated on this thread, over up to
    /// <see cref="Passes"/> runs. The caller warms the path first, as before: this measures the
    /// steady state and not the first call.
    /// </summary>
    internal static long Least(Action pass)
    {
        ArgumentNullException.ThrowIfNull(pass);

        long least = long.MaxValue;

        for (int run = 0; run < Passes && least > 0; run++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();

            pass();

            least = Math.Min(least, GC.GetAllocatedBytesForCurrentThread() - before);
        }

        return least;
    }
}
