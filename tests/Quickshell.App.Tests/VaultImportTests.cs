using System.Diagnostics;
using System.IO;
using System.Text;

using Quickshell.Transport;

using Xunit;

namespace Quickshell.App.Tests;

/// <summary>
/// QS308: saved passwords imported from a KeePass vault, for a new machine whose Credential Manager
/// is empty. A vault QS306 exported round-trips; one keepassxc-cli made imports the same way; one that
/// was changed is refused by name; and nothing already saved is replaced without being ticked.
/// </summary>
public sealed class VaultImportTests : IDisposable
{
    private const string Provisioned = @"C:\ProgramData\quickshell-cases\keepassxc\keepassxc-cli.exe";

    private const string Strong = "lunar oxidize payable sprawl unclip gravity";

    private readonly string _here = Path.Combine(Path.GetTempPath(), "qs-import-" + Guid.NewGuid().ToString("N"));

    public VaultImportTests() => Directory.CreateDirectory(_here);

    public void Dispose() => Directory.Delete(_here, recursive: true);

    private string Store => Path.Combine(_here, "sessions.json");

    /// <summary>
    /// Exported from one store and imported into an empty one: every session finds its password where
    /// signing in looks for it — under its credential where it names one, for its account otherwise.
    /// </summary>
    [Fact]
    public void AnExportedVaultRestoresEveryPasswordIntoAnEmptyStore()
    {
        WriteStore();

        SecretStore before = SecretStore.In(Path.Combine(_here, "old-machine"));

        Save(before, SshEndpoint.For("web01.example.invalid", "deploy", 2222), "web-password");
        SaveNamed(before, "dba", "shared-password");

        string vault = Path.Combine(_here, "sessions.kdbx");

        using (VaultExport export = VaultExport.Gather(SessionTree.ReadFrom(Store), before))
        using (Secret passphrase = Secret.From(Strong.AsSpan()))
        using (KdbxKey key = KdbxKey.Derive(passphrase, kdf: new KdbxKdf(8 * 1024, 2)))
        {
            export.Write(vault, key);
        }

        SecretStore after = SecretStore.In(Path.Combine(_here, "new-machine"));
        List<string> said = [];

        int? saved = Sta.Run(() => Window(after, vault, said, replace: []).ImportVault());

        Assert.Equal(2, saved);
        Assert.Equal("web-password", Loaded(after.Load(SshEndpoint.For("web01.example.invalid", "deploy", 2222))));
        Assert.Equal("shared-password", Loaded(after.LoadNamed("dba")));
        Assert.Null(after.Load(SshEndpoint.For("nas.example.invalid", "admin", 22)));
        Assert.Contains(said, sentence => sentence.StartsWith("2 passwords were saved from", StringComparison.Ordinal));
    }

    /// <summary>A vault keepassxc-cli made, KDBX 3.1, imports its entry for the session its URL names.</summary>
    [Fact]
    public void AVaultKeePassXcMadeImportsTheSameWay()
    {
        string cli = Cli();

        WriteStore();

        string vault = Path.Combine(_here, "made.kdbx");

        Run(cli, ["db-create", "-q", "-p", vault], Strong + "\n" + Strong + "\n");
        Run(cli, ["add", "-q", "-u", "deploy", "--url", "ssh://deploy@web01.example.invalid:2222", "-p", vault, "web01"], Strong + "\nfrom-keepassxc\n");
        Run(cli, ["add", "-q", "-u", "someone", "--url", "https://example.invalid", "-p", vault, "a website"], Strong + "\nweb\n");

        SecretStore after = SecretStore.In(Path.Combine(_here, "new-machine"));
        VaultImport? seen = null;

        int? saved = Sta.Run(() =>
        {
            MainWindow window = Window(after, vault, [], replace: []);

            window.ConfirmsVaultImport = (plan, _) =>
            {
                seen = plan;

                return new HashSet<string>();
            };

            return window.ImportVault();
        });

        Assert.Equal(1, saved);
        Assert.Equal("from-keepassxc", Loaded(after.Load(SshEndpoint.For("web01.example.invalid", "deploy", 2222))));
        Assert.Equal(["a website"], seen!.Unmatched);
    }

    /// <summary>A vault changed after it was written is refused, naming it, and nothing is saved.</summary>
    [Fact]
    public void AnAlteredVaultIsRefusedByName()
    {
        WriteStore();

        string vault = Path.Combine(_here, "altered.kdbx");

        using (Secret password = Secret.From("web-password".AsSpan()))
        using (Secret passphrase = Secret.From(Strong.AsSpan()))
        {
            KdbxWriter.Write(vault, new KdbxGroup
            {
                Name = "x",
                Entries = [new KdbxEntry { Title = "web01", UserName = "deploy", Url = "ssh://deploy@web01.example.invalid:2222", Password = password }],
            }, passphrase, kdf: new KdbxKdf(8 * 1024, 2));
        }

        byte[] bytes = File.ReadAllBytes(vault);
        bytes[^60] ^= 0x40;
        File.WriteAllBytes(vault, bytes);

        SecretStore after = SecretStore.In(Path.Combine(_here, "new-machine"));
        List<string> said = [];

        int? saved = Sta.Run(() => Window(after, vault, said, replace: []).ImportVault());

        Assert.Null(saved);
        Assert.Null(after.Load(SshEndpoint.For("web01.example.invalid", "deploy", 2222)));
        Assert.Contains(said, sentence => sentence.StartsWith($"{vault} was changed or damaged after it was written", StringComparison.Ordinal));
    }

