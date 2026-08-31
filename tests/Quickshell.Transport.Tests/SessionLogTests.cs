using System.Net.Sockets;
using System.Reflection;
using System.Text;
using Quickshell.Transport;
using Xunit;

namespace Quickshell.Transport.Tests;

/// <summary>
/// The log, and the one thing it must never contain.
///
/// <para>Two ways of asking. The surface is checked for any member that could accept a secret, since
/// a log that cannot be handed one has nothing to leak; and a real session is driven with real
/// secrets and every file is then read looking for them.</para>
/// </summary>
public sealed class SessionLogTests : IDisposable
{
    private const string Host = "127.0.0.1";
    private const int Port = 2222;

    private readonly string _here =
        Path.Combine(Path.GetTempPath(), $"quickshell-log-{Guid.NewGuid():N}");

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
    /// The line's own falsification: no secret appears at any level.
    ///
    /// <para>Two real connections to a real server with the trace on, because the two moments a
    /// client is tempted to write down what it tried are opposites. One <b>fails</b> — a wrong
    /// password the server refuses, which is when a client wants to record what it sent so somebody
    /// can see why it was refused. One <b>succeeds</b> — the fixture's passphrase-protected key,
    /// with the passphrase that really opens it, so the secret is genuinely read, decrypted and used
    /// rather than merely held. Then every file the log has is read looking for either.</para>
    /// </summary>
    [Fact]
    public async Task NoSecretAppearsInTheLogAtAnyLevel()
    {
        SkipWithoutFixture();

        const string WrongPassword = "correct-horse-battery-staple";

        // The passphrase that really opens the fixture's locked key: see fixture/up.sh. A wrong one
        // would fail while the file was being read and never reach the wire, which is the weaker
        // half of this question.
        const string Passphrase = "sesame";

        await using SessionLog log = SessionLog.InFolder(_here, LogDetail.Trace);

        // Refused: a password this server will not take.
        await using (SshNetTransport refused = new() { Log = log })
        {
            using SshCredential.Password password = new(Secret.From(WrongPassword));

            SshException told = await Assert.ThrowsAsync<SshException>(
                async () => await refused.ConnectAsync(SshEndpoint.For(Host, "probe", Port),
                                                       [password], Trusting, Stop));

            Assert.Contains(told.Kind,
                            (SshFailureKind[])[SshFailureKind.NoMethodAccepted,
                                               SshFailureKind.CredentialRejected]);
        }

        // Accepted: a key whose passphrase is right, so the secret is decrypted and used.
        await using (SshNetTransport accepted = new() { Log = log })
        {
            SshCredential.PrivateKey locked =
                new(Path.Combine(Fixture(), "probe_locked"), Passphrase);

            await accepted.ConnectAsync(SshEndpoint.For(Host, "probe", Port), [locked], Trusting,
                                        Stop);

            Assert.True(accepted.IsConnected);
        }

        string everything = await Everything(log);

        Assert.NotEqual(string.Empty, everything);

        Assert.DoesNotContain(WrongPassword, everything, StringComparison.Ordinal);
        Assert.DoesNotContain(Passphrase, everything, StringComparison.Ordinal);

        // And it did record the shape of both, so the absence above is not the absence of a log.
        AssertRecorded(everything, "auth-offered", "kind=Password");
        AssertRecorded(everything, "auth-refused", "kind=Password");
        AssertRecorded(everything, "auth-offered", "kind=PrivateKey");
        AssertRecorded(everything, "auth-accepted");
    }

    /// <summary>
    /// A key that cannot be opened is a failure the log records, and it is the hardest one to
    /// explain afterwards: no server was involved, so there is nothing on the far side to ask.
    /// </summary>
    [Fact]
    public async Task AKeyThatCannotBeOpenedIsRecordedAndItsPassphraseIsNot()
    {
        const string Passphrase = "the-passphrase-nobody-should-see";

        await using SessionLog log = SessionLog.InFolder(_here, LogDetail.Trace);

        await using SshNetTransport session = new() { Log = log };

        SshCredential.PrivateKey locked = new(Path.Combine(Fixture(), "probe_locked"), Passphrase);

        await Assert.ThrowsAsync<SshException>(
            async () => await session.ConnectAsync(SshEndpoint.For(Host, "probe", Port), [locked],
                                                   Trusting, Stop));

        string everything = await Everything(log);

        Assert.Contains("failed", everything, StringComparison.Ordinal);
        Assert.DoesNotContain(Passphrase, everything, StringComparison.Ordinal);
    }

