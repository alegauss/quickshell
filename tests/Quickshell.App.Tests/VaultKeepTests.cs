using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows.Threading;
using System.Xml;

using Quickshell.Transport;

using Xunit;

namespace Quickshell.App.Tests;

/// <summary>
/// QS307: an exported vault kept current. Once an export asks to keep its file up to date, a password
/// saved or changed anywhere writes the vault again without its passphrase; a vault edited outside
/// the client is never written over.
/// </summary>
public sealed class VaultKeepTests : IDisposable
{
    private const string Provisioned = @"C:\ProgramData\quickshell-cases\keepassxc\keepassxc-cli.exe";

    private const string Strong = "lunar oxidize payable sprawl unclip gravity";

    private static readonly SshEndpoint Web = SshEndpoint.For("web01.example.invalid", "deploy", 22);

    private readonly string _here = Path.Combine(Path.GetTempPath(), "qs-keep-" + Guid.NewGuid().ToString("N"));

    public VaultKeepTests() => Directory.CreateDirectory(_here);

    public void Dispose() => Directory.Delete(_here, recursive: true);

    private string Vault => Path.Combine(_here, "kept.kdbx");

    private string SettingsPath => Path.Combine(_here, "settings.json");

    /// <summary>
    /// A password changed after the export reaches the vault on its own, through the store's change
    /// and not a call, and KeePassXC reads the new one; the setting names the file.
    /// </summary>
    [Fact]
    public void AChangedPasswordIsWrittenToTheKeptVaultAndKeePassXcReadsIt()
    {
        string cli = Cli();
        SecretStore secrets = SecretStore.In(Path.Combine(_here, "secrets"));

        Save(secrets, "first-password");

        string written = Sta.Run(() =>
        {
            MainWindow window = Window(secrets, []);

            Assert.Equal(Vault, Pump(window.ExportVaultAsync()));

            string before = KdbxWriter.Hash(Vault);

            // The change comes from a store of its own, as a connection's would.
            Save(SecretStore.In(Path.Combine(_here, "secrets")), "second-password");

            Stopwatch waited = Stopwatch.StartNew();

            while (KdbxWriter.Hash(Vault) == before && waited.Elapsed < TimeSpan.FromSeconds(20))
            {
                Pump(Task.Delay(50));
            }

            window.Close();

            return KdbxWriter.Hash(Vault) == before ? "not rewritten" : "rewritten";
        });

        Assert.Equal("rewritten", written);
        Assert.Equal(Vault, SettingsFile.ReadFrom(SettingsPath).VaultFile);
        Assert.Equal("second-password", Password(cli, "web01"));
    }

    /// <summary>A vault edited outside the client is left as it is, and the person is told which file.</summary>
    [Fact]
    public void AVaultEditedOutsideTheClientIsNotWrittenOver()
    {
        SecretStore secrets = SecretStore.In(Path.Combine(_here, "secrets"));
        List<string> said = [];

        Save(secrets, "first-password");

        (VaultKept outcome, byte[] after) = Sta.Run(() =>
        {
            MainWindow window = Window(secrets, said);

            Pump(window.ExportVaultAsync());

            // What KeePassXC saving an edit amounts to, as far as this can tell: other bytes.
            File.AppendAllText(Vault, "edited elsewhere");
            byte[] edited = File.ReadAllBytes(Vault);

            Save(secrets, "second-password");
            VaultKeeping kept = Pump(window.KeepVaultAsync());

            Assert.Equal(edited, File.ReadAllBytes(Vault));

            window.Close();

            return (kept.Outcome, edited);
        });

        Assert.Equal(VaultKept.Edited, outcome);
        Assert.Equal(after, File.ReadAllBytes(Vault));
        Assert.Contains(said, sentence => sentence.StartsWith($"{Vault} has changed since this client last wrote it", StringComparison.Ordinal));
    }

    /// <summary>A change that leaves the vault's content as it was writes nothing.</summary>
    [Fact]
    public void AChangeThatLeavesTheContentAsItWasWritesNothing()
    {
        SecretStore secrets = SecretStore.In(Path.Combine(_here, "secrets"));

        Save(secrets, "same-password");

        (VaultKept outcome, bool same) = Sta.Run(() =>
        {
            MainWindow window = Window(secrets, []);

            Pump(window.ExportVaultAsync());

            string before = KdbxWriter.Hash(Vault);
            VaultKeeping kept = Pump(window.KeepVaultAsync());

            window.Close();

            return (kept.Outcome, KdbxWriter.Hash(Vault) == before);
        });

        Assert.Equal(VaultKept.Unchanged, outcome);
        Assert.True(same);
    }

