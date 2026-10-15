using System.Diagnostics;
using System.IO;
using System.Text;
using System.Xml;

using Quickshell.Transport;

using Xunit;

namespace Quickshell.App.Tests;

/// <summary>
/// QS306: the saved sessions and their passwords exported to a KeePass vault, behind Windows Hello
/// and a passphrase strong enough for a file anyone may copy. The vault is read back by
/// KeePassXC's own command line, placed in the guest by <c>tools/provision-guest-keepassxc.ps1</c>.
/// </summary>
public sealed class VaultExportTests : IDisposable
{
    private const string Provisioned = @"C:\ProgramData\quickshell-cases\keepassxc\keepassxc-cli.exe";

    private const string Strong = "lunar oxidize payable sprawl unclip gravity";

    private readonly string _here = Path.Combine(Path.GetTempPath(), "qs-vault-" + Guid.NewGuid().ToString("N"));

    public VaultExportTests() => Directory.CreateDirectory(_here);

    public void Dispose() => Directory.Delete(_here, recursive: true);

    /// <summary>
    /// Folders, a shared credential and a session with nothing saved: each session is an entry as
    /// the store held it, in the group its folder became, and KeePassXC reads every one.
    /// </summary>
    [Fact]
    public void EverySessionIsInTheVaultAsTheStoreHeldItAndKeePassXcReadsIt()
    {
        string cli = Cli();
        SecretStore secrets = Store();
        string vault = Path.Combine(_here, "sessions.kdbx");
        List<string> said = [];

        using (Secret password = Secret.From("web-password".AsSpan()))
        using (Secret shared = Secret.From("shared-password".AsSpan()))
        {
            secrets.Save(SshEndpoint.For("web01.example.invalid", "deploy", 2222), password);
            secrets.SaveNamed("dba", shared);
        }

        string? written = Sta.Run(() =>
        {
            MainWindow window = Window(said, secrets, vault, HelloVerdict.Verified);

            return window.ExportVaultAsync().GetAwaiter().GetResult();
        });

        Assert.Equal(vault, written);
        Assert.Contains(said, sentence => sentence.StartsWith("4 sessions, 3 of them with a saved password", StringComparison.Ordinal));

        XmlDocument export = Export(cli, vault);

        Assert.Equal(VaultExport.DatabaseName, export.SelectSingleNode("/KeePassFile/Root/Group/Name")?.InnerText);

        XmlNode web = Entry(export, "Work", "web01");
        Assert.Equal("deploy", Value(web, "UserName"));
        Assert.Equal("ssh://deploy@web01.example.invalid:2222", Value(web, "URL"));
        Assert.Equal("web-password", Value(web, "Password"));

        // Two sessions naming one credential carry its one password, and say that they share it.
        XmlNode primary = Entry(export, "Work/Databases", "db-primary");
        XmlNode replica = Entry(export, "Work/Databases", "db-replica");
        Assert.Equal("shared-password", Value(primary, "Password"));
        Assert.Equal("shared-password", Value(replica, "Password"));
        Assert.Equal("postgres", Value(replica, "UserName"));
        Assert.Contains("\"dba\"", Value(primary, "Notes"), StringComparison.Ordinal);

        // Nothing saved, and still in the vault: it is the list of where the person goes.
        XmlNode nas = Entry(export, "Home", "nas");
        Assert.Equal("ssh://admin@nas.example.invalid:22", Value(nas, "URL"));
        Assert.Equal(string.Empty, Value(nas, "Password"));
    }

    /// <summary>
    /// With the key files asked for, a key a folder names goes in once, on its first session, with its
    /// .pub, and KeePassXC hands its bytes back unchanged; without, nothing is attached (QS345).
    /// </summary>
    [Fact]
    public void TheKeyFilesSessionsNameGoInOnceWhenAskedFor()
    {
        string cli = Cli();
        string key = Path.Combine(_here, "deploy_ed25519");
        byte[] keyBytes = Encoding.ASCII.GetBytes("-----BEGIN OPENSSH PRIVATE KEY-----\nnot a real key\n-----END OPENSSH PRIVATE KEY-----\n");

        File.WriteAllBytes(key, keyBytes);
        File.WriteAllText(key + ".pub", "ssh-ed25519 AAAA deploy\n");

        string store = Path.Combine(_here, "keyed-sessions.json");

        File.WriteAllText(store, $$"""
            { "name": "", "children": [
                { "name": "Work", "settings": { "user": "deploy", "key": {{System.Text.Json.JsonSerializer.Serialize(key)}} }, "children": [
                    { "name": "web01", "host": "web01.example.invalid" },
                    { "name": "web02", "host": "web02.example.invalid" }
                ] }
            ] }
            """);

        string with = Path.Combine(_here, "with-keys.kdbx");
        string without = Path.Combine(_here, "without-keys.kdbx");

        Sta.Run(() =>
        {
            foreach ((string vault, bool keys) in new[] { (with, true), (without, false) })
            {
                MainWindow window = new()
                {
                    SessionsFile = store,
                    Tells = _ => { },
                    OpensSecrets = Store,
                    AsksVaultExport = (_, _) => new VaultExportAnswer(vault, Strong, Keys: keys),
                    Verifies = _ => Task.FromResult(HelloVerdict.Verified),
                };

                window.ExportVaultAsync().GetAwaiter().GetResult();
            }

            return 0;
        });

        string exported = Path.Combine(_here, "exported-key");

        Run(cli, ["attachment-export", "-q", with, "Work/web01", "deploy_ed25519", exported]);
        Assert.Equal(keyBytes, File.ReadAllBytes(exported));

        XmlDocument keyed = Export(cli, with);
        Assert.Equal(2, keyed.SelectNodes("//Entry[String[Key='Title']/Value='web01']/Binary")!.Count);
        Assert.Equal(0, keyed.SelectNodes("//Entry[String[Key='Title']/Value='web02']/Binary")!.Count);
        Assert.Contains("attached to the entry Work/web01", Value(Entry(keyed, "Work", "web02"), "Notes"), StringComparison.Ordinal);

        Assert.Equal(0, Export(cli, without).SelectNodes("//Binary")!.Count);
    }