    /// <summary>
    /// A real handshake reaches the trace: the versions exchanged and what was agreed.
    ///
    /// <para>The transport is what fills this in, and a level nothing writes to is a level that does
    /// not exist — which is why this asks a live server rather than the log's own methods. The
    /// connection fails at authentication, so everything asserted here happened before that: the
    /// version exchange and the key exchange, which is the ground an appliance disagrees on.</para>
    /// </summary>
    [Fact]
    public async Task ARealHandshakeReachesTheTrace()
    {
        SkipWithoutFixture();

        await using SessionLog log = SessionLog.InFolder(_here, LogDetail.Trace);

        await using SshNetTransport session = new() { Log = log };

        using SshCredential.Password password = new(Secret.From("not-the-password"));

        try
        {
            await session.ConnectAsync(SshEndpoint.For(Host, "probe", Port), [password], Trusting,
                                       Stop);
        }
        catch (SshException)
        {
            // Expected. The trace is the point.
        }

        string everything = await Everything(log);

        // Both version strings, which is the first thing two sides disagree about.
        Assert.Contains("versions", everything, StringComparison.Ordinal);
        Assert.Contains("SSH-2.0-", everything, StringComparison.Ordinal);

        // And each negotiation, with what this client offered and what the two settled on.
        Assert.Contains("negotiated what=kex", everything, StringComparison.Ordinal);
        Assert.Contains("negotiated what=host key", everything, StringComparison.Ordinal);
        Assert.Contains("negotiated what=cipher", everything, StringComparison.Ordinal);

        // The key exchange finished — authentication is what failed — so something was agreed.
        Assert.DoesNotContain("negotiated what=kex ours=none", everything, StringComparison.Ordinal);
    }

    /// <summary>
    /// A refused port does not become a story about the user's keys.
    ///
    /// <para>Nothing is listening on this one, so the failure never reaches an authentication
    /// exchange — and a log that wrote <c>auth-refused</c> here sends a user hunting through their
    /// credentials for a failure that was a closed port.</para>
    /// </summary>
    [Fact]
    public async Task APortWithNothingOnItIsNotRecordedAsARefusedCredential()
    {
        await using SessionLog log = SessionLog.InFolder(_here, LogDetail.Trace);

        await using SshNetTransport session = new() { Log = log, Timeout = TimeSpan.FromSeconds(5) };

        using SshCredential.Password password = new(Secret.From("irrelevant"));

        try
        {
            // Chosen out of the range nothing is registered on, and on loopback so the answer is a
            // refusal rather than a wait.
            await session.ConnectAsync(SshEndpoint.For(Host, "probe", 47_811), [password], Trusting,
                                       Stop);
        }
        catch (SshException)
        {
            // Expected.
        }

        string everything = await Everything(log);

        Assert.Contains("failed", everything, StringComparison.Ordinal);
        Assert.Contains("auth-offered", everything, StringComparison.Ordinal);

        // The credential was offered and never refused, because it was never tried.
        Assert.DoesNotContain("auth-refused", everything, StringComparison.Ordinal);
        Assert.DoesNotContain("auth-accepted", everything, StringComparison.Ordinal);
    }

