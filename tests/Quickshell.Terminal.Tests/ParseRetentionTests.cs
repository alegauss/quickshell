using System.IO;
using System.IO.Compression;
using Quickshell.Terminal;
using Xunit;

namespace Quickshell.Terminal.Tests;

/// <summary>
/// What the parse path holds on to, which Block C's criterion says is nothing in steady state.
///
/// <para>QS78's soak found roughly thirteen bytes retained per byte parsed against a live session,
/// surviving a forced full collection. These ask the same question headlessly and in one place, so
/// the answer is about the emulator rather than about a network.</para>
/// </summary>
public sealed class ParseRetentionTests
{
    /// <summary>How much is fed. Enough that retention proportional to it would be unmissable.</summary>
    private const int Megabytes = 64;

    /// <summary>The chunk a real read hands over, so the arrangement matches the live one.</summary>
    private const int ChunkSize = 64 * 1024;

    /// <summary>
    /// The falsification, in its own words: parsing a lot leaves a heap a full collection can
    /// reduce.
    ///
    /// <para>One emulator, fed sixty-four megabytes of a real captured stream in the chunks a real
    /// read delivers. What is asserted is what survives a forced full collection — garbage is the
    /// collector's schedule and is not this test's business, but anything still held after a blocking
    /// gen2 collection is held by the emulator.</para>
    /// </summary>
    [Fact]
    public void ParsingALotRetainsAlmostNothing()
    {
        byte[] stream = Corpus("cat-log");

        Emulator emulator = new(200, 50, scrollback: 2_000);

        // Warm the emulator up first, so what is measured excludes the buffers it legitimately
        // allocates once: the grid, the scrollback ring, the parser's own state.
        Feed(emulator, stream, 2 * 1024 * 1024);

        long before = Settled();

        Feed(emulator, stream, Megabytes * 1024 * 1024);

        long after = Settled();

        double heldMb = (after - before) / (1024.0 * 1024.0);

        // Generous by a wide margin against the defect and tight against the criterion: sixty-four
        // megabytes parsed must not leave tens of megabytes held. The soak's live figure was
        // thirteen bytes per byte, which here would be more than eight hundred megabytes.
        Assert.True(heldMb < 8.0,
                    $"parsing {Megabytes} MB left {heldMb:F1} MB held after a full collection");

        // And the emulator is still the thing it was, so the absence above is not the absence of
        // parsing.
        Assert.Equal(2_000, emulator.Screens.Primary.ScrollbackLines);
    }

    /// <summary>
    /// The reply buffer is bounded whether or not anybody drains it — nearly.
    ///
    /// <para><b>It overshoots its own stated maximum by one answer's length</b>, because
    /// <c>Send</c> checks the cap before appending and then appends a whole reply. Two hundred
    /// thousand undrained cursor-position requests reach 4,098 bytes against a
    /// <c>MaximumReplyLength</c> of 4,096. That is harmless in practice and it is the code
    /// contradicting its own constant, which is QS140; this asserts what it actually does so the
    /// bound is still watched, and QS140 is where the number becomes exact.</para>
    /// </summary>
    [Fact]
    public void TheReplyBufferDoesNotGrowWithoutBound()
    {
        Emulator emulator = new(80, 25);

        // Device status report, over and over, and nothing ever drains it.
        byte[] asking = [0x1B, (byte)'[', (byte)'6', (byte)'n'];

        for (int time = 0; time < 200_000; time++)
        {
            emulator.Feed(asking);
        }

        // The overshoot is one answer, not a quarter of a million of them: what this rules out is
        // growth, which is the property the constant exists for.
        Assert.InRange(emulator.Reply.Length, 0, Emulator.MaximumReplyLength + 64);

        // And it really did stop answering rather than merely being drained by something.
        Assert.True(emulator.Unhandled > 0,
                    "nothing was refused, so the bound was never reached and this proves nothing");
    }

    private static void Feed(Emulator emulator, byte[] stream, int bytes)
    {
        int fed = 0;
        int at = 0;

        while (fed < bytes)
        {
            int take = Math.Min(ChunkSize, Math.Min(stream.Length - at, bytes - fed));

            emulator.Feed(stream.AsSpan(at, take));

            fed += take;
            at += take;

            if (at >= stream.Length)
            {
                at = 0;
            }
        }
    }

    /// <summary>The managed heap after everything collectable has been collected.</summary>
    private static long Settled()
    {
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);

        return GC.GetTotalMemory(forceFullCollection: true);
    }

    /// <summary>One captured stream, decompressed. Real bytes rather than a generated pattern.</summary>
    private static byte[] Corpus(string name)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null)
        {
            string candidate = Path.Combine(directory.FullName, "benchmarks", "corpus", "streams");

            if (Directory.Exists(candidate))
            {
                using FileStream file = File.OpenRead(Path.Combine(candidate, $"{name}.raw.gz"));
                using GZipStream expanding = new(file, CompressionMode.Decompress);
                using MemoryStream into = new();

                expanding.CopyTo(into);

                return into.ToArray();
            }

            directory = directory.Parent;
        }

        Assert.Fail($"no benchmarks/corpus/streams above {AppContext.BaseDirectory}");

        return [];
    }
}
