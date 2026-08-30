using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using Quickshell.Transport;
using Xunit;

namespace Quickshell.Transport.Tests;

/// <summary>
/// File transfer as a channel of a session that already exists.
///
/// <para><b>The falsification is checked against the server and not against this client.</b> Whether
/// a second authentication happened is not something the client can be trusted to report — it is a
/// fact about what the far end saw, so these tests read the fixture's own sshd log. A client that
/// quietly opened a second connection would still say it had not.</para>
/// </summary>
public sealed class SftpChannelTests
{
    private const string Host = "127.0.0.1";
    private const int Port = 2222;

    private static CancellationToken Stop => TestContext.Current.CancellationToken;

    private static ValueTask<SshHostKeyVerdict> Trusting(SshEndpoint _, SshHostKey __, CancellationToken ___) =>
        ValueTask.FromResult(SshHostKeyVerdict.Accept);

    // ---- The falsification ----

    /// <summary>
    /// The line's own falsification: opening the file browser costs no credential the session has
    /// already used.
    ///
    /// <para>The server's log is the witness. One <c>Accepted publickey</c> appears for the whole
    /// exchange, and the <c>sftp</c> subsystem starts on the same source port as the shell — which
    /// is to say on the same TCP connection. A second connection would show a second acceptance from
    /// a different port, and that is exactly what most clients produce.</para>
    /// </summary>
    [Fact]
    public async Task OpeningFileTransferCostsNoSecondAuthentication()
    {
        SkipWithoutFixture();
        SkipWithoutDocker();

        int before = LinesSoFar();

        await using SshNetTransport session = new();

        await session.ConnectAsync(SshEndpoint.For(Host, "probe", Port), [Key()], Trusting, Stop);

        await using IPtyChannel shell = await session.OpenShellAsync(80, 25, Stop);
        await using IFileTransferChannel files = await session.OpenFileTransferAsync(Stop);

        // Something real over each, so neither is merely opened.
        await shell.WriteAsync(Encoding.ASCII.GetBytes("true\r"), Stop);

        _ = await files.StatAsync(".", Stop);

        string[] said = Since(before);

        string[] accepted = [.. said.Where(line =>
            line.Contains("Accepted publickey", StringComparison.Ordinal))];

        string[] subsystem = [.. said.Where(line =>
            line.Contains("subsystem 'sftp'", StringComparison.Ordinal))];

        // One authentication for the whole exchange.
        Assert.Single(accepted);
        Assert.Single(subsystem);

        // And the file channel arrived on the connection that authentication opened: the server
        // names the source port on both lines, and they are the same port.
        Assert.Equal(SourcePort(accepted[0]), SourcePort(subsystem[0]));
    }

    /// <summary>
    /// Version three at least, which is what nearly every server speaks and so the floor this can
    /// rely on.
    /// </summary>
    [Fact]
    public async Task TheChannelSpeaksAtLeastVersionThree()
    {
        SkipWithoutFixture();

        await using SshNetTransport session = new();

        await session.ConnectAsync(SshEndpoint.For(Host, "probe", Port), [Key()], Trusting, Stop);

        await using IFileTransferChannel files = await session.OpenFileTransferAsync(Stop);

        Assert.True(files.ProtocolVersion >= 3,
                    $"the server agreed version {files.ProtocolVersion}");

        // And a relative path is taken from the account's home, as the far side reports it.
        Assert.Equal("/home/probe", files.WorkingDirectory);
    }

    /// <summary>Closing the session closes the file channel with it, because it is one connection.</summary>
    [Fact]
    public async Task ClosingTheSessionClosesTheFileChannel()
    {
        SkipWithoutFixture();

        SshNetTransport session = new();

        await session.ConnectAsync(SshEndpoint.For(Host, "probe", Port), [Key()], Trusting, Stop);

        IFileTransferChannel files = await session.OpenFileTransferAsync(Stop);

        _ = await files.StatAsync(".", Stop);

        await session.DisposeAsync();

        await Assert.ThrowsAnyAsync<Exception>(async () => await files.StatAsync(".", Stop));
    }

    // ---- The ordinary operations ----

