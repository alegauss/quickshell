using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using Quickshell.App;
using Quickshell.Terminal;
using Quickshell.Transport;
using Xunit;

namespace Quickshell.App.Tests;

/// <summary>
/// QS293: a session asks Pageant for a key, through its window, beside Windows' OpenSSH agent.
/// </summary>
public sealed class PageantSessionTests
{
    private const int Port = 2240;
    private const string Folder = @"C:\ProgramData\quickshell-cases";

    private static CancellationToken Stop => TestContext.Current.CancellationToken;

    /// <summary>Pageant alone is offered as an agent where it is the only one that runs.</summary>
    [Fact]
    public void PageantIsOfferedWhereItRuns()
    {
        IReadOnlyList<SshCredential> offered = RemoteShell.Credentials(Unkeyed(), openSsh: false, pageant: true);

        Assert.Equal([new SshCredential.Agent(SshAgent.PageantWindow)], offered.OfType<SshCredential.Agent>());
    }

    /// <summary>Both are offered where both run, Windows' own first; neither where neither does.</summary>
    [Fact]
    public void BothAgentsAreOfferedWindowsOwnFirst()
    {
        Assert.Equal([new SshCredential.Agent(), new SshCredential.Agent(SshAgent.PageantWindow)],
                     RemoteShell.Credentials(Unkeyed(), openSsh: true, pageant: true).OfType<SshCredential.Agent>());

        Assert.Empty(RemoteShell.Credentials(Unkeyed(), openSsh: false, pageant: false).OfType<SshCredential.Agent>());
    }

    /// <summary>
    /// The falsification, in the guest: a saved session that names no key opens on the guest's sshd,
    /// whose probe account trusts one key, held by the Pageant 0.77 that
    /// tools/provision-guest-pageant.ps1 placed — and by nothing this session could read itself.
    /// </summary>
    [Fact]
    public async Task ASessionOpensWithAKeyOnlyPageantHolds()
    {
        string exe = Path.Combine(Folder, "pageant.exe");
        string ppk = Path.Combine(Folder, "probe.ppk");

        Assert.SkipUnless(SshFixture.Listening(Port) && File.Exists(exe) && File.Exists(ppk),
            "no Pageant with the case key, or no server trusting it: run tools/provision-guest-sshd.ps1 and then tools/provision-guest-pageant.ps1, which set both up in the guest");

        Assert.SkipWhen(PageantOpen, "a Pageant this did not start is open, and would answer instead");

        using Process pageant = Process.Start(new ProcessStartInfo(exe, $"\"{ppk}\"") { UseShellExecute = false })
            ?? throw new InvalidOperationException($"{exe} did not start");

        try
        {
            Stopwatch waited = Stopwatch.StartNew();

            while (!PageantOpen && waited.Elapsed < TimeSpan.FromSeconds(10))
            {
                await Task.Delay(50, Stop);
            }

            ResolvedSession session = SessionTree.Of(new SessionNode
            {
                Children = [new SessionNode { Name = "agent", Host = "127.0.0.1", Settings = new SessionSettings { User = "probe", Port = Port } }],
            }).Session("agent")!;

            // Pageant's, and a key file on this desk would not do: probe's authorized_keys holds the
            // case key alone, and the session names no file, so only an agent can sign.
            Assert.Contains(new SshCredential.Agent(SshAgent.PageantWindow), RemoteShell.Credentials(session));

            TrustOnFirstUse trust = new(KnownHosts.ReadFrom(Path.Combine(Path.GetTempPath(), $"qs293-{Guid.NewGuid():N}")),
                                        (_, _) => ValueTask.FromResult(SshHostKeyVerdict.Accept));

            // Opened is signed in: OpenAsync throws on a refused credential.
            await using RemoteShell shell = await RemoteShell.OpenAsync(session, trust, new Emulator(80, 25), new DamageSignal(), 80, 25, Stop);

            Assert.Equal(1, shell.Connections);
        }
        finally
        {
            if (!pageant.HasExited)
            {
                pageant.Kill();
                await pageant.WaitForExitAsync(Stop);
            }
        }
    }

    private static ResolvedSession Unkeyed() =>
        SessionTree.Of(new SessionNode
        {
            Children = [new SessionNode { Name = "unkeyed", Host = "host.example" }],
        }).Session("unkeyed")!;

    private static bool PageantOpen => new SshAgent(SshAgent.PageantWindow).IsRunning;
}
