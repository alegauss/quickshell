using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Quickshell.App;

/// <summary>How much an agent may do on a session (QS322).</summary>
public enum PolicyLevel
{
    /// <summary>Nothing at all.</summary>
    Deny,

    /// <summary>Reads run; a mutation is refused.</summary>
    Observe,

    /// <summary>Reads run; a mutation asks the person.</summary>
    Operate,
}

/// <summary>What the gateway does with a call.</summary>
public enum Verdict
{
    /// <summary>It runs.</summary>
    Allow,

    /// <summary>It waits for the person (QS337); refused while nothing can ask.</summary>
    Ask,

    /// <summary>It does not run, and the agent is told which rule said so.</summary>
    Refuse,
}

/// <summary>A verdict and the rule that gave it, which the agent, the feed and the audit log are told.</summary>
/// <param name="Verdict">Allow, ask or refuse.</param>
/// <param name="Rule">The rule, in words: which line of the policy, or which default.</param>
/// <param name="Read">Whether the call was judged a read.</param>
public sealed record PolicyVerdict(Verdict Verdict, string Rule, bool Read);

/// <summary>
/// The policy an agent is held to (QS322), judged before every call that reaches a server (QS336).
///
/// <para><b>Pure.</b> Given the policy, a session with its tags, a call and the time, it returns a
/// verdict and touches nothing, which is what lets it be tested on a corpus with no server.</para>
///
/// <para><b>Levels by tag, or by a glob over session paths where nobody tagged.</b> The narrowest level
/// of every rule that matches wins. A session no rule matches takes the file's <c>default</c>, which is
/// <c>deny</c> unless the person wrote otherwise; a session tagged <c>prod</c> that no rule names is
/// <c>deny</c> whatever the default says. With no file at all, every session is at <c>operate</c>, so a
/// first run asks rather than allows, and <c>prod</c> is at <c>observe</c>.</para>
///
/// <para><b>Every widening ends.</b> A grant lets named argument vectors, matched whole, run without
/// asking on named sessions until a time. One with no <c>until</c>, or one more than four hours away, is
/// ignored, and so is one that has passed. <c>break-glass</c> is not a level a rule may stand at; it is
/// a grant (<c>"level": "break-glass"</c>) that lets everything run on its sessions until its time.</para>
///
/// <para><b>A file that does not read refuses everything</b>, naming why: guessing at a broken policy is
/// how a typo becomes an allow.</para>
/// </summary>
public sealed class AgentPolicy
{
    /// <summary>The longest a grant may run.</summary>
    public static readonly TimeSpan LongestGrant = TimeSpan.FromHours(4);

    private readonly PolicyLevel? _default;
    private readonly IReadOnlyList<Rule> _rules;
    private readonly IReadOnlyList<Grant> _grants;
    private readonly string? _broken;

    private AgentPolicy(PolicyLevel? fallback, IReadOnlyList<Rule> rules, IReadOnlyList<Grant> grants, string? broken)
    {
        _default = fallback;
        _rules = rules;
        _grants = grants;
        _broken = broken;
    }

    /// <summary>The policy with no file: every session at operate, prod at observe.</summary>
    public static AgentPolicy None { get; } = new(null, [], [], null);

    /// <summary>The policy at <paramref name="file"/>, <see cref="None"/> where there is no file, or a refusing one where it does not read.</summary>
    public static AgentPolicy Load(string file)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(file);

        if (!File.Exists(file))
        {
            return None;
        }