    /// <summary>Every operation the design names, against a real server, in one round.</summary>
    [Fact]
    public async Task TheOperationsAreTheOnesADesignNames()
    {
        SkipWithoutFixture();

        await using SshNetTransport session = new();

        await session.ConnectAsync(SshEndpoint.For(Host, "probe", Port), [Key()], Trusting, Stop);

        await using IFileTransferChannel files = await session.OpenFileTransferAsync(Stop);

        string mine = $"/tmp/qs-{Guid.NewGuid():N}";

        await files.CreateDirectoryAsync(mine, Stop);

        try
        {
            // write
            await using (Stream writing = await files.OpenWriteAsync($"{mine}/one", Stop))
            {
                await writing.WriteAsync(Encoding.UTF8.GetBytes("hello"), Stop);
            }

            // read
            await using (Stream reading = await files.OpenReadAsync($"{mine}/one", Stop))
            {
                using StreamReader text = new(reading);

                Assert.Equal("hello", await text.ReadToEndAsync(Stop));
            }

            // stat
            RemoteEntry one = await files.StatAsync($"{mine}/one", Stop);

            Assert.Equal("one", one.Name);
            Assert.Equal(5, one.Length);
            Assert.False(one.IsDirectory);

            // chmod, and the mode comes back as a person reads it
            await files.ChangePermissionsAsync($"{mine}/one", 0b_110_100_100, Stop);

            Assert.Equal("-rw-r--r--", (await files.StatAsync($"{mine}/one", Stop)).Permissions);

            // set times
            DateTimeOffset then = new(2001, 2, 3, 4, 5, 6, TimeSpan.Zero);

            await files.SetLastWriteTimeAsync($"{mine}/one", then, Stop);

            Assert.Equal(then, (await files.StatAsync($"{mine}/one", Stop)).Modified);

            // symlink, and the two ways of looking at one differ on purpose: a listing reports the
            // link as a link, while a stat answers what is at the path and so follows it.
            await files.SymbolicLinkAsync($"{mine}/one", $"{mine}/link", Stop);

            Assert.StartsWith("-", (await files.StatAsync($"{mine}/link", Stop)).Permissions,
                              StringComparison.Ordinal);

            Assert.StartsWith("l", (await Only(files, mine, "link")).Permissions,
                              StringComparison.Ordinal);

            // rename
            await files.RenameAsync($"{mine}/one", $"{mine}/two", Stop);

            // list
            List<RemoteEntry> here = [];

            await foreach (RemoteEntry entry in files.ListAsync(mine, Stop))
            {
                here.Add(entry);
            }

            Assert.Contains(here, entry => entry.Name == "two");
            Assert.DoesNotContain(here, entry => entry.Name == "one");

            // remove
            await files.DeleteAsync($"{mine}/two", Stop);
            await files.DeleteAsync($"{mine}/link", Stop);

            await Assert.ThrowsAsync<SshException>(async () =>
                await files.StatAsync($"{mine}/two", Stop));
        }
        finally
        {
            await Quietly(files, mine);
        }
    }

    /// <summary>
    /// A whole file moves through the pipelined member and arrives byte for byte.
    ///
    /// <para>Two megabytes, which is enough that a request-and-wait implementation would be visibly
    /// slower and not enough to make the suite tiresome.</para>
    /// </summary>
    [Fact]
    public async Task AFileMovesBothWaysAndArrivesUnchanged()
    {
        SkipWithoutFixture();

        await using SshNetTransport session = new();

        await session.ConnectAsync(SshEndpoint.For(Host, "probe", Port), [Key()], Trusting, Stop);

        await using IFileTransferChannel files = await session.OpenFileTransferAsync(Stop);

        string mine = $"/tmp/qs-{Guid.NewGuid():N}";

        byte[] sent = new byte[2 * 1024 * 1024];

        Random.Shared.NextBytes(sent);

        List<long> reported = [];

        try
        {
            using MemoryStream source = new(sent);

            await files.UploadAsync(source, mine,
                                    new Progress<long>(moved => reported.Add(moved)), Stop);

            using MemoryStream back = new();

            await files.DownloadAsync(mine, back, null, Stop);

            Assert.Equal(sent.Length, back.Length);
            Assert.Equal(sent, back.ToArray());
        }
        finally
        {
            await Quietly(files, mine);
        }
    }

    // ---- The paths are the server's ----

