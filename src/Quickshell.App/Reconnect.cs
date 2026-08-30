namespace Quickshell.App;

/// <summary>Where a session is in its own life, which is the whole of what a status bar shows.</summary>
public enum SessionState
{
    /// <summary>Not started yet.</summary>
    Idle,

    /// <summary>An attempt is in flight.</summary>
    Connecting,

    /// <summary>Connected, with a shell on the other end.</summary>
    Live,

    /// <summary>The link went and another attempt is due. <see cref="SessionStatus.NextIn"/> says when.</summary>
    Waiting,

    /// <summary>Over, and not coming back: the shell exited, the attempts ran out, or somebody stopped it.</summary>
    Ended,
}

/// <summary>
/// What to say about a session right now.
///
/// <para>The design asks that every attempt be visible — <b>which attempt this is, when the next one
/// is due, and how to stop</b> — and this is the first two. The third is
/// <see cref="RemoteSession.Stop"/>, which is a method rather than a field because "how to stop" is
/// something a user does and not something they read.</para>
/// </summary>
/// <param name="State">Where the session is.</param>
/// <param name="Attempt">Which attempt this is, counting from one. Zero before the first.</param>
/// <param name="NextIn">How long until the next attempt, while <see cref="SessionState.Waiting"/>.</param>
/// <param name="Reason">Why it is not connected, in the words the failure carried.</param>
public readonly record struct SessionStatus(SessionState State, int Attempt, TimeSpan NextIn,
                                            string Reason)
{
    /// <summary>A session that has not started.</summary>
    public static SessionStatus Idle => new(SessionState.Idle, 0, TimeSpan.Zero, string.Empty);

    /// <summary>Whether there is a shell on the other end right now.</summary>
    public bool IsLive => State == SessionState.Live;
}

/// <summary>
/// When to try again, and when to stop trying.
///
/// <para><b>Every attempt is bounded and every attempt is visible.</b> A client that reconnects
/// silently and forever is a client hammering a server that is deliberately refusing it, and the
/// server's operator has no way to tell that from an attack.</para>
///
/// <para><b>No jitter, deliberately.</b> Jitter exists to stop a thousand clients retrying in step;
/// one terminal reconnecting to one host is not a herd, and a delay a user cannot predict is a delay
/// they cannot be shown a countdown for.</para>
/// </summary>
public sealed record ReconnectPolicy
{
    /// <summary>
    /// Off, which is the default and is deliberate.
    ///
    /// <para>An unexpected new login is itself an event on plenty of hosts — it writes a line to a
    /// log somebody reads, it may page somebody, and on a jump box it may be the thing being
    /// watched for. So reconnecting is something a user turns on for a host, per session, rather
    /// than something that happens to them.</para>
    /// </summary>
    public static ReconnectPolicy Off { get; } = new() { Enabled = false };

    /// <summary>A reasonable default for a host where reconnecting is wanted.</summary>
    public static ReconnectPolicy Default { get; } = new() { Enabled = true };

    /// <summary>Whether to try again at all.</summary>
    public bool Enabled { get; init; }

    /// <summary>How long to wait before the first retry.</summary>
    public TimeSpan First { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>The longest this will ever wait between attempts, however many have failed.</summary>
    public TimeSpan Ceiling { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How many attempts before giving up and saying so.
    ///
    /// <para>Six at these defaults is a minute and a half of trying, which covers a lift, a train
    /// tunnel and a wireless handover, and does not cover a host that has been decommissioned.</para>
    /// </summary>
    public int MaximumAttempts { get; init; } = 6;

    /// <summary>
    /// How long to wait before attempt <paramref name="attempt"/>, counting from one.
    ///
    /// <para>Doubling from <see cref="First"/> and held at <see cref="Ceiling"/>. Computed rather
    /// than accumulated so that it is a function of the attempt number and nothing else — a schedule
    /// that depends on how it got here is a schedule nobody can predict from the outside.</para>
    /// </summary>
    public TimeSpan Delay(int attempt)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(attempt, 1);

        // Shifting rather than Math.Pow, and capped at the exponent that could overflow a long: at
        // these defaults the ceiling is reached by attempt six, so the arithmetic past it only has
        // to not be wrong.
        long doublings = Math.Min(attempt - 1, 62);
        long ticks = First.Ticks;

        for (long doubling = 0; doubling < doublings && ticks < Ceiling.Ticks; doubling++)
        {
            ticks *= 2;
        }

        return TimeSpan.FromTicks(Math.Min(ticks, Ceiling.Ticks));
    }
}
