using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Quickshell.App;

/// <summary>
/// Hides the shapes secrets take in text on its way out to an agent (QS330): an <c>.env</c> read while
/// debugging otherwise lives on in a transcript, in the provider's request, and in any log of either.
///
/// <para><b>A guard against accidental spread, not a boundary.</b> A command can encode a secret past
/// any pattern, base64 or a byte at a time; the threat model (QS320) says so. What this stops is the
/// ordinary case, a value printed as it is stored, and it says how many it hid so the agent can ask the
/// person rather than guess.</para>
///
/// <para><b>A key block cut in half is still a key.</b> An answer fitted to its budget (QS329) can keep
/// a block's first line and lose its last, so a <c>BEGIN … PRIVATE KEY</c> with no end is hidden to the
/// end of the text, and an end with no beginning from the start of it.</para>
/// </summary>
public static partial class SecretRedactor
{
    [GeneratedRegex(@"-----BEGIN [A-Z0-9 ]*PRIVATE KEY-----.*?(-----END [A-Z0-9 ]*PRIVATE KEY-----|\z)", RegexOptions.Singleline)]
    private static partial Regex KeyBlock { get; }

    [GeneratedRegex(@"\A.*?-----END [A-Z0-9 ]*PRIVATE KEY-----", RegexOptions.Singleline)]
    private static partial Regex KeyTail { get; }

    [GeneratedRegex(@"(?i)\b(Authorization:\s*)?Bearer\s+[A-Za-z0-9\-._~+/]{8,}=*")]
    private static partial Regex Bearer { get; }

    [GeneratedRegex(@"\b([a-z][a-z0-9+.\-]*://[^\s:/@]+:)([^\s@/]+)(@)", RegexOptions.IgnoreCase)]
    private static partial Regex UrlPassword { get; }

    [GeneratedRegex(@"\b(AKIA|ASIA)[0-9A-Z]{16}\b")]
    private static partial Regex Aws { get; }

    [GeneratedRegex(@"\b(gh[pousr]_[A-Za-z0-9]{36,}|github_pat_[A-Za-z0-9_]{22,})\b")]
    private static partial Regex GitHub { get; }

    [GeneratedRegex(@"\bxox[abposr]-[A-Za-z0-9-]{10,}\b")]
    private static partial Regex Slack { get; }

    [GeneratedRegex(@"\beyJ[A-Za-z0-9_-]{8,}\.eyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\b")]
    private static partial Regex Jwt { get; }

    /// <summary>A name that says its value is secret, then <c>=</c> or <c>:</c>, then the value to the end of the token.</summary>
    [GeneratedRegex(@"(?i)\b([A-Za-z0-9_.\-]*(?:password|passwd|pwd|secret|token|api[_-]?key|access[_-]?key|private[_-]?key|credential)[A-Za-z0-9_.\-]*""?\s*[:=]\s*)(""[^""\n]*""|'[^'\n]*'|[^\s""',;]+)")]
    private static partial Regex Assignment { get; }

    /// <summary>The text with every secret shape replaced, and how many were.</summary>
    public static (string Text, int Count) Redact(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        int count = 0;

        string Hidden(string kind)
        {
            count++;

            return $"«redacted:{kind}»";
        }

        // Key blocks first, so nothing inside one is matched again as something smaller.
        string done = KeyBlock.Replace(text, _ => Hidden("private_key"));
        done = KeyTail.Replace(done, _ => Hidden("private_key"));
        done = Bearer.Replace(done, match => match.Groups[1].Value + Hidden("bearer"));
        done = UrlPassword.Replace(done, match => match.Groups[1].Value + Hidden("url_password") + match.Groups[3].Value);
        done = Aws.Replace(done, _ => Hidden("aws_key"));
        done = GitHub.Replace(done, _ => Hidden("github_token"));
        done = Slack.Replace(done, _ => Hidden("slack_token"));
        done = Jwt.Replace(done, _ => Hidden("jwt"));
        done = Assignment.Replace(done, match =>
            match.Groups[2].Value.StartsWith('«') ? match.Value : match.Groups[1].Value + Hidden("assignment"));

        return (done, count);
    }

    /// <summary>Every string under <paramref name="node"/> redacted in place; how many secrets were hidden.</summary>
    public static int RedactAll(JsonNode? node)
    {
        int count = 0;

        switch (node)
        {
            case JsonObject members:
                foreach (string name in members.Select(member => member.Key).ToList())
                {
                    if (members[name] is JsonValue value && value.TryGetValue(out string? text))
                    {
                        (string done, int hidden) = Redact(text);

                        if (hidden > 0)
                        {
                            members[name] = done;
                            count += hidden;
                        }
                    }
                    else
                    {
                        count += RedactAll(members[name]);
                    }
                }

                break;

            case JsonArray items:
                for (int at = 0; at < items.Count; at++)
                {
                    if (items[at] is JsonValue value && value.TryGetValue(out string? text))
                    {
                        (string done, int hidden) = Redact(text);

                        if (hidden > 0)
                        {
                            items[at] = done;
                            count += hidden;
                        }
                    }
                    else
                    {
                        count += RedactAll(items[at]);
                    }
                }

                break;
        }

        return count;
    }
}
