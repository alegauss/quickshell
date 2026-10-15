using System.Diagnostics;
using System.Text;
using System.Xml;
using Quickshell.Transport;
using Xunit;

namespace Quickshell.Transport.Tests;

/// <summary>
/// The KDBX 4 writer (QS305), held to the reader that matters: KeePassXC's own command line, the one
/// <c>tools/provision-guest-keepassxc.ps1</c> places in the guest. Its export is the database as
/// KeePassXC decrypted it, so every field written here is read back from there and compared.
///
/// <para>Where keepassxc-cli is neither placed nor on the PATH those tests skip, naming the script;
/// what no plain text reaching the disk means is checked everywhere.</para>
/// </summary>
public sealed class KdbxWriterTests : IDisposable
{
    private const string Provisioned = @"C:\ProgramData\quickshell-cases\keepassxc\keepassxc-cli.exe";

    /// <summary>A passphrase with a character outside ASCII, so its encoding is part of what is checked.</summary>
    private const string Passphrase = "correct horse battery stäple";

    /// <summary>Cheap enough for a test to derive several times; the default is what QS306 writes.</summary>
    private static readonly KdbxKdf Quick = new(8 * 1024, 2);

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "quickshell-kdbx-" + Guid.NewGuid().ToString("N"));

    public KdbxWriterTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    /// <summary>Groups, entries, a protected password and an attachment come back from KeePassXC as written.</summary>
    [Fact]
    public void KeePassXcOpensTheVaultAndReadsEveryFieldAsWritten()
    {
        string cli = Cli();
        string vault = Path.Combine(_folder, "sessions.kdbx");

        using Secret first = Secret.From("s3cret-ção".AsSpan());
        using Secret second = Secret.From("another one".AsSpan());
        using Secret passphrase = Secret.From(Passphrase.AsSpan());

        KdbxWriter.Write(vault, Sample(first, second), passphrase, kdf: KdbxKdf.Default);

        XmlDocument export = Export(cli, vault, Passphrase, keyFile: null);

        Assert.Equal("Quickshell sessions", export.SelectSingleNode("/KeePassFile/Root/Group/Name")?.InnerText);
        Assert.Equal("Production", export.SelectSingleNode("/KeePassFile/Root/Group/Group/Name")?.InnerText);

        XmlNode web = Entry(export, "deploy@web01");
        Assert.Equal("deploy", Value(web, "UserName"));
        Assert.Equal("ssh://deploy@web01.example:2222", Value(web, "URL"));
        Assert.Equal("Jump through bastion.", Value(web, "Notes"));
        Assert.Equal("s3cret-ção", Value(web, "Password"));

        XmlNode db = Entry(export, "postgres@db01");
        Assert.Equal("another one", Value(db, "Password"));
        Assert.NotNull(db.SelectSingleNode("Binary[Key='id_ed25519.pub']"));

        // To a file and not to stdout, which Windows' text mode would give back with \r\n.
        string exported = Path.Combine(_folder, "exported.pub");
        (int code, string said) = Run(cli, ["attachment-export", "-q", vault, "Production/postgres@db01", "id_ed25519.pub", exported], Passphrase);

        Assert.True(code == 0, $"keepassxc-cli did not export the attachment (exit {code}): {said}");
        Assert.Equal("id_ed25519.pub contents\n"u8.ToArray(), File.ReadAllBytes(exported));
    }

    /// <summary>A vault written with a key file opens with the passphrase and that file, and not with the passphrase alone.</summary>
    [Fact]
    public void AVaultWithAKeyFileOpensWithItAndNotWithoutIt()
    {
        string cli = Cli();
        string vault = Path.Combine(_folder, "keyed.kdbx");
        string keyFile = Path.Combine(_folder, "vault.keyx");

        KdbxKeyFile.Create(keyFile);

        using Secret password = Secret.From("behind two factors".AsSpan());
        using Secret passphrase = Secret.From(Passphrase.AsSpan());

        KdbxWriter.Write(vault, new KdbxGroup
        {
            Name = "Keyed",
            Entries = [new KdbxEntry { Title = "one", Password = password }],
        }, passphrase, keyFile, Quick);

        Assert.Equal("behind two factors", Value(Entry(Export(cli, vault, Passphrase, keyFile), "one"), "Password"));

        (int code, string said) = Run(cli, ["export", "-q", "-f", "xml", vault], Passphrase);

        Assert.True(code != 0, $"KeePassXC opened a vault that needs a key file without it: {said}");
    }

    /// <summary>A wrong passphrase is refused, which is the header's HMAC and the derivation doing their work.</summary>
    [Fact]
    public void AWrongPassphraseDoesNotOpenTheVault()
    {
        string cli = Cli();
        string vault = Path.Combine(_folder, "wrong.kdbx");

        using Secret passphrase = Secret.From(Passphrase.AsSpan());

        KdbxWriter.Write(vault, new KdbxGroup { Name = "Wrong" }, passphrase, kdf: Quick);

        (int code, string said) = Run(cli, ["export", "-q", "-f", "xml", vault], "not the passphrase");

        Assert.True(code != 0, $"KeePassXC opened the vault with the wrong passphrase: {said}");
    }

    /// <summary>Nothing written in clear reaches the disk, and the temporary name is gone once the vault is there.</summary>
    [Fact]
    public void NoPlainTextReachesTheDiskAndNoTemporaryFileIsLeft()
    {
        string vault = Path.Combine(_folder, "clear.kdbx");

        using Secret first = Secret.From("s3cret-ção".AsSpan());
        using Secret second = Secret.From("another one".AsSpan());
        using Secret passphrase = Secret.From(Passphrase.AsSpan());

        KdbxWriter.Write(vault, Sample(first, second), passphrase, kdf: Quick);

        byte[] written = File.ReadAllBytes(vault);

        foreach (string clear in new[] { "s3cret", "another one", "deploy", "web01", "Quickshell sessions", "KeePassFile", "id_ed25519" })
        {
            Assert.True(written.AsSpan().IndexOf(Encoding.UTF8.GetBytes(clear)) < 0, $"\"{clear}\" is in the vault in clear");
        }

        Assert.Equal([0x03, 0xD9, 0xA2, 0x9A, 0x67, 0xFB, 0x4B, 0xB5, 0x00, 0x00, 0x04, 0x00], written[..12]);
        Assert.Equal(["clear.kdbx"], Directory.GetFiles(_folder).Select(Path.GetFileName));
    }

    private static KdbxGroup Sample(Secret first, Secret second) => new()
    {
        Name = "Quickshell sessions",
        Groups =
        [
            new KdbxGroup
            {
                Name = "Production",
                Entries =
                [
                    new KdbxEntry
                    {
                        Title = "deploy@web01",
                        UserName = "deploy",
                        Url = "ssh://deploy@web01.example:2222",
                        Notes = "Jump through bastion.",
                        Password = first,
                    },
                    new KdbxEntry
                    {
                        Title = "postgres@db01",
                        UserName = "postgres",
                        Password = second,
                        Attachments = [new KdbxAttachment("id_ed25519.pub", Encoding.UTF8.GetBytes("id_ed25519.pub contents\n"))],
                    },
                ],
            },
        ],
    };

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
    private static XmlDocument Export(string cli, string vault, string passphrase, string? keyFile)
    {
        List<string> arguments = ["export", "-q", "-f", "xml"];

        if (keyFile is not null)
        {
            arguments.AddRange(["-k", keyFile]);
        }

        arguments.Add(vault);

        (int code, string said) = Run(cli, arguments, passphrase);

        Assert.True(code == 0, $"keepassxc-cli did not open the vault (exit {code}): {said}");

        XmlDocument document = new() { XmlResolver = null };
        document.LoadXml(said[said.IndexOf('<', StringComparison.Ordinal)..]);

        return document;
    }

    private static (int Code, string Said) Run(string cli, IEnumerable<string> arguments, string passphrase)
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

        process.StandardInput.WriteLine(passphrase);
        process.StandardInput.Close();

        Task<string> output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        Task<string> error = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);

        Assert.True(process.WaitForExit(TimeSpan.FromSeconds(60)), "keepassxc-cli did not finish within a minute");

        return (process.ExitCode, output.Result + error.Result);
    }

    private static XmlNode Entry(XmlDocument export, string title) =>
        export.SelectNodes("//Entry")!.Cast<XmlNode>().SingleOrDefault(entry => Value(entry, "Title") == title)
        ?? throw new Xunit.Sdk.XunitException($"KeePassXC lists no entry titled {title}");

    private static string? Value(XmlNode entry, string key) =>
        entry.SelectSingleNode($"String[Key='{key}']/Value")?.InnerText;
}
