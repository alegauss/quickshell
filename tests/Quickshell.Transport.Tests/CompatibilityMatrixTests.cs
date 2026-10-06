using System.Net.Sockets;
using System.Text;
using Quickshell.Transport;
using Xunit;

namespace Quickshell.Transport.Tests;

/// <summary>
/// The servers that are not the fixture's OpenSSH 9.6 (QS40), each connected to deliberately: an
/// interoperability failure is found by connecting to an unusual server and by no other method.
///
/// <para><b>A row is the negotiation, not "it worked".</b> Each one records the server's version
/// string and what was agreed for the key exchange, the host key, the cipher and the MAC, from the
/// session log at trace level — an entry saying only that it worked has been visited, not tested.
/// Then a full-screen program runs, because a server that negotiates and then hands the emulator
/// something it cannot draw has not been met either. <c>docs/measurements/compatibility.md</c> is
/// where the rows are written down.</para>
///
/// <para>The servers are containers, <c>prototypes/SshProbe/matrix</c>, so the matrix is
/// reproducible by somebody who owns none of the hardware they stand for. Each row skips by name
/// where its container is not up.</para>
/// </summary>
public sealed class CompatibilityMatrixTests : IDisposable
{
    private const string Host = "127.0.0.1";

    private readonly string _here =
        Path.Combine(Path.GetTempPath(), $"quickshell-matrix-{Guid.NewGuid():N}");

