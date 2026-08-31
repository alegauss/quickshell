using System.Globalization;
using System.Net.Sockets;
using System.Text;
using Quickshell.Transport;
using Xunit;

namespace Quickshell.Transport.Tests;

/// <summary>
/// What an open channel costs while nobody is reading it.
///
/// <para>QS78's soak found roughly a gigabyte sitting in the large object heap during a flood, flat
/// and surviving a compacting collection, and QS139 traced it to this side of the seam rather than to
/// the emulator. This asks the question directly: a host told to print without stopping, and a
/// consumer that never reads. Whatever grows here grows with nothing above it to blame.</para>
///
/// <para><b>A session must make the host wait, not buffer for it.</b> That is what a protocol window
/// is for. A client that accepts everything a server can send and holds it is a client whose memory
/// is decided by the fastest host it ever connects to.</para>
/// </summary>
public sealed class ChannelBackpressureTests
{
    private const string Host = "127.0.0.1";
    private const int Port = 2222;

    /// <summary>How long the host is left printing with nobody reading.</summary>
    private static readonly TimeSpan Flooding = TimeSpan.FromSeconds(10);

    /// <summary>
    /// What an unread channel may hold. Generous: the window plus a read's worth, several times
    /// over.
    /// </summary>
    private const double BoundMb = 64.0;

    private static CancellationToken Stop => TestContext.Current.CancellationToken;

    /// <summary>
    /// An open channel nobody reads does not grow without bound.
    ///
    /// <para>The shell is opened, the host is told to print sixty-four megabytes on a loop, and then
    /// nothing reads it for ten seconds. At the link speed measured against this fixture that is
    /// enough for a gigabyte to arrive if arriving is all it takes.</para>
    ///
    /// <para><b>Skipped, and deliberately not silently.</b> It fails today: ten seconds of an unread
    /// channel held <b>3,072 MB</b>, essentially all of it in the large object heap. That is QS139,
    /// and no public knob in SSH.NET bounds it — <c>ConnectionInfo</c> has no window-size member, and
    /// the 64 KB handed to <c>CreateShellStream</c> plainly is not a bound. The reproduction is kept
    /// here rather than in a commit message so the fix has something to turn green, and the skip
    /// reason carries the number so every run prints it.</para>
    /// </summary>
    [Fact(Skip = "QS139: an unread channel holds 3,072 MB in ten seconds and SSH.NET exposes no "
                 + "window bound. Kept as the reproduction for the fix, not as a passing claim.")]
    public async Task AnUnreadChannelDoesNotBufferWithoutBound()
    {
        SkipWithoutFixture();

        long quiet = Settled();

        await using SshNetTransport session = new();

        SshCredential.PrivateKey key = new(Path.Combine(Fixture(), "probe_ed25519"));

        await session.ConnectAsync(SshEndpoint.For(Host, "probe", Port), [key], Trusting, Stop);

        IPtyChannel shell = await session.OpenShellAsync(200, 50, Stop);

        await shell.WriteAsync(Encoding.ASCII.GetBytes("while :; do cat /srv/big.txt; done\n"), Stop);

        // Nothing reads. This is the whole of the arrangement.
        await Task.Delay(Flooding, Stop);

        long held = Settled();

        double grewMb = (held - quiet) / (1024.0 * 1024.0);
        double lohMb = LargeObjectHeap();

        await shell.DisposeAsync();

        Assert.True(grewMb < BoundMb,
                    $"ten seconds of an unread channel held {grewMb.ToString("F1", CultureInfo.InvariantCulture)} MB "
                    + $"after a compacting collection, of which the large object heap is "
                    + $"{lohMb.ToString("F1", CultureInfo.InvariantCulture)} MB. A session must make "
                    + "the host wait rather than buffer for it.");
    }

    /// <summary>The managed heap after everything collectable has gone.</summary>
    private static long Settled()
    {
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);

        return GC.GetTotalMemory(forceFullCollection: true);
    }

    /// <summary>
    /// The large object heap's size, which is where the soak found the gigabyte — reported in the
    /// failure so a reader is not left to guess which generation holds it.
    /// </summary>
    private static double LargeObjectHeap()
    {
        GCMemoryInfo gc = GC.GetGCMemoryInfo();

        return gc.GenerationInfo.Length > 3
            ? gc.GenerationInfo[3].SizeAfterBytes / (1024.0 * 1024.0)
            : 0;
    }

    private static ValueTask<SshHostKeyVerdict> Trusting(SshEndpoint _, SshHostKey __,
                                                         CancellationToken ___) =>
        ValueTask.FromResult(SshHostKeyVerdict.Accept);

    private static string Fixture() =>
        Path.Combine(RepositoryRoot(), "prototypes", "SshProbe", "fixture", "keys");

    private static void SkipWithoutFixture()
    {
        bool up;

        try
        {
            using TcpClient probe = new();

            up = probe.ConnectAsync(Host, Port).Wait(TimeSpan.FromSeconds(2));
        }
        catch (Exception)
        {
            up = false;
        }

        Assert.SkipUnless(up, "nothing is listening on 127.0.0.1:2222: run prototypes/SshProbe/fixture/up.sh");
    }

    private static string RepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Quickshell.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);

        return directory.FullName;
    }
}
