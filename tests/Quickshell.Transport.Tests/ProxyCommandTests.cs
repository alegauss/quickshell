using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using Quickshell.Transport;
using Xunit;

namespace Quickshell.Transport.Tests;

/// <summary>
/// The escape hatch, exercised with the escape hatch people actually write: <c>ssh -W</c>.
///
/// <para>Windows ships OpenSSH, so the proxy command here is a real one and not a stand-in. A test
/// that invented its own protocol would prove this client can talk to that invention and nothing
/// about the configs it will meet.</para>
/// </summary>
public sealed class ProxyCommandTests : IDisposable
{
    private readonly string _own =
        Path.Combine(Path.GetTempPath(), $"quickshell-proxy-{Guid.NewGuid():N}");

    private const string Host = "127.0.0.1";
    private const int JumpPort = 2223;

    /// <summary>Reachable only on the container network, so reaching it proves the program worked.</summary>
    private const string TargetOnTheNetwork = "qs-sshd-target";

    private static CancellationToken Stop => TestContext.Current.CancellationToken;

    private static ValueTask<SshHostKeyVerdict> Trusting(SshEndpoint _, SshHostKey __, CancellationToken ___) =>
        ValueTask.FromResult(SshHostKeyVerdict.Accept);

    // ---- The tokens ----

    /// <summary>
    /// Every token OpenSSH substitutes, substituted. A client that expanded these differently would
    /// run a command line the user has already tested under <c>ssh</c> and get a different program.
    /// </summary>
    [Fact]
    public void TheTokensAreTheOnesOpenSshSubstitutes()
    {
        SshEndpoint target = SshEndpoint.For("example.test", "deploy", 2200);

        Assert.Equal("connect example.test 2200 deploy",
                     ProxyCommandChannel.Expand("connect %h %p %r", target));
    }

    /// <summary>
    /// A doubled percent is one percent and stops there. Handled in any other pass, <c>%%h</c> would
    /// become the host name, and a command line with a literal percent in it would silently change.
    /// </summary>
    [Fact]
    public void ADoubledPercentIsALiteralOneAndDoesNotEatWhatFollows()
    {
        SshEndpoint target = SshEndpoint.For("example.test", "deploy", 22);

        Assert.Equal("give 50%h back", ProxyCommandChannel.Expand("give 50%%h back", target));
    }

    /// <summary>A token this does not know is left exactly as it was, rather than deleted.</summary>
    [Fact]
    public void AnUnknownTokenIsLeftAlone()
    {
        SshEndpoint target = SshEndpoint.For("example.test", "deploy", 22);

        Assert.Equal("helper %j and %", ProxyCommandChannel.Expand("helper %j and %", target));
    }

    /// <summary>A program in a path with a space in it, which is most programs on this platform.</summary>
    [Fact]
    public void AQuotedProgramKeepsItsPathAndLosesItsQuotes()
    {
        (string program, string arguments) =
            ProxyCommandChannel.Split("\"C:\\Program Files\\thing\\connect.exe\" -W host:22");

        Assert.Equal("C:\\Program Files\\thing\\connect.exe", program);
        Assert.Equal("-W host:22", arguments);
    }

    /// <summary>And the rest of the line is handed on exactly as the user wrote it.</summary>
    [Fact]
    public void TheArgumentsAreNotRewritten()
    {
        (string program, string arguments) =
            ProxyCommandChannel.Split("corkscrew proxy 8080 \"a host\" 22");

        Assert.Equal("corkscrew", program);
        Assert.Equal("proxy 8080 \"a host\" 22", arguments);
    }

    // ---- Running one ----

    /// <summary>
    /// The symptom this exists for: a host no library here can reach, reached by a program that can.
    ///
    /// <para><c>qs-sshd-target</c> resolves only on the container network. The session that opens is
    /// carried by <c>ssh -W</c> over its own standard streams, and this client verified the target's
    /// own host key at the far end of it.</para>
    /// </summary>
    [Fact]
    public async Task AHostIsReachedByRunningAProgramThatKnowsHowTo()
    {
        SkipWithoutFixture();
        SkipWithoutOpenSsh();

        await using SshChain chain = new([
            new SshHop(SshEndpoint.For(TargetOnTheNetwork, "probe", 22), [Key()], Trusting,
                       ProxyCommand: Through()),
        ]);

        await chain.ConnectAsync(chain.Endpoint, [Key()], Trusting, Stop);

        Assert.True(chain.IsConnected);

        await using IPtyChannel channel = await chain.OpenShellAsync(80, 25, Stop);

        await channel.WriteAsync(Encoding.ASCII.GetBytes("hostname\r"), Stop);

        Assert.Contains("qs-sshd-target", await Until(channel, "qs-sshd-target"),
                        StringComparison.OrdinalIgnoreCase);
    }

    // ---- Failing to run one ----

