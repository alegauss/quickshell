using Xunit;

namespace Quickshell.App.Tests;

/// <summary>QS330: the shapes secrets take, hidden from what an agent is sent, and counted.</summary>
public sealed class SecretRedactorTests
{
    /// <summary>One of each kind, as an .env and a shell history hold them. None of it is a real secret.</summary>
    public const string Secrets = """
        DB_PASSWORD=hunter2-db-pass
        API_KEY: "sk-live-0123456789abcdef"
        export GITHUB_TOKEN=ghp_0123456789abcdefghijABCDEFGHIJ0123456789
        curl -H "Authorization: Bearer abcdefghij0123456789xyz" https://api.example.invalid
        DATABASE_URL=postgres://app:p4ss-w0rd@db.example.invalid:5432/app
        aws_access_key_id = AKIAIOSFODNN7EXAMPLE
        SLACK=xoxb-123456789012-abcdefghijkl
        session eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.dozjgNryP4J3jVmNHl0w5N_XgL0n3I9PlFUP0THsR8U
        -----BEGIN OPENSSH PRIVATE KEY-----
        b3BlbnNzaC1rZXktdjEAAAAABG5vbmUAAAAEbm9uZQAAAAAAAAABAAAAMwAAAAtzc2gtZW
        -----END OPENSSH PRIVATE KEY-----
        listen = 80
        """;

    /// <summary>The values, any of which reaching an agent is the failure.</summary>
    public static readonly string[] Values =
    [
        "hunter2-db-pass", "sk-live-0123456789abcdef", "ghp_0123456789abcdefghijABCDEFGHIJ0123456789",
        "abcdefghij0123456789xyz", "p4ss-w0rd", "AKIAIOSFODNN7EXAMPLE", "xoxb-123456789012-abcdefghijkl",
        "eyJhbGciOiJIUzI1NiJ9", "b3BlbnNzaC1rZXktdjEAAAAABG5vbmUAAAAEbm9uZQ",
    ];

    [Fact]
    public void EveryKindIsHiddenAndCountedAndTheRestIsKept()
    {
        (string text, int count) = SecretRedactor.Redact(Secrets);

        Assert.All(Values, value => Assert.DoesNotContain(value, text, StringComparison.Ordinal));
        Assert.Equal(9, count);

        // What is not a secret stays, and so do the names, so the agent knows what was there.
        Assert.Contains("listen = 80", text, StringComparison.Ordinal);
        Assert.Contains("DB_PASSWORD=«redacted:assignment»", text, StringComparison.Ordinal);
        Assert.Contains("postgres://app:«redacted:url_password»@db.example.invalid", text, StringComparison.Ordinal);
        Assert.Contains("«redacted:private_key»", text, StringComparison.Ordinal);
    }

    /// <summary>A key block whose end a budget cut off is hidden to the end of the text.</summary>
    [Fact]
    public void AKeyBlockCutInHalfIsStillHidden()
    {
        (string head, _) = SecretRedactor.Redact("before\n-----BEGIN RSA PRIVATE KEY-----\nMIIEowIBAAKCAQEA0Z\n[... 40 lines cut ...]\n");
        (string tail, _) = SecretRedactor.Redact("[... cut ...]\nq7Zt9wIDAQAB\n-----END RSA PRIVATE KEY-----\nafter\n");

        Assert.Equal("before\n«redacted:private_key»", head);
        Assert.Equal("«redacted:private_key»\nafter\n", tail);
    }

    [Fact]
    public void TextWithNoSecretIsUntouched() =>
        Assert.Equal(("total 12\ndrwxr-xr-x 2 root root 4096 app\n", 0), SecretRedactor.Redact("total 12\ndrwxr-xr-x 2 root root 4096 app\n"));
}
