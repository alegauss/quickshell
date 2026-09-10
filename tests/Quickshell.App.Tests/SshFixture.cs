using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using Quickshell.Transport;
using Xunit;

namespace Quickshell.App.Tests;

/// <summary>
/// The SSH servers `prototypes/SshProbe/fixture/up.sh` brings up, as the file browser's tests reach
/// them: a real sshd, so what is claimed about a remote directory is claimed about a server and not
/// about a fake that says what the test wants.
///
/// <para>A test that needs it is skipped with the command that brings it up when nothing is
/// listening, never failed and never quietly passed.</para>
/// </summary>
internal static class SshFixture
{
    private const string Host = "127.0.0.1";
    private const int Port = 2222;

    /// <summary>The container behind that port, which a test asks to prepare a directory.</summary>
    public const string Container = "qs-sshd-target";

    /// <summary>What the pane naming that server is headed with.</summary>
    public const string Title = "qs-sshd-target";

    /// <summary>Skips the calling test where nothing is listening.</summary>
    public static void SkipWithoutIt()
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

    /// <summary>A session to the target as the probe account, trusting its key.</summary>
    public static async Task<SshNetTransport> ConnectAsync(CancellationToken cancellationToken)
    {
        SshNetTransport session = new();

        await session.ConnectAsync(SshEndpoint.For(Host, "probe", Port), [Key()],
                                   (_, _, _) => ValueTask.FromResult(SshHostKeyVerdict.Accept),
                                   cancellationToken);

        return session;
    }

    /// <summary>Runs a command in the target container as root, and says whether it worked.</summary>
    public static bool Docker(string command)
    {
        try
        {
            using Process docker = Process.Start(new ProcessStartInfo("docker")
            {
                ArgumentList = { "exec", Container, "sh", "-c", command },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            })!;

            docker.StandardOutput.ReadToEnd();
            docker.StandardError.ReadToEnd();
            docker.WaitForExit();

            return docker.ExitCode == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static SshCredential.PrivateKey Key() =>
        new(Path.Combine(RepositoryRoot(), "prototypes", "SshProbe", "fixture", "keys", "probe_ed25519"));

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
