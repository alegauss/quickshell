using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Quickshell.App;

/// <summary>
/// The agent gateway's own record of what an agent did (QS333): one JSON line per call, appended and
/// never edited, in quickshell's data folder and outside any workspace an agent is pointed at.
///
/// <para><b>Each line names the one before it</b> by its SHA-256, so a line removed or changed in the
/// middle breaks the chain where it was, and <see cref="Read"/> says so. It is evidence against a slip
/// and a casual edit, not a seal: a process running as the same user can rewrite the whole file and
/// every hash in it, which the threat model (QS320) leaves to the server-side log.</para>
///
/// <para><b>What a line holds:</b> when, which call in the run, the tool, the session and its tags,
/// the arguments as redacted as any answer is, the verdict and the rule that gave it, how it was
/// approved, whether it failed, its exit code where it ran a command, and the SHA-256 of the whole
/// answer. Until a policy exists (QS336) the verdict is <c>allowed</c> by <c>no policy yet</c>, and the
/// approval <c>none</c>, which is the truth about a spike.</para>
/// </summary>
public sealed class AuditLog
{
    /// <summary>Past this, a run's log goes on in a file with the next number, its chain unbroken.</summary>
    public const long RotateAt = 16L * 1024 * 1024;

    /// <summary>What a call does to a server, for the summary: read, changed, or ran a command.</summary>
    private static readonly Dictionary<string, string> Kinds = new(StringComparer.Ordinal)
    {
        ["hosts_list"] = "read", ["read_file"] = "read", ["job_status"] = "read", ["job_output"] = "read",
        ["output_page"] = "read", ["output_grep"] = "read",
        ["edit_file"] = "changed", ["job_start"] = "ran", ["job_cancel"] = "changed", ["exec"] = "ran",
    };

    private readonly string _folder;
    private readonly Lock _writing = new();
    private string _previous = "start";
    private int _sequence;
    private int _part;

    private AuditLog(string folder, string run)
    {
        _folder = folder;
        Run = run;
    }

    /// <summary>This gateway run's id, which names its files and which <c>--audit</c> takes.</summary>
    public string Run { get; }

    /// <summary>A log for a new gateway run, in <paramref name="folder"/>.</summary>
    public static AuditLog Start(string folder, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);

        Directory.CreateDirectory(folder);

