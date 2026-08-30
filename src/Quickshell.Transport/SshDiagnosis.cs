using System.Globalization;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Renci.SshNet.Common;

namespace Quickshell.Transport;

/// <summary>
/// Turns whatever the library threw into the one failure type that crosses the seam, with the three
/// clauses a user is shown.
///
/// <para><b>Every rule here was written against a run rather than against documentation.</b> The
/// probe that produced them connected to a name that does not resolve, a port that refuses, an
/// address nothing routes to, a socket that answers with HTTP, a socket that answers with silence,
/// an account whose method the server does not allow, and a key the server does not authorise —
/// and recorded what came back. The comments name what each rule was measured on, because a rule
/// matched against a guess describes the wrong failure with total confidence.</para>
///
/// <para><b>Where a rule matches on message text, that is said out loud.</b> A socket error carries
/// a number and the number is what is read; the library's own exceptions carry only English
/// sentences, so two of these read the sentence. That is brittle across library versions, which is
/// why <c>SshFailureTests</c> pins each one — a version that rewords its messages fails a test
/// rather than quietly misclassifying every timeout as the wrong sort of timeout.</para>
/// </summary>
internal static partial class SshDiagnosis
{
    /// <summary>
    /// Measured: <c>"No suitable authentication method found to complete authentication
    /// (publickey,keyboard-interactive)."</c> — the parenthesised list is what the <em>server</em>
    /// will accept, which is the single most useful thing to tell a user who cannot get in.
    /// </summary>
    [GeneratedRegex(@"\(([a-z0-9\-,@\.]+)\)", RegexOptions.CultureInvariant)]
    private static partial Regex Methods { get; }

    /// <summary>What the library threw, as this client's failure.</summary>
    public static SshException Translate(SshEndpoint endpoint, Exception failure) => failure switch
    {
        OperationCanceledException => new SshException(
            SshFailureKind.Cancelled,
            $"The attempt to reach {endpoint} was stopped.",
            "Nothing was left half-done: the connection had not been established."),

        SocketException socket => FromSocket(endpoint, socket),

        SshAuthenticationException authentication => FromAuthentication(endpoint, authentication),

        SshOperationTimeoutException timeout => FromTimeout(endpoint, timeout),

        SshConnectionException connection => FromConnection(endpoint, connection),

        _ => SshException.From(
            SshFailureKind.Unrecognised,
            $"{endpoint} failed in a way this client does not recognise.",
            failure,
            "This is a gap in quickshell rather than a diagnosis of the server.",
            "The detail is in the log, and it belongs in a bug report."),
    };

    /// <summary>
    /// A shell that the server would not open on a connection that authenticated.
    ///
    /// <para>Separated from <see cref="Translate"/> because the same library exception means
    /// something quite different here: by this point the credentials were accepted, so nothing about
    /// them is worth suggesting.</para>
    /// </summary>
    public static SshException Shell(SshEndpoint endpoint, Exception failure) =>
        failure is SshOperationTimeoutException or SshConnectionException or Renci.SshNet.Common.SshException
            ? SshException.From(
                SshFailureKind.ShellRefused,
                $"{endpoint} accepted the login and would not open a shell.",
                failure,
                "The account exists and is not allowed an interactive session — which is what a "
                + "restricted or file-transfer-only account looks like.",
                "Use the file transfer pane instead, or ask for shell access on this account.")
            : Translate(endpoint, failure);

    /// <summary>
    /// The socket layer, which is the only one here that reports a number rather than a sentence.
    ///
    /// <para><c>SocketException.Message</c> is localised — it came back in Portuguese on the machine
    /// this was written on — so it is never read. <see cref="SocketException.SocketErrorCode"/> is
    /// the same value everywhere.</para>
    /// </summary>
    private static SshException FromSocket(SshEndpoint endpoint, SocketException socket) =>
        socket.SocketErrorCode switch
        {
            // Measured against "no-such-host.invalid".
            SocketError.HostNotFound or SocketError.NoData => SshException.From(
                SshFailureKind.NameNotFound,
                $"There is no host called {endpoint.Host}.",
                socket,
                "The name could not be resolved, so nothing was contacted.",
                "Check the spelling, or whether this name needs a VPN or an internal resolver."),

            // Measured against 127.0.0.1 port 2.
            SocketError.ConnectionRefused => SshException.From(
                SshFailureKind.Refused,
                $"{endpoint.Host} refused a connection on port {endpoint.Port}.",
                socket,
                "The host is reachable and nothing is listening on that port.",
                $"Check the port. SSH is usually {SshEndpoint.DefaultPort}."),

            _ => SshException.From(
                SshFailureKind.Unreachable,
                $"{endpoint.Host} could not be reached.",
                socket,
                "The network refused or dropped the attempt before any server answered.",
                "Check the connection, a VPN, or a firewall between here and there."),
        };