    /// <summary>
    /// A program that is not there says so, naming the program. The alternative is a connection that
    /// fails with a socket error about a local port the user never wrote down.
    /// </summary>
    [Fact]
    public async Task AProgramThatIsNotThereNamesItself()
    {
        SshException missing = await Assert.ThrowsAsync<SshException>(async () =>
            await ProxyCommandChannel.StartAsync("no-such-proxy-program-at-all --go",
                                                 SshEndpoint.For("example.test", "probe"), Stop));

        Assert.Contains("no-such-proxy-program-at-all", missing.Message, StringComparison.Ordinal);
        Assert.Contains("ProxyCommand", missing.Remedy, StringComparison.Ordinal);
    }

    /// <summary>
    /// The design's real point: what the program printed is what says why it failed.
    ///
    /// <para>A proxy command fails by exiting, which from SSH.NET's side is a link that dropped for
    /// no reason. Losing the program's own message leaves a user with an error that describes this
    /// client's plumbing and says nothing about the thing that actually went wrong.</para>
    /// </summary>
    [Fact]
    public async Task WhatTheProgramPrintedSurvivesIntoTheFailure()
    {
        await using SshChain chain = new([
            new SshHop(SshEndpoint.For("example.test", "probe", 22), [Key()], Trusting,
                       ProxyCommand: "cmd /c \"echo the vpn is not up 1>&2 & exit 1\""),
        ]);

        SshException failed = await Assert.ThrowsAsync<SshException>(async () =>
            await chain.ConnectAsync(chain.Endpoint, [Key()], Trusting, Stop));

        Assert.Contains("Hop 1 of 1", failed.Message, StringComparison.Ordinal);
        Assert.Contains("the vpn is not up", failed.Means, StringComparison.Ordinal);
    }

    /// <summary>
    /// The program is not left behind when the channel goes away.
    ///
    /// <para>A proxy command outliving its session is a process holding a connection open for as
    /// long as the client runs, one per session opened and closed — which on a busy day is a client
    /// that has quietly accumulated a hundred of them.</para>
    /// </summary>
    [Fact]
    public async Task TheProgramDoesNotOutliveTheChannel()
    {
        SkipWithoutFixture();
        SkipWithoutOpenSsh();

        ProxyCommandChannel proxy = await ProxyCommandChannel.StartAsync(
            Through(), SshEndpoint.For(TargetOnTheNetwork, "probe", 22), Stop);

        Assert.True(proxy.IsRunning);

        int running = Assert.NotNull(proxy.Id);

        await proxy.DisposeAsync();

        Assert.False(proxy.IsRunning);

        // And the operating system agrees, which is the only check that means anything: a flag this
        // class sets on itself would say "gone" whether or not the process were.
        Assert.Throws<ArgumentException>(() => Process.GetProcessById(running));
    }

    // ---- plumbing ----

    /// <summary>
    /// The command line a user would actually write, pointed at the fixture's jump host.
    ///
    /// <para>Host key checking is turned off for OpenSSH here and only here: this is the fixture's
    /// bastion, its key changes every time the container is recreated, and what this test is about
    /// is the hop beyond it — whose key <em>is</em> checked, by this client.</para>
    /// </summary>
    private string Through() =>
        $"\"{OpenSsh()}\" -W %h:%p -p {JumpPort} -i \"{Guarded()}\" "
        + "-o StrictHostKeyChecking=no -o UserKnownHostsFile=NUL -o LogLevel=ERROR "
        + $"probe@{Host}";

    /// <summary>
    /// The fixture's key, copied somewhere only this account can read it.
    ///
    /// <para>Windows' OpenSSH refuses a private key whose ACL lets anybody else read it, and a key
    /// committed to a repository is readable by everything the repository is. So the test does what
    /// a user on this platform has to do: takes its own copy and closes it. Not a workaround for the
    /// check — the check is right, and the key in the repository really is public.</para>
    /// </summary>
    private string Guarded()
    {
        Directory.CreateDirectory(_own);

        string mine = Path.Combine(_own, "probe_ed25519");

        if (File.Exists(mine))
        {
            return mine;
        }

        File.Copy(KeyFile(), mine);

        using Process icacls = Process.Start(new ProcessStartInfo(
            "icacls", $"\"{mine}\" /inheritance:r /grant:r \"{Environment.UserName}:(R)\"")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        })!;

        icacls.WaitForExit();

        Assert.Equal(0, icacls.ExitCode);

        return mine;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (Directory.Exists(_own))
        {
            Directory.Delete(_own, recursive: true);
        }
    }

    private static string KeyFile() =>
        Path.Combine(RepositoryRoot(), "prototypes", "SshProbe", "fixture", "keys", "probe_ed25519");

    private static SshCredential.PrivateKey Key() => new(KeyFile());

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

    /// <summary>
    /// Windows' own OpenSSH, by its full path.
    ///
    /// <para>Named rather than looked up on PATH: this machine has two <c>ssh.exe</c> on it, and a
    /// test that ran whichever one PATH happened to reach first would pass or fail depending on which
    /// shell started it — which is a test that measures the developer's environment.</para>
    /// </summary>
    private static string OpenSsh() =>
        Path.Combine(Environment.SystemDirectory, "OpenSSH", "ssh.exe");

    private static void SkipWithoutOpenSsh() =>
        Assert.SkipUnless(File.Exists(OpenSsh()),
                          "the Windows OpenSSH client is not installed, so there is no real proxy command to run");

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
