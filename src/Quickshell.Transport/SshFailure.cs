namespace Quickshell.Transport;

/// <summary>
/// What went wrong, at the grain a user can act on.
///
/// <para>The list is short because it is a list of <em>different things to do next</em>, not a
/// taxonomy of protocol errors. A wrong port and a wrong key are two entries because they send a
/// person to two different places; a dozen library exception types that all mean "the server said
/// no" are one.</para>
/// </summary>
public enum SshFailureKind
{
    /// <summary>Nothing answered: no route, no listener, a firewall, a name that does not resolve.</summary>
    Unreachable,

    /// <summary>Something answered and would not talk SSH, or would not agree on how to.</summary>
    Protocol,

    /// <summary>The server's key is not the one this client saw last time, or nobody vouched for it.</summary>
    HostKey,

    /// <summary>The server understood who was asking and said no.</summary>
    Authentication,

    /// <summary>The connection was up and is not any more.</summary>
    Dropped,

    /// <summary>The caller asked to stop.</summary>
    Cancelled,
}

/// <summary>
/// The only failure type that crosses the seam.
///
/// <para><b>The library's exception is deliberately not the inner exception.</b> That is the obvious
/// thing to do and it would undo the whole line: an <c>InnerException</c> is reachable from every
/// assembly above, so the library's type would be part of this client's surface by accident, and the
/// day it was replaced every <c>catch</c> written against it would compile and stop matching. What is
/// kept instead is what a diagnostic actually needs — the type's name and its message, as text.</para>
///
/// <para>Which kind a library error becomes is QS39's work and not this seam's. What this settles is
/// that the translation happens at the seam rather than at the top, where the information needed to
/// do it well has already been lost.</para>
/// </summary>
public sealed class SshException : Exception
{
    /// <summary>A failure with a reason a user can read.</summary>
    /// <param name="kind">What sort of thing went wrong.</param>
    /// <param name="reason">The sentence a user is shown.</param>
    /// <param name="origin">
    /// What the implementation was holding when it gave up, as text — a library exception's type
    /// name and message, a socket error, the server's own words. Null where there was nothing
    /// underneath.
    /// </param>
    public SshException(SshFailureKind kind, string reason, string? origin = null)
        : base(reason)
    {
        Kind = kind;
        Origin = origin;
    }

    /// <summary>What sort of thing went wrong, which is what decides what a user should do.</summary>
    public SshFailureKind Kind { get; }

    /// <summary>
    /// What was underneath, as text rather than as an object. For a log and a bug report; never for
    /// a decision, because its wording belongs to whichever library is currently in use.
    /// </summary>
    public string? Origin { get; }

    /// <summary>A failure whose origin is a library exception, flattened to text at the seam.</summary>
    public static SshException From(SshFailureKind kind, string reason, Exception origin)
    {
        ArgumentNullException.ThrowIfNull(origin);

        return new SshException(kind, reason, $"{origin.GetType().FullName}: {origin.Message}");
    }
}
