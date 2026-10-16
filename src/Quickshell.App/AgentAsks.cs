using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Quickshell.App;

/// <summary>One call in a batch the person is asked about, as the dialog shows it.</summary>
/// <param name="Session">The session's path.</param>
/// <param name="Address">Where it connects, <c>user@host:port</c>.</param>
/// <param name="Tags">Its tags, prod among them where it is one.</param>
/// <param name="Tool">The gateway tool: exec, job_start or edit_file.</param>
/// <param name="Words">The command as the words it runs, for exec and job_start.</param>
/// <param name="Path">The file, for edit_file.</param>
/// <param name="Diff">The change, for edit_file.</param>
/// <param name="Rule">Why it asks: the policy's rule.</param>
public sealed record AskedCall(string Session, string Address, IReadOnlyList<string> Tags, string Tool,
                               IReadOnlyList<string> Words, string? Path, string? Diff, string Rule);

/// <summary>A batch the person is asked to approve, bound to its hash (QS324, QS337).</summary>
/// <param name="Id">The request's own name, which its files carry.</param>
/// <param name="Run">The gateway run asking.</param>
/// <param name="Calls">What would run, in order.</param>
public sealed record ApprovalRequest(string Id, string Run, IReadOnlyList<AskedCall> Calls)
{
    /// <summary>
    /// The SHA-256 of the batch in a canonical form: each call's session, tool, then each word
    /// length-prefixed, then an edit's path and diff. A batch that changes by one byte after it was shown
    /// is another hash, so an approval can never be spent on something else.
    /// </summary>
    public string Hash
    {
        get
        {
            StringBuilder canonical = new();

            foreach (AskedCall call in Calls)
            {
                Framed(canonical, call.Session);
                Framed(canonical, call.Tool);

                foreach (string word in call.Words)
                {
                    Framed(canonical, word);
                }

                Framed(canonical, call.Path ?? string.Empty);
                Framed(canonical, call.Diff ?? string.Empty);
                canonical.Append('\n');
            }

            return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));

            static void Framed(StringBuilder into, string value) =>
                into.Append(value.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(value).Append(';');
        }
    }

    /// <summary>The hash's first twelve characters, which the agent's request and the dialog both show.</summary>
    public string ShortHash => Hash[..12];
}

/// <summary>What the person answered.</summary>
public enum Decision
{
    /// <summary>Run this batch, exactly.</summary>
    Approve,

    /// <summary>Run it, and let the same commands run on the same sessions without asking until a time.</summary>
    Grant,

    /// <summary>Do not run it.</summary>
    Refuse,

    /// <summary>Nobody answered within the wait.</summary>
    TimedOut,

    /// <summary>No quickshell window was open to ask in.</summary>
    Nobody,
}

/// <summary>The person's answer to a batch.</summary>
/// <param name="Decision">What they decided.</param>
/// <param name="Hash">The hash they answered, which must be the batch's.</param>
/// <param name="Note">What they said, which the agent receives with a refusal.</param>
/// <param name="GrantMinutes">How long a grant runs, for a grant.</param>
public sealed record ApprovalAnswer(Decision Decision, string Hash, string Note = "", int GrantMinutes = 0);

/// <summary>
/// The approval's two ends, across the two processes (QS337): the gateway writes a request into
/// <c>agent-asks</c> in quickshell's folder and waits for the answer file beside it; the window, while it
/// is open, holds a named object the gateway can see, and answers what it finds.
///
/// <para><b>A file and a name, no port</b>, as the stop (QS334) and the feed (QS335) are, so nothing new
/// listens. With no window open the gateway knows at once and refuses, saying no one was there to answer,
/// which is the safe direction.</para>
/// </summary>
public static class AgentAsks
{
    /// <summary>The name the window holds while it can answer.</summary>
    public const string WindowName = "Local\\quickshell-agent-asks";

    /// <summary>How long the gateway waits for an answer before it says nobody gave one.</summary>
    public static readonly TimeSpan Patience = TimeSpan.FromMinutes(10);

    private static readonly JsonSerializerOptions Written = new() { WriteIndented = true };

    /// <summary>Whether a quickshell window is open to ask in.</summary>
    public static bool WindowOpen() => Mutex.TryOpenExisting(WindowName, out Mutex? held) && Dispose(held);

