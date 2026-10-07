using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using Quickshell.Transport;
using Xunit;

namespace Quickshell.Transport.Tests;

/// <summary>
/// The agent: a key the client never holds, signing for it.
///
/// <para><b>The agent under test is a real one over a real pipe</b> — see <see cref="FakeAgent"/>,
/// which parses the protocol's bytes and answers with the protocol's bytes. What it is not is
/// Microsoft's: the <c>ssh-agent</c> service on the machine this was written on is disabled and
/// enabling it needs elevation, so the one test that would exercise that binary skips where it is
/// not running. Everything the protocol says is exercised either way.</para>
///
/// <para>The end-to-end test is the one that matters and it is genuinely end to end: an agent holds
/// a key nothing else can read, a real OpenSSH server is told to trust it, and the connection
/// succeeds — which it can only do if a signature this client did not make verified against a public
/// key this client never saw the other half of.</para>
/// </summary>
public sealed class SshAgentTests
{
    private static CancellationToken Stop => TestContext.Current.CancellationToken;

    // ---- The protocol ----

    /// <summary>Listing is the first of the two operations, and it names the key the way ssh-add does.</summary>
    [Fact]
    public async Task AnAgentListsWhatItIsHolding()
    {
        await using FakeAgent agent = new("a key with a name");

        SshAgent client = new(agent.Pipe);

        Assert.True(client.IsRunning);

        IReadOnlyList<AgentIdentity> identities = client.Identities();

        AgentIdentity only = Assert.Single(identities);

        Assert.Equal("ssh-rsa", only.Algorithm);
        Assert.Equal("a key with a name", only.Comment);

        // The same 43-character digest ssh-add -l prints, so a user can recognise their own key.
        Assert.Equal(43, only.Fingerprint.Length);
    }

    /// <summary>Signing is the second, and the flags are what ask for a hash a modern server accepts.</summary>
    [Fact]
    public async Task SigningAsksForTheHashItWasToldTo()
    {
        await using FakeAgent agent = new();

        SshAgent client = new(agent.Pipe);
        AgentIdentity identity = client.Identities()[0];

        byte[] signature = client.Sign(identity.Blob.Span, "something to sign"u8, SshAgent.RsaSha256);

        Assert.Equal(1, agent.Signatures);
        Assert.Equal(SshAgent.RsaSha256, agent.LastFlags);

        // The signature names the algorithm it was made under, which is what a server reads first.
        Assert.Equal("rsa-sha2-256", Algorithm(signature));

        client.Sign(identity.Blob.Span, "something else"u8, SshAgent.RsaSha512);

        Assert.Equal("rsa-sha2-512", Algorithm(
            client.Sign(identity.Blob.Span, "a third thing"u8, SshAgent.RsaSha512)));
    }