    /// <summary>
    /// A name Windows could not hold is created, listed and read back exactly as it was given.
    ///
    /// <para>A colon, a backslash, a trailing space and two names differing only in case: every one
    /// of them is legal on the far side and none is on this one. A client that normalised any of
    /// them would rename the user's file, and the user would find out later.</para>
    /// </summary>
    [Fact]
    public async Task ANameWindowsCouldNotHoldSurvivesIntact()
    {
        SkipWithoutFixture();

        await using SshNetTransport session = new();

        await session.ConnectAsync(SshEndpoint.For(Host, "probe", Port), [Key()], Trusting, Stop);

        await using IFileTransferChannel files = await session.OpenFileTransferAsync(Stop);

        string mine = $"/tmp/qs-{Guid.NewGuid():N}";

        await files.CreateDirectoryAsync(mine, Stop);

        string[] awkward = ["a:b", "back\\slash", "trailing ", "Case", "case", "star*", "quote\"d"];

        try
        {
            foreach (string name in awkward)
            {
                await using Stream writing = await files.OpenWriteAsync($"{mine}/{name}", Stop);

                await writing.WriteAsync(Encoding.UTF8.GetBytes(name), Stop);
            }

            List<string> found = [];

            await foreach (RemoteEntry entry in files.ListAsync(mine, Stop))
            {
                if (entry.Name is not ("." or ".."))
                {
                    found.Add(entry.Name);
                }
            }

            // Every one of them, spelled as it was written.
            Assert.Equal([.. awkward.Order(StringComparer.Ordinal)],
                         [.. found.Order(StringComparer.Ordinal)]);

            // Including the two that differ only in case, which are two files and not one.
            Assert.Contains("Case", found);
            Assert.Contains("case", found);
        }
        finally
        {
            foreach (string name in awkward)
            {
                await Quietly(files, $"{mine}/{name}");
            }

            await Quietly(files, mine);
        }
    }

    /// <summary>
    /// Deleting a symbolic link removes the link, and the file it points at is still there.
    ///
    /// <para><b>This is the sharpest edge of "paths belong to the server".</b> The supported route
    /// through SSH.NET resolves a path with the server's own <c>realpath</c> before acting on it,
    /// and <c>realpath</c> follows links — so deleting a shortcut deletes the document and leaves
    /// the shortcut. Nothing warns, and the user finds out later.</para>
    /// </summary>
    [Fact]
    public async Task DeletingALinkRemovesTheLinkAndNotWhatItPointsAt()
    {
        SkipWithoutFixture();

        await using SshNetTransport session = new();

        await session.ConnectAsync(SshEndpoint.For(Host, "probe", Port), [Key()], Trusting, Stop);

        await using IFileTransferChannel files = await session.OpenFileTransferAsync(Stop);

        string mine = $"/tmp/qs-{Guid.NewGuid():N}";

        await files.CreateDirectoryAsync(mine, Stop);

        try
        {
            await using (Stream writing = await files.OpenWriteAsync($"{mine}/document", Stop))
            {
                await writing.WriteAsync(Encoding.UTF8.GetBytes("worth keeping"), Stop);
            }

            await files.SymbolicLinkAsync($"{mine}/document", $"{mine}/shortcut", Stop);

            await files.DeleteAsync($"{mine}/shortcut", Stop);

            List<string> left = [];

            await foreach (RemoteEntry entry in files.ListAsync(mine, Stop))
            {
                if (entry.Name is not ("." or ".."))
                {
                    left.Add(entry.Name);
                }
            }

            // The document, and only the document. The opposite — a surviving shortcut and no
            // document — is what the supported route produces.
            Assert.Equal(["document"], left);

            await using Stream reading = await files.OpenReadAsync($"{mine}/document", Stop);
            using StreamReader text = new(reading);

            Assert.Equal("worth keeping", await text.ReadToEndAsync(Stop));
        }
        finally
        {
            await Quietly(files, $"{mine}/shortcut");
            await Quietly(files, $"{mine}/document");
            await Quietly(files, mine);
        }
    }