    /// <summary>
    /// A password already saved and different is a conflict: left as it is unless ticked, replaced
    /// when it is. One saved and the same is counted and not written.
    /// </summary>
    [Fact]
    public void ADifferentSavedPasswordIsReplacedOnlyWhenTicked()
    {
        WriteStore();

        string vault = Path.Combine(_here, "vault.kdbx");
        SshEndpoint web = SshEndpoint.For("web01.example.invalid", "deploy", 2222);

        using (Secret password = Secret.From("vault-password".AsSpan()))
        using (Secret shared = Secret.From("shared-password".AsSpan()))
        using (Secret passphrase = Secret.From(Strong.AsSpan()))
        {
            KdbxWriter.Write(vault, new KdbxGroup
            {
                Name = "x",
                Entries =
                [
                    new KdbxEntry { Title = "web01", UserName = "deploy", Url = "ssh://deploy@web01.example.invalid:2222", Password = password },
                    new KdbxEntry { Title = "db", UserName = "postgres", Url = "db1.example.invalid", Password = shared },
                ],
            }, passphrase, kdf: new KdbxKdf(8 * 1024, 2));
        }

        SecretStore secrets = SecretStore.In(Path.Combine(_here, "secrets"));

        Save(secrets, web, "saved-password");
        SaveNamed(secrets, "dba", "shared-password");

        (int? kept, ImportState webState, ImportState dbState) = Sta.Run(() =>
        {
            VaultImport? plan = null;
            MainWindow window = Window(secrets, vault, [], replace: []);

            window.ConfirmsVaultImport = (seen, _) =>
            {
                plan = seen;

                return new HashSet<string>();
            };

            int? result = window.ImportVault();

            return (result, plan!.Items.Single(item => item.Credential is null).State, plan.Items.Single(item => item.Credential == "dba").State);
        });

        Assert.Equal(0, kept);
        Assert.Equal(ImportState.Differs, webState);
        Assert.Equal(ImportState.Same, dbState);
        Assert.Equal("saved-password", Loaded(secrets.Load(web)));

        int? replaced = Sta.Run(() => Window(secrets, vault, [], replace: ["deploy@web01.example.invalid:2222"]).ImportVault());

        Assert.Equal(1, replaced);
        Assert.Equal("vault-password", Loaded(secrets.Load(web)));
    }

    /// <summary>What an entry says it is for, from its URL and its user name.</summary>
    [Theory]
    [InlineData("ssh://deploy@web01.example:2222", "", "web01.example", 2222, "deploy")]
    [InlineData("ssh://Web01.Example", "admin", "web01.example", 22, "admin")]
    [InlineData("web01.example:2200", "admin", "web01.example", 2200, "admin")]
    [InlineData("root@web01.example", "", "web01.example", 22, "root")]
    public void AnEntrysAccountIsReadFromItsUrlAndUserName(string url, string user, string host, int port, string login)
    {
        Assert.Equal((host, port, login), VaultImport.Account(new KdbxEntry { Title = "x", Url = url, UserName = user }));
    }

    /// <summary>An entry for a website, or with no user to be found, is for no session.</summary>
    [Theory]
    [InlineData("https://example.invalid/login", "someone")]
    [InlineData("ssh://web01.example", "")]
    [InlineData("", "someone")]
    public void AnEntryForSomethingElseIsForNoSession(string url, string user)
    {
        Assert.Null(VaultImport.Account(new KdbxEntry { Title = "x", Url = url, UserName = user }));
    }

    /// <summary>The import is in the palette.</summary>
    [Fact]
    public void TheImportIsInThePalette()
    {
        Assert.True(Sta.Run(() => new MainWindow().Actions.Any(one => one.Name == "Import saved passwords from a KeePass vault")));
    }

    private void WriteStore() => File.WriteAllText(Store, """
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
            { "name": "nas", "host": "nas.example.invalid", "settings": { "user": "admin" } }
          ]
        }
        """);

    private MainWindow Window(SecretStore secrets, string vault, List<string> said, string[] replace) => new()
    {
        SessionsFile = Store,
        Tells = said.Add,
        OpensSecrets = () => secrets,
        AsksVaultImport = () => new VaultImportAnswer(vault, Strong, null),
        ConfirmsVaultImport = (_, _) => new HashSet<string>(replace, StringComparer.Ordinal),
    };

    private static void Save(SecretStore secrets, SshEndpoint endpoint, string password)
    {
        using Secret secret = Secret.From(password.AsSpan());

        secrets.Save(endpoint, secret);
    }

    private static void SaveNamed(SecretStore secrets, string credential, string password)
    {
        using Secret secret = Secret.From(password.AsSpan());

        secrets.SaveNamed(credential, secret);
    }

    private static string? Loaded(Secret? secret)
    {
        using (secret)
        {
            return secret is null ? null : Encoding.UTF8.GetString(secret.Bytes);
        }
    }

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

    private static void Run(string cli, IEnumerable<string> arguments, string input)
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

        process.StandardInput.Write(input);
        process.StandardInput.Close();

        string said = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();

        Assert.True(process.WaitForExit(TimeSpan.FromSeconds(60)), "keepassxc-cli did not finish within a minute");
        Assert.True(process.ExitCode == 0, $"keepassxc-cli {string.Join(' ', arguments)} failed: {said}");
    }
}