        try
        {
            return Parse(File.ReadAllText(file));
        }
        catch (Exception unreadable) when (unreadable is IOException or UnauthorizedAccessException)
        {
            return new AgentPolicy(PolicyLevel.Deny, [], [], $"{file} could not be read: {unreadable.Message}");
        }
    }

    /// <summary>A policy from its JSON, or a refusing one naming what is wrong with it.</summary>
    public static AgentPolicy Parse(string json)
    {
        try
        {
            JsonNode root = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })
                            ?? throw new InvalidDataException("the policy is empty");

            PolicyLevel fallback = root["default"] is { } named ? Level(named.GetValue<string>()) : PolicyLevel.Deny;

            List<Rule> rules = [.. (root["rules"] as JsonArray ?? []).Select((rule, at) => new Rule(
                at + 1,
                Strings(rule?["tags"]),
                Strings(rule?["sessions"]),
                Level(rule?["level"]?.GetValue<string>() ?? throw new InvalidDataException($"rule {at + 1} names no level")),
                [.. (rule!["reads"] as JsonArray ?? []).Select(CommandJudge.Read)]))];

            List<Grant> grants = [.. (root["grants"] as JsonArray ?? []).Select((grant, at) => new Grant(
                at + 1,
                Strings(grant?["tags"]),
                Strings(grant?["sessions"]),
                [.. (grant?["commands"] as JsonArray ?? []).Select(command => (IReadOnlyList<string>)Strings(command))],
                grant?["until"] is { } until ? DateTimeOffset.Parse(until.GetValue<string>(), CultureInfo.InvariantCulture) : null,
                grant?["why"]?.GetValue<string>() ?? string.Empty,
                grant?["level"]?.GetValue<string>() switch
                {
                    null => false,
                    "break-glass" => true,
                    { } other => throw new InvalidDataException($"grant {at + 1} names level {other}; a grant's only level is break-glass"),
                }))];

            return new AgentPolicy(fallback, rules, grants, null);
        }
        catch (Exception wrong) when (wrong is JsonException or InvalidDataException or InvalidOperationException or FormatException)
        {
            return new AgentPolicy(PolicyLevel.Deny, [], [], $"the agent policy does not read: {wrong.Message}");
        }
    }

    /// <summary>
    /// The verdict on a call to <paramref name="session"/>: <paramref name="argv"/> for a command, or
    /// <paramref name="read"/> for a call whose kind is fixed by its tool.
    /// </summary>
    public PolicyVerdict Judge(ResolvedSession session, IReadOnlyList<string>? argv, bool read, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(session);

        if (_broken is { } why)
        {
            return new PolicyVerdict(Verdict.Refuse, why, read);
        }

        List<Rule> matching = [.. _rules.Where(rule => Matches(session, rule.Tags, rule.Sessions))];
        bool isRead = argv is null ? read : CommandJudge.Shipped.With(matching.SelectMany(rule => rule.Reads)).IsRead(argv);

        // break-glass: everything on the sessions it names, until its time, and never a standing level.
        if (_grants.FirstOrDefault(grant => grant.BreakGlass && Live(grant, now) && Matches(session, grant.Tags, grant.Sessions)) is { } glass)
        {
            return new PolicyVerdict(Verdict.Allow,
                string.Create(CultureInfo.InvariantCulture, $"break-glass grant {glass.Number} until {glass.Until:yyyy-MM-dd HH:mm zzz}")
                + (glass.Why.Length > 0 ? $" ({glass.Why})" : string.Empty), isRead);
        }

        // A grant names whole argument vectors, so it is checked before the level: it is the person's
        // own widening, bounded in time.
        if (argv is not null && _grants.FirstOrDefault(grant => Live(grant, now) && Matches(session, grant.Tags, grant.Sessions)
                                                                && grant.Commands.Any(command => command.SequenceEqual(argv, StringComparer.Ordinal))) is { } granted)
        {
            return new PolicyVerdict(Verdict.Allow,
                string.Create(CultureInfo.InvariantCulture, $"grant {granted.Number} until {granted.Until:yyyy-MM-dd HH:mm zzz}")
                + (granted.Why.Length > 0 ? $" ({granted.Why})" : string.Empty), isRead);
        }

        bool prod = session.Tags.Contains("prod", StringComparer.OrdinalIgnoreCase);
        (PolicyLevel level, string rule) = matching.Count > 0
            ? (matching.Min(each => each.Level), Describe(matching.MinBy(each => each.Level)!))
            : _default is null
                ? prod ? (PolicyLevel.Observe, "no policy file: a session tagged prod is at observe")
                       : (PolicyLevel.Operate, "no policy file: every session is at operate")
                : prod ? (PolicyLevel.Deny, "a session tagged prod that no rule names is at deny")
                       : (_default.Value, $"no rule names this session, so the default, {Name(_default.Value)}");

        return level switch
        {
            PolicyLevel.Deny => new PolicyVerdict(Verdict.Refuse, rule, isRead),
            _ when isRead => new PolicyVerdict(Verdict.Allow, rule, true),
            PolicyLevel.Observe => new PolicyVerdict(Verdict.Refuse, rule + "; it allows reads only, and this is not one", false),
            _ => new PolicyVerdict(Verdict.Ask, rule + "; a call that is not certainly a read asks the person", false),
        };
    }

    /// <summary>
    /// What the grants naming <paramref name="session"/> have let an agent run as root, live or passed: each
    /// <c>sudo</c> command they list, with <c>sudo</c> and its options taken off. It is where the server-side
    /// setup's list starts (QS340), since a command the person granted here is one they meant it to run.
    /// </summary>
    public IReadOnlyList<IReadOnlyList<string>> GrantedAsRoot(ResolvedSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        return [.. _grants.Where(grant => Matches(session, grant.Tags, grant.Sessions))
                          .SelectMany(grant => grant.Commands)
                          .Where(command => command.Count > 1 && command[0] == "sudo")
                          .Select(command => (IReadOnlyList<string>)[.. command.Skip(1).SkipWhile(word => word.StartsWith('-'))])
                          .Where(command => command.Count > 0)
                          .DistinctBy(command => string.Join('\0', command), StringComparer.Ordinal)];
    }

    private static bool Live(Grant grant, DateTimeOffset now) =>
        grant.Until is { } until && until > now && until - now <= LongestGrant;

    private static bool Matches(ResolvedSession session, IReadOnlyList<string> tags, IReadOnlyList<string> sessions) =>
        tags.Any(tag => session.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
        || sessions.Any(glob => Regex.IsMatch(session.Path, "^" + Regex.Escape(glob).Replace("\\*", ".*", StringComparison.Ordinal).Replace("\\?", ".", StringComparison.Ordinal) + "$",
                                              RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)));

    private static string Describe(Rule rule) =>
        $"rule {rule.Number} ({string.Join(", ", rule.Tags.Select(tag => "tag " + tag).Concat(rule.Sessions.Select(glob => "sessions " + glob)))}) at {Name(rule.Level)}";

    private static PolicyLevel Level(string named) => named switch
    {
        "deny" => PolicyLevel.Deny,
        "observe" => PolicyLevel.Observe,
        "operate" => PolicyLevel.Operate,
        "break-glass" => throw new InvalidDataException("break-glass is a grant, never a level a rule stands at"),
        _ => throw new InvalidDataException($"{named} is not a level: deny, observe or operate"),
    };

    private static string Name(PolicyLevel level) => level.ToString().ToLowerInvariant();

    private static List<string> Strings(JsonNode? node) => [.. (node as JsonArray ?? []).Select(each => each!.GetValue<string>())];

    private sealed record Rule(int Number, IReadOnlyList<string> Tags, IReadOnlyList<string> Sessions, PolicyLevel Level, IReadOnlyList<ReadCommand> Reads);

    private sealed record Grant(int Number, IReadOnlyList<string> Tags, IReadOnlyList<string> Sessions,
                                IReadOnlyList<IReadOnlyList<string>> Commands, DateTimeOffset? Until, string Why, bool BreakGlass);
}
