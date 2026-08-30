using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text;

namespace Quickshell.Transport;

/// <summary>One key an agent is holding, as the agent describes it.</summary>
/// <param name="Blob">The public key, in the wire form a signature request names it by.</param>
/// <param name="Comment">What the agent calls it, which is usually the file it came from.</param>
public readonly record struct AgentIdentity(ReadOnlyMemory<byte> Blob, string Comment)
{
    /// <summary>The key's algorithm, which is the first string inside its own blob.</summary>
    public string Algorithm => SshWire.FirstString(Blob.Span);

    /// <summary>The same fingerprint <c>ssh-add -l</c> prints, so a user can recognise their key.</summary>
    public string Fingerprint => new SshHostKey(Algorithm, Blob).Fingerprint;
}

/// <summary>
/// An SSH agent, reached over a named pipe.
///
/// <para><b>Two operations and no more.</b> List identities, and sign with one of them. The agent
/// protocol has a dozen others — add a key, lock, remove — and none of them is a thing a terminal
/// should be doing to a user's agent.</para>
///
/// <para><b>Why this exists at all.</b> QS5 established that the library has no agent support and
/// that its authentication seam is public, so this is written directly. The protocol is small enough
/// that writing it is cheaper than the alternative, and there is no alternative for the case that
/// matters most: a key on a smart card or a hardware token is not extractable, so an agent is not a
/// convenience there, it is the only route that exists.</para>
///
/// <para><b>Two agents, one protocol.</b> Windows' own OpenSSH agent listens on
/// <see cref="OpenSshPipe"/>. Pageant — which the PuTTY and MobaXterm users this client is for
/// already run — speaks the same requests, over a shared-memory transport in older versions and a
/// named pipe in newer ones. Only the transport differs, so only the transport is a parameter.</para>
/// </summary>
public sealed class SshAgent
{
    /// <summary>Where Windows' own OpenSSH agent listens.</summary>
    public const string OpenSshPipe = "openssh-ssh-agent";

    /// <summary>Ask for the identities the agent holds.</summary>
    private const byte RequestIdentities = 11;

    /// <summary>The answer to that.</summary>
    private const byte IdentitiesAnswer = 12;

    /// <summary>Ask the agent to sign something.</summary>
    private const byte SignRequest = 13;

    /// <summary>The answer to that.</summary>
    private const byte SignResponse = 14;

    /// <summary>Ask for an RSA signature under SHA-256 rather than the legacy SHA-1.</summary>
    public const uint RsaSha256 = 2;

    /// <summary>Ask for an RSA signature under SHA-512.</summary>
    public const uint RsaSha512 = 4;

    /// <summary>
    /// The largest message this will read.
    ///
    /// <para>A bound rather than a guess: the agent is another process and a length field is a thing
    /// it could get wrong, so a wrong one must cost a refusal rather than an allocation the size of
    /// whatever number arrived.</para>
    /// </summary>
    private const int MaximumMessage = 256 * 1024;

    private readonly string _pipe;