    /// <summary>Without Windows Hello there is no way to confirm the person, so nothing is read and nothing written.</summary>
    [Fact]
    public void WithoutWindowsHelloNothingIsExportedAndThePersonIsToldWhy()
    {
        List<string> said = [];
        string vault = Path.Combine(_here, "refused.kdbx");

        string? written = Sta.Run(() => Window(said, Store(), vault, HelloVerdict.Unavailable).ExportVaultAsync().GetAwaiter().GetResult());

        Assert.Null(written);
        Assert.False(File.Exists(vault));
        Assert.Contains(said, sentence => sentence.StartsWith("Windows Hello is not set up for this account", StringComparison.Ordinal));
    }

    /// <summary>Declining Windows Hello's prompt writes nothing and says nothing more.</summary>
    [Fact]
    public void DecliningWindowsHelloWritesNothing()
    {
        List<string> said = [];
        string vault = Path.Combine(_here, "declined.kdbx");

        string? written = Sta.Run(() => Window(said, Store(), vault, HelloVerdict.Declined).ExportVaultAsync().GetAwaiter().GetResult());

        Assert.Null(written);
        Assert.False(File.Exists(vault));
        Assert.Empty(said);
    }

    /// <summary>The export is in the palette, which is where the Commands button on the strip leads.</summary>
    [Fact]
    public void TheExportIsInThePalette()
    {
        bool listed = Sta.Run(() => new MainWindow().Actions.Any(one => one.Name == "Export saved passwords to a KeePass vault"));

        Assert.True(listed);
    }

    /// <summary>
    /// The dialog will not export under a weak passphrase, says why, and takes a strong one only
    /// when it is typed twice alike; a generated one is six words of the list, shown to be written down.
    /// </summary>
    [Fact]
    public void TheDialogRefusesAWeakPassphraseAndOffersAGeneratedOne()
    {
        (bool atFirst, bool weak, string weakSaid, bool mismatched, bool strong, bool generated, string? words) = Sta.Run(() =>
        {
            VaultExportDialog dialog = new(Path.Combine(_here, "x.kdbx"), 3);

            bool first = dialog.CanExport;

            dialog.Type("Password123!");
            bool afterWeak = dialog.CanExport;
            string why = dialog.Verdict;

            dialog.Type(Strong, Strong + " ");
            bool afterMismatch = dialog.CanExport;

            dialog.Type(Strong);
            bool afterStrong = dialog.CanExport;

            dialog.Generate();

            return (first, afterWeak, why, afterMismatch, afterStrong, dialog.CanExport, dialog.GeneratedPassphrase);
        });

        Assert.False(atFirst);
        Assert.False(weak);
        Assert.Contains("bits", weakSaid, StringComparison.Ordinal);
        Assert.False(mismatched);
        Assert.True(strong);
        Assert.True(generated);

        string[] drawn = words!.Split(' ');
        Assert.Equal(Passphrase.Words, drawn.Length);
        Assert.All(drawn, word => Assert.Contains(word, Passphrase.Wordlist));
    }

    /// <summary>The estimate refuses what a guesser tries first and accepts six words of the list.</summary>
    [Theory]
    [InlineData("Password123!", false)]
    [InlineData("correct horse battery staple", false)]
    [InlineData("Summer2024", false)]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaa", false)]
    [InlineData("correct horse battery staple glimmer unicorn", true)]
    [InlineData(Strong, true)]
    public void ThePassphraseEstimateRefusesWhatAGuesserTriesFirst(string passphrase, bool accepted)
    {
        Assert.Equal(accepted, Passphrase.Refusal(passphrase) is null);
    }

    /// <summary>Every generated passphrase clears the bar it is offered against.</summary>
    [Fact]
    public void AGeneratedPassphraseIsAlwaysAccepted()
    {
        for (int each = 0; each < 200; each++)
        {
            string generated = Passphrase.Generate();

            Assert.True(Passphrase.Refusal(generated) is null, $"\"{generated}\" was refused");
        }
    }