    /// <summary>
    /// Authentication, split the way the design asks: no method accepted, against a method that was
    /// tried and failed.
    ///
    /// <para>Measured. A server that allows only <c>publickey</c>, offered a password:
    /// <c>"No suitable authentication method found to complete authentication (publickey)."</c> — the
    /// client's method was never tried. A server that allows <c>publickey</c>, offered a key it has
    /// not authorised: <c>"Permission denied (publickey)."</c> — tried, and refused. The two have
    /// opposite remedies, which is the whole reason they are two kinds.</para>
    /// </summary>
    private static SshException FromAuthentication(SshEndpoint endpoint,
                                                   SshAuthenticationException authentication)
    {
        if (!authentication.Message.Contains("No suitable authentication method",
                                             StringComparison.OrdinalIgnoreCase))
        {
            return SshException.From(
                SshFailureKind.CredentialRejected,
                $"{endpoint} rejected the credential offered.",
                authentication,
                "The server understood who was asking and said no.",
                "Check the key, the passphrase or the password, and that this key is authorised "
                + "for this account.");
        }

        Match offered = Methods.Match(authentication.Message);
        string wanted = offered.Success
            ? offered.Groups[1].Value.Replace(",", ", ", StringComparison.Ordinal)
            : string.Empty;

        return SshException.From(
            SshFailureKind.NoMethodAccepted,
            $"{endpoint} would not accept any of the ways this client offered to identify itself.",
            authentication,
            wanted.Length > 0
                ? $"The server accepts {wanted}, and none of those was offered."
                : "The server declined every method offered before any credential was tried.",
            "Offer a credential of a kind the server allows.");
    }

    /// <summary>
    /// A timeout, which is as far as this can be narrowed.
    ///
    /// <para><b>The design asks for a connect timeout and a handshake timeout to be told apart, and
    /// through this library's asynchronous path they cannot be.</b> Measured: an address routed
    /// nowhere and a socket that accepts and then says nothing both give
    /// <c>SshOperationTimeoutException: "Connection has timed out."</c> — the same type and the same
    /// sentence. The synchronous <c>Connect()</c> does distinguish them, with "Connection failed to
    /// establish within N milliseconds" against "Socket read operation has timed out after N
    /// milliseconds", but it takes no cancellation token, and a connection attempt a user cannot
    /// abandon is a worse trade than a message that covers two readings.</para>
    ///
    /// <para>So the message covers both rather than asserting the one it cannot know. A message that
    /// confidently named the wrong one would send a user to check a firewall when the port is wrong,
    /// which is worse than saying honestly that it is one of two things.</para>
    /// </summary>
    private static SshException FromTimeout(SshEndpoint endpoint, SshOperationTimeoutException timeout) =>
        SshException.From(
            SshFailureKind.NotResponding,
            $"{endpoint.Host} did not answer as an SSH server on port {endpoint.Port}.",
            timeout,
            "Either nothing there took the connection — a firewall dropping rather than refusing, "
            + "or no route — or something took it and never identified itself as SSH.",
            $"Check that port {endpoint.Port} is this host's SSH port, and then check a VPN or a "
            + "firewall between here and there.");

    /// <summary>
    /// A connection that ended, which before authentication is usually negotiation and afterwards is
    /// usually the link.
    /// </summary>
    private static SshException FromConnection(SshEndpoint endpoint,
                                               SshConnectionException connection) =>
        connection.Message.Contains("algorithm", StringComparison.OrdinalIgnoreCase)
        || connection.Message.Contains("negotiat", StringComparison.OrdinalIgnoreCase)
            ? SshException.From(
                SshFailureKind.NoSharedAlgorithm,
                $"{endpoint} and this client share no algorithm they will both use.",
                connection,
                "Both sides speak SSH and disagree about the cryptography. Either the server is old "
                + "enough to offer only algorithms this client refuses as insecure, or new enough to "
                + "require one this client does not implement — and those have opposite remedies.",
                "The log names what was refused. An old appliance needs its own configuration "
                + "changed; a new server needs this client updated.")
            : SshException.From(
                SshFailureKind.Dropped,
                string.Format(CultureInfo.InvariantCulture, "The connection to {0} ended.", endpoint),
                connection,
                "The link went away rather than the server saying no.",
                "It may come back on its own; a session with reconnecting turned on will try.");
}
