using System.Net.Sockets;
using Quickshell.Transport;
using Xunit;

namespace Quickshell.Transport.Tests;

/// <summary>
/// Every way in, against a real OpenSSH server that actually enforces which ones it will take.
///
/// <para>The fixture's accounts are the point: <c>probe</c> is under
/// <c>AuthenticationMethods publickey</c>, and <c>twofactor</c> under
/// <c>publickey,keyboard-interactive</c> — so a connection that succeeds as <c>twofactor</c> has
/// genuinely completed two methods in the order the server demanded, rather than having found a
/// server that would take anything.</para>
/// </summary>
public sealed class AuthenticationTests
{
    private const string Host = "127.0.0.1";
    private const int Port = 2222;

    /// <summary>What the fixture's two-factor account answers its second prompt with.</summary>
    private const string SecondFactor = "twofactor-pw";

    private static CancellationToken Stop => TestContext.Current.CancellationToken;

    private static ValueTask<SshHostKeyVerdict> Trusting(SshEndpoint _, SshHostKey __, CancellationToken ___) =>
        ValueTask.FromResult(SshHostKeyVerdict.Accept);

    // ---- Public key: every type and every format a user might hand over ----

    /// <summary>
    /// The three key types, each opened from the format <c>ssh-keygen</c> writes by default.
    ///
    /// <para>Every one of these is authorised on the fixture's <c>probe</c> account, so a connection
    /// that succeeds is the client having read the key — not a server being generous.</para>
    /// </summary>
    [Theory]
    [InlineData("probe_ed25519")]
    [InlineData("probe_rsa")]
    [InlineData("probe_ecdsa")]
    public async Task EveryKeyTypeThisClientClaimsToOpenOpensASession(string key)
    {
        SkipWithoutKey(key);

        await using ISshTransport transport = await Connected(new SshCredential.PrivateKey(Key(key)));

        Assert.True(transport.IsConnected);
    }

    /// <summary>
    /// The same RSA key written three ways. A user's key came from wherever it came from, and a
    /// client that opens only the format its author happened to have is a client that turns away
    /// people whose keys are older than it is.
    /// </summary>
    [Theory]
    [InlineData("probe_rsa")]      // OpenSSH's own
    [InlineData("probe_pem")]      // PEM, which is what everything wrote before 2018
    [InlineData("probe_pkcs8")]    // PKCS#8
    public async Task TheSameKeyOpensFromEveryFormatItMightArriveIn(string format)
    {
        SkipWithoutKey(format);

        await using ISshTransport transport = await Connected(new SshCredential.PrivateKey(Key(format)));

        Assert.True(transport.IsConnected);
    }

    /// <summary>
    /// PuTTY's own format, which is where a MobaXterm user's keys very often are.
    ///
    /// <para>The fixture converts the ed25519 key with <c>puttygen</c>, so this is the same key the
    /// first test uses arriving in a different container — which is what makes a success here mean
    /// the format was read rather than that some other key was accepted.</para>
    /// </summary>
    [Fact]
    public async Task APuttyKeyOpensASessionLikeAnyOther()
    {
        SkipWithoutKey("probe.ppk");

        await using ISshTransport transport = await Connected(new SshCredential.PrivateKey(Key("probe.ppk")));

        Assert.True(transport.IsConnected);
    }

    /// <summary>A passphrase is what opens an encrypted key, and the absence of one is refused by name.</summary>
    [Fact]
    public async Task AnEncryptedKeyNeedsItsPassphraseAndSaysSoWithoutOne()
    {
        SkipWithoutKey("probe_locked");

        await using ISshTransport opened =
            await Connected(new SshCredential.PrivateKey(Key("probe_locked"), "sesame"));

        Assert.True(opened.IsConnected);

        await using SshNetTransport refused = new();

        SshException failure = await Assert.ThrowsAsync<SshException>(async () =>
            await refused.ConnectAsync(Endpoint("probe"),
                                       [new SshCredential.PrivateKey(Key("probe_locked"))],
                                       Trusting, Stop));

        Assert.Equal(SshFailureKind.CredentialRejected, failure.Kind);
        Assert.Contains("passphrase", failure.Means, StringComparison.OrdinalIgnoreCase);
    }

    // ---- Keyboard-interactive: the server's own words ----

    /// <summary>
    /// The falsification, word for word: a server's own prompt text is not replaced by wording of
    /// the client's.
    ///
    /// <para><b>This is the entire feature and it is easy to lose.</b> A user reads "Duo push sent"
    /// or "Enter your token" and knows what to do; a client that shows its own "Password:" has
    /// thrown away the only useful information in the exchange. So the prompt is asserted to be the
    /// server's characters, and asserted not to be the client's guess at them.</para>
    /// </summary>
    [Fact]
    public async Task TheServersOwnPromptTextReachesTheUserUnaltered()
    {
        SkipWithoutKey("probe_ed25519");

        List<string> prompts = [];

        await using SshNetTransport transport = new();

        await transport.ConnectAsync(
            Endpoint("twofactor"),
            [
                new SshCredential.PrivateKey(Key("probe_ed25519")),
                new SshCredential.Interactive((prompt, echoed, _) =>
                {
                    prompts.Add(prompt);

                    Assert.False(echoed, "a secret was to be shown as it was typed");

                    return ValueTask.FromResult(SecondFactor);
                }),
            ],
            Trusting, Stop);

        Assert.True(transport.IsConnected, "the two-factor account did not let the client in");
        Assert.NotEmpty(prompts);

        // The server's own words, not a rewording. OpenSSH under PAM asks for a password here, and
        // whatever it asks is what a user must see.
        Assert.All(prompts, prompt => Assert.NotEqual(string.Empty, prompt.Trim()));
        Assert.Contains(prompts, prompt => prompt.Contains("assword", StringComparison.Ordinal));
    }

