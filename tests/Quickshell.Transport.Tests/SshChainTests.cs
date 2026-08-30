using System.Net.Sockets;
using System.Text;
using Quickshell.Transport;
using Xunit;

namespace Quickshell.Transport.Tests;

/// <summary>
/// A session reached through a bastion, against the two-server fixture QS5 built for exactly this.
///
/// <para>The target is reached by its name on the container network — <c>qs-sshd-target</c> — which
/// nothing outside that network can resolve. So a connection that succeeds went through the jump
/// host, and could not have taken the target's own published port by accident.</para>
/// </summary>
public sealed class SshChainTests
{
    private const string Host = "127.0.0.1";
    private const int JumpPort = 2223;
    private const int TargetPort = 2222;

    /// <summary>The target as the jump host sees it, which is the only way this test can reach it.</summary>
    private const string TargetOnTheNetwork = "qs-sshd-target";

    /// <summary>And the jump host as it sees itself, for a chain longer than two.</summary>
    private const string JumpOnTheNetwork = "qs-sshd-jump";

    private static CancellationToken Stop => TestContext.Current.CancellationToken;

    private static ValueTask<SshHostKeyVerdict> Trusting(SshEndpoint _, SshHostKey __, CancellationToken ___) =>
        ValueTask.FromResult(SshHostKeyVerdict.Accept);

    private static SshCredential.PrivateKey Key() =>
        new(Path.Combine(RepositoryRoot(), "prototypes", "SshProbe", "fixture", "keys", "probe_ed25519"));

    private static SshHop Hop(string host, int port, SshHostKeyCheck? check = null) =>
        new(SshEndpoint.For(host, "probe", port), [Key()], check ?? Trusting);

    // ---- Reaching what cannot be reached directly ----