    /// <summary>An agent on a named pipe; Windows' own OpenSSH agent unless told otherwise.</summary>
    public SshAgent(string pipe = OpenSshPipe)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipe);

        _pipe = pipe;
    }

    /// <summary>
    /// Whether anything is listening. A user with no agent running is the ordinary case and not a
    /// failure.
    ///
    /// <para><b>Asked by listing rather than by opening.</b> Connecting to find out consumes one of
    /// the agent's listeners and hangs up on it, which leaves a window in which the next real
    /// request finds nothing there — the worst possible answer from a probe whose whole job is to
    /// say whether an agent exists. <c>File.Exists</c> is no better: on Windows it opens a handle to
    /// the pipe to ask, so it is the same intrusion wearing a read-only name. Enumerating the pipe
    /// directory opens nothing.</para>
    /// </summary>
    public bool IsRunning =>
        Directory.EnumerateFiles(@"\\.\pipe\")
                 .Any(pipe => string.Equals(System.IO.Path.GetFileName(pipe), _pipe,
                                            StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The keys the agent is holding, in the order it lists them.
    ///
    /// <para>The order is the agent's and is kept: a user who put their most-used key in first
    /// expects it tried first, and a server's authentication attempt limit means later ones may
    /// never be reached at all.</para>
    /// </summary>
    public IReadOnlyList<AgentIdentity> Identities()
    {
        byte[] answer = Exchange([RequestIdentities]);

        if (answer.Length < 5 || answer[0] != IdentitiesAnswer)
        {
            return [];
        }

        int at = 1;
        uint count = SshWire.ReadUInt32(answer, ref at);
        List<AgentIdentity> identities = [];

        for (uint index = 0; index < count && at < answer.Length; index++)
        {
            byte[] blob = SshWire.ReadString(answer, ref at);
            byte[] comment = SshWire.ReadString(answer, ref at);

            identities.Add(new AgentIdentity(blob, Encoding.UTF8.GetString(comment)));
        }

        return identities;
    }

    /// <summary>
    /// Asks the agent to sign, which is the operation the private key never leaves for.
    /// </summary>
    /// <param name="blob">Which key, named by its public blob exactly as the agent listed it.</param>
    /// <param name="data">What to sign.</param>
    /// <param name="flags">
    /// <see cref="RsaSha256"/> or <see cref="RsaSha512"/> to ask an RSA key for a modern signature.
    /// Zero for everything else, and for RSA against a server that still wants SHA-1.
    /// </param>
    /// <returns>The signature blob, already in the wire form the protocol wants.</returns>
    /// <exception cref="SshException">The agent refused or is not there.</exception>
    public byte[] Sign(ReadOnlySpan<byte> blob, ReadOnlySpan<byte> data, uint flags = 0)
    {
        List<byte> request = [SignRequest];

        SshWire.WriteString(request, blob);
        SshWire.WriteString(request, data);
        SshWire.WriteUInt32(request, flags);

        byte[] answer = Exchange([.. request]);

        if (answer.Length < 1 || answer[0] != SignResponse)
        {
            throw new SshException(
                SshFailureKind.CredentialRejected,
                "The agent would not sign with that key.",
                "It holds the key and declined to use it — a hardware token may be waiting for a "
                + "touch, or the key may have been removed since it was listed.",
                "Touch the token if there is one, or check that the key is still loaded.");
        }

        int at = 1;

        return SshWire.ReadString(answer, ref at);
    }

    /// <summary>One request, one answer, on a connection that lasts exactly that long.</summary>
    private byte[] Exchange(byte[] payload)
    {
        try
        {
            using NamedPipeClientStream pipe = new(".", _pipe, PipeDirection.InOut);

            pipe.Connect(2000);

            byte[] framed = new byte[4 + payload.Length];

            BinaryPrimitives.WriteUInt32BigEndian(framed, (uint)payload.Length);
            payload.CopyTo(framed, 4);

            pipe.Write(framed);
            pipe.Flush();

            byte[] header = new byte[4];

            Fill(pipe, header);

            uint length = BinaryPrimitives.ReadUInt32BigEndian(header);

            if (length == 0 || length > MaximumMessage)
            {
                throw new SshException(
                    SshFailureKind.Unrecognised,
                    $"The agent answered with a {length}-byte message.",
                    "That is not a length this client will allocate for, so the answer was refused.");
            }

            byte[] answer = new byte[length];

            Fill(pipe, answer);

            return answer;
        }
        catch (Exception failure) when (failure is TimeoutException or IOException
                                        or UnauthorizedAccessException)
        {
            throw SshException.From(
                SshFailureKind.NoMethodAccepted,
                "No SSH agent answered.",
                failure,
                $"Nothing is listening on the {_pipe} pipe.",
                "Start the agent, or point this client at a key file instead.");
        }
    }

    /// <summary>Reads exactly this many bytes, because a pipe read may answer with fewer.</summary>
    private static void Fill(Stream pipe, Span<byte> destination)
    {
        int filled = 0;

        while (filled < destination.Length)
        {
            int read = pipe.Read(destination[filled..]);

            if (read <= 0)
            {
                throw new IOException("the agent closed the pipe mid-message");
            }

            filled += read;
        }
    }
}

/// <summary>
/// The four shapes the SSH wire format is made of, which the agent protocol is built from.
///
/// <para>Big-endian lengths and length-prefixed strings, and nothing else. Written here rather than
/// borrowed from the library because this is the one place in the client that speaks a protocol
/// directly, and borrowing would put a library type in the middle of it.</para>
/// </summary>
internal static class SshWire
{
    /// <summary>The first length-prefixed string of a blob, which for a key is its algorithm.</summary>
    public static string FirstString(ReadOnlySpan<byte> blob)
    {
        if (blob.Length < 4)
        {
            return string.Empty;
        }

        uint length = BinaryPrimitives.ReadUInt32BigEndian(blob);

        return length > blob.Length - 4
            ? string.Empty
            : Encoding.ASCII.GetString(blob.Slice(4, (int)length));
    }

    public static uint ReadUInt32(ReadOnlySpan<byte> source, ref int at)
    {
        if (at + 4 > source.Length)
        {
            at = source.Length;

            return 0;
        }

        uint value = BinaryPrimitives.ReadUInt32BigEndian(source[at..]);

        at += 4;

        return value;
    }

    public static byte[] ReadString(ReadOnlySpan<byte> source, ref int at)
    {
        uint length = ReadUInt32(source, ref at);

        if (length > source.Length - at)
        {
            at = source.Length;

            return [];
        }

        byte[] value = source.Slice(at, (int)length).ToArray();

        at += (int)length;

        return value;
    }

    public static void WriteUInt32(List<byte> destination, uint value)
    {
        Span<byte> four = stackalloc byte[4];

        BinaryPrimitives.WriteUInt32BigEndian(four, value);
        destination.AddRange(four);
    }

    public static void WriteString(List<byte> destination, ReadOnlySpan<byte> value)
    {
        WriteUInt32(destination, (uint)value.Length);
        destination.AddRange(value);
    }
}