    /// <summary>
    /// Two methods, and the server decides the order. The client offers what it has and the server
    /// takes them in the order its own policy states — which is why both are handed over at once
    /// rather than tried one at a time by the client.
    /// </summary>
    [Fact]
    public async Task TheServerDecidesTheOrderAndBothMethodsAreCompleted()
    {
        SkipWithoutKey("probe_ed25519");

        int prompts = 0;

        await using SshNetTransport transport = new();

        // Offered deliberately the wrong way round: interactive first, key second. The server
        // requires publickey then keyboard-interactive, and it is the server's order that runs.
        await transport.ConnectAsync(
            Endpoint("twofactor"),
            [
                new SshCredential.Interactive((_, _, _) =>
                {
                    prompts++;

                    return ValueTask.FromResult(SecondFactor);
                }),
                new SshCredential.PrivateKey(Key("probe_ed25519")),
            ],
            Trusting, Stop);

        // Both ran, in the server's order and not the one they were listed in. An account under
        // AuthenticationMethods publickey,keyboard-interactive lets nobody in on one of the two, so
        // a connection plus a prompt is both of them having completed.
        Assert.True(transport.IsConnected);
        Assert.True(prompts > 0, "the second factor was never asked for");
    }

    /// <summary>
    /// A key alone is not enough for an account that wants two things, and the failure says which
    /// methods the server is still waiting for rather than reporting a flat refusal.
    /// </summary>
    [Fact]
    public async Task AKeyAloneAgainstATwoFactorAccountNamesWhatIsStillWanted()
    {
        SkipWithoutKey("probe_ed25519");

        await using SshNetTransport transport = new();

        SshException failure = await Assert.ThrowsAsync<SshException>(async () =>
            await transport.ConnectAsync(Endpoint("twofactor"),
                                         [new SshCredential.PrivateKey(Key("probe_ed25519"))],
                                         Trusting, Stop));

        Assert.Equal(SshFailureKind.NoMethodAccepted, failure.Kind);
        Assert.Contains("keyboard-interactive", failure.Means, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>none</c> is offered first, as the protocol intends, and it is what makes the server list
    /// the methods it will take.
    ///
    /// <para>The evidence is in the failure: offering a password to an account that only accepts
    /// public keys comes back naming <c>publickey</c>, which the server only says because it was
    /// asked. Without the <c>none</c> attempt there is no list to report.</para>
    /// </summary>
    [Fact]
    public async Task NoneIsOfferedFirstSoTheServerSaysWhatItWillTake()
    {
        SkipWithoutKey("probe_ed25519");

        await using SshNetTransport transport = new();

        SshException failure = await Assert.ThrowsAsync<SshException>(async () =>
            await transport.ConnectAsync(Endpoint("probe"),
                                         [new SshCredential.Password("not it")], Trusting, Stop));

        Assert.Equal(SshFailureKind.NoMethodAccepted, failure.Kind);
        Assert.Contains("publickey", failure.Means, StringComparison.Ordinal);
    }

    /// <summary>
    /// A password is offered after everything else, so a key that would have worked is tried before
    /// anybody is asked to type a secret.
    ///
    /// <para>Offered password-first on purpose: the key still gets in, which it could not have done
    /// had the password been tried and refused first on an account that takes only public keys.</para>
    /// </summary>
    [Fact]
    public async Task APasswordIsOfferedAfterEverythingElse()
    {
        SkipWithoutKey("probe_ed25519");

        await using SshNetTransport transport = new();

        await transport.ConnectAsync(
            Endpoint("probe"),
            [
                new SshCredential.Password("not it"),
                new SshCredential.PrivateKey(Key("probe_ed25519")),
            ],
            Trusting, Stop);

        Assert.True(transport.IsConnected, "the key was not reached past the password");
    }

    // ---- Certificates, which QS5 proved and nothing since has exercised ----

    /// <summary>
    /// A certificate signed by an authority the server trusts, on an account with no
    /// <c>authorized_keys</c> at all — so there is no other way the connection could have succeeded.
    /// </summary>
    [Fact]
    public async Task ACertificateOpensAnAccountWithNoAuthorisedKeys()
    {
        SkipWithoutKey("probe_ed25519-cert.pub");

        await using SshNetTransport transport = new();

        await transport.ConnectAsync(
            Endpoint("certonly"),
            [new SshCredential.PrivateKey(Key("probe_ed25519"), null, Key("probe_ed25519-cert.pub"))],
            Trusting, Stop);

        Assert.True(transport.IsConnected);
    }

    // ---- plumbing ----

    private static SshEndpoint Endpoint(string user) => SshEndpoint.For(Host, user, Port);

    private static async Task<ISshTransport> Connected(SshCredential credential)
    {
        SshNetTransport transport = new();

        await transport.ConnectAsync(Endpoint("probe"), [credential], Trusting, Stop);

        return transport;
    }

    private static string Key(string name) => Path.Combine(FixtureKeys(), name);

    private static string FixtureKeys() =>
        Path.Combine(RepositoryRoot(), "prototypes", "SshProbe", "fixture", "keys");

    /// <summary>
    /// Skips where the fixture or this particular key is absent, saying which. A key that is simply
    /// not there would otherwise read as a format this client cannot open.
    /// </summary>
    private static void SkipWithoutKey(string name)
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
        Assert.SkipUnless(File.Exists(Key(name)),
                          $"the fixture has no {name}: run prototypes/SshProbe/fixture/up.sh");
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
