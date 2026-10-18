using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Text;

using Quickshell.Transport;

using UglyToad.PdfPig;

using Xunit;

namespace Quickshell.App.Tests;

/// <summary>
/// QS315: a PDF of chosen sessions, read back as text — every field with the node that set it, the
/// jump chain hop by hop and the forwards, and no secret, from a store and a machine holding all three
/// kinds of one.
/// </summary>
public sealed class SessionDocumentTests : IDisposable
{
    private const string Password = "hunter2-dba-password";
    private const string Passphrase = "correct-horse-key-passphrase";
    private const string PrivateMarker = "PRIVATE-HALF-NEVER-PRINTED";

    private readonly string _here = Path.Combine(Path.GetTempPath(), "qs-document-" + Guid.NewGuid().ToString("N"));

    public SessionDocumentTests() => Directory.CreateDirectory(_here);

    public void Dispose() => Directory.Delete(_here, recursive: true);

    /// <summary>
    /// A folder's defaults, a two-hop jump chain and three forwards: each value is in the document with
    /// where it came from, and the saved password, the key's passphrase and its private half are not.
    /// </summary>
    [Fact]
    public void EveryFieldIsDocumentedWithItsOriginAndNoSecretIs()
    {
        (byte[] publicBlob, string privateBody) = Key(Path.Combine(_here, "id_ed25519"));
        string pdf = Path.Combine(_here, "sessions.pdf");
        List<string> said = [];

        SecretStore secrets = SecretStore.In(Path.Combine(_here, "secrets"));

        using (Secret password = Secret.From(Password.AsSpan()))
        using (Secret passphrase = Secret.From(Passphrase.AsSpan()))
        {
            secrets.SaveNamed("dba", password);
            secrets.Save(SshEndpoint.For("db1.example.invalid", "deploy", 2200), passphrase);
        }

        string? written = Sta.Run(() =>
        {
            MainWindow window = Window(said, secrets, pdf);

            window.ToggleFolder("Work");
            window.SelectSessionRows(["Work", "nas"]);

            return window.DocumentSessions();
        });

        Assert.Equal(pdf, written);
        Assert.Contains(said, sentence => sentence.StartsWith($"3 sessions are documented in {pdf}", StringComparison.Ordinal));

        string read = Text(pdf);
        string text = Squeezed(read);
        string fingerprint = Convert.ToBase64String(SHA256.HashData(publicBlob)).TrimEnd('=');

        string[] expected =
        [
            // The cover.
            "3 sessions, from 2 folders:",
            "No password, passphrase or private key is in this document.",

            // The session with everything, inheriting its user, credential and reconnect from Work.
            "db-primary", "In Work.",
            "Host db1.example.invalid this session",
            "Port 2200 this session",
            "User deploy inherited from the folder Work",
            "Signs in with the key file below",
            $"Key fingerprint ssh-ed25519 SHA256:{fingerprint}",
            "Saved credential dba inherited from the folder Work",
            "Reconnect after a drop yes inherited from the folder Work",
            "Tags prod",

            // Hop by hop, each with whom it signs in as, then the session itself.
            "Set by this session: ops@bastion1.example.invalid:2222,bastion2.example.invalid",
            "1 bastion1.example.invalid:2222 ops",
            "2 bastion2.example.invalid:22 deploy",
            "then db1.example.invalid:2200 deploy",

            // Three forwards.
            "Local 127.0.0.1:5432 localhost:5432 from the server",
            "Remote port 8080 on the server localhost:3000 from this machine",
            "Dynamic 127.0.0.1:1080 any host, as a SOCKS proxy through the server",

            "Typed after login", "cd /srv", "sudo -l",

            // A sibling that signs in with the password Work names, and says it is saved.
            "web01",
            "Signs in with a password, saved under the credential \"dba\"",
            "Port 22, the default nothing set, so the default",

            // And one at the top level whose credential has nothing saved under it.
            "nas", "At the top level.",
            "none is saved under \"nas-admin\" on this machine",
        ];

        // Every miss at once, and the document's whole text beside them: a report truncates a long
        // string, and the words around the miss are what says whether it is wrong or only laid out.
        string[] words = read.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string[] missing = [.. expected.Where(each => !InOrder(words, each))];

        Assert.True(missing.Length == 0,
            $"not in the document: {string.Join(" | ", missing)}{Environment.NewLine}the document reads: {read}");

        // The falsification: no secret, as text or anywhere in the file's bytes.
        string bytes = Encoding.Latin1.GetString(File.ReadAllBytes(pdf));

        foreach (string secret in (string[])[Password, Passphrase, PrivateMarker, privateBody[..40]])
        {
            Assert.DoesNotContain(Squeezed(secret), text, StringComparison.Ordinal);
            Assert.DoesNotContain(secret, bytes, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// QS365: the palette's entry documents every saved session even where one row is selected, as a
    /// connection leaves the row of the session it opened.
    /// </summary>
    [Fact]
    public void ThePaletteDocumentsEverySessionWhateverOneRowIsSelected()
    {
        string pdf = Path.Combine(_here, "all.pdf");
        List<string> said = [];

        string? written = Sta.Run(() =>
        {
            MainWindow window = Window(said, SecretStore.In(Path.Combine(_here, "secrets")), pdf);

            window.SelectSessionRows(["nas"]);

            return window.DocumentAllSessions();
        });

        Assert.Equal(pdf, written);
        Assert.Contains(said, sentence => sentence.StartsWith($"4 sessions are documented in {pdf}", StringComparison.Ordinal));

        string[] words = Text(pdf).Split(' ', StringSplitOptions.RemoveEmptyEntries);

        Assert.All(["db-primary", "web01", "pi", "nas"], session => Assert.Contains(session, words));
    }

    /// <summary>A key whose public half is beside it is fingerprinted from that, as ssh-keygen -l prints it.</summary>
    [Fact]
    public void AKeyBesideItsPubIsFingerprintedFromThePub()
    {
        string key = Path.Combine(_here, "id_rsa");
        byte[] blob = [0, 0, 0, 7, .. "ssh-rsa"u8, 0, 0, 0, 3, 1, 0, 1];

        File.WriteAllText(key, "-----BEGIN RSA PRIVATE KEY-----\nProc-Type: 4,ENCRYPTED\n-----END RSA PRIVATE KEY-----\n");
        File.WriteAllText(key + ".pub", $"ssh-rsa {Convert.ToBase64String(blob)} someone@example\n");

        (string? fingerprint, _) = KeyFingerprint.Of(key);

        Assert.Equal($"ssh-rsa SHA256:{Convert.ToBase64String(SHA256.HashData(blob)).TrimEnd('=')}", fingerprint);
    }

    /// <summary>A key in a format whose public half is sealed, with no .pub beside it, says so rather than guessing.</summary>
    [Fact]
    public void AnEncryptedPemKeyWithNoPubSaysWhyItHasNoFingerprint()
    {
        string key = Path.Combine(_here, "legacy");

        File.WriteAllText(key, "-----BEGIN RSA PRIVATE KEY-----\nProc-Type: 4,ENCRYPTED\n-----END RSA PRIVATE KEY-----\n");

        (string? fingerprint, string why) = KeyFingerprint.Of(key);

        Assert.Null(fingerprint);
        Assert.Contains("no .pub is beside it", why, StringComparison.Ordinal);
    }

    private MainWindow Window(List<string> said, SecretStore secrets, string pdf)
    {
        string store = Path.Combine(_here, "sessions.json");
        string key = Path.Combine(_here, "id_ed25519").Replace('\\', '/');

        File.WriteAllText(store, $$"""
            { "name": "", "children": [
                { "name": "Work", "settings": { "user": "deploy", "credential": "dba", "reconnect": true }, "children": [
                    {
                      "name": "db-primary", "host": "db1.example.invalid",
                      "settings": { "port": 2200, "key": "{{key}}", "jumpHost": "ops@bastion1.example.invalid:2222,bastion2.example.invalid" },
                      "tags": [ "prod" ],
                      "postLogin": "cd /srv\nsudo -l",
                      "forwards": [
                        { "kind": "Local", "listenPort": 5432, "targetHost": "localhost", "targetPort": 5432 },
                        { "kind": "Remote", "listenPort": 8080, "targetHost": "localhost", "targetPort": 3000 },
                        { "kind": "Dynamic", "listenPort": 1080 }
                      ]
                    },
                    { "name": "web01", "host": "web01.example.invalid" }
                ] },
                { "name": "Home", "children": [ { "name": "pi", "host": "pi.example.invalid" } ] },
                { "name": "nas", "host": "nas.example.invalid", "settings": { "user": "admin", "credential": "nas-admin" } }
            ] }
            """);

        MainWindow window = new()
        {
            SessionsFile = store,
            Tells = said.Add,
            OpensSecrets = () => secrets,
            AsksDocumentFile = (_, _) => pdf,
        };

        window.RefreshSessions();

        return window;
    }

    /// <summary>
    /// An unencrypted OpenSSH key file with no .pub beside it, whose public half only its header
    /// carries, and whose private section holds a marker the document must never print.
    /// </summary>
    private static (byte[] PublicBlob, string PrivateBody) Key(string path)
    {
        byte[] point = RandomNumberGenerator.GetBytes(32);
        byte[] blob = [.. Str("ssh-ed25519"u8), .. Str(point)];

        byte[] body =
        [
            .. "openssh-key-v1\0"u8,
            .. Str("none"u8), .. Str("none"u8), .. Str([]),
            0, 0, 0, 1,
            .. Str(blob),
            .. Str([.. "\x01\x02\x03\x04\x01\x02\x03\x04"u8, .. Str(blob), .. Str(Encoding.ASCII.GetBytes(PrivateMarker))]),
        ];

        string base64 = Convert.ToBase64String(body);
        string wrapped = string.Join('\n', base64.Chunk(70).Select(line => new string(line)));

        File.WriteAllText(path, $"-----BEGIN OPENSSH PRIVATE KEY-----\n{wrapped}\n-----END OPENSSH PRIVATE KEY-----\n");

        return (blob, base64[^60..]);

        static byte[] Str(ReadOnlySpan<byte> value)
        {
            byte[] framed = new byte[4 + value.Length];

            BinaryPrimitives.WriteUInt32BigEndian(framed, (uint)value.Length);
            value.CopyTo(framed.AsSpan(4));

            return framed;
        }
    }

    private static string Text(string pdf)
    {
        using PdfDocument document = PdfDocument.Open(pdf);

        return string.Join(' ', document.GetPages().SelectMany(page => page.GetWords()).Select(word => word.Text));
    }

    /// <summary>
    /// Whether the words of <paramref name="expected"/> are in the document in order, with at most a
    /// few others between two of them. A value that wraps in its cell comes back with the next cell's
    /// words between its lines, because the text is read in the order it was drawn.
    /// </summary>
    private static bool InOrder(string[] words, string expected)
    {
        const int between = 6;
        string[] wanted = expected.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        for (int start = 0; start < words.Length; start++)
        {
            if (words[start] != wanted[0])
            {
                continue;
            }

            int at = start;
            int matched = 1;

            while (matched < wanted.Length
                   && Array.IndexOf(words, wanted[matched], at + 1, Math.Min(between + 1, words.Length - at - 1)) is var next and >= 0)
            {
                at = next;
                matched++;
            }

            if (matched == wanted.Length)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Without whitespace, since a table's cells and a wrapped line come back spaced as the layout put them.</summary>
    private static string Squeezed(string text) => string.Concat(text.Where(each => !char.IsWhiteSpace(each)));
}
