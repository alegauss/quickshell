namespace Quickshell.Transport;

/// <summary>
/// What went wrong, at the grain a user can act on.
///
/// <para><b>The list is a list of different things to do next</b>, not a taxonomy of protocol
/// errors. A wrong port and a wrong key are two entries because they send a person to two different
/// places; a dozen library exception types that all mean "the server said no" are one.</para>
///
/// <para>Every one of these was produced against a real server or a real socket before it was
/// written down — see <c>SshFailureTests</c>, which provokes each in turn. Two are marked as
/// classified but unexercised, and marked <em>here</em> rather than only in a commit, because an
/// unexercised branch is the one most likely to be describing the wrong failure.</para>
/// </summary>
public enum SshFailureKind
{
    /// <summary>The name does not resolve. Nothing was contacted at all.</summary>
    NameNotFound,

    /// <summary>Something is at that address and nothing is listening on that port.</summary>
    Refused,

    /// <summary>Nothing answered in time: no route, a firewall dropping rather than refusing.</summary>
    Unreachable,

    /// <summary>The connection opened and the far end never got as far as being an SSH server.</summary>
    NotResponding,

    /// <summary>
    /// Both sides talk SSH and share no algorithm they will both use.
    ///
    /// <para><b>Classified but unexercised.</b> No configuration of OpenSSH 9.6 tried here would
    /// produce it — the library's algorithm coverage turned out to be wide enough to negotiate
    /// against a server narrowed to one modern algorithm and against one narrowed to a
    /// twenty-year-old one. It is kept because appliances that cannot be upgraded are exactly where
    /// this failure lives, and falling through to "unrecognised" would serve that user worst.</para>
    /// </summary>
    NoSharedAlgorithm,

    /// <summary>The server's key is not the one this client saw last time, or nobody vouched for it.</summary>
    HostKey,

    /// <summary>The server would not accept any of the ways this client offered to identify itself.</summary>
    NoMethodAccepted,

    /// <summary>A method was tried with a credential and the server said no to it.</summary>
    CredentialRejected,

    /// <summary>
    /// Authenticated, and the account is not allowed a shell. What a restricted or file-transfer-only
    /// account looks like.
    ///
    /// <para><b>Classified but unexercised</b>, for want of such an account in the fixture.</para>
    /// </summary>
    ShellRefused,

    /// <summary>The connection was up and is not any more.</summary>
    Dropped,

    /// <summary>The caller asked to stop.</summary>
    Cancelled,

    /// <summary>None of the above, which is a failure this client has not learned to read.</summary>
    Unrecognised,
}

/// <summary>
/// The only failure type that crosses the seam, and the message a user is shown.
///
/// <para><b>Three clauses, and they are separate fields.</b> What happened, what it means, and what
/// to do about it. Joined into one string they read as a paragraph nobody finishes; separated, a
/// dialog can put the first in its title and the third beside a button, and a log can take all
/// three. The error message is the documentation a user reads at the moment something fails, which
/// is far more often than they read anything else.</para>
///
/// <para><b>The library's exception is deliberately not the inner exception.</b> That is the obvious
/// thing to do and it would undo QS36: an <c>InnerException</c> is reachable from every assembly
/// above, so the library's type would be part of this client's surface by accident, and the day it
/// was replaced every <c>catch</c> written against it would compile and stop matching.
/// <see cref="Origin"/> is what is kept instead — the type's name and its message, as text, for the
/// log rather than for the dialog.</para>
/// </summary>
public sealed class SshException : Exception
{
    /// <summary>A failure with a reason a user can read.</summary>
    /// <param name="kind">What sort of thing went wrong.</param>
    /// <param name="reason">What happened, which is the sentence a user is shown first.</param>
    /// <param name="means">What that means, in words that do not assume the protocol.</param>
    /// <param name="remedy">What the user might do about it.</param>
    /// <param name="origin">
    /// What the implementation was holding when it gave up, as text — a library exception's type
    /// name and message, a socket error, the server's own words. Null where there was nothing
    /// underneath.
    /// </param>
    public SshException(SshFailureKind kind, string reason, string means = "", string remedy = "",
                        string? origin = null)
        : base(reason)
    {
        Kind = kind;
        Means = means;
        Remedy = remedy;
        Origin = origin;
    }

    /// <summary>What sort of thing went wrong, which is what decides what a user should do.</summary>
    public SshFailureKind Kind { get; }

    /// <summary>What it means, for somebody who has not read the code.</summary>
    public string Means { get; }

    /// <summary>What the user might do about it. Empty where there is honestly nothing to suggest.</summary>
    public string Remedy { get; }

    /// <summary>
    /// What was underneath, as text rather than as an object. For a log and a bug report; never for
    /// a decision, because its wording belongs to whichever library is currently in use.
    /// </summary>
    public string? Origin { get; }

    /// <summary>The three clauses, in order, for a log or a place with room for all of them.</summary>
    public string Full =>
        string.Join(" ", new[] { Message, Means, Remedy }.Where(clause => clause.Length > 0));

    /// <summary>A failure whose origin is a library exception, flattened to text at the seam.</summary>
    public static SshException From(SshFailureKind kind, string reason, Exception origin,
                                    string means = "", string remedy = "")
    {
        ArgumentNullException.ThrowIfNull(origin);

        return new SshException(kind, reason, means, remedy,
                                $"{origin.GetType().FullName}: {origin.Message}");
    }
}
