using System.Text;
using Quickshell.Transport;
using Xunit;

namespace Quickshell.Transport.Tests;

/// <summary>
/// The seam's second implementation, which is what says it is an interface rather than a
/// description of one class.
///
/// <para>Every test here runs with no server, no socket and no protocol library — which is the
/// claim QS36 makes about why the seam is worth having, asserted rather than stated.</para>
/// </summary>
public sealed class ReplayTransportTests
{
    private static readonly SshEndpoint Somewhere = SshEndpoint.For("host.example", "user");

    private static readonly IReadOnlyList<SshCredential> APassword =
        [new SshCredential.Password("hunter2")];

    /// <summary>The harness's own token, so a cancelled run stops these rather than waiting them out.</summary>
    private static CancellationToken Stop => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ARecordedSessionReachesAReaderAsAnOrdinaryChannel()
    {
        const string recorded = "quickshell $ uname -a\r\nLinux host 6.8.0\r\n";

        await using ReplayTransport transport =
            ReplayTransport.Replaying(Encoding.UTF8.GetBytes(recorded));

        await transport.ConnectAsync(Somewhere, APassword, null, Stop);

        Assert.True(transport.IsConnected);
        Assert.Equal(Somewhere, transport.Endpoint);

        await using IPtyChannel channel = await transport.OpenShellAsync(80, 25, Stop);

        Assert.Equal((80, 25), channel.Size);
        Assert.Equal(recorded, await ReadAll(channel, recorded.Length));
    }

    [Fact]
    public async Task WhatTheClientTypedIsKeptForATestToAssertOn()
    {
        await using ReplayTransport transport = ReplayTransport.Replaying([]);

        await transport.ConnectAsync(Somewhere, APassword, null, Stop);

        await using IPtyChannel channel = await transport.OpenShellAsync(80, 25, Stop);

        await channel.WriteAsync(Encoding.UTF8.GetBytes("ls -l\r"), Stop);

        Assert.Equal("ls -l\r", Encoding.UTF8.GetString(transport.Written));
    }

    /// <summary>
    /// The end of a recording is not the end of a stream. A channel that answered zero here would
    /// tell a session loop the shell had gone, and the loop would stop reading a live idle one.
    /// </summary>
    [Fact]
    public async Task TheEndOfTheRecordingIsNotTheEndOfTheChannel()
    {
        await using ReplayTransport transport = ReplayTransport.Replaying("hi"u8);

        await transport.ConnectAsync(Somewhere, APassword, null, Stop);

        IPtyChannel channel = await transport.OpenShellAsync(80, 25, Stop);
        byte[] buffer = new byte[16];

        Assert.Equal(2, await channel.ReadAsync(buffer, Stop));

        ValueTask<int> waiting = channel.ReadAsync(buffer, Stop);

        Assert.False(waiting.IsCompleted, "a spent recording answered instead of waiting");

        await channel.DisposeAsync();

        Assert.Equal(0, await waiting);
    }

    /// <summary>A link that drops ends the channel as a failure and not as a program that exited.</summary>
    [Fact]
    public async Task ADroppedLinkEndsTheChannelAsAFailure()
    {
        await using ReplayTransport transport = ReplayTransport.Replaying("hi"u8);

        await transport.ConnectAsync(Somewhere, APassword, null, Stop);

        IPtyChannel channel = await transport.OpenShellAsync(80, 25, Stop);

        transport.Drop("the network went away");

        PtyExit exit = await channel.Closed;

        Assert.False(exit.IsExit);
        Assert.Equal("the network went away", exit.Reason);
        Assert.False(transport.IsConnected);

        SshException? why = await transport.Disconnected;

        Assert.NotNull(why);
        Assert.Equal(SshFailureKind.Dropped, why.Kind);
    }

    // ---- The host key: asked first, and refusing means refused ----

    /// <summary>
    /// The key is checked before anything is authenticated. A secret offered to a server nobody
    /// verified has already been given away, so the order is the point rather than a detail.
    /// </summary>
    [Fact]
    public async Task TheHostKeyIsCheckedBeforeACredentialIsOffered()
    {
        await using ReplayTransport transport = ReplayTransport.Replaying([]);

        bool asked = false;

        await transport.ConnectAsync(Somewhere, APassword, (endpoint, key, _) =>
        {
            asked = true;

            Assert.Equal(Somewhere, endpoint);
            Assert.Equal("ssh-ed25519", key.Algorithm);
            Assert.StartsWith("ssh-ed25519 SHA256:", key.ToString(), StringComparison.Ordinal);

            return ValueTask.FromResult(SshHostKeyVerdict.Accept);
        }, Stop);

        Assert.True(asked, "the connection authenticated without anybody looking at the key");
        Assert.Equal(SshHostKeyVerdict.Accept, transport.Verdict);
    }

    [Fact]
    public async Task ARefusedHostKeyRefusesTheConnection()
    {
        await using ReplayTransport transport = ReplayTransport.Replaying([]);

        SshException refused = await Assert.ThrowsAsync<SshException>(async () =>
            await transport.ConnectAsync(Somewhere, APassword,
                (_, _, _) => ValueTask.FromResult(SshHostKeyVerdict.Refuse), Stop));

        Assert.Equal(SshFailureKind.HostKey, refused.Kind);
        Assert.False(transport.IsConnected);
    }