    /// <summary>
    /// Asks: writes the request, waits for its answer, and removes both. An answer to another hash is
    /// treated as a refusal, since it approved something else.
    /// </summary>
    public static async Task<ApprovalAnswer> AskAsync(string folder, ApprovalRequest request, TimeSpan patience, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        Directory.CreateDirectory(folder);

        string asked = Path.Combine(folder, request.Id + ".ask.json");
        string answered = Path.Combine(folder, request.Id + ".answer.json");

        await File.WriteAllTextAsync(asked + ".tmp", Request(request).ToJsonString(Written), cancellationToken).ConfigureAwait(false);
        File.Move(asked + ".tmp", asked, overwrite: true);

        using CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        limit.CancelAfter(patience);

        try
        {
            while (true)
            {
                if (File.Exists(answered) && Answer(await File.ReadAllTextAsync(answered, limit.Token).ConfigureAwait(false)) is { } answer)
                {
                    return answer.Hash == request.Hash ? answer : answer with { Decision = Decision.Refuse, Note = "the answer was to another batch" };
                }

                await Task.Delay(250, limit.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new ApprovalAnswer(Decision.TimedOut, request.Hash);
        }
        finally
        {
            Remove(asked);
            Remove(answered);
        }
    }

    /// <summary>A request as its file holds it.</summary>
    public static JsonObject Request(ApprovalRequest request) => new()
    {
        ["id"] = request.Id,
        ["run"] = request.Run,
        ["hash"] = request.Hash,
        ["calls"] = new JsonArray([.. request.Calls.Select(call => (JsonNode?)new JsonObject
        {
            ["session"] = call.Session,
            ["address"] = call.Address,
            ["tags"] = new JsonArray([.. call.Tags.Select(tag => (JsonNode?)JsonValue.Create(tag))]),
            ["tool"] = call.Tool,
            ["words"] = new JsonArray([.. call.Words.Select(word => (JsonNode?)JsonValue.Create(word))]),
            ["path"] = call.Path,
            ["diff"] = call.Diff,
            ["rule"] = call.Rule,
        })]),
    };

    /// <summary>A request read back from its file, or null where it does not read.</summary>
    public static ApprovalRequest? ReadRequest(string json)
    {
        try
        {
            JsonNode root = JsonNode.Parse(json)!;

            return new ApprovalRequest(
                root["id"]!.GetValue<string>(),
                root["run"]?.GetValue<string>() ?? string.Empty,
                [.. root["calls"]!.AsArray().Select(call => new AskedCall(
                    call!["session"]!.GetValue<string>(),
                    call["address"]?.GetValue<string>() ?? string.Empty,
                    [.. (call["tags"] as JsonArray ?? []).Select(tag => tag!.GetValue<string>())],
                    call["tool"]!.GetValue<string>(),
                    [.. (call["words"] as JsonArray ?? []).Select(word => word!.GetValue<string>())],
                    call["path"]?.GetValue<string>(),
                    call["diff"]?.GetValue<string>(),
                    call["rule"]?.GetValue<string>() ?? string.Empty))]);
        }
        catch (Exception unreadable) when (unreadable is JsonException or InvalidOperationException or NullReferenceException)
        {
            return null;
        }
    }

    /// <summary>Writes the person's answer beside its request.</summary>
    public static void WriteAnswer(string folder, string id, ApprovalAnswer answer)
    {
        ArgumentNullException.ThrowIfNull(answer);

        string file = Path.Combine(folder, id + ".answer.json");

        File.WriteAllText(file + ".tmp", new JsonObject
        {
            ["decision"] = answer.Decision.ToString().ToLowerInvariant(),
            ["hash"] = answer.Hash,
            ["note"] = answer.Note,
            ["grantMinutes"] = answer.GrantMinutes,
        }.ToJsonString(Written));
        File.Move(file + ".tmp", file, overwrite: true);
    }

    private static ApprovalAnswer? Answer(string json)
    {
        try
        {
            JsonNode root = JsonNode.Parse(json)!;

            return new ApprovalAnswer(
                Enum.Parse<Decision>(root["decision"]!.GetValue<string>(), ignoreCase: true),
                root["hash"]!.GetValue<string>(),
                root["note"]?.GetValue<string>() ?? string.Empty,
                root["grantMinutes"]?.GetValue<int>() ?? 0);
        }
        catch (Exception unreadable) when (unreadable is JsonException or ArgumentException or InvalidOperationException or NullReferenceException)
        {
            // Being written as it was read; the next look reads it whole.
            return null;
        }
    }

    private static void Remove(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (IOException)
        {
            // The window may hold it a moment; it is the request's own file and harms nothing left.
        }
    }

    private static bool Dispose(Mutex? held)
    {
        held?.Dispose();

        return held is not null;
    }
}
