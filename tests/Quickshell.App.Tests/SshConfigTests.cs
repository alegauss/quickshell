using System.IO;
using System.Reflection;
using System.Text;
using Quickshell.App;
using Xunit;

namespace Quickshell.App.Tests;

/// <summary>
/// The file the user already maintains, read and never written.
///
/// <para>The configs here are written the way people write them — mixed indentation, an equals sign
/// in one place and a space in another, a wildcard block at the bottom — because a parser tested only
/// against its own output is a parser tested against nothing.</para>
/// </summary>
public sealed class SshConfigTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"quickshell-config-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    /// <summary>A config of the shape a developer accumulates over years.</summary>
    private const string Typical = """
        # Everything goes through the bastion unless it says otherwise.
        Host bastion
            HostName bastion.example.com
            User jump
            Port 2222

        Host *.prod !db.prod
            User deploy
            ProxyJump bastion
            IdentityFile ~/.ssh/id_prod

        Host db.prod
            HostName 10.0.0.5
            User postgres

        Host old-appliance
            HostName 192.168.1.1
            ProxyCommand /usr/bin/corkscrew proxy 8080 %h %p

        Host *
            IdentityFile ~/.ssh/id_ed25519
            ServerAliveInterval 30
            StrictHostKeyChecking yes
        """;

    // ---- The falsification ----

    /// <summary>
    /// The line's own falsification: this client never writes to a file OpenSSH also reads.
    ///
    /// <para>Guaranteed by there being no method that could. The file is shared with <c>ssh</c>,
    /// <c>scp</c>, <c>rsync</c> and <c>git</c>, so a client that reformatted it would have quietly
    /// broken four other tools.</para>
    /// </summary>
    [Fact]
    public void ThereIsNoWayForThisClientToWriteAnSshConfig()
    {
        MethodInfo[] members = typeof(SshConfig).GetMethods(BindingFlags.Public | BindingFlags.NonPublic
                                                            | BindingFlags.Instance | BindingFlags.Static);

        Assert.DoesNotContain(members, member => member.Name.Contains("Write", StringComparison.Ordinal));
        Assert.DoesNotContain(members, member => member.Name.Contains("Save", StringComparison.Ordinal));
    }

    /// <summary>And reading one leaves the bytes and the timestamp exactly as they were.</summary>
    [Fact]
    public void ReadingAConfigDoesNotTouchIt()
    {
        string file = Write("config", Typical);

        byte[] before = File.ReadAllBytes(file);
        DateTime written = File.GetLastWriteTimeUtc(file);

        SshConfig config = SshConfig.ReadFrom(file);

        _ = config.Resolve("web.prod");
        _ = config.Aliases;

        Assert.Equal(before, File.ReadAllBytes(file));
        Assert.Equal(written, File.GetLastWriteTimeUtc(file));
    }

    // ---- What it says about a host ----

    /// <summary>A host with its own block gets what that block says, and nothing is retyped.</summary>
    [Fact]
    public void AHostGetsWhatItsOwnBlockSays()
    {
        SshConfigHost bastion = SshConfig.Parse(Typical).Resolve("bastion");

        Assert.Equal("bastion.example.com", bastion.HostName);
        Assert.Equal("bastion.example.com", bastion.Target);
        Assert.Equal("jump", bastion.User);
        Assert.Equal(2222, bastion.Port);
    }

    /// <summary>An alias with no HostName connects to itself, which is what OpenSSH does.</summary>
    [Fact]
    public void AnAliasWithNoHostNameIsItsOwnTarget()
    {
        Assert.Equal("web.prod", SshConfig.Parse(Typical).Resolve("web.prod").Target);
    }

    /// <summary>A wildcard block reaches the hosts it matches.</summary>
    [Fact]
    public void AWildcardBlockReachesWhatItMatches()
    {
        SshConfigHost web = SshConfig.Parse(Typical).Resolve("web.prod");

        Assert.Equal("deploy", web.User);
        Assert.Equal("bastion", web.ProxyJump);
        Assert.Equal(TimeSpan.FromSeconds(30), web.ServerAliveInterval);
        Assert.Equal("yes", web.StrictHostKeyChecking);
    }

    /// <summary>
    /// A negation beats the pattern it sits beside. <c>Host *.prod !db.prod</c> is every production
    /// host except that one, and a client that applied the positive anyway would send database
    /// traffic through a bastion it was deliberately kept off.
    /// </summary>
    [Fact]
    public void ANegatedPatternIsExcludedFromItsOwnBlock()
    {
        SshConfigHost database = SshConfig.Parse(Typical).Resolve("db.prod");

        Assert.Equal("postgres", database.User);
        Assert.Null(database.ProxyJump);
        Assert.Equal("10.0.0.5", database.HostName);
    }

    /// <summary>
    /// First value wins, which is OpenSSH's own rule. A config written against it behaves
    /// differently under any other, so taking the last value would connect a user's hosts to the
    /// wrong places while looking entirely reasonable.
    /// </summary>
    [Fact]
    public void TheFirstValueWinsAndNotTheLast()
    {
        SshConfig config = SshConfig.Parse("""
            Host target
                User first

            Host target
                User second

            Host *
                User last
            """);

        Assert.Equal("first", config.Resolve("target").User);
    }

    /// <summary>Identity files accumulate in order, because OpenSSH tries them in turn.</summary>
    [Fact]
    public void IdentityFilesAccumulateInOrder()
    {
        IReadOnlyList<string> keys = SshConfig.Parse(Typical).Resolve("web.prod").IdentityFiles;

        Assert.Equal(2, keys.Count);
        Assert.EndsWith("id_prod", keys[0], StringComparison.Ordinal);
        Assert.EndsWith("id_ed25519", keys[1], StringComparison.Ordinal);

        // And a tilde is expanded, because every one of these files uses one.
        Assert.DoesNotContain('~', keys[0]);
    }

    /// <summary>An equals sign is as valid a separator as a space, and both are in the wild.</summary>
    [Fact]
    public void AnEqualsSignSeparatesAsWellAsASpace()
    {
        SshConfig config = SshConfig.Parse("""
            Host equals
                HostName=equals.example
                Port = 2200
            """);

        Assert.Equal("equals.example", config.Resolve("equals").HostName);
        Assert.Equal(2200, config.Resolve("equals").Port);
    }

    /// <summary>Keywords are case-insensitive, which is how people actually write them.</summary>
    [Fact]
    public void KeywordsAreCaseInsensitive()
    {
        SshConfig config = SshConfig.Parse("""
            host Shouty
                HOSTNAME shouty.example
                user SOMEBODY
            """);

        Assert.Equal("shouty.example", config.Resolve("Shouty").HostName);
        Assert.Equal("SOMEBODY", config.Resolve("shouty").User);
    }

    // ---- What a palette lists ----

    /// <summary>
    /// The hosts named literally, and not the patterns. <c>Host *.prod</c> is a rule and not a
    /// machine, and offering it to connect to would be offering a name no server has.
    /// </summary>
    [Fact]
    public void OnlyLiteralHostsAreOffered()
    {
        IReadOnlyList<string> aliases = SshConfig.Parse(Typical).Aliases;

        Assert.Contains("bastion", aliases);
        Assert.Contains("db.prod", aliases);
        Assert.Contains("old-appliance", aliases);

        Assert.DoesNotContain("*", aliases);
        Assert.DoesNotContain("*.prod", aliases);
        Assert.DoesNotContain("!db.prod", aliases);
    }

    // ---- Include ----

    [Fact]
    public void AnIncludedFileIsReadWhereItIsIncluded()
    {
        Directory.CreateDirectory(Path.Combine(_directory, "conf.d"));

        File.WriteAllText(Path.Combine(_directory, "conf.d", "extra"), """
            Host included
                HostName included.example
                User somebody
            """, new UTF8Encoding(false));

        SshConfig config = SshConfig.ReadFrom(Write("config", """
            Include conf.d/*

            Host *
                User fallback
            """));

        Assert.Equal("included.example", config.Resolve("included").HostName);
        Assert.Equal("somebody", config.Resolve("included").User);
        Assert.Equal("fallback", config.Resolve("anything-else").User);
    }

    // ---- What is not honoured is said, not swallowed ----

    /// <summary>
    /// The design's sharpest point: silently dropping a <c>ProxyCommand</c> produces a host that
    /// looks configured and simply never connects, which is the worst diagnostic outcome available.
    /// </summary>
    [Fact]
    public void AProxyCommandIsReportedRatherThanDropped()
    {
        SshConfig config = SshConfig.Parse(Typical, "config");

        UnhonouredDirective reported = Assert.Single(
            config.Unhonoured,
            directive => directive.Keyword.Equals("ProxyCommand", StringComparison.OrdinalIgnoreCase));

        Assert.Equal("config", reported.File);
        Assert.Contains("corkscrew", reported.Value, StringComparison.Ordinal);
        Assert.Contains("will not connect", reported.Why, StringComparison.Ordinal);

        // And it names the line, so a user can go and look at it.
        Assert.True(reported.Line > 0);
        Assert.Contains("config:", reported.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("LocalForward 8080 localhost:80", "forward will not be made")]
    [InlineData("ControlMaster auto", "no effect here")]
    [InlineData("RemoteCommand tmux attach", "ignored")]
    public void EveryDirectiveThatIsNotActedOnSaysWhatHappensInstead(string line, string expected)
    {
        SshConfig config = SshConfig.Parse($"Host somewhere\n    {line}\n");

        UnhonouredDirective reported = Assert.Single(config.Unhonoured);

        Assert.Contains(expected, reported.Why, StringComparison.Ordinal);
    }

    /// <summary>
    /// A <c>Match</c> this client cannot evaluate matches nothing and says so, rather than matching
    /// everything and quietly applying settings a user meant for one case.
    /// </summary>
    [Fact]
    public void AMatchThatCannotBeEvaluatedMatchesNothingAndIsReported()
    {
        SshConfig config = SshConfig.Parse("""
            Match exec "test -f /tmp/flag"
                User conditional

            Host *
                User ordinary
            """);

        Assert.Equal("ordinary", config.Resolve("anything").User);
        Assert.Single(config.Unhonoured,
                      directive => directive.Keyword.Equals("Match", StringComparison.Ordinal));
    }

    /// <summary><c>Match host</c> is the one form a client can honour, and it is honoured.</summary>
    [Fact]
    public void MatchHostIsApplied()
    {
        SshConfig config = SshConfig.Parse("""
            Match host build-*
                User builder
            """);

        Assert.Equal("builder", config.Resolve("build-01").User);
        Assert.Null(config.Resolve("web-01").User);
    }

    /// <summary>A config that is not there is an empty one, which is a user who has never used ssh.</summary>
    [Fact]
    public void AConfigThatIsNotThereIsEmpty()
    {
        SshConfig config = SshConfig.ReadFrom(Path.Combine(_directory, "nothing"));

        Assert.Empty(config.Aliases);
        Assert.Empty(config.Unhonoured);
        Assert.Null(config.Resolve("anything").User);
    }

    private string Write(string name, string content)
    {
        Directory.CreateDirectory(_directory);

        string file = Path.Combine(_directory, name);

        File.WriteAllText(file, content, new UTF8Encoding(false));

        return file;
    }
}