    /// <summary>Emptying the setting forgets the vault's key.</summary>
    [Fact]
    public void EmptyingTheSettingForgetsTheKey()
    {
        SecretStore secrets = SecretStore.In(Path.Combine(_here, "secrets"));
        string keyFile = Path.Combine(_here, "vault-key.json");

        Save(secrets, "a-password");

        (bool keptAfterExport, VaultKept outcome) = Sta.Run(() =>
        {
            MainWindow window = Window(secrets, []);

            Pump(window.ExportVaultAsync());

            bool kept = File.Exists(keyFile);

            window.Apply(window.Settings with { VaultFile = string.Empty });

            VaultKeeping forgot = Pump(window.KeepVaultAsync());

            window.Close();

            return (kept, forgot.Outcome);
        });

        Assert.True(keptAfterExport);
        Assert.Equal(VaultKept.Off, outcome);
        Assert.False(File.Exists(keyFile));
    }

    /// <summary>A vault named by hand, with no export behind it, is not kept, and the person is told how to start.</summary>
    [Fact]
    public void AVaultNamedByHandIsNotKeptUntilExportedTo()
    {
        List<string> said = [];

        VaultKept outcome = Sta.Run(() =>
        {
            MainWindow window = Window(SecretStore.In(Path.Combine(_here, "secrets")), said);

            window.Apply(window.Settings with { VaultFile = Vault });

            VaultKept result = Pump(window.KeepVaultAsync()).Outcome;

            window.Close();

            return result;
        });

        Assert.Equal(VaultKept.NoKey, outcome);
        Assert.False(File.Exists(Vault));
        Assert.Contains(said, sentence => sentence.Contains("holds no key for it", StringComparison.Ordinal));
    }

    private MainWindow Window(SecretStore secrets, List<string> said)
    {
        string store = Path.Combine(_here, "sessions.json");

        File.WriteAllText(store, """
            { "name": "", "children": [ { "name": "web01", "host": "web01.example.invalid", "settings": { "user": "deploy" } } ] }
            """);

        return new MainWindow
        {
            SessionsFile = store,
            SettingsPath = SettingsPath,
            VaultKeyFile = Path.Combine(_here, "vault-key.json"),
            VaultKdf = new KdbxKdf(8 * 1024, 2),
            Tells = said.Add,
            OpensSecrets = () => secrets,
            AsksVaultExport = (_, _) => new VaultExportAnswer(Vault, Strong, Keep: true),
            Verifies = _ => Task.FromResult(HelloVerdict.Verified),
        };
    }

    private static void Save(SecretStore secrets, string password)
    {
        using Secret secret = Secret.From(password.AsSpan());

        secrets.Save(Web, secret);
    }

    /// <summary>Runs the window's dispatcher until the task is done, as the client's message loop would.</summary>
    private static T Pump<T>(Task<T> task)
    {
        Pump((Task)task);

        return task.Result;
    }

    private static void Pump(Task task)
    {
        Stopwatch waited = Stopwatch.StartNew();

        while (!task.IsCompleted && waited.Elapsed < TimeSpan.FromSeconds(30))
        {
            DispatcherFrame frame = new();

            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, () => frame.Continue = false);
            Dispatcher.PushFrame(frame);
            Thread.Sleep(10);
        }

        Assert.True(task.IsCompleted, "the vault was not kept within thirty seconds");
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

    /// <summary>The password KeePassXC reads for the entry titled so.</summary>
    private string? Password(string cli, string title)
    {
        ProcessStartInfo start = new(cli)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (string argument in new[] { "export", "-q", "-f", "xml", Vault })
        {
            start.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(start) ?? throw new InvalidOperationException($"{cli} did not start");

        process.StandardInput.WriteLine(Strong);
        process.StandardInput.Close();

        string output = process.StandardOutput.ReadToEnd();

        Assert.True(process.WaitForExit(TimeSpan.FromSeconds(60)), "keepassxc-cli did not finish within a minute");
        Assert.True(process.ExitCode == 0, $"keepassxc-cli did not open the vault (exit {process.ExitCode}): {output}");

        XmlDocument document = new() { XmlResolver = null };
        document.LoadXml(output[output.IndexOf('<', StringComparison.Ordinal)..]);

        return document.SelectSingleNode($"//Entry[String[Key='Title']/Value='{title}']/String[Key='Password']/Value")?.InnerText;
    }
}
