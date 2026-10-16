using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

using Quickshell.Transport;

using Xunit;

namespace Quickshell.App.Tests;

/// <summary>
/// QS341: quickshell as a small SSH certificate authority, so the agent signs in on a certificate that lasts
/// minutes and a key that leaks is worth nothing on its own.
/// </summary>
public sealed class AgentCertificateTests : IDisposable
{
    private readonly string _here = Path.Combine(Path.GetTempPath(), "qs-cert-" + Guid.NewGuid().ToString("N"));

    public AgentCertificateTests() => Directory.CreateDirectory(_here);

    private static CancellationToken Stop => TestContext.Current.CancellationToken;

    public void Dispose() => Directory.Delete(_here, recursive: true);

    /// <summary>The authority is made once, its private half never left in the clear, and nothing of it left behind by a signing.</summary>
    [Fact]
    public void TheAuthorityIsMadeOnceAndKeptSealed()
    {
        AgentAuthority authority = new(Path.Combine(_here, "keys"));

        Assert.False(authority.Exists);

        string first = authority.PublicKey();
        string again = new AgentAuthority(Path.Combine(_here, "keys")).PublicKey();

        Assert.StartsWith("ssh-ed25519 ", first, StringComparison.Ordinal);
        Assert.Equal(first, again);

        string key = Path.Combine(_here, "keys", "db_ed25519");

        AgentAccountSetup.MakeKey(key);
        authority.Sign(key, "agent", TimeSpan.FromMinutes(5));

        string[] files = [.. Directory.GetFiles(Path.Combine(_here, "keys")).Select(Path.GetFileName)!];

        Assert.Equal(["ca_ed25519.pub", "ca_ed25519.sealed", "db_ed25519", "db_ed25519-cert.pub", "db_ed25519.pub"], files.Order(StringComparer.Ordinal));
        Assert.DoesNotContain("PRIVATE KEY", File.ReadAllText(Path.Combine(_here, "keys", "ca_ed25519.sealed"), Encoding.Latin1), StringComparison.Ordinal);
    }

    /// <summary>A certificate names the account alone, and lasts the lifetime given from a minute back.</summary>
    [Fact]
    public void ACertificateNamesTheAccountAndLastsItsLifetime()
    {
        AgentAuthority authority = new(_here);
        string key = Path.Combine(_here, "db_ed25519");

        authority.PublicKey();
        AgentAccountSetup.MakeKey(key);

        string listed = Listed(authority.Sign(key, "agent", TimeSpan.FromMinutes(7)));
        Match valid = Regex.Match(listed, @"Valid: from (\S+) to (\S+)");

        Assert.Matches(@"Principals:\s+agent\s", listed);
        Assert.True(valid.Success, listed);
        Assert.Equal(TimeSpan.FromMinutes(8),
                     DateTime.Parse(valid.Groups[2].Value, CultureInfo.InvariantCulture) - DateTime.Parse(valid.Groups[1].Value, CultureInfo.InvariantCulture));
    }

    /// <summary>With no authority made, signing is refused in words, not with a file nobody asked for.</summary>
    [Fact]
    public void SigningWithNoAuthorityIsRefused()
    {
        string key = Path.Combine(_here, "db_ed25519");

        AgentAccountSetup.MakeKey(key);

        Assert.Throws<AgentSetupRefusal>(() => new AgentAuthority(Path.Combine(_here, "none")).Sign(key, "agent", TimeSpan.FromMinutes(5)));
    }

    /// <summary>The server trusts the authority for this account's principal, under the same restrictions a key would carry.</summary>
    [Fact]
    public void ThePlanTrustsTheAuthorityForTheAccountAlone()
    {
        string authority = new AgentAuthority(_here).PublicKey();
        AgentSetupPlan plan = AgentAccountSetup.Plan("agent", authority, "10.0.0.5", [], authority: true);

        Assert.Contains($"'cert-authority,principals=\"agent\",restrict,from=\"10.0.0.5\" {authority}'", plan.Script, StringComparison.Ordinal);
    }

