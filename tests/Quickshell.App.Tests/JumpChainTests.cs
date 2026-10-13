using System.IO;
using Quickshell.App;
using Quickshell.Terminal;
using Quickshell.Transport;
using Xunit;

namespace Quickshell.App.Tests;

/// <summary>QS297: a session's JumpHost names a chain as OpenSSH does, a comma between hops.</summary>
public sealed class JumpChainTests
{
    private const int Port = 2240;
    private const string Key = @"C:\ProgramData\quickshell-cases\probe_ed25519";

    private static CancellationToken Stop => TestContext.Current.CancellationToken;

    /// <summary>Each hop in order, each with its own user and port, or the session's user and 22.</summary>
    [Fact]
    public void AChainIsReadHopByHop()
    {
        IReadOnlyList<SshEndpoint> hops = RemoteShell.Hops("jumper@10.0.0.1, ops@10.0.0.2:2200,10.0.0.3", "alex");

        Assert.Equal(["10.0.0.1", "10.0.0.2", "10.0.0.3"], hops.Select(hop => hop.Host));
        Assert.Equal(["jumper", "ops", "alex"], hops.Select(hop => hop.User));
        Assert.Equal([22, 2200, 22], hops.Select(hop => hop.Port));
    }

    /// <summary>
    /// In the guest: a saved session through two jumps — the guest's own sshd twice, then itself as
    /// the target, which each hop reaches as 127.0.0.1 — opens.
    /// </summary>
    [Fact]
    public async Task ASessionThroughTwoJumpsOpens()
    {
        Assert.SkipUnless(SshFixture.Listening(Port) && File.Exists(Key),
            $"Windows OpenSSH is not on 127.0.0.1:{Port}: run tools/provision-guest-sshd.ps1 once, which sets it up in the guest");

        ResolvedSession session = SessionTree.Of(new SessionNode
        {
            Children =
            [
                new SessionNode
                {
                    Name = "chained",
                    Host = "127.0.0.1",
                    Settings = new SessionSettings
                    {
                        User = "probe",
                        Port = Port,
                        Key = Key,
                        JumpHost = $"probe@127.0.0.1:{Port},probe@127.0.0.1:{Port}",
                    },
                },
            ],
        }).Session("chained")!;

        Assert.Equal(2, RemoteShell.Hops(session.JumpHost!.Value.Value, "probe").Count);

        TrustOnFirstUse trust = new(KnownHosts.ReadFrom(Path.Combine(Path.GetTempPath(), $"qs297-{Guid.NewGuid():N}")),
                                    (_, _) => ValueTask.FromResult(SshHostKeyVerdict.Accept));

        // Opened is signed in at every hop: OpenAsync throws where any of them refuses.
        await using RemoteShell shell = await RemoteShell.OpenAsync(session, trust, new Emulator(80, 25), new DamageSignal(), 80, 25, Stop);

        Assert.Equal(1, shell.Connections);
    }
}