    /// <summary>And renaming a link moves the link, leaving what it points at where it was.</summary>
    [Fact]
    public async Task RenamingALinkMovesTheLinkAndNotItsTarget()
    {
        SkipWithoutFixture();

        await using SshNetTransport session = new();

        await session.ConnectAsync(SshEndpoint.For(Host, "probe", Port), [Key()], Trusting, Stop);

        await using IFileTransferChannel files = await session.OpenFileTransferAsync(Stop);

        string mine = $"/tmp/qs-{Guid.NewGuid():N}";

        await files.CreateDirectoryAsync(mine, Stop);

        try
        {
            await using (Stream writing = await files.OpenWriteAsync($"{mine}/document", Stop))
            {
                await writing.WriteAsync(Encoding.UTF8.GetBytes("worth keeping"), Stop);
            }

            await files.SymbolicLinkAsync($"{mine}/document", $"{mine}/shortcut", Stop);

            await files.RenameAsync($"{mine}/shortcut", $"{mine}/moved", Stop);

            List<string> left = [];

            await foreach (RemoteEntry entry in files.ListAsync(mine, Stop))
            {
                if (entry.Name is not ("." or ".."))
                {
                    left.Add(entry.Name);
                }
            }

            Assert.Equal(["document", "moved"], [.. left.Order(StringComparer.Ordinal)]);
        }
        finally
        {
            await Quietly(files, $"{mine}/moved");
            await Quietly(files, $"{mine}/document");
            await Quietly(files, mine);
        }
    }

    /// <summary>A path that is not there is said with the path in it, not with a status code.</summary>
    [Fact]
    public async Task AMissingPathIsNamedInTheFailure()
    {
        SkipWithoutFixture();

        await using SshNetTransport session = new();

        await session.ConnectAsync(SshEndpoint.For(Host, "probe", Port), [Key()], Trusting, Stop);

        await using IFileTransferChannel files = await session.OpenFileTransferAsync(Stop);

        SshException missing = await Assert.ThrowsAsync<SshException>(async () =>
            await files.StatAsync("/tmp/there-is-nothing-here-at-all", Stop));

        Assert.Contains("/tmp/there-is-nothing-here-at-all", missing.Message, StringComparison.Ordinal);
        Assert.Contains("case-sensitive", missing.Remedy, StringComparison.Ordinal);
    }

    // ---- plumbing ----

    /// <summary>One named entry out of a listing, which is how a link is seen as a link.</summary>
    private static async Task<RemoteEntry> Only(IFileTransferChannel files, string directory,
                                                string name)
    {
        await foreach (RemoteEntry entry in files.ListAsync(directory, Stop))
        {
            if (entry.Name == name)
            {
                return entry;
            }
        }

        Assert.Fail($"{name} was not listed in {directory}");

        return default;
    }

    private static async Task Quietly(IFileTransferChannel files, string path)
    {
        try
        {
            await files.DeleteAsync(path, CancellationToken.None);
        }
        catch (Exception)
        {
            // Cleaning up after a test that already failed should not replace its failure.
        }
    }

    /// <summary>The source port the server named on one of its own log lines.</summary>
    private static string SourcePort(string line)
    {
        string[] words = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        int at = Array.LastIndexOf(words, "port");

        Assert.True(at >= 0 && at + 1 < words.Length, $"no port in: {line}");

        return words[at + 1];
    }

    private static int LinesSoFar() => Log().Length;

    private static string[] Since(int before) => [.. Log().Skip(before)];

    /// <summary>What the fixture's sshd has said, which is the only impartial account available.</summary>
    private static string[] Log()
    {
        using Process docker = Process.Start(new ProcessStartInfo("docker", "logs qs-sshd-target")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;

        // sshd writes to stderr under docker, and both are read to keep the pipes from filling.
        string errors = docker.StandardError.ReadToEnd();
        string output = docker.StandardOutput.ReadToEnd();

        docker.WaitForExit();

        return [.. (output + errors).Split('\n', StringSplitOptions.RemoveEmptyEntries)];
    }

    private static SshCredential.PrivateKey Key() =>
        new(Path.Combine(RepositoryRoot(), "prototypes", "SshProbe", "fixture", "keys",
                         "probe_ed25519"));

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

    private static void SkipWithoutDocker()
    {
        bool there;

        try
        {
            there = Log().Length > 0;
        }
        catch (Exception)
        {
            there = false;
        }

        Assert.SkipUnless(there, "docker logs are not readable, so the server cannot be asked what it saw");
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
