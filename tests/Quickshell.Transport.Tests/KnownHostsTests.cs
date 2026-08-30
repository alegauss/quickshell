using System.Diagnostics;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Quickshell.Transport;
using Xunit;

namespace Quickshell.Transport.Tests;

/// <summary>
/// The one check that makes an encrypted session mean anything.
///
/// <para><b>The store is the user's own file</b>, so these tests read and write real
/// <c>known_hosts</c> syntax in a temporary directory, and one of them hands what this client wrote
/// to <c>ssh</c> itself and watches it connect — which is the only way to know the format is right
/// rather than merely self-consistent.</para>
/// </summary>
public sealed class KnownHostsTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"quickshell-hosts-{Guid.NewGuid():N}");

    private static CancellationToken Stop => TestContext.Current.CancellationToken;

    private static SshEndpoint Somewhere => SshEndpoint.For("host.example", "user");

    /// <summary>Two keys that differ, built from bytes so a test never depends on a real server.</summary>
    private static SshHostKey Key(string seed) =>
        new("ssh-ed25519", SHA256.HashData(Encoding.UTF8.GetBytes(seed)));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    // ---- The three outcomes ----

    /// <summary>A host nobody has met is unknown, which is a question and not a failure.</summary>
    [Fact]
    public void AHostNobodyHasMetIsUnknown()
    {
        KnownHosts store = KnownHosts.ReadFrom(File("empty"));

        Assert.Equal(KnownHostVerdict.Unknown, store.Check(Somewhere, Key("a"), out SshHostKey? stored));
        Assert.Null(stored);
        Assert.Equal(0, store.Count);
    }

    /// <summary>Remembered, then met again: no question at all the second time.</summary>
    [Fact]
    public void AKeyThatWasRememberedIsRecognised()
    {
        KnownHosts store = KnownHosts.ReadFrom(File("remembered"));

        store.Add(Somewhere, Key("a"));

        Assert.Equal(KnownHostVerdict.Matches, store.Check(Somewhere, Key("a"), out _));

        // And by a store that has only the file to go on, which is what the next launch has.
        KnownHosts reopened = KnownHosts.ReadFrom(File("remembered"));

        Assert.Equal(1, reopened.Count);
        Assert.Equal(KnownHostVerdict.Matches, reopened.Check(Somewhere, Key("a"), out _));
    }

    /// <summary>
    /// A different key for a host that is known is the failure this whole line exists for, and the
    /// old key comes back with it — a user comparing two fingerprints can recognise a server they
    /// rebuilt, and a user shown only the new one cannot.
    /// </summary>
    [Fact]
    public void AChangedKeyIsChangedAndSaysWhatWasThereBefore()
    {
        KnownHosts store = KnownHosts.ReadFrom(File("changed"));

        store.Add(Somewhere, Key("original"));

        Assert.Equal(KnownHostVerdict.Changed,
                     store.Check(Somewhere, Key("impostor"), out SshHostKey? stored));

        Assert.NotNull(stored);
        Assert.Equal(Key("original").Fingerprint, stored.Value.Fingerprint);
    }

    /// <summary>
    /// A host offering an algorithm the store has never seen for it is a new key to learn, not an
    /// interception. Several keys of different algorithms for one host is how every real server is.
    /// </summary>
    [Fact]
    public void ASecondAlgorithmForAKnownHostIsNotAMismatch()
    {
        KnownHosts store = KnownHosts.ReadFrom(File("algorithms"));

        store.Add(Somewhere, Key("ed25519"));

        SshHostKey rsa = new("ssh-rsa", SHA256.HashData("rsa"u8));

        Assert.Equal(KnownHostVerdict.Unknown, store.Check(Somewhere, rsa, out _));

        store.Add(Somewhere, rsa);

        // And now both are recognised, which is the state a real known_hosts is usually in.
        Assert.Equal(KnownHostVerdict.Matches, store.Check(Somewhere, Key("ed25519"), out _));
        Assert.Equal(KnownHostVerdict.Matches, store.Check(Somewhere, rsa, out _));
    }

    /// <summary>A revoked key is not a question either, and is refused by its own name.</summary>
    [Fact]
    public void ARevokedKeyIsRefusedAsRevoked()
    {
        SshHostKey bad = Key("revoked");

        Write("revocation", $"@revoked {Somewhere.Host} {bad.Stored}");

        KnownHosts store = KnownHosts.ReadFrom(File("revocation"));

        Assert.Equal(KnownHostVerdict.Revoked, store.Check(Somewhere, bad, out _));
    }

    // ---- The file is somebody else's, and is read as they wrote it ----

    /// <summary>
    /// Hashed entries are read. <c>HashKnownHosts</c> is on by default on several distributions, so
    /// a client that skipped them would look at a full file, recognise nothing, and ask a user to
    /// re-trust every host they already trust — which trains them to click through the one dialog
    /// that must never become a reflex.
    /// </summary>
    [Fact]
    public void AHashedEntryIsReadLikeAnyOther()
    {
        SshHostKey key = Key("hashed");
        byte[] salt = RandomNumberGenerator.GetBytes(20);
        byte[] digest = HMACSHA1.HashData(salt, Encoding.UTF8.GetBytes(Somewhere.Host));

        Write("hashed",
              $"|1|{Convert.ToBase64String(salt)}|{Convert.ToBase64String(digest)} {key.Stored}");

        KnownHosts store = KnownHosts.ReadFrom(File("hashed"));

        Assert.Equal(KnownHostVerdict.Matches, store.Check(Somewhere, key, out _));

        // And a different host does not match that hash, which is the half that would make a
        // credulous implementation dangerous rather than merely useless.
        Assert.Equal(KnownHostVerdict.Unknown,
                     store.Check(SshEndpoint.For("elsewhere.example", "user"), key, out _));
    }

    /// <summary>A port that is not 22 is its own entry, in OpenSSH's bracket spelling.</summary>
    [Fact]
    public void APortThatIsNotTwentyTwoIsItsOwnEntry()
    {
        KnownHosts store = KnownHosts.ReadFrom(File("ports"));

        SshEndpoint odd = SshEndpoint.For("host.example", "user", 2222);

        store.Add(odd, Key("a"));

        Assert.Equal(KnownHostVerdict.Matches, store.Check(odd, Key("a"), out _));

        // The same machine on the usual port is a different service and is not covered by it.
        Assert.Equal(KnownHostVerdict.Unknown, store.Check(Somewhere, Key("a"), out _));

        Assert.Contains("[host.example]:2222", ReadBack("ports"), StringComparison.Ordinal);
    }

    /// <summary>Comments, blanks and lines this client cannot read are left alone and acted on never.</summary>
    [Fact]
    public void WhatCannotBeReadIsNotActedOn()
    {
        Write("messy",
              "# a comment",
              string.Empty,
              "not-enough-fields",
              $"@newer-marker-nobody-knows {Somewhere.Host} ssh-ed25519 !!!not-base64!!!",
              $"{Somewhere.Host} {Key("real").Stored}");

        KnownHosts store = KnownHosts.ReadFrom(File("messy"));

        Assert.Equal(1, store.Count);
        Assert.Equal(KnownHostVerdict.Matches, store.Check(Somewhere, Key("real"), out _));
    }

    /// <summary>
    /// Forgetting rewrites the file without that host's lines and leaves everybody else's alone.
    /// This is the deliberate act a changed key requires, and it must not take anything with it.
    /// </summary>
    [Fact]
    public void ForgettingRemovesOneHostAndKeepsTheRest()
    {
        SshEndpoint other = SshEndpoint.For("other.example", "user");

        KnownHosts store = KnownHosts.ReadFrom(File("forget"));

        store.Add(Somewhere, Key("a"));
        store.Add(other, Key("b"));

        Assert.Equal(1, store.Forget(Somewhere));

        Assert.Equal(KnownHostVerdict.Unknown, store.Check(Somewhere, Key("a"), out _));
        Assert.Equal(KnownHostVerdict.Matches, store.Check(other, Key("b"), out _));

        KnownHosts reopened = KnownHosts.ReadFrom(File("forget"));

        Assert.Equal(1, reopened.Count);
        Assert.Equal(KnownHostVerdict.Matches, reopened.Check(other, Key("b"), out _));
    }

    /// <summary>A file that does not exist is an empty store, not a failure: a first run has none.</summary>
    [Fact]
    public void AMissingFileIsAnEmptyStore()
    {
        KnownHosts store = KnownHosts.ReadFrom(Path.Combine(_directory, "nothing", "here"));

        Assert.Equal(0, store.Count);
        Assert.Equal(KnownHostVerdict.Unknown, store.Check(Somewhere, Key("a"), out _));
    }

    // ---- The check itself: closed by default, and a change is not a question ----

    /// <summary>A key the store knows connects with nobody being asked anything.</summary>
    [Fact]
    public async Task AKnownKeyIsNotPutToTheUser()
    {
        KnownHosts store = KnownHosts.ReadFrom(File("silent"));

        store.Add(Somewhere, Key("a"));

        bool asked = false;

        TrustOnFirstUse trust = new(store, (_, _) =>
        {
            asked = true;

            return ValueTask.FromResult(SshHostKeyVerdict.Refuse);
        });

        Assert.Equal(SshHostKeyVerdict.Accept, await trust.CheckAsync(Somewhere, Key("a"), Stop));
        Assert.False(asked, "a key the store already had was put to the user anyway");
    }

    /// <summary>
    /// The design's own criterion: a changed host key cannot be accepted by clicking a default
    /// button. Here there is no button — the decision is never asked for, so a caller that always
    /// says yes still cannot get through.
    /// </summary>
    [Fact]
    public async Task AChangedKeyIsRefusedEvenByACallerThatSaysYesToEverything()
    {
        KnownHosts store = KnownHosts.ReadFrom(File("firm"));

        store.Add(Somewhere, Key("original"));

        bool asked = false;

        TrustOnFirstUse trust = new(store, (_, _) =>
        {
            asked = true;

            return ValueTask.FromResult(SshHostKeyVerdict.AcceptAndRemember);
        });

        Assert.Equal(SshHostKeyVerdict.Refuse, await trust.CheckAsync(Somewhere, Key("impostor"), Stop));
        Assert.False(asked, "a changed key was put to the user as a question");

        // And it did not quietly learn the new key on the way past.
        Assert.Equal(KnownHostVerdict.Changed, store.Check(Somewhere, Key("impostor"), out _));
    }

    /// <summary>An unknown key is asked about, and remembering it is what writes the line.</summary>
    [Fact]
    public async Task AnUnknownKeyIsAskedAboutAndRememberingItWritesTheLine()
    {
        KnownHosts store = KnownHosts.ReadFrom(File("first-use"));

        HostKeyQuestion? seen = null;

        TrustOnFirstUse trust = new(store, (question, _) =>
        {
            seen = question;

            return ValueTask.FromResult(SshHostKeyVerdict.AcceptAndRemember);
        });

        Assert.Equal(SshHostKeyVerdict.AcceptAndRemember,
                     await trust.CheckAsync(Somewhere, Key("a"), Stop));

        Assert.NotNull(seen);
        Assert.False(seen.Value.IsChange);
        Assert.Null(seen.Value.Stored);

        // Both fingerprints are offered, because a user comparing against a runbook written in 2014
        // has only the older one to compare with.
        Assert.Equal(43, seen.Value.Key.Fingerprint.Length);
        Assert.Contains(":", seen.Value.Key.LegacyFingerprint, StringComparison.Ordinal);

        Assert.Equal(KnownHostVerdict.Matches, KnownHosts.ReadFrom(File("first-use"))
                                                         .Check(Somewhere, Key("a"), out _));
    }

    /// <summary>Accepting without remembering connects and leaves the file alone.</summary>
    [Fact]
    public async Task AcceptingWithoutRememberingWritesNothing()
    {
        KnownHosts store = KnownHosts.ReadFrom(File("once"));

        TrustOnFirstUse trust = new(store, (_, _) => ValueTask.FromResult(SshHostKeyVerdict.Accept));

        Assert.Equal(SshHostKeyVerdict.Accept, await trust.CheckAsync(Somewhere, Key("a"), Stop));
        Assert.Equal(0, KnownHosts.ReadFrom(File("once")).Count);
    }

    /// <summary>
    /// The warning names both readings. A rebuilt server looks exactly like a machine in the middle,
    /// and a message that mentioned only the frightening one would be dismissed by the many users
    /// for whom it is the boring one.
    /// </summary>
    [Fact]
    public void TheWarningForAChangedKeyNamesBothReadings()
    {
        KnownHosts store = KnownHosts.ReadFrom(File("wording"));

        store.Add(Somewhere, Key("original"));
        store.Check(Somewhere, Key("impostor"), out SshHostKey? stored);

        SshException refused = TrustOnFirstUse.Refused(
            new HostKeyQuestion(Somewhere, Key("impostor"), stored, KnownHostVerdict.Changed));

        Assert.Equal(SshFailureKind.HostKey, refused.Kind);
        Assert.Contains("rebuilt", refused.Means, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("in the middle", refused.Means, StringComparison.OrdinalIgnoreCase);

        // Both fingerprints, so a user can recognise the one they know.
        Assert.Contains(Key("impostor").Fingerprint, refused.Means, StringComparison.Ordinal);
        Assert.Contains(Key("original").Fingerprint, refused.Means, StringComparison.Ordinal);

        // And the remedy is a deliberate removal rather than a button.
        Assert.Contains("known_hosts", refused.Remedy, StringComparison.Ordinal);
    }

    // ---- The format is OpenSSH's, judged by OpenSSH ----

    /// <summary>
    /// The design's other criterion: <c>known_hosts</c> written by this client is read by OpenSSH
    /// unchanged.
    ///
    /// <para>This connects to the fixture, takes the key the server presented, writes it with this
    /// client's own code, and then hands that file to <c>ssh</c> with
    /// <c>StrictHostKeyChecking=yes</c> — which refuses to connect to a host it cannot find in the
    /// file it was given. A connection is <c>ssh</c> saying the line is right.</para>
    /// </summary>
    [Fact]
    public async Task WhatThisClientWritesIsReadBySshItself()
    {
        SkipWithoutFixture();

        SshEndpoint fixture = SshEndpoint.For("127.0.0.1", "probe", 2222);
        string file = File("interop");

        KnownHosts store = KnownHosts.ReadFrom(file);
        TrustOnFirstUse trust = new(store, (_, _) =>
            ValueTask.FromResult(SshHostKeyVerdict.AcceptAndRemember));

        await using (SshNetTransport transport = new())
        {
            await transport.ConnectAsync(fixture, [new SshCredential.PrivateKey(FixtureKey())],
                                         trust.CheckAsync, Stop);

            Assert.True(transport.IsConnected);
        }

        Assert.Equal(1, KnownHosts.ReadFrom(file).Count);

        // Judged on the host-key stage and not on the connection, deliberately. StrictHostKeyChecking
        // refuses before authentication, so "Host key verification failed" is ssh rejecting the file
        // and its absence is ssh accepting it — and this then does not depend on the fixture key's
        // permissions, which Windows ssh refuses for a key living in a repository.
        (_, string accepted) = Ssh(
            $"-o StrictHostKeyChecking=yes -o UserKnownHostsFile=\"{file}\" -o BatchMode=yes "
            + "-p 2222 probe@127.0.0.1 true");

        Assert.DoesNotContain("Host key verification failed", accepted, StringComparison.Ordinal);

        // And the other half, which is what says the file did it: with an empty one, ssh refuses at
        // exactly that stage. Without this the test above would pass against any file at all.
        (_, string refused) = Ssh(
            $"-o StrictHostKeyChecking=yes -o UserKnownHostsFile=\"{File("nothing-known")}\" "
            + "-o BatchMode=yes -p 2222 probe@127.0.0.1 true");

        Assert.Contains("Host key verification failed", refused, StringComparison.Ordinal);
    }

    // ---- plumbing ----

    private string File(string name)
    {
        Directory.CreateDirectory(_directory);

        string path = Path.Combine(_directory, name);

        // Created empty rather than absent: ssh treats a missing UserKnownHostsFile as a reason to
        // ask rather than to refuse, and the negative half of the interop test needs a refusal.
        if (!System.IO.File.Exists(path))
        {
            System.IO.File.WriteAllText(path, string.Empty);
        }

        return path;
    }

    private void Write(string name, params string[] lines) =>
        System.IO.File.WriteAllLines(File(name), lines);

    private string ReadBack(string name) => System.IO.File.ReadAllText(File(name));

    private static string FixtureKey() =>
        Path.Combine(RepositoryRoot(), "prototypes", "SshProbe", "fixture", "keys", "probe_ed25519");

    /// <summary>Runs the OpenSSH client, which is the judge this test exists to consult.</summary>
    private static (int Code, string Output) Ssh(string arguments)
    {
        using Process? ssh = Process.Start(new ProcessStartInfo("ssh", arguments)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        });

        if (ssh is null)
        {
            return (-1, "ssh did not start");
        }

        string output = ssh.StandardOutput.ReadToEnd() + ssh.StandardError.ReadToEnd();

        ssh.WaitForExit(TimeSpan.FromSeconds(30));

        return (ssh.ExitCode, output);
    }

    private static void SkipWithoutFixture()
    {
        bool up;

        try
        {
            using TcpClient probe = new();

            up = probe.ConnectAsync("127.0.0.1", 2222).Wait(TimeSpan.FromSeconds(2));
        }
        catch (Exception)
        {
            up = false;
        }

        Assert.SkipUnless(up && System.IO.File.Exists(FixtureKey()),
            "nothing is listening on 127.0.0.1:2222: run prototypes/SshProbe/fixture/up.sh");
    }

    private static string RepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null && !System.IO.File.Exists(Path.Combine(directory.FullName, "Quickshell.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);

        return directory.FullName;
    }
}