        string run = now.ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture)
                     + "-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(3));

        return new AuditLog(folder, run);
    }

    /// <summary>Appends one call.</summary>
    public void Record(DateTimeOffset when, string tool, ResolvedSession? session, JsonObject arguments, JsonObject answer, bool failed,
                       PolicyVerdict? judged = null, string? approval = null)
    {
        ArgumentNullException.ThrowIfNull(tool);
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(answer);

        JsonObject redacted = (JsonObject)arguments.DeepClone();

        SecretRedactor.RedactAll(redacted);

        lock (_writing)
        {
            JsonObject line = new()
            {
                ["seq"] = ++_sequence,
                ["time"] = when.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                ["tool"] = tool,
                ["kind"] = Kinds.GetValueOrDefault(tool, "other"),
                ["host"] = session?.Path ?? (arguments["host"] is JsonValue asked && asked.TryGetValue(out string? named) ? named : null),
                ["tags"] = session is null ? null : new JsonArray([.. session.Tags.Select(tag => (JsonNode?)JsonValue.Create(tag))]),
                ["arguments"] = redacted,
                ["verdict"] = judged?.Verdict.ToString().ToLowerInvariant() ?? "allowed",
                ["rule"] = judged?.Rule ?? "not a call to a session",
                ["approval"] = approval ?? "none",
                ["failed"] = failed,
                ["exitCode"] = answer["exitCode"]?.DeepClone(),
                ["output"] = "sha256:" + Hash(answer.ToJsonString()),
                ["previous"] = _previous,
            };

            string text = line.ToJsonString();
            string file = File(_part);

            if (System.IO.File.Exists(file) && new FileInfo(file).Length > RotateAt)
            {
                file = File(++_part);
            }

            System.IO.File.AppendAllText(file, text + "\n", new UTF8Encoding(false));
            _previous = "sha256:" + Hash(text);
        }
    }

    /// <summary>
    /// A run's log read back as a summary a person can paste into a ticket, with every break in its
    /// chain named; null where there is no such run.
    /// </summary>
    public static AuditSummary? Read(string folder, string run)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        ArgumentException.ThrowIfNullOrWhiteSpace(run);

        // The run's first file, then its rotations in order: <run>.jsonl, <run>.1.jsonl, <run>.2.jsonl.
        List<string> files = Directory.Exists(folder)
            ? [.. Directory.GetFiles(folder, run + "*.jsonl").OrderBy(file => Path.GetFileName(file).Count(each => each == '.'))
                                                           .ThenBy(file => file.Length).ThenBy(file => file, StringComparer.Ordinal)]
            : [];

        if (files.Count == 0)
        {
            return null;
        }

        List<string> breaks = [];
        List<string> read = [];
        List<string> changed = [];
        List<string> ran = [];
        List<string> refused = [];
        string previous = "start";
        int number = 0;

        foreach (string line in files.SelectMany(System.IO.File.ReadLines))
        {
            number++;

            if (line.Length == 0)
            {
                continue;
            }

            JsonNode? entry;

            try
            {
                entry = JsonNode.Parse(line);
            }
            catch (JsonException)
            {
                breaks.Add($"line {number} is not a record");
                previous = "sha256:" + Hash(line);

                continue;
            }

            if (entry?["previous"]?.GetValue<string>() != previous)
            {
                breaks.Add($"line {number} does not follow the line before it: a line was removed, added or edited there");
            }

            previous = "sha256:" + Hash(line);

            string said = Describe(entry!);

            if (entry!["failed"]?.GetValue<bool>() == true)
            {
                refused.Add(said);
            }
            else
            {
                (entry["kind"]?.GetValue<string>() switch
                {
                    "changed" => changed,
                    "ran" => ran,
                    _ => read,
                }).Add(said);
            }
        }

        return new AuditSummary(run, read, changed, ran, refused, breaks);
    }

    /// <summary>The runs whose logs are in <paramref name="folder"/>, newest first.</summary>
    public static IReadOnlyList<string> Runs(string folder) =>
        Directory.Exists(folder)
            ? [.. Directory.GetFiles(folder, "*.jsonl")
                           .Select(file => Path.GetFileNameWithoutExtension(file).Split('.')[0])
                           .Distinct(StringComparer.Ordinal)
                           .OrderDescending(StringComparer.Ordinal)]
            : [];

    private string File(int part) => Path.Combine(_folder, part == 0 ? $"{Run}.jsonl" : $"{Run}.{part}.jsonl");

    private static string Describe(JsonNode entry)
    {
        JsonNode? arguments = entry["arguments"];
        string what = entry["tool"]?.GetValue<string>() switch
        {
            "exec" or "job_start" => "ran " + string.Join(' ', (arguments?["argv"]?.AsArray() ?? []).Select(word => word?.GetValue<string>())),
            "edit_file" => "edited " + arguments?["path"]?.GetValue<string>(),
            "read_file" => "read " + arguments?["path"]?.GetValue<string>(),
            "job_cancel" => "cancelled job " + arguments?["job"]?.GetValue<string>(),
            { } tool => tool + (arguments?["job"] is { } job ? " " + job.GetValue<string>() : string.Empty),
            null => "an unnamed call",
        };

        string host = entry["host"]?.GetValue<string>() is { } named ? $" on {named}" : string.Empty;
        string exit = entry["exitCode"] is JsonValue code && code.TryGetValue(out int value) ? $" (exit {value})" : string.Empty;

        return $"{entry["time"]?.GetValue<string>()} {what}{host}{exit}";
    }

    private static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}

/// <summary>A run's audit log as a person reads it.</summary>
public sealed record AuditSummary(string Run, IReadOnlyList<string> Read, IReadOnlyList<string> Changed,
                                  IReadOnlyList<string> Ran, IReadOnlyList<string> Refused, IReadOnlyList<string> Breaks)
{
    /// <summary>The summary as text, with the chain's state first.</summary>
    public override string ToString()
    {
        StringBuilder text = new();

        text.Append("Agent gateway run ").Append(Run).Append('\n');
        text.Append(Breaks.Count == 0 ? "The log is whole: every line follows the one before it.\n" : "THE LOG HAS BEEN TAMPERED WITH OR DAMAGED:\n");

        foreach (string broken in Breaks)
        {
            text.Append("  ").Append(broken).Append('\n');
        }

        Section("Changed", Changed);
        Section("Ran", Ran);
        Section("Refused or failed", Refused);
        Section("Read", Read);

        return text.ToString();

        void Section(string title, IReadOnlyList<string> lines)
        {
            text.Append('\n').Append(title).Append(" (").Append(lines.Count.ToString(CultureInfo.InvariantCulture)).Append(")\n");

            foreach (string line in lines)
            {
                text.Append("  ").Append(line).Append('\n');
            }
        }
    }
}