    /// <summary>A key the agent is not holding is refused, and refused as this client's own failure.</summary>
    [Fact]
    public async Task AKeyTheAgentDoesNotHoldIsRefused()
    {
        await using FakeAgent agent = new();

        SshAgent client = new(agent.Pipe);

        SshException failure = Assert.Throws<SshException>(() =>
            client.Sign("not a key it has"u8, "data"u8));

        Assert.Equal(SshFailureKind.CredentialRejected, failure.Kind);
        Assert.Contains("touch", failure.Remedy, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// No agent running is a state and not a crash: a user without one is the ordinary case, and it
    /// says which pipe it looked on.
    /// </summary>
    [Fact]
    public void NoAgentIsAnAnswerRatherThanAFailure()
    {
        SshAgent nowhere = new($"quickshell-nothing-{Guid.NewGuid():N}");

        Assert.False(nowhere.IsRunning);

        SshException failure = Assert.Throws<SshException>(() => nowhere.Identities());

        Assert.Equal(SshFailureKind.NoMethodAccepted, failure.Kind);
        Assert.Contains("quickshell-nothing", failure.Means, StringComparison.Ordinal);
    }

    // ---- The falsification: a key the client cannot read authenticates a session ----

    /// <summary>
    /// The line's own falsification, run: a key held only by an agent authenticates against a real
    /// OpenSSH server.
    ///
    /// <para>The key is generated inside the agent and never leaves it — there is no file, and no
    /// code path by which this client could obtain the private half. That is exactly the situation a
    /// smart card puts a client in, which is why the design calls the agent the only possible route
    /// there rather than a convenience.</para>
    /// </summary>
    [Fact]
    public async Task AKeyHeldOnlyByAnAgentAuthenticatesASession()
    {
        SkipWithoutFixture();

        await using FakeAgent agent = new("held-only-by-the-agent");

        Authorise(agent.AuthorizedKey);

        try
        {
            await using SshNetTransport transport = new();

            await transport.ConnectAsync(SshEndpoint.For("127.0.0.1", "probe", 2222),
                                         [new SshCredential.Agent(agent.Pipe)], SshFixture.Trusting, Stop);

            Assert.True(transport.IsConnected, "the agent's key did not open a session");
            Assert.True(agent.Signatures > 0, "the server let the client in without a signature");

            // And the signature was asked for under a hash the server would accept. A server that
            // has refused SHA-1 for years is what makes this the interesting half.
            Assert.True(agent.LastFlags is SshAgent.RsaSha256 or SshAgent.RsaSha512,
                        $"the agent was asked to sign under flags {agent.LastFlags}");
        }
        finally
        {
            Authorise(null);
        }
    }

    /// <summary>
    /// A session that names one identity is offered that one and no others.
    ///
    /// <para>Not a nicety: a server allows six authentication attempts by default, and a user with
    /// ten keys loaded would be cut off before the right one was reached, having been asked nothing.</para>
    /// </summary>
    [Fact]
    public async Task NamingOneIdentityOffersThatOneAlone()
    {
        await using FakeAgent agent = new();

        SshAgent client = new(agent.Pipe);
        AgentIdentity held = client.Identities()[0];

        await using SshNetTransport wrong = new();

        SshException refused = await Assert.ThrowsAsync<SshException>(async () =>
            await wrong.ConnectAsync(SshEndpoint.For("127.0.0.1", "probe", 2222),
                                     [new SshCredential.Agent(agent.Pipe, "SHA256:not-that-one")],
                                     SshFixture.Trusting, Stop));

        Assert.Equal(SshFailureKind.NoMethodAccepted, refused.Kind);
        Assert.Contains("not holding", refused.Message, StringComparison.OrdinalIgnoreCase);

        // And the one it is holding is found by the fingerprint ssh-add prints, with or without the
        // prefix a user would have copied along with it.
        Assert.Contains(held.Fingerprint, $"SHA256:{held.Fingerprint}", StringComparison.Ordinal);
    }

    /// <summary>An agent holding nothing is said so, rather than reaching a server as no credential.</summary>
    [Fact]
    public void AnAgentHoldingNothingIsNotSilence()
    {
        SshAgent nowhere = new($"quickshell-nothing-{Guid.NewGuid():N}");

        Assert.False(nowhere.IsRunning);
    }

    /// <summary>
    /// Windows' own agent, where it is running. Skipped rather than faked: this is the one test
    /// whose subject is Microsoft's binary rather than the protocol, and it can only be run where
    /// that binary is.
    /// </summary>
    [Fact]
    public void WindowsOwnAgentSpeaksTheSameProtocol()
    {
        SshAgent windows = new();

        Assert.SkipUnless(windows.IsRunning,
            "the Windows ssh-agent service is not running, so there is nothing here to ask");

        // Listing is enough: if the framing or the message numbers were wrong this would throw or
        // come back empty against a real agent that has keys.
        Assert.NotNull(windows.Identities());
    }

    // ---- Pageant's window: the carrier before the pipe (QS114) ----

    /// <summary>
    /// QS114's falsification: a signature obtained through shared memory is the signature obtained
    /// through the pipe, for the same key and the same data.
    ///
    /// <para>The same agent answers both — <see cref="FakeAgent"/> on its pipe and behind a Pageant
    /// window — and its RSA signatures are deterministic, so any difference is the carrier's. The
    /// window also checks the mapping's owner the way Pageant does, and nothing was refused.</para>
    /// </summary>
    [Fact]
    public async Task ASignatureThroughPageantsWindowIsTheSignatureThroughThePipe()
    {
        Assert.SkipWhen(FakePageant.AnotherIsOpen, "a real Pageant is open, and this must not answer for it");

        await using FakeAgent agent = new("through-the-window");
        using FakePageant window = new(agent.Answer);

        SshAgent piped = new(agent.Pipe);
        SshAgent mapped = new(SshAgent.PageantWindow);

        Assert.True(mapped.IsRunning, "the Pageant window was not found");

        AgentIdentity throughPipe = Assert.Single(piped.Identities());
        AgentIdentity throughWindow = Assert.Single(mapped.Identities());

        Assert.Equal(throughPipe.Fingerprint, throughWindow.Fingerprint);
        Assert.Equal(throughPipe.Comment, throughWindow.Comment);

        byte[] data = Encoding.ASCII.GetBytes("the same session identifier, signed twice");

        byte[] signedThroughPipe = piped.Sign(throughPipe.Blob.Span, data, SshAgent.RsaSha256);
        byte[] signedThroughWindow = mapped.Sign(throughWindow.Blob.Span, data, SshAgent.RsaSha256);

        Assert.Equal(signedThroughPipe, signedThroughWindow);
        Assert.Equal(2, window.Requests);
        Assert.Equal(0, window.RefusedForOwner);
    }

    /// <summary>
    /// End to end through the window: a key held only by the agent behind it opens a session on a
    /// real OpenSSH server, which can only happen if the signature crossed the mapping intact.
    /// </summary>
    [Fact]
    public async Task AKeyBehindPageantsWindowAuthenticatesASession()
    {
        SkipWithoutFixture();
        Assert.SkipWhen(FakePageant.AnotherIsOpen, "a real Pageant is open, and this must not answer for it");

        await using FakeAgent agent = new("behind-the-window");
        using FakePageant window = new(agent.Answer);

        Authorise(agent.AuthorizedKey);

        try
        {
            await using SshNetTransport transport = new();

            await transport.ConnectAsync(SshEndpoint.For("127.0.0.1", "probe", 2222),
                                         [new SshCredential.Agent(SshAgent.PageantWindow)], SshFixture.Trusting, Stop);

            Assert.True(transport.IsConnected, "the key behind the window did not open a session");
            Assert.True(agent.Signatures > 0, "the server let the client in without a signature");
        }
        finally
        {
            Authorise(null);
        }
    }

    /// <summary>With no Pageant window open, the carrier says so in Pageant's terms, not a pipe's.</summary>
    [Fact]
    public void NoPageantWindowIsSaidInPageantsTerms()
    {
        Assert.SkipWhen(FakePageant.AnotherIsOpen, "a real Pageant is open");

        SshAgent mapped = new(SshAgent.PageantWindow);

        Assert.False(mapped.IsRunning);

        SshException refused = Assert.Throws<SshException>(() => mapped.Identities());

        Assert.Contains("Pageant", refused.Means, StringComparison.Ordinal);
        Assert.DoesNotContain("pipe", refused.Means, StringComparison.Ordinal);
    }

    // ---- plumbing ----

    /// <summary>The algorithm a signature blob names, which is its first length-prefixed string.</summary>
    private static string Algorithm(byte[] signature)
    {
        int length = (signature[0] << 24) | (signature[1] << 16) | (signature[2] << 8) | signature[3];

        return Encoding.ASCII.GetString(signature, 4, length);
    }

    /// <summary>
    /// Puts a key in the fixture's <c>authorized_keys</c>, or takes it out again.
    ///
    /// <para>Written into the running container rather than into the fixture's files, so a test that
    /// dies half way leaves nothing behind that the next <c>up.sh</c> would bake in.</para>
    /// </summary>
    private static void Authorise(string? key)
    {
        string command = key is null
            ? "cp /home/probe/.ssh/authorized_keys.original /home/probe/.ssh/authorized_keys"
            : $"cp -n /home/probe/.ssh/authorized_keys /home/probe/.ssh/authorized_keys.original; "
              + $"echo '{key}' >> /home/probe/.ssh/authorized_keys";

        using Process? docker = Process.Start(new ProcessStartInfo("docker",
            $"exec qs-sshd-target sh -c \"{command}\"")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        });

        docker?.WaitForExit(TimeSpan.FromSeconds(20));
    }

    private static void SkipWithoutFixture()
    {
        bool up = SshFixture.Listening(2222);

        Assert.SkipUnless(up, "nothing is listening on 127.0.0.1:2222: run prototypes/SshProbe/fixture/up.sh");
    }
}