    private static CancellationToken Stop => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        if (Directory.Exists(_here))
        {
            Directory.Delete(_here, recursive: true);
        }
    }

    /// <summary>
    /// Every server connects, says what it agreed to, and runs a full-screen program whose screen
    /// arrives as cursor addressing rather than as lines.
    /// </summary>
    [Theory]
    [InlineData("OpenSSH 9.6 (Ubuntu 24.04)", 2222, "fixture")]
    [InlineData("OpenSSH 8.2 (Ubuntu 20.04)", 2232, "matrix")]
    [InlineData("OpenSSH 7.2 (Ubuntu 16.04)", 2231, "matrix")]
    [InlineData("Dropbear (Alpine 3.20)", 2233, "matrix")]
    [InlineData("OpenSSH 6.6 (Ubuntu 14.04)", 2230, "matrix")]
    public async Task EveryServerNegotiatesAndRunsAFullScreenProgram(string server, int port, string fixture)
    {
        SkipUnlessListening(port, server, fixture);

        await using SessionLog log = SessionLog.InFolder(_here, LogDetail.Trace);

        await using SshNetTransport transport = new() { Log = log };

        await transport.ConnectAsync(SshEndpoint.For(Host, "probe", port), [Key()], Trusting, Stop);

        string agreed = await Agreed(log);

        TestContext.Current.TestOutputHelper?.WriteLine($"{server}\n{agreed}");

        foreach (string what in (string[])["kex", "host key", "cipher", "mac"])
        {
            Assert.Contains($"{what}: ", agreed, StringComparison.Ordinal);
            Assert.DoesNotContain($"{what}: none", agreed, StringComparison.Ordinal);
        }

        await using IPtyChannel channel = await transport.OpenShellAsync(80, 25, Stop);

        // top on every row: procps's on Ubuntu, BusyBox's on Alpine. Both clear the screen and place
        // what they draw by cursor address, which is what makes a program full-screen.
        await channel.WriteAsync("top\n"u8.ToArray(), Stop);

        string screen = await Until(channel, seen =>
            seen.Contains("load average", StringComparison.OrdinalIgnoreCase)
            && seen.Contains("\e[H", StringComparison.Ordinal));

        await channel.WriteAsync("q"u8.ToArray(), Stop);

        Assert.Contains("load average", screen, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// An RSA key, on every row, because the RSA signature is where the versions part: before
    /// OpenSSH 7.2 a server knows only <c>ssh-rsa</c> over SHA-1, and a client that signs only with
    /// rsa-sha2 is refused there with a message about the key rather than about the server's age.
    /// </summary>
    [Theory]
    [InlineData("OpenSSH 8.2 (Ubuntu 20.04)", 2232)]
    [InlineData("OpenSSH 7.2 (Ubuntu 16.04)", 2231)]
    [InlineData("OpenSSH 6.6 (Ubuntu 14.04)", 2230)]
    [InlineData("Dropbear (Alpine 3.20)", 2233)]
    public async Task AnRsaKeyIsAcceptedByEveryServer(string server, int port)
    {
        SkipUnlessListening(port, server, "matrix");

        await using SshNetTransport transport = new();

        SshCredential.PrivateKey rsa = new(Path.Combine(Path.GetDirectoryName(Key().Path)!, "probe_rsa"));

        await transport.ConnectAsync(SshEndpoint.For(Host, "probe", port), [rsa], Trusting, Stop);

        Assert.True(transport.IsConnected, $"{server} did not take an RSA key");
    }

    /// <summary>
    /// The version string and the four agreements, one per line, from the trace the transport wrote.
    /// </summary>
    private static async Task<string> Agreed(SessionLog log)
    {
        StringBuilder rows = new();

        foreach (string file in log.Files)
        {
            await using FileStream reading = new(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

            using StreamReader text = new(reading);

            while (await text.ReadLineAsync(Stop) is { } line)
            {
                int at = line.IndexOf(" versions ", StringComparison.Ordinal);

                if (at >= 0)
                {
                    rows.Append("server: ").AppendLine(After(line, " theirs="));
                }

                at = line.IndexOf(" negotiated what=", StringComparison.Ordinal);

                if (at >= 0)
                {
                    string what = line[(at + " negotiated what=".Length)..line.IndexOf(" ours=", StringComparison.Ordinal)];

                    rows.Append(what).Append(": ").AppendLine(After(line, " chosen="));
                }
            }
        }

        return rows.ToString();
    }

    private static string After(string line, string field)
    {
        int at = line.LastIndexOf(field, StringComparison.Ordinal);

        return at < 0 ? string.Empty : line[(at + field.Length)..];
    }

    /// <summary>Reads until what has arrived satisfies the caller, or gives up saying what did.</summary>
    private static async Task<string> Until(IPtyChannel channel, Func<string, bool> enough)
    {
        StringBuilder seen = new();
        byte[] buffer = new byte[8 * 1024];

        using CancellationTokenSource waiting = CancellationTokenSource.CreateLinkedTokenSource(Stop);

        waiting.CancelAfter(TimeSpan.FromSeconds(15));

        try
        {
            while (!enough(seen.ToString()))
            {
                int read = await channel.ReadAsync(buffer, waiting.Token);

                if (read == 0)
                {
                    break;
                }

                seen.Append(Encoding.UTF8.GetString(buffer, 0, read));
            }
        }
        catch (OperationCanceledException) when (!Stop.IsCancellationRequested)
        {
            // Out of time: the assertion below says what arrived instead.
        }

        Assert.True(enough(seen.ToString()), $"the full-screen program never drew; what arrived: {seen}");

        return seen.ToString();
    }

    private static ValueTask<SshHostKeyVerdict> Trusting(SshEndpoint _, SshHostKey __, CancellationToken ___) =>
        ValueTask.FromResult(SshHostKeyVerdict.Accept);

    private static SshCredential.PrivateKey Key() =>
        new(Path.Combine(RepositoryRoot(), "prototypes", "SshProbe", "fixture", "keys", "probe_ed25519"));

    private static void SkipUnlessListening(int port, string server, string fixture)
    {
        bool up;

        try
        {
            using TcpClient probe = new();

            up = probe.ConnectAsync(Host, port).Wait(TimeSpan.FromSeconds(2));
        }
        catch (Exception failure) when (failure is SocketException or AggregateException)
        {
            up = false;
        }

        Assert.SkipUnless(up && File.Exists(Key().Path),
            $"{server} is not on {Host}:{port}: run prototypes/SshProbe/{fixture}/up.sh");
    }

    private static string RepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Quickshell.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Quickshell.sln was not found above the test.");
    }
}
