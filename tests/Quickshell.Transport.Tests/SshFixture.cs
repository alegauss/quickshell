using System.Net.Sockets;
using Quickshell.Transport;

namespace Quickshell.Transport.Tests;

/// <summary>
/// What every test against the SSH fixture asks of it, written once for this assembly (QS195):
/// whether a server is up, and a host-key check that trusts the fixture's.
///
/// <para><b>Each class keeps its own skip and its own sentence</b>, because the sentence is what a
/// waived run counts and what <c>tests/skips.json</c> holds it to (QS136). What they shared, and had
/// copied seventeen times, is the probe beneath the sentence.</para>
/// </summary>
internal static class SshFixture
{
    /// <summary>Where the fixture's servers listen.</summary>
    public const string Host = "127.0.0.1";

    /// <summary>How long a server is given to answer before it is taken not to be there.</summary>
    private static readonly TimeSpan Answer = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Accepts any host key. The fixture's keys are made fresh each time its containers are, so there
    /// is nothing to pin, and what these tests are about is not the host-key check.
    /// </summary>
    public static ValueTask<SshHostKeyVerdict> Trusting(SshEndpoint endpoint, SshHostKey key,
                                                        CancellationToken cancellationToken) =>
        ValueTask.FromResult(SshHostKeyVerdict.Accept);

    /// <summary>The addresses found with nothing listening, which are not asked again in this run.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(string, int), bool> Absent = new();

    /// <summary>
    /// Whether something accepts a connection on the port within two seconds.
    ///
    /// <para><b>An absence is remembered for the run.</b> A refused connection on Windows' loopback
    /// takes about two seconds to come back, and a guest with no Docker fixture asked it once per test:
    /// a hundred and thirty-six times, four and a half minutes of the transport suite spent hearing
    /// the same no. A server found up is asked again each time, since tests restart theirs.</para>
    /// </summary>
    public static bool Listening(int port, string host = Host)
    {
        if (Absent.ContainsKey((host, port)))
        {
            return false;
        }

        bool up;

        try
        {
            using TcpClient probe = new();

            up = probe.ConnectAsync(host, port).Wait(Answer);
        }
        catch (Exception)
        {
            // Refused, unreachable or reset: nothing there to test against, which the caller's
            // skip says in its own words.
            up = false;
        }

        if (!up)
        {
            Absent[(host, port)] = true;
        }

        return up;
    }
}