    /// <summary>
    /// The interface identifier Windows Hello's answer is asked for is derived as WinRT derives one:
    /// checked against two whose values Windows publishes.
    /// </summary>
    [Theory]
    [InlineData("pinterface({9fc2b0bb-e446-44e2-aa61-9cab8f636af2};b1)", "cdb5efb3-5788-509d-9be1-71ccb8a3362a")]
    [InlineData("pinterface({61c17706-2d65-11e0-9ae8-d48564015472};i4)", "548cefbd-bc8a-5fa0-8df2-957440fc8bf4")]
    public void AParameterisedInterfaceIdIsDerivedAsWinRtDerivesIt(string signature, string expected)
    {
        Assert.Equal(new Guid(expected), WindowsHello.Parameterised(signature));
    }

    private MainWindow Window(List<string> said, SecretStore secrets, string vault, HelloVerdict verdict)
    {
        string store = Path.Combine(_here, "sessions.json");

        File.WriteAllText(store, """
            {
              "name": "",
              "children": [
                {
                  "name": "Work",
                  "settings": { "user": "deploy" },
                  "children": [
                    { "name": "web01", "host": "web01.example.invalid", "settings": { "port": 2222 } },
                    {
                      "name": "Databases",
                      "settings": { "user": "postgres", "credential": "dba" },
                      "children": [
                        { "name": "db-primary", "host": "db1.example.invalid" },
                        { "name": "db-replica", "host": "db2.example.invalid" }
                      ]
                    }
                  ]
                },
                {
                  "name": "Home",
                  "children": [ { "name": "nas", "host": "nas.example.invalid", "settings": { "user": "admin" } } ]
                }
              ]
            }
            """);

        return new MainWindow
        {
            SessionsFile = store,
            Tells = said.Add,
            OpensSecrets = () => secrets,
            AsksVaultExport = (_, _) => new VaultExportAnswer(vault, Strong),
            Verifies = _ => Task.FromResult(verdict),
        };
    }

    private SecretStore Store() => SecretStore.In(Path.Combine(_here, "secrets"));

    /// <summary>keepassxc-cli on the PATH, else the provisioned one, else a skip naming the script.</summary>
    private static string Cli()
    {
        string? onPath = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(folder => Path.Combine(folder, "keepassxc-cli.exe"))
            .FirstOrDefault(File.Exists);

        string cli = onPath ?? Provisioned;

        Assert.SkipUnless(File.Exists(cli),
            "keepassxc-cli is not placed: run tools/provision-guest-keepassxc.ps1 once, which puts KeePassXC's command line in the guest");

        return cli;
    }

    /// <summary>The vault as KeePassXC decrypted it, in its XML export.</summary>
    private static XmlDocument Export(string cli, string vault)
    {
        ProcessStartInfo start = new(cli)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            StandardInputEncoding = new UTF8Encoding(false),
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (string argument in new[] { "export", "-q", "-f", "xml", vault })
        {
            start.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(start) ?? throw new InvalidOperationException($"{cli} did not start");

        process.StandardInput.WriteLine(Strong);
        process.StandardInput.Close();

        Task<string> output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        Task<string> error = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);

        Assert.True(process.WaitForExit(TimeSpan.FromSeconds(60)), "keepassxc-cli did not finish within a minute");
        Assert.True(process.ExitCode == 0, $"keepassxc-cli did not open the vault (exit {process.ExitCode}): {output.Result}{error.Result}");

        XmlDocument document = new() { XmlResolver = null };
        document.LoadXml(output.Result[output.Result.IndexOf('<', StringComparison.Ordinal)..]);

        return document;
    }

    private static void Run(string cli, IEnumerable<string> arguments)
    {
        ProcessStartInfo start = new(cli)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(start) ?? throw new InvalidOperationException($"{cli} did not start");

        process.StandardInput.WriteLine(Strong);
        process.StandardInput.Close();

        string said = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();

        Assert.True(process.WaitForExit(TimeSpan.FromSeconds(60)), "keepassxc-cli did not finish within a minute");
        Assert.True(process.ExitCode == 0, $"keepassxc-cli {string.Join(' ', arguments)} failed: {said}");
    }

    /// <summary>The entry titled so, in the group at that path under the root.</summary>
    private static XmlNode Entry(XmlDocument export, string groups, string title)
    {
        string path = "/KeePassFile/Root/Group" + string.Concat(groups.Split('/').Select(group => $"/Group[Name='{group}']"));

        return export.SelectSingleNode($"{path}/Entry[String[Key='Title']/Value='{title}']")
            ?? throw new Xunit.Sdk.XunitException($"KeePassXC lists no entry {title} under {groups}");
    }

    private static string? Value(XmlNode entry, string key) =>
        entry.SelectSingleNode($"String[Key='{key}']/Value")?.InnerText ?? (entry.SelectSingleNode($"String[Key='{key}']") is null ? null : string.Empty);
}
