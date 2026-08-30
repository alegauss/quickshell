using System.Net.Sockets;
using System.Text;
using Quickshell.Transport;
using Xunit;

namespace Quickshell.Transport.Tests;

/// <summary>
/// The fallback, against a server that really has no sftp subsystem.
///
/// <para>The fixture's <c>nosftp</c> container has the <c>Subsystem sftp</c> line removed from its
/// sshd config, so the refusal comes from a server rather than from a switch this client threw. A
/// fallback tested against a pretend refusal is a fallback nobody has run.</para>
/// </summary>
public sealed class ScpChannelTests : IDisposable
{
    private const string Host = "127.0.0.1";
    private const int Ordinary = 2222;
    private const int WithoutSftp = 2225;

    private readonly string _here =
        Path.Combine(Path.GetTempPath(), $"quickshell-scp-{Guid.NewGuid():N}");

    private static CancellationToken Stop => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        if (Directory.Exists(_here))
        {
            Directory.Delete(_here, recursive: true);
        }
    }

    // ---- The falsification ----

    /// <summary>
    /// The line's own falsification: a name full of shell metacharacters transfers as a name.
    ///
    /// <para>Every one of these is a command on the far side if the quoting is wrong: a semicolon
    /// ends a command, backticks and <c>$()</c> substitute one, <c>&amp;</c> backgrounds one, and a
    /// single quote closes the quoting itself. The file is written, read back, and its contents
    /// compared — and the marker file that a successful injection would have created is checked for
    /// and must not exist.</para>
    /// </summary>
    [Theory]
    [InlineData("semi;colon.txt")]
    [InlineData("back`tick`.txt")]
    [InlineData("dollar$(id).txt")]
    [InlineData("amper&sand.txt")]
    [InlineData("quote'single.txt")]
    [InlineData("pipe|bar.txt")]
    [InlineData("new line.txt")]
    [InlineData("star*glob?.txt")]
    [InlineData("dash-first.txt")]
    public async Task ANameFullOfShellMetacharactersTransfersAsAName(string name)
    {
        SkipWithout(WithoutSftp);

        await using SshNetTransport session = new();

        await session.ConnectAsync(SshEndpoint.For(Host, "probe", WithoutSftp), [Key()], Trusting,
                                   Stop);

        await using IFileCopy copy = await session.OpenFileCopyAsync(Stop);

        Assert.Equal(TransferProtocol.Scp, copy.Protocol);

        string mine = $"/tmp/qs-{Guid.NewGuid():N}";
        string evidence = $"/tmp/qs-injected-{Guid.NewGuid():N}";

        await Run(session, $"mkdir -p {ShellWord.Quote(mine)}");

        byte[] sent = Encoding.UTF8.GetBytes($"the contents of {name}");

        try
        {
            using (MemoryStream from = new(sent))
            {
                await copy.SendAsync(from, sent.Length, $"{mine}/{name}", null, Stop);
            }

            using MemoryStream back = new();

            await copy.ReceiveAsync($"{mine}/{name}", back, null, Stop);

            Assert.Equal(sent, back.ToArray());

            // The name is one file on the far side, spelled exactly as it was given. -N is
            // literal output: GNU ls puts quotes round a name with a metacharacter in it, which
            // would make this test read its own display convention as a transfer failure.
            string listed = await Run(session, $"ls -1N {ShellWord.Quote(mine)}");

            Assert.Equal(name, listed.Trim('\n', '\r'));

            // And nothing in it ran: a substitution would have left this behind.
            Assert.Equal(string.Empty,
                         (await Run(session, $"ls {ShellWord.Quote(evidence)} 2>/dev/null")).Trim());
        }
        finally
        {
            await Run(session, $"rm -rf {ShellWord.Quote(mine)} {ShellWord.Quote(evidence)}");
        }
    }

    /// <summary>
    /// A name that would end the protocol's own record is refused rather than sent.
    ///
    /// <para>There is no escaping in this protocol to carry a line break with, so the honest answer
    /// is to refuse and say why — sending it would let everything after the break be read as the
    /// next instruction.</para>
    /// </summary>
    [Fact]
    public async Task ANameWithALineBreakIsRefusedRatherThanSent()
    {
        SkipWithout(WithoutSftp);

        await using SshNetTransport session = new();

        await session.ConnectAsync(SshEndpoint.For(Host, "probe", WithoutSftp), [Key()], Trusting,
                                   Stop);

        await using IFileCopy copy = await session.OpenFileCopyAsync(Stop);

        using MemoryStream from = new([1, 2, 3]);

        SshException refused = await Assert.ThrowsAsync<SshException>(async () =>
            await copy.SendAsync(from, 3, "/tmp/two\nlines.txt", null, Stop));

        Assert.Contains("line break", refused.Message, StringComparison.Ordinal);
        Assert.Contains("sftp", refused.Remedy, StringComparison.Ordinal);
    }

    /// <summary>Quoting is the same for every word, and a name that looks harmless gets it too.</summary>
    [Theory]
    [InlineData("plain.txt", "'plain.txt'")]
    [InlineData("has space", "'has space'")]
    [InlineData("it's", @"'it'\''s'")]
    [InlineData("$(id)", "'$(id)'")]
    [InlineData("", "''")]
    public void EveryWordIsQuotedTheSameWay(string word, string expected) =>
        Assert.Equal(expected, ShellWord.Quote(word));

    // ---- The fallback announces itself ----

    /// <summary>
    /// A host with no subsystem falls back, and says what that costs before anybody wonders.
    ///
    /// <para>Silently dropping to scp leaves a user with an empty file pane, a transfer that starts
    /// again from zero and a progress bar that lies — three symptoms, one cause, nothing on screen
    /// mentioning it.</para>
    /// </summary>
    [Fact]
    public async Task AHostWithNoSubsystemFallsBackAndSaysSo()
    {
        SkipWithout(WithoutSftp);

        await using SshNetTransport session = new();

        await session.ConnectAsync(SshEndpoint.For(Host, "probe", WithoutSftp), [Key()], Trusting,
                                   Stop);

        await using IFileCopy copy = await session.OpenFileCopyAsync(Stop);

        Assert.Equal(TransferProtocol.Scp, copy.Protocol);

        Assert.False(copy.CanList);
        Assert.False(copy.CanResume);
        Assert.False(copy.CanMeasureProgress);

        Assert.Contains("no SFTP subsystem", copy.Announcement, StringComparison.Ordinal);
        Assert.Contains("starts again from the beginning", copy.Announcement,
                        StringComparison.Ordinal);

        // The browser cannot be built on it, and the type says so rather than a comment.
        Assert.Null(copy.Browsing);
    }

    /// <summary>
    /// And a host that does offer the subsystem uses it, with nothing to announce.
    ///
    /// <para>The fallback must be a fallback: a client that used scp everywhere would pass every
    /// test above and be worse at its job.</para>
    /// </summary>
    [Fact]
    public async Task AHostWithTheSubsystemUsesIt()
    {
        SkipWithout(Ordinary);

        await using SshNetTransport session = new();

        await session.ConnectAsync(SshEndpoint.For(Host, "probe", Ordinary), [Key()], Trusting, Stop);

        await using IFileCopy copy = await session.OpenFileCopyAsync(Stop);

        Assert.Equal(TransferProtocol.Sftp, copy.Protocol);
        Assert.True(copy.CanList);
        Assert.True(copy.CanResume);
        Assert.Equal(string.Empty, copy.Announcement);

        Assert.NotNull(copy.Browsing);
    }

    // ---- Moving the bytes ----

    /// <summary>A file of some size goes over and comes back byte for byte.</summary>
    [Fact]
    public async Task AFileGoesOverAndComesBackUnchanged()
    {
        SkipWithout(WithoutSftp);

        await using SshNetTransport session = new();

        await session.ConnectAsync(SshEndpoint.For(Host, "probe", WithoutSftp), [Key()], Trusting,
                                   Stop);

        await using IFileCopy copy = await session.OpenFileCopyAsync(Stop);

        byte[] sent = new byte[512 * 1024];

        Random.Shared.NextBytes(sent);

        string there = $"/tmp/qs-{Guid.NewGuid():N}.bin";

        Lock counting = new();
        long furthest = 0;

        try
        {
            using (MemoryStream from = new(sent))
            {
                await copy.SendAsync(from, sent.Length, there, new Progress<long>(moved =>
                {
                    lock (counting)
                    {
                        furthest += moved;
                    }
                }), Stop);
            }

            using MemoryStream back = new();

            await copy.ReceiveAsync(there, back, null, Stop);

            Assert.Equal(sent.Length, back.Length);
            Assert.Equal(sent, back.ToArray());

            Assert.Equal(sent.Length, furthest);
        }
        finally
        {
            await Run(session, $"rm -f {ShellWord.Quote(there)}");
        }
    }

    /// <summary>A whole directory goes over, nested folders and all.</summary>
    [Fact]
    public async Task ADirectoryGoesOverWhole()
    {
        SkipWithout(WithoutSftp);

        await using SshNetTransport session = new();

        await session.ConnectAsync(SshEndpoint.For(Host, "probe", WithoutSftp), [Key()], Trusting,
                                   Stop);

        Directory.CreateDirectory(Path.Combine(Mine(), "tree", "below"));

        await File.WriteAllTextAsync(Path.Combine(Mine(), "tree", "top.txt"), "top", Stop);
        await File.WriteAllTextAsync(Path.Combine(Mine(), "tree", "below", "deep.txt"), "deep",
                                     Stop);

        string there = $"/tmp/qs-{Guid.NewGuid():N}";

        await Run(session, $"mkdir -p {ShellWord.Quote(there)}");

        try
        {
            await using IFileCopy copy = await session.OpenFileCopyAsync(Stop);

            await copy.SendDirectoryAsync(Path.Combine(Mine(), "tree"), there, null, Stop);

            // The whole shape, not just one file: a directory send that dropped the nested folder
            // would still put top.txt where this expects it.
            string tree = await Run(session, $"find {ShellWord.Quote(there)} | sort");

            Assert.Contains("/tree/top.txt", tree, StringComparison.Ordinal);
            Assert.Contains("/tree/below/deep.txt", tree, StringComparison.Ordinal);

            Assert.Equal("top", (await Run(session, $"cat {ShellWord.Quote($"{there}/tree/top.txt")}")).Trim());
            Assert.Equal("deep", (await Run(session, $"cat {ShellWord.Quote($"{there}/tree/below/deep.txt")}")).Trim());
        }
        finally
        {
            await Run(session, $"rm -rf {ShellWord.Quote(there)}");
        }
    }

    /// <summary>A file that is not there fails with the path in it, rather than hanging.</summary>
    [Fact]
    public async Task AMissingFileFailsWithItsPath()
    {
        SkipWithout(WithoutSftp);

        await using SshNetTransport session = new();

        await session.ConnectAsync(SshEndpoint.For(Host, "probe", WithoutSftp), [Key()], Trusting,
                                   Stop);

        await using IFileCopy copy = await session.OpenFileCopyAsync(Stop);

        using MemoryStream back = new();

        SshException missing = await Assert.ThrowsAsync<SshException>(async () =>
            await copy.ReceiveAsync("/tmp/there-is-nothing-here-at-all", back, null, Stop));

        Assert.Contains("there-is-nothing-here-at-all", missing.Message, StringComparison.Ordinal);
    }

    // ---- plumbing ----

    /// <summary>
    /// Runs a command on the far side and returns what it printed.
    ///
    /// <para>Bracketed by two markers this call invented, and the opening one is found by its
    /// trailing newline: the shell echoes the command line back first, and in that echo the marker
    /// is followed by a semicolon rather than by the end of a line.</para>
    /// </summary>
    private static async Task<string> Run(SshNetTransport session, string command)
    {
        await using IPtyChannel shell = await session.OpenShellAsync(200, 25, Stop);

        string begin = $"qsB{Guid.NewGuid():N}";
        string end = $"qsE{Guid.NewGuid():N}";

        StringBuilder seen = new();
        byte[] buffer = new byte[8 * 1024];

        await shell.WriteAsync(
            Encoding.UTF8.GetBytes($"echo {begin}; {command}; echo {end}\n"), Stop);

        using CancellationTokenSource waiting = CancellationTokenSource.CreateLinkedTokenSource(Stop);
        waiting.CancelAfter(TimeSpan.FromSeconds(20));

        try
        {
            // The end marker itself, not a newline before it: a command whose output has no
            // trailing newline runs straight into the marker, and waiting for one that never comes
            // is twenty seconds of nothing followed by an empty answer.
            while (!seen.ToString().Contains($"{begin}\n", StringComparison.Ordinal)
                   || !After(seen.ToString(), begin, end))
            {
                int read = await shell.ReadAsync(buffer, waiting.Token);

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

        string all = seen.ToString().Replace("\r", string.Empty, StringComparison.Ordinal);

        int from = all.IndexOf($"{begin}\n", StringComparison.Ordinal);

        if (from < 0)
        {
            return string.Empty;
        }

        from += begin.Length + 1;

        int to = all.IndexOf(end, from, StringComparison.Ordinal);

        if (to <= from)
        {
            return string.Empty;
        }

        string between = all[from..to];

        return between.EndsWith('\n') ? between[..^1] : between;
    }

    /// <summary>Whether the closing marker has arrived after the opening one.</summary>
    private static bool After(string all, string begin, string end)
    {
        int from = all.IndexOf($"{begin}\n", StringComparison.Ordinal);

        return from >= 0 && all.IndexOf(end, from + begin.Length + 1, StringComparison.Ordinal) >= 0;
    }

    private string Mine()
    {
        Directory.CreateDirectory(_here);

        return _here;
    }

    private static ValueTask<SshHostKeyVerdict> Trusting(SshEndpoint _, SshHostKey __,
                                                         CancellationToken ___) =>
        ValueTask.FromResult(SshHostKeyVerdict.Accept);

    private static SshCredential.PrivateKey Key() =>
        new(Path.Combine(RepositoryRoot(), "prototypes", "SshProbe", "fixture", "keys",
                         "probe_ed25519"));

    private static void SkipWithout(int port)
    {
        bool up;

        try
        {
            using TcpClient probe = new();

            up = probe.ConnectAsync(Host, port).Wait(TimeSpan.FromSeconds(2));
        }
        catch (Exception)
        {
            up = false;
        }

        Assert.SkipUnless(up, $"nothing is listening on 127.0.0.1:{port}: run prototypes/SshProbe/fixture/up.sh");
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
