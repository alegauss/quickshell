using System.Diagnostics;
using System.Text;
using Quickshell.Transport;
using Xunit;

namespace Quickshell.Transport.Tests;

/// <summary>
/// The KDBX reader (QS308): what this client wrote comes back as written, what KeePassXC wrote is
/// read too, and a vault that was changed, or opened with the wrong key, is refused by name.
/// </summary>
public sealed class KdbxReaderTests : IDisposable
{
    private const string Provisioned = @"C:\ProgramData\quickshell-cases\keepassxc\keepassxc-cli.exe";

    private const string Passphrase = "correct horse battery stäple";

    private static readonly KdbxKdf Quick = new(8 * 1024, 2);

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "quickshell-kdbx-read-" + Guid.NewGuid().ToString("N"));

    public KdbxReaderTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    /// <summary>A vault this client wrote reads back group for group and field for field.</summary>
    [Fact]
    public void AVaultThisClientWroteReadsBackAsWritten()
    {
        string vault = Write("round.kdbx", Quick);

        using Secret passphrase = Secret.From(Passphrase.AsSpan());
        using KdbxVault read = KdbxReader.Read(vault, passphrase);

        Assert.Equal("Sessions", read.Root.Name);
        KdbxGroup work = Assert.Single(read.Root.Groups);
        Assert.Equal("Work", work.Name);

        KdbxEntry web = Assert.Single(work.Entries);
        Assert.Equal("web01", web.Title);
        Assert.Equal("deploy", web.UserName);
        Assert.Equal("ssh://deploy@web01.example.invalid:2222", web.Url);
        Assert.Equal("s3cret-ção", Encoding.UTF8.GetString(web.Password!.Bytes));

        KdbxEntry nas = Assert.Single(read.Root.Entries);
        Assert.Equal(0, nas.Password!.Length);
    }

    /// <summary>A vault with a key file reads with it, and is refused without it.</summary>
    [Fact]
    public void AKeyFileIsNeededWhereOneWasSet()
    {
        string keyFile = Path.Combine(_folder, "vault.keyx");

        KdbxKeyFile.Create(keyFile);

        string vault = Write("keyed.kdbx", Quick, keyFile);

        using Secret passphrase = Secret.From(Passphrase.AsSpan());

        using (KdbxVault read = KdbxReader.Read(vault, passphrase, keyFile))
        {
            Assert.Equal(2, read.Entries.Count());
        }

        KdbxException refused = Assert.Throws<KdbxException>(() => KdbxReader.Read(vault, passphrase));

        Assert.Contains("passphrase or the key file is wrong", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>The wrong passphrase is refused, naming the file.</summary>
    [Fact]
    public void AWrongPassphraseIsRefused()
    {
        string vault = Write("wrong.kdbx", Quick);

        using Secret wrong = Secret.From("not it".AsSpan());

        KdbxException refused = Assert.Throws<KdbxException>(() => KdbxReader.Read(vault, wrong));

        Assert.StartsWith($"{vault} did not open", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>One byte changed in the encrypted payload is a file refused whole, naming it.</summary>
    [Fact]
    public void AnAlteredVaultIsRefusedWhole()
    {
        string vault = Write("altered.kdbx", Quick);
        byte[] bytes = File.ReadAllBytes(vault);

        bytes[^60] ^= 0x01;
        File.WriteAllBytes(vault, bytes);

        using Secret passphrase = Secret.From(Passphrase.AsSpan());

        KdbxException refused = Assert.Throws<KdbxException>(() => KdbxReader.Read(vault, passphrase));

        Assert.StartsWith($"{vault} was changed or damaged after it was written", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Argon2d over two lanes, KeePassXC's own shape: written here, opened by KeePassXC, which proves
    /// the managed Argon2d derives what KeePassXC derives, and read back here.
    /// </summary>
    [Fact]
    public void AnArgonDVaultOverSeveralLanesIsOpenedByKeePassXcAndReadBack()
    {
        string cli = Cli();
        string vault = Write("argon2d.kdbx", new KdbxKdf(16 * 1024, 2, Lanes: 2, Argon2d: true));

        (int code, string said) = Run(cli, ["show", "-q", "-s", "-a", "Password", vault, "Work/web01"], Passphrase + "\n");

        Assert.True(code == 0, $"keepassxc-cli did not open the Argon2d vault (exit {code}): {said}");
        Assert.Equal("s3cret-ção", said.Trim());

        using Secret passphrase = Secret.From(Passphrase.AsSpan());
        using KdbxVault read = KdbxReader.Read(vault, passphrase);

        Assert.Equal("s3cret-ção", Encoding.UTF8.GetString(read.Root.Groups[0].Entries[0].Password!.Bytes));
    }

    /// <summary>
    /// A KDBX 4 vault encrypted with ChaCha20, and one derived with AES-KDF: each written here, opened by
    /// KeePassXC, which proves the writer agrees with it, and read back here, which proves the reader
    /// agrees with both (QS346).
    /// </summary>
    [Theory]
    [InlineData("chacha", false)]
    [InlineData("aes-kdf", true)]
    public void AChaChaOrAesKdfVaultIsOpenedByKeePassXcAndReadBack(string name, bool aesKdf)
    {
        string cli = Cli();
        string vault = Path.Combine(_folder, name + ".kdbx");

        using (Secret password = Secret.From("s3cret-ção".AsSpan()))
        using (Secret passphrase = Secret.From(Passphrase.AsSpan()))
        using (KdbxKey key = KdbxKey.Derive(passphrase, kdf: aesKdf ? new KdbxKdf(0, 0, AesRounds: 6000) : Quick))
        {
            KdbxWriter.Write(vault, new KdbxGroup
            {
                Name = "Sessions",
                Groups = [new KdbxGroup { Name = "Work", Entries = [new KdbxEntry { Title = "web01", UserName = "deploy", Password = password }] }],
            }, key, aesKdf ? KdbxCipher.Aes256 : KdbxCipher.ChaCha20);
        }

        (int code, string said) = Run(cli, ["show", "-q", "-s", "-a", "Password", vault, "Work/web01"], Passphrase + "\n");

        Assert.True(code == 0, $"keepassxc-cli did not open the {name} vault (exit {code}): {said}");
        Assert.Equal("s3cret-ção", said.Trim());

        using Secret again = Secret.From(Passphrase.AsSpan());
        using KdbxVault read = KdbxReader.Read(vault, again);

        Assert.Equal("s3cret-ção", Encoding.UTF8.GetString(read.Root.Groups[0].Entries[0].Password!.Bytes));
    }

    /// <summary>A vault keepassxc-cli made itself, KDBX 3.1 with AES-KDF and Salsa20, reads its entry.</summary>
    [Fact]
    public void AVaultKeePassXcMadeIsRead()
    {
        string cli = Cli();
        string vault = Path.Combine(_folder, "made.kdbx");

        (int created, string createdSaid) = Run(cli, ["db-create", "-q", "-p", vault], Passphrase + "\n" + Passphrase + "\n");
        Assert.True(created == 0, $"keepassxc-cli did not create a vault: {createdSaid}");

        (int added, string addedSaid) = Run(cli, ["add", "-q", "-u", "deploy", "--url", "ssh://deploy@web01.example.invalid:2222", "-p", vault, "web01"],
                                            Passphrase + "\nfrom-keepassxc\n");
        Assert.True(added == 0, $"keepassxc-cli did not add the entry: {addedSaid}");

        using Secret passphrase = Secret.From(Passphrase.AsSpan());
        using KdbxVault read = KdbxReader.Read(vault, passphrase);

        KdbxEntry web = Assert.Single(read.Entries, entry => entry.Title == "web01");
        Assert.Equal("deploy", web.UserName);
        Assert.Equal("ssh://deploy@web01.example.invalid:2222", web.Url);
        Assert.Equal("from-keepassxc", Encoding.UTF8.GetString(web.Password!.Bytes));
    }

    private string Write(string name, KdbxKdf kdf, string? keyFile = null)
    {
        string vault = Path.Combine(_folder, name);

        using Secret password = Secret.From("s3cret-ção".AsSpan());
        using Secret empty = Secret.From(ReadOnlySpan<byte>.Empty);
        using Secret passphrase = Secret.From(Passphrase.AsSpan());

        KdbxWriter.Write(vault, new KdbxGroup
        {
            Name = "Sessions",
            Entries = [new KdbxEntry { Title = "nas", Password = empty }],
            Groups =
            [
                new KdbxGroup
                {
                    Name = "Work",
                    Entries =
                    [
                        new KdbxEntry
                        {
                            Title = "web01",
                            UserName = "deploy",
                            Url = "ssh://deploy@web01.example.invalid:2222",
                            Password = password,
                        },
                    ],
                },
            ],
        }, passphrase, keyFile, kdf);

        return vault;
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

    private static (int Code, string Said) Run(string cli, IEnumerable<string> arguments, string input)
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

        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(start) ?? throw new InvalidOperationException($"{cli} did not start");

        process.StandardInput.Write(input);
        process.StandardInput.Close();

        Task<string> output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        Task<string> error = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);

        Assert.True(process.WaitForExit(TimeSpan.FromSeconds(60)), "keepassxc-cli did not finish within a minute");

        return (process.ExitCode, output.Result + error.Result);
    }
}