    /// <summary>The policy sets the lifetime, five minutes where it says nothing, and one outside an hour refuses everything.</summary>
    [Fact]
    public void ThePolicySetsTheLifetimeWithinAnHour()
    {
        ResolvedSession db = SessionTree.Of(new SessionNode { Children = [new SessionNode { Name = "db", Host = "db" }] }).Session("db")!;

        Assert.Equal(TimeSpan.FromMinutes(5), AgentPolicy.Parse("{}").CertificateLifetime);
        Assert.Equal(TimeSpan.FromMinutes(2), AgentPolicy.Parse("""{ "certificateMinutes": 2 }""").CertificateLifetime);
        Assert.Equal(Verdict.Refuse, AgentPolicy.Parse("""{ "default": "operate", "certificateMinutes": 600 }""").Judge(db, ["uptime"], false, DateTimeOffset.Now).Verdict);
    }

    /// <summary>
    /// Against the Linux fixture: an account set up to trust the authority takes the agent in on a fresh
    /// certificate through the gateway, and refuses both the key alone and the same certificate once its
    /// window has passed.
    /// </summary>
    [Fact]
    public async Task OnlyAFreshCertificateSignsTheAgentIn()
    {
        SshFixture.SkipWithoutIt();

        string account = "qsagent" + Guid.NewGuid().ToString("N")[..6];
        string probeKey = Path.Combine(Repository.Root, "prototypes", "SshProbe", "fixture", "keys", "probe_ed25519");
        AgentAuthority authority = new(Path.Combine(_here, "keys"));
        string key = AgentAccountSetup.KeyFor(authority.Folder, "db");
        TrustOnFirstUse trust = new(KnownHosts.ReadFrom(Path.Combine(_here, "known_hosts")), (_, _) => ValueTask.FromResult(SshHostKeyVerdict.Accept));

        AgentAccountSetup.MakeKey(key);

        SessionTree store = SessionTree.Of(new SessionNode
        {
            Children = [new SessionNode { Name = "db", Host = "127.0.0.1", Settings = new SessionSettings { User = "probe", Port = 2222, Key = probeKey } }],
        });

        AgentSetupPlan plan;

        await using (IGatewayConnection person = await AgentGateway.Connect(store.Session("db")!, trust, Stop))
        {
            plan = AgentAccountSetup.Plan(account, authority.PublicKey(), await AgentAccountSetup.SeenFromAsync(person, Stop), [], authority: true);
        }

        Assert.True(SshFixture.Docker($"echo {Convert.ToBase64String(Encoding.UTF8.GetBytes(plan.Script))} | base64 -d | sh"), "the setup did not run as root");

        try
        {
            store = AgentAccountSetup.Record(store, "db", account, key, certificate: true);

            await using (AgentGateway gateway = new(() => store, (session, token) => AgentGateway.Connect(session, trust, token, new AgentSigning(authority, TimeSpan.FromMinutes(1))), "test")
            {
                PolicyFile = Policies.BreakGlass(),
            })
            {
                Assert.Equal(account, (await Whoami(gateway))["stdout"]!.GetValue<string>().Trim());
            }

            SshEndpoint target = SshEndpoint.For("127.0.0.1", account, 2222);

            // The key alone: the account trusts the authority and nothing else.
            await Assert.ThrowsAsync<SshException>(() => SignIn(target, new SshCredential.PrivateKey(key), trust));

            // A certificate whose window has passed.
            string brief = authority.Sign(key, account, TimeSpan.FromSeconds(2));

            await Task.Delay(TimeSpan.FromSeconds(4), Stop);
            await Assert.ThrowsAsync<SshException>(() => SignIn(target, new SshCredential.PrivateKey(key, CertificatePath: brief), trust));

            // And a fresh one again, so it is the window that refused and nothing else.
            await SignIn(target, new SshCredential.PrivateKey(key, CertificatePath: authority.Sign(key, account, TimeSpan.FromMinutes(1))), trust);
        }
        finally
        {
            SshFixture.Docker($"echo {Convert.ToBase64String(Encoding.UTF8.GetBytes(plan.Undo))} | base64 -d | sh");
        }
    }