    // ---- Failures: this client's type, carrying words a person can read ----

    [Fact]
    public async Task ARefusingTransportFailsWithAReasonAndNotALibraryException()
    {
        await using ReplayTransport transport =
            ReplayTransport.Refusing(SshFailureKind.Unreachable, "nothing is listening on port 22");

        SshException failed = await Assert.ThrowsAsync<SshException>(async () =>
            await transport.ConnectAsync(Somewhere, APassword, null, Stop));

        Assert.Equal(SshFailureKind.Unreachable, failed.Kind);
        Assert.Equal("nothing is listening on port 22", failed.Message);
        Assert.False(transport.IsConnected);
    }

    /// <summary>
    /// The library's exception is flattened to text at the seam and is not the inner exception, so
    /// no assembly above can catch it, name it, or come to depend on its wording.
    /// </summary>
    [Fact]
    public void ALibraryExceptionCrossesAsTextAndNotAsAnObject()
    {
        InvalidOperationException library = new("the library's own words");

        SshException crossed = SshException.From(SshFailureKind.Dropped, "the server hung up", library);

        Assert.Null(crossed.InnerException);
        Assert.Equal("the server hung up", crossed.Message);
        Assert.Contains("InvalidOperationException", crossed.Origin, StringComparison.Ordinal);
        Assert.Contains("the library's own words", crossed.Origin, StringComparison.Ordinal);
    }

    /// <summary>A caller offering nothing has not decided yet, and is refused rather than tried.</summary>
    [Fact]
    public async Task AConnectionWithNoCredentialIsRefused()
    {
        await using ReplayTransport transport = ReplayTransport.Replaying([]);

        SshException refused = await Assert.ThrowsAsync<SshException>(async () =>
            await transport.ConnectAsync(Somewhere, [], null, Stop));

        Assert.Equal(SshFailureKind.NoMethodAccepted, refused.Kind);
    }

    [Fact]
    public async Task ChannelsAreRefusedBeforeThereIsAConnection()
    {
        await using ReplayTransport transport = ReplayTransport.Replaying([]);

        SshException refused = await Assert.ThrowsAsync<SshException>(async () =>
            await transport.OpenShellAsync(80, 25, Stop));

        Assert.Equal(SshFailureKind.Dropped, refused.Kind);
    }

    /// <summary>
    /// The two channels a recording cannot carry say so. Returning an empty file listing instead
    /// would put a picture of a bug on screen where a missing implementation belongs.
    /// </summary>
    [Fact]
    public async Task TheChannelsARecordingCannotCarrySayS0()
    {
        await using ReplayTransport transport = ReplayTransport.Replaying([]);

        await transport.ConnectAsync(Somewhere, APassword, null, Stop);

        await Assert.ThrowsAsync<SshException>(async () => await transport.OpenFileTransferAsync(Stop));
        await Assert.ThrowsAsync<SshException>(async () => await transport.OpenForwardAsync("far", 80, Stop));
    }

    // ---- The vocabulary itself ----

    [Fact]
    public void AnEndpointReadsBackTheWayAPersonWroteIt()
    {
        Assert.Equal("user@host.example", SshEndpoint.For("host.example", "user").ToString());
        Assert.Equal("user@host.example:2222",
                     SshEndpoint.For("host.example", "user", 2222).ToString());
        Assert.Equal(22, SshEndpoint.DefaultPort);
    }

    [Theory]
    [InlineData("", "user", 22)]
    [InlineData("host", "", 22)]
    [InlineData("host", "user", 0)]
    [InlineData("host", "user", 65536)]
    public void AnEndpointNobodyCouldConnectToIsRefusedWhereItIsBuilt(string host, string user, int port)
    {
        Assert.ThrowsAny<ArgumentException>(() => SshEndpoint.For(host, user, port));
    }

    /// <summary>
    /// The credentials are values a profile can hold, not objects a library owns. Four cases, and
    /// they are the four QS5 found a real sshd asking for.
    /// </summary>
    [Fact]
    public void EveryCredentialIsAValueAProfileCouldHold()
    {
        SshCredential[] all =
        [
            new SshCredential.Password("secret"),
            new SshCredential.PrivateKey("id_ed25519", "phrase", "id_ed25519-cert.pub"),
            new SshCredential.Agent(),
            new SshCredential.Interactive((_, _, _) => ValueTask.FromResult("42")),
        ];

        Assert.All(all, credential => Assert.IsAssignableFrom<SshCredential>(credential));

        // Values, so two built the same way are the same thing — which is what lets a profile be
        // compared, saved and read back without the library being involved at all.
        Assert.Equal(new SshCredential.Password("secret"), all[0]);
        Assert.Equal(new SshCredential.PrivateKey("id_ed25519", "phrase", "id_ed25519-cert.pub"), all[1]);
        Assert.Equal(new SshCredential.Agent(), all[2]);
    }

    private static async Task<string> ReadAll(IPtyChannel channel, int expected)
    {
        byte[] buffer = new byte[expected];
        int filled = 0;

        while (filled < expected)
        {
            int read = await channel.ReadAsync(buffer.AsMemory(filled), Stop);

            if (read == 0)
            {
                break;
            }

            filled += read;
        }

        return Encoding.UTF8.GetString(buffer, 0, filled);
    }
}
