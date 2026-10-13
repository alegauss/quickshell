using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using NSec.Cryptography;
using Quickshell.Transport;
using Xunit;

namespace Quickshell.Transport.Tests;

/// <summary>
/// Pageant itself, not a window answering as it does (QS114): the one
/// <c>tools/provision-guest-pageant.ps1</c> places in the guest, 0.77, the last release that has
/// only the window and no pipe, given the guest's case key as <c>probe.ppk</c>.
///
/// <para>0.77 has no pipe to compare the window against, so the signature is held to the key
/// itself instead: what Pageant signed through the mapping verifies against the public half of the
/// key it was given, or the carrier changed it. Then the guest's own sshd is opened with nothing
/// but the window as a credential.</para>
///
/// <para><b>Each test starts that Pageant and ends it.</b> One left running would answer for the
/// stand-in window <see cref="SshAgentTests"/> opens, whose tests therefore skip while it is there —
/// and the shared collection keeps the two from running at once. Each skips, naming the script,
/// where the Pageant or the server is not there, which is everywhere but the guest.</para>
/// </summary>
[Collection(PageantWindow.Collection)]
public sealed partial class RealPageantTests
{
    private const int Port = 2240;
    private const string Folder = @"C:\ProgramData\quickshell-cases";
    private const string Key = Folder + @"\probe_ed25519";

    private static CancellationToken Stop => TestContext.Current.CancellationToken;

    /// <summary>The key Pageant was given is the key it lists, and its signature verifies against it.</summary>
    [Fact]
    public void ARealPageantSignsThroughItsWindowWhatTheKeyItselfVerifies()
    {
        using Pageant pageant = Pageant.Start();

        SshAgent mapped = new(SshAgent.PageantWindow);

        byte[] blob = PublicBlob();
        AgentIdentity held = Assert.Single(mapped.Identities(), identity => identity.Blob.Span.SequenceEqual(blob));

        byte[] data = Encoding.ASCII.GetBytes("a session identifier, signed through the window");
        byte[] signed = mapped.Sign(held.Blob.Span, data);

        int at = 0;
        string algorithm = Encoding.ASCII.GetString(Field(signed, ref at));
        byte[] signature = Field(signed, ref at);

        // The blob is the algorithm's name then the 32-byte point, each with its length in front.
        PublicKey key = PublicKey.Import(SignatureAlgorithm.Ed25519, blob.AsSpan(blob.Length - 32), KeyBlobFormat.RawPublicKey);

        TestContext.Current.TestOutputHelper?.WriteLine($"Pageant {pageant.Version}: {held.Fingerprint} {held.Comment}");

        Assert.Equal("ssh-ed25519", algorithm);
        Assert.True(SignatureAlgorithm.Ed25519.Verify(key, data, signature),
            $"Pageant {pageant.Version}'s signature through the window does not verify against the key it was given");
    }

    /// <summary>A session opens on the guest's sshd with the key only Pageant holds, through its window.</summary>
    [Fact]
    public async Task AKeyInARealPageantOpensASession()
    {
        Assert.SkipUnless(SshFixture.Listening(Port),
            $"Windows OpenSSH is not on 127.0.0.1:{Port}: run tools/provision-guest-sshd.ps1 once, which sets it up in the guest");

        using Pageant pageant = Pageant.Start();

        await using SshNetTransport transport = new();

        await transport.ConnectAsync(SshEndpoint.For("127.0.0.1", "probe", Port),
                                     [new SshCredential.Agent(SshAgent.PageantWindow)], SshFixture.Trusting, Stop);

        Assert.True(transport.IsConnected, $"the key in Pageant {pageant.Version} did not open a session");
    }

    /// <summary>The case key's public blob, from the <c>.pub</c> beside it.</summary>
    private static byte[] PublicBlob() =>
        Convert.FromBase64String(File.ReadAllText(Key + ".pub").Split(' ', StringSplitOptions.RemoveEmptyEntries)[1]);

    /// <summary>One length-prefixed string of a wire blob, moving past it.</summary>
    private static byte[] Field(byte[] blob, ref int at)
    {
        int length = (blob[at] << 24) | (blob[at + 1] << 16) | (blob[at + 2] << 8) | blob[at + 3];
        byte[] field = blob[(at + 4)..(at + 4 + length)];

        at += 4 + length;

        return field;
    }

    /// <summary>The provisioned Pageant, running for one test.</summary>
    private sealed class Pageant : IDisposable
    {
        private readonly Process _process;

        private Pageant(Process process, string version)
        {
            _process = process;
            Version = version;
        }

        /// <summary>The release, as its executable says, e.g. "Release 0.77".</summary>
        public string Version { get; }

        /// <summary>
        /// Starts it with the case key and waits for its window, skipping where it was never placed,
        /// where it is 0.78 or later — which would answer too, and prove nothing about the older
        /// ones — or where some other Pageant already holds the window.
        /// </summary>
        public static Pageant Start()
        {
            string exe = Path.Combine(Folder, "pageant.exe");
            string ppk = Path.Combine(Folder, "probe.ppk");

            Assert.SkipUnless(File.Exists(exe) && File.Exists(ppk) && File.Exists(Key + ".pub"),
                "no Pageant is placed with the case key: run tools/provision-guest-pageant.ps1 once, which puts 0.77 in the guest");

            Assert.SkipWhen(FakePageant.AnotherIsOpen, "a Pageant this did not start is open, and would answer instead");

            string version = FileVersionInfo.GetVersionInfo(exe).ProductVersion ?? string.Empty;

            Assert.SkipUnless(version.StartsWith("Release 0.", StringComparison.Ordinal)
                              && int.TryParse(version["Release 0.".Length..], out int minor) && minor < 78,
                $"the Pageant placed is {version}, not one older than 0.78: run tools/provision-guest-pageant.ps1 with 0.77");

            Process process = Process.Start(new ProcessStartInfo(exe, $"\"{ppk}\"") { UseShellExecute = false })
                ?? throw new InvalidOperationException($"{exe} did not start");

            Stopwatch waited = Stopwatch.StartNew();

            while (!FakePageant.AnotherIsOpen && !process.HasExited && waited.Elapsed < TimeSpan.FromSeconds(10))
            {
                Thread.Sleep(50);
            }

            Pageant started = new(process, version);

            if (!FakePageant.AnotherIsOpen)
            {
                started.Dispose();

                Assert.Fail($"Pageant {version} opened no window within {waited.Elapsed.TotalSeconds:0}s");
            }

            return started;
        }

        public void Dispose()
        {
            if (!_process.HasExited)
            {
                _process.Kill();
                _process.WaitForExit(TimeSpan.FromSeconds(10));
            }

            _process.Dispose();
        }
    }
}