    /// <summary>
    /// The symptom, gone: a host reachable only through a bastion is reached.
    ///
    /// <para><c>qs-sshd-target</c> is a name on the container network and nothing here can resolve
    /// it, so the session that opens is one carried inside the jump host's connection.</para>
    /// </summary>
    [Fact]
    public async Task AHostReachableOnlyThroughABastionIsReached()
    {
        SkipWithoutFixture();

        await using SshChain chain = new([
            Hop(Host, JumpPort),
            Hop(TargetOnTheNetwork, 22),
        ]);

        await chain.ConnectAsync(chain.Endpoint, [Key()], Trusting, Stop);

        Assert.True(chain.IsConnected);
        Assert.Equal(2, chain.Hops);

        await using IPtyChannel channel = await chain.OpenShellAsync(80, 25, Stop);

        // The far end says which machine it is, and it is not the bastion.
        await channel.WriteAsync(Encoding.ASCII.GetBytes("hostname\r"), Stop);

        Assert.Contains("target", await Until(channel, "target"), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// And the same code path carries three hops. The design asks for it explicitly, and the way to
    /// be sure is that there is only one path: this is the loop running three times.
    /// </summary>
    [Fact]
    public async Task AThreeHopChainTakesTheSamePathAsATwoHopOne()
    {
        SkipWithoutFixture();

        await using SshChain chain = new([
            Hop(Host, JumpPort),
            Hop(JumpOnTheNetwork, 22),
            Hop(TargetOnTheNetwork, 22),
        ]);

        await chain.ConnectAsync(chain.Endpoint, [Key()], Trusting, Stop);

        Assert.True(chain.IsConnected);
        Assert.Equal(3, chain.Hops);

        await using IPtyChannel channel = await chain.OpenShellAsync(80, 25, Stop);

        await channel.WriteAsync(Encoding.ASCII.GetBytes("hostname\r"), Stop);

        Assert.Contains("target", await Until(channel, "target"), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>One hop is a plain connection, through the same loop with one iteration.</summary>
    [Fact]
    public async Task OneHopIsAnOrdinaryConnection()
    {
        SkipWithoutFixture();

        await using SshChain chain = new([Hop(Host, TargetPort)]);

        await chain.ConnectAsync(chain.Endpoint, [Key()], Trusting, Stop);

        Assert.True(chain.IsConnected);
        Assert.Equal(1, chain.Hops);
    }

    // ---- The falsification: which hop failed ----

    /// <summary>
    /// The line's own falsification: a failure in a two-hop chain names the hop that failed.
    ///
    /// <para>The first hop is a port with nothing on it, so the failure is the bastion's and the
    /// message must say so. A bare connection-refused with no hop named is the least useful message
    /// a chain can produce.</para>
    /// </summary>
    [Fact]
    public async Task AFailureAtTheFirstHopNamesTheFirstHop()
    {
        await using SshChain chain = new([
            Hop("127.0.0.1", 2),
            Hop(TargetOnTheNetwork, 22),
        ]);

        SshException failed = await Assert.ThrowsAsync<SshException>(async () =>
            await chain.ConnectAsync(chain.Endpoint, [Key()], Trusting, Stop));

        Assert.Contains("Hop 1 of 2", failed.Message, StringComparison.Ordinal);
        Assert.Contains("127.0.0.1", failed.Message, StringComparison.Ordinal);
        Assert.Equal(SshFailureKind.Refused, failed.Kind);
    }

    /// <summary>
    /// And a failure at the second names the second — with the hop's real name rather than the local
    /// address it was reached at, because a user who wrote a bastion's name is looking for that name.
    /// </summary>
    [Fact]
    public async Task AFailureAtTheSecondHopNamesTheSecondHop()
    {
        SkipWithoutFixture();

        await using SshChain chain = new([
            Hop(Host, JumpPort),
            new SshHop(SshEndpoint.For("no-such-host-on-the-network", "probe", 22), [Key()], Trusting),
        ]);

        SshException failed = await Assert.ThrowsAsync<SshException>(async () =>
            await chain.ConnectAsync(chain.Endpoint, [Key()], Trusting, Stop));

        Assert.Contains("Hop 2 of 2", failed.Message, StringComparison.Ordinal);
        Assert.Contains("no-such-host-on-the-network", failed.Message, StringComparison.Ordinal);

        // And not this client's own plumbing, which is what the user did not write down.
        Assert.DoesNotContain("Hop 1", failed.Message, StringComparison.Ordinal);
    }

    // ---- Whose key, and whose credentials ----

    /// <summary>
    /// The target's own host key is checked, not the bastion's — and each hop's is checked with the
    /// delegate that hop was given.
    ///
    /// <para>A chain does not inherit trust from the hop that carried it, which is the whole security
    /// claim of doing this properly rather than tunnelling and hoping. Two different fingerprints
    /// arriving in order is what says two different machines were verified.</para>
    /// </summary>
    [Fact]
    public async Task EachHopsOwnHostKeyIsCheckedWithItsOwnDelegate()
    {
        SkipWithoutFixture();

        List<(string Which, string Fingerprint)> asked = [];

        SshHostKeyCheck Recording(string which) =>
            (_, key, _) =>
            {
                asked.Add((which, key.Fingerprint));

                return ValueTask.FromResult(SshHostKeyVerdict.Accept);
            };

        await using SshChain chain = new([
            new SshHop(SshEndpoint.For(Host, "probe", JumpPort), [Key()], Recording("jump")),
            new SshHop(SshEndpoint.For(TargetOnTheNetwork, "probe", 22), [Key()], Recording("target")),
        ]);

        await chain.ConnectAsync(chain.Endpoint, [Key()], Trusting, Stop);

        Assert.Equal(["jump", "target"], asked.Select(hop => hop.Which));

        // Two machines, two keys. One fingerprint twice would mean the second hop had been checked
        // against the bastion, which is the failure this test exists for.
        Assert.NotEqual(asked[0].Fingerprint, asked[1].Fingerprint);
    }

    /// <summary>A chain with no hops has nowhere to go, and says so where it is built.</summary>
    [Fact]
    public void AChainWithNoHopsIsRefusedWhereItIsBuilt()
    {
        SshException empty = Assert.Throws<SshException>(() => new SshChain([]));

        Assert.Contains("nowhere to connect", empty.Message, StringComparison.Ordinal);
    }

    /// <summary>Channels are refused before the chain is connected, naming how many hops are shut.</summary>
    [Fact]
    public async Task ChannelsAreRefusedBeforeTheChainIsOpen()
    {
        await using SshChain chain = new([Hop(Host, JumpPort), Hop(TargetOnTheNetwork, 22)]);

        SshException refused = await Assert.ThrowsAsync<SshException>(async () =>
            await chain.OpenShellAsync(80, 25, Stop));

        Assert.Equal(SshFailureKind.Dropped, refused.Kind);
        Assert.Contains("2 hops", refused.Means, StringComparison.Ordinal);
    }

    // ---- plumbing ----

    private static async Task<string> Until(IPtyChannel channel, string wanted)
    {
        StringBuilder seen = new();
        byte[] buffer = new byte[8 * 1024];

        using CancellationTokenSource waiting = CancellationTokenSource.CreateLinkedTokenSource(Stop);
        waiting.CancelAfter(TimeSpan.FromSeconds(20));

        try
        {
            while (!seen.ToString().Contains(wanted, StringComparison.OrdinalIgnoreCase))
            {
                int read = await channel.ReadAsync(buffer, waiting.Token);

                if (read == 0)
                {
                    break;
                }

                seen.Append(Encoding.UTF8.GetString(buffer, 0, read));
            }
        }
        catch (OperationCanceledException)
        {
        }

        return seen.ToString();
    }

    private static void SkipWithoutFixture()
    {
        bool up;

        try
        {
            using TcpClient probe = new();

            up = probe.ConnectAsync(Host, JumpPort).Wait(TimeSpan.FromSeconds(2));
        }
        catch (Exception)
        {
            up = false;
        }

        Assert.SkipUnless(up, "nothing is listening on 127.0.0.1:2223: run prototypes/SshProbe/fixture/up.sh");
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
