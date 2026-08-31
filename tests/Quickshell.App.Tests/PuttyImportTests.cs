using Microsoft.Win32;
using Quickshell.App;
using Xunit;

namespace Quickshell.App.Tests;

/// <summary>
/// Reading PuTTY's sessions out of the registry.
///
/// <para>Against a key this test makes and removes, under its own name — not against
/// <c>HKCU\Software\SimonTatham</c>, which is somebody's real estate and is not a test fixture. The
/// reader takes the key rather than opening it for exactly this reason: a reader that could only
/// read the real one could only be tested on a machine that happened to have PuTTY, and this one
/// does not.</para>
/// </summary>
public sealed class PuttyImportTests : IDisposable
{
    private readonly string _under = $@"Software\quickshell-tests\{Guid.NewGuid():N}";

    public void Dispose()
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(_under, throwOnMissingSubKey: false);
        }
        catch (Exception)
        {
            // A key that will not delete is not worth failing a passing test over; it is under a
            // name nothing else reads and carries no data but this test's own.
        }
    }

    // ---- The falsification, in this reader's terms ----

    /// <summary>
    /// Nothing the session held disappears without being named, including values this import has
    /// never heard of.
    /// </summary>
    [Fact]
    public void NoValueTheSessionCarriedIsDroppedInSilence()
    {
        using RegistryKey sessions = Sessions(("work box", new Dictionary<string, object>
        {
            ["HostName"] = "10.0.0.9",
            ["PortNumber"] = 2222,
            ["UserName"] = "root",
            ["Protocol"] = "ssh",
            ["X11Forward"] = 1,
            ["RemoteCommand"] = "tmux attach",
            ["SomethingNobodyHasIdentified"] = "set",

            // PuTTY writes every setting into every session, so an off one must not be reported —
            // otherwise the two that matter drown in a hundred that do not.
            ["Compression"] = 0,
            ["ProxyHost"] = "",
        }));

        ImportPreview preview = PuttyImport.Preview(sessions);

        ImportedSession imported = Assert.Single(preview.Sessions);

        Assert.True(imported.Carried);

        // The name is the one the user typed, not the escaped subkey.
        Assert.Equal("work box", imported.Name);
        Assert.Equal("10.0.0.9", imported.Node!.Host);
        Assert.Equal(2222, imported.Node.Settings.Port);
        Assert.Equal("root", imported.Node.Settings.User);

        string named = string.Join(" | ", imported.Unmapped);

        Assert.Contains("X11", named, StringComparison.Ordinal);
        Assert.Contains("command to run on login", named, StringComparison.Ordinal);
        Assert.Contains("SomethingNobodyHasIdentified", named, StringComparison.Ordinal);

        // And what PuTTY wrote as its own default is not reported as a choice somebody made.
        Assert.DoesNotContain("compression", named, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("proxy", named, StringComparison.OrdinalIgnoreCase);
    }

    // ---- What does not come across ----

    [Theory]
    [InlineData("telnet", "Telnet")]
    [InlineData("serial", "Serial")]
    [InlineData("raw", "raw socket")]
    [InlineData("rlogin", "Rlogin")]
    public void AProtocolThisClientDoesNotSpeakIsNamedRatherThanMissing(string protocol, string what)
    {
        using RegistryKey sessions = Sessions(("elsewhere", new Dictionary<string, object>
        {
            ["HostName"] = "host.example",
            ["Protocol"] = protocol,
        }));

        ImportedSession imported = Assert.Single(PuttyImport.Preview(sessions).Sessions);

        Assert.False(imported.Carried);
        Assert.Contains(what, imported.Skipped, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// PuTTY's "Default Settings" is a template and not a place to connect. Importing it would put
    /// an entry in the tree that goes nowhere.
    /// </summary>
    [Fact]
    public void TheSavedDefaultsAreNamedRatherThanImportedAsASession()
    {
        using RegistryKey sessions = Sessions(("Default%20Settings", new Dictionary<string, object>
        {
            ["Protocol"] = "ssh",
            ["PortNumber"] = 22,
        }));

        ImportedSession imported = Assert.Single(PuttyImport.Preview(sessions).Sessions);

        Assert.False(imported.Carried);
        Assert.Contains("saved default", imported.Skipped, StringComparison.Ordinal);
        Assert.Equal("Default Settings", imported.Name);
    }

    /// <summary>A key with nothing under it is an empty preview and not a failure.</summary>
    [Fact]
    public void NoSessionsIsAnEmptyPreview()
    {
        using RegistryKey sessions = Sessions();

        ImportPreview preview = PuttyImport.Preview(sessions);

        Assert.Empty(preview.Sessions);
        Assert.Equal(0, preview.Carrying);
    }

    /// <summary>
    /// And the same reader against whatever PuTTY this machine has, where there is one.
    ///
    /// <para>Shapes only, never contents.</para>
    ///
    /// <para><b>Skipped on an empty key and not only on an absent one</b>, which is a distinction
    /// this test was written without and passed vacuously for it: the key exists on this machine and
    /// holds no sessions, so the loop below ran over nothing and reported success. A green over an
    /// empty read is the one thing this repository keeps finding and refusing — QS136, QS145 — and
    /// it is no better when the test doing it is this one.</para>
    /// </summary>
    [Fact]
    public void ARealRegistryParsesAndAccountsForEverySession()
    {
        using RegistryKey? real = PuttyImport.Find();

        Assert.SkipWhen(real is null || real.GetSubKeyNames().Length == 0,
                        @"HKCU\Software\SimonTatham\PuTTY\Sessions holds no sessions on this "
                        + "machine, so there is nothing real to read - the fixture tests above are "
                        + "what run everywhere");

        ImportPreview preview = PuttyImport.Preview(real!);

        // Never vacuous: the skip above is what an empty machine gets, so reaching here means there
        // was something to account for.
        Assert.NotEmpty(preview.Sessions);

        foreach (ImportedSession session in preview.Sessions)
        {
            if (session.Carried)
            {
                Assert.False(string.IsNullOrWhiteSpace(session.Node!.Host));
            }
            else
            {
                Assert.False(string.IsNullOrWhiteSpace(session.Skipped));
            }
        }
    }

    // ---- plumbing ----

    /// <summary>A sessions key under this test's own name, holding what it was given.</summary>
    private RegistryKey Sessions(params (string Name, Dictionary<string, object> Values)[] sessions)
    {
        RegistryKey root = Registry.CurrentUser.CreateSubKey($@"{_under}\Sessions");

        foreach ((string name, Dictionary<string, object> values) in sessions)
        {
            using RegistryKey session = root.CreateSubKey(name);

            foreach ((string value, object held) in values)
            {
                session.SetValue(value, held);
            }
        }

        return root;
    }
}