    /// <summary>
    /// And there is no way to hand it one: no member takes bytes, a credential, or a secret.
    ///
    /// <para>This is the stronger half. Redaction applied afterwards is a list of things somebody
    /// remembered, and the forgotten one is always the one that matters — a surface that cannot
    /// express a secret has nothing to forget.</para>
    /// </summary>
    [Fact]
    public void ThereIsNoMemberThatCouldBeHandedASecret()
    {
        Type[] refused =
        [
            typeof(byte[]), typeof(char[]), typeof(Secret), typeof(SshCredential),
            typeof(Stream), typeof(ReadOnlyMemory<byte>), typeof(Memory<byte>),
            typeof(SshCredential.Password), typeof(SshCredential.PrivateKey),
        ];

        List<string> offenders = [];

        foreach (MethodInfo member in typeof(SessionLog)
                     .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
        {
            foreach (ParameterInfo taken in member.GetParameters())
            {
                if (refused.Contains(taken.ParameterType))
                {
                    offenders.Add($"{member.Name} takes {taken.ParameterType.Name}");
                }
            }
        }

        Assert.Empty(offenders);
    }

    /// <summary>
    /// A payload is a count and a direction, never the bytes.
    ///
    /// <para>A byte of a channel is a byte of somebody's session: a password typed at a prompt, a
    /// file being read. There is no overload that takes one.</para>
    /// </summary>
    [Fact]
    public async Task APayloadIsRecordedAsItsLengthAndNothingElse()
    {
        await using SessionLog log = SessionLog.InFolder(_here, LogDetail.Trace);

        log.Payload(ChannelKind.Shell, inbound: true, length: 4096);

        string written = await Everything(log);

        Assert.Contains("payload", written, StringComparison.Ordinal);
        Assert.Contains("length=4096", written, StringComparison.Ordinal);

        MethodInfo payload = typeof(SessionLog).GetMethod(nameof(SessionLog.Payload))!;

        Assert.DoesNotContain(payload.GetParameters(),
                              taken => taken.ParameterType != typeof(ChannelKind)
                                       && taken.ParameterType != typeof(bool)
                                       && taken.ParameterType != typeof(int));
    }

    // ---- The two levels ----

    /// <summary>The ordinary level records the shape and leaves the negotiation out.</summary>
    [Fact]
    public async Task TheOrdinaryLevelLeavesTheNegotiationOut()
    {
        await using SessionLog log = SessionLog.InFolder(_here, LogDetail.Ordinary);

        log.Connecting(SshEndpoint.For("example.test", "somebody"));
        log.Negotiated("kex", "ours", "theirs", "chosen");

        string written = await Everything(log);

        Assert.Contains("connecting", written, StringComparison.Ordinal);
        Assert.DoesNotContain("negotiated", written, StringComparison.Ordinal);
    }

    /// <summary>And the trace records both, which is what diagnoses an appliance.</summary>
    [Fact]
    public async Task TheTraceRecordsWhatEachSideOffered()
    {
        await using SessionLog log = SessionLog.InFolder(_here, LogDetail.Trace);

        log.Connecting(SshEndpoint.For("example.test", "somebody"));
        log.Negotiated("kex", "curve25519-sha256", "diffie-hellman-group1-sha1", string.Empty);

        string written = await Everything(log);

        Assert.Contains("connecting", written, StringComparison.Ordinal);

        // Both offers, which is the whole of "no algorithm in common".
        Assert.Contains("curve25519-sha256", written, StringComparison.Ordinal);
        Assert.Contains("diffie-hellman-group1-sha1", written, StringComparison.Ordinal);
    }

    /// <summary>Off writes nothing at all.</summary>
    [Fact]
    public async Task OffWritesNothing()
    {
        await using SessionLog log = SessionLog.InFolder(_here, LogDetail.Off);

        log.Connecting(SshEndpoint.For("example.test", "somebody"));
        log.Negotiated("kex", "a", "b", "c");

        Assert.Equal(string.Empty, await Everything(log));
    }

    // ---- Bounded, and findable ----

    /// <summary>
    /// It rotates against a bounded total, so a trace left running overnight cannot fill a disk.
    /// </summary>
    [Fact]
    public async Task ItRotatesAgainstABoundedTotal()
    {
        await using SessionLog log = SessionLog.InFolder(_here, LogDetail.Trace, each: 2048, keep: 2);

        for (int line = 0; line < 400; line++)
        {
            log.Payload(ChannelKind.Shell, inbound: true, length: line);
        }

        IReadOnlyList<string> files = log.Files;

        // The current one and no more than the two behind it.
        Assert.InRange(files.Count, 1, 3);

        long total = files.Sum(file => new FileInfo(file).Length);

        Assert.True(total <= log.Bounded,
                    $"{total} bytes across {files.Count} files, and the bound was {log.Bounded}");

        // And what it kept is the newest, so the rotation did not discard what just happened.
        Assert.Contains("length=399", await Everything(log), StringComparison.Ordinal);
    }

    /// <summary>The file's location is a property, because a log a user cannot find is not one.</summary>
    [Fact]
    public async Task WhereItIsCanBeAsked()
    {
        await using SessionLog log = SessionLog.InFolder(_here);

        log.Connecting(SshEndpoint.For("example.test", "somebody"));

        Assert.True(File.Exists(log.Path), $"{log.Path} is not there");
        Assert.StartsWith(_here, log.Path, StringComparison.Ordinal);
    }

    // ---- plumbing ----

    /// <summary>
    /// One record carries all of these, whatever order the fields are written in.
    ///
    /// <para>A whole-file substring match would pass on two fields that happen to sit in different
    /// records, which is how a log gets read as saying something it did not.</para>
    /// </summary>
    private static void AssertRecorded(string everything, params string[] fields)
    {
        bool found = everything
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Any(record => fields.All(field => record.Contains(field, StringComparison.Ordinal)));

        Assert.True(found, $"no record carries {string.Join(" and ", fields)}");
    }

    private static async Task<string> Everything(SessionLog log)
    {
        StringBuilder all = new();

        foreach (string file in log.Files)
        {
            // Opened sharing with the writer, because the log is still open: this is the same thing
            // a user does when they tail it.
            await using FileStream reading = new(file, FileMode.Open, FileAccess.Read,
                                                 FileShare.ReadWrite);

            using StreamReader text = new(reading);

            all.Append(await text.ReadToEndAsync(Stop));
        }

        return all.ToString();
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