    /// <summary>
    /// QS360: where the signing must be confirmed and it is not, the gateway refuses the connection naming why,
    /// and nothing is signed: no certificate is left beside the key. Asked with no quickshell window open, the
    /// Windows Hello confirmation says there is no one to ask.
    /// </summary>
    [Fact]
    public async Task AnUnconfirmedSigningSignsNothingAndSaysWhy()
    {
        AgentAuthority authority = new(Path.Combine(_here, "keys"));
        string key = AgentAccountSetup.KeyFor(authority.Folder, "db");

        authority.PublicKey();
        AgentAccountSetup.MakeKey(key);

        ResolvedSession session = SessionTree.Of(new SessionNode
        {
            Children = [new SessionNode { Name = "db", Host = "127.0.0.1", Settings = new SessionSettings { Port = 1, AgentUser = "agent", AgentKey = key, AgentCertificate = true } }],
        }).Session("db")!;
        TrustOnFirstUse trust = new(KnownHosts.ReadFrom(Path.Combine(_here, "known_hosts")), (_, _) => ValueTask.FromResult(SshHostKeyVerdict.Accept));
        List<string> asked = [];

        AgentSigning declined = new(authority, TimeSpan.FromMinutes(1))
        {
            Confirms = (who, principal, _) =>
            {
                asked.Add($"{who.Path} as {principal}");

                return Task.FromResult<string?>("Windows Hello was declined at the desk");
            },
        };

        SshException refused = await Assert.ThrowsAsync<SshException>(async () => await AgentGateway.Connect(session, trust, Stop, declined));

        Assert.Equal(["db as agent"], asked);
        Assert.Contains("Windows Hello was declined at the desk", refused.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(key + "-cert.pub"), "a certificate was signed though the signing was not confirmed");

        Assert.False(AgentAsks.WindowOpen(), "a quickshell window holds the asks name in this test process");
        Assert.Equal("no quickshell window is open to ask Windows Hello in",
                     await AgentSigning.ByHello(Path.Combine(_here, "asks"))(session, "agent", Stop));
    }

    /// <summary>The window answers a signing with Windows Hello: verified approves, and declined or not set up refuses, saying which.</summary>
    [Theory]
    [InlineData(HelloVerdict.Verified, Decision.Approve, "confirmed with Windows Hello")]
    [InlineData(HelloVerdict.Declined, Decision.Refuse, "Windows Hello was declined at the desk")]
    [InlineData(HelloVerdict.Unavailable, Decision.Refuse, "Windows Hello is not set up for this Windows account")]
    public void TheWindowAnswersASigningWithWindowsHello(HelloVerdict verdict, Decision decision, string said)
    {
        ApprovalRequest request = new("r1", "sign", [new AskedCall("Work/db", "db", ["prod"], AgentSigning.SignTool, ["agent"], null, null, "agentCertificateHello")]);

        (ApprovalAnswer answer, string? prompt) = Sta.Run(() =>
        {
            string? shown = null;
            MainWindow window = new()
            {
                Verifies = message =>
                {
                    shown = message;

                    return Task.FromResult(verdict);
                },
            };

            return (window.AnswerSigning(request).GetAwaiter().GetResult(), shown);
        });

        Assert.Equal(decision, answer.Decision);
        Assert.Equal(request.Hash, answer.Hash);
        Assert.StartsWith(said, answer.Note, StringComparison.Ordinal);
        Assert.Equal("An agent is signing in to Work/db as agent. Confirm it is you.", prompt);
    }

    private static async Task SignIn(SshEndpoint target, SshCredential credential, TrustOnFirstUse trust)
    {
        await using SshNetTransport transport = new();

        await transport.ConnectAsync(target, [credential], trust.CheckAsync, Stop);
    }

    private static async Task<JsonNode> Whoami(AgentGateway gateway)
    {
        JsonObject answer = (await gateway.AnswerAsync(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "tools/call",
            ["params"] = new JsonObject
            {
                ["name"] = "exec",
                ["arguments"] = new JsonObject { ["host"] = "db", ["argv"] = new JsonArray("whoami") },
            },
        }.ToJsonString(), Stop))!;

        return answer["result"]!["structuredContent"] ?? throw new Xunit.Sdk.XunitException(answer.ToJsonString());
    }

    /// <summary>What ssh-keygen says a certificate holds.</summary>
    private static string Listed(string certificate)
    {
        using Process keygen = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "OpenSSH", "ssh-keygen.exe"))
        {
            ArgumentList = { "-L", "-f", certificate },
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        })!;

        string listed = keygen.StandardOutput.ReadToEnd();

        keygen.WaitForExit();

        return listed;
    }
}
