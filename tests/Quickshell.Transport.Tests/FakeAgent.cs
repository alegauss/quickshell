using System.Buffers.Binary;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;

namespace Quickshell.Transport.Tests;

/// <summary>
/// An SSH agent, on a named pipe, speaking the real protocol.
///
/// <para><b>This is not a mock and the distinction matters.</b> It parses the bytes the client sends
/// and answers with the bytes the specification says, and the signatures it makes are real ed25519
/// signatures over the real data — which a real OpenSSH server then verifies. A stub that returned a
/// canned answer would test that this client can talk to that stub.</para>
///
/// <para>It exists because the machine this was written on cannot run the Windows agent: the service
/// is disabled and enabling it needs elevation. So the agent under test is one whose behaviour is
/// visible in this file, and the thing being proven is that a real server accepts what comes out of
/// the pipe.</para>
/// </summary>
internal sealed class FakeAgent : IAsyncDisposable
{
    private readonly CancellationTokenSource _stopping = new();
    private readonly ManualResetEventSlim _listening = new();
    private readonly RsaIdentity _identity;
    private readonly Task _serving;

    internal FakeAgent(string comment = "quickshell-test")
    {
        Pipe = $"quickshell-agent-{Guid.NewGuid():N}";

        _identity = new RsaIdentity(comment);
        _serving = Task.Run(ServeAsync);

        // The constructor does not return until the pipe is there. A fixture that is not ready when
        // it has been built is a fixture every test using it has to remember to wait for, and the
        // one that forgets fails somewhere else entirely.
        if (!_listening.Wait(TimeSpan.FromSeconds(5)))
        {
            throw new TimeoutException($"the agent never began listening on {Pipe}");
        }
    }

    /// <summary>The pipe name to point a client at.</summary>
    internal string Pipe { get; }

    /// <summary>How many signatures have been asked for, which is what says the agent did the work.</summary>
    internal int Signatures { get; private set; }

    /// <summary>The public key in OpenSSH's <c>authorized_keys</c> spelling, for a server to trust.</summary>
    internal string AuthorizedKey => _identity.AuthorizedKey;

    /// <summary>The flags the last signature was asked for under, which say which hash was wanted.</summary>
    internal uint LastFlags { get; private set; }

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync();

        // Connecting to it once is what unblocks a WaitForConnectionAsync that is already waiting.
        try
        {
            using NamedPipeClientStream nudge = new(".", Pipe, PipeDirection.InOut);

            await nudge.ConnectAsync(200, CancellationToken.None);
        }
        catch (Exception)
        {
            // Already gone, which is the outcome this was arranging.
        }

        try
        {
            await _serving;
        }
        catch (Exception)
        {
            // The serving loop ending is what was asked for.
        }

        _stopping.Dispose();
        _listening.Dispose();
        _identity.Dispose();
    }

    /// <summary>
    /// Accepts, then goes straight back to accepting while the last one is served.
    ///
    /// <para>Serving before listening again was the first shape of this and it does not work: a real
    /// client asks whether an agent is there by connecting and hanging up, and that connection eats
    /// the only listener. The next request then finds nothing on the pipe and reads as "no agent" —
    /// which is exactly the answer a probe that is looking for an agent must not get from the agent
    /// it just found.</para>
    /// </summary>
    private async Task ServeAsync()
    {
        while (!_stopping.IsCancellationRequested)
        {
            NamedPipeServerStream pipe = new(Pipe, PipeDirection.InOut,
                                             NamedPipeServerStream.MaxAllowedServerInstances);

            _listening.Set();

            try
            {
                await pipe.WaitForConnectionAsync(_stopping.Token);
            }
            catch (Exception)
            {
                await pipe.DisposeAsync();

                return;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    byte[] request = await ReadMessageAsync(pipe);

                    await WriteMessageAsync(pipe, Answer(request));
                    pipe.WaitForPipeDrain();
                }
                catch (Exception)
                {
                    // A client that hung up mid-message is a client that hung up, which is what a
                    // probe asking whether an agent exists does.
                }
                finally
                {
                    await pipe.DisposeAsync();
                }
            });
        }
    }

    /// <summary>The protocol: list identities, or sign with the one there is.</summary>
    private byte[] Answer(byte[] request)
    {
        const byte RequestIdentities = 11;
        const byte IdentitiesAnswer = 12;
        const byte SignRequest = 13;
        const byte SignResponse = 14;
        const byte Failure = 5;

        if (request.Length == 0)
        {
            return [Failure];
        }

        if (request[0] == RequestIdentities)
        {
            List<byte> answer = [IdentitiesAnswer];

            WriteUInt32(answer, 1);
            WriteString(answer, _identity.Blob);
            WriteString(answer, Encoding.UTF8.GetBytes(_identity.Comment));

            return [.. answer];
        }

        if (request[0] != SignRequest)
        {
            return [Failure];
        }

        int at = 1;
        byte[] named = ReadString(request, ref at);
        byte[] data = ReadString(request, ref at);
        uint flags = BinaryPrimitives.ReadUInt32BigEndian(request.AsSpan(at));

        if (!named.AsSpan().SequenceEqual(_identity.Blob))
        {
            return [Failure];
        }

        Signatures++;
        LastFlags = flags;

        List<byte> signed = [SignResponse];

        WriteString(signed, _identity.Sign(data, flags));

        return [.. signed];
    }

    private static async Task<byte[]> ReadMessageAsync(Stream pipe)
    {
        byte[] header = new byte[4];

        await Fill(pipe, header);

        byte[] payload = new byte[BinaryPrimitives.ReadUInt32BigEndian(header)];

        await Fill(pipe, payload);

        return payload;
    }

    private static async Task WriteMessageAsync(Stream pipe, byte[] payload)
    {
        byte[] framed = new byte[4 + payload.Length];

        BinaryPrimitives.WriteUInt32BigEndian(framed, (uint)payload.Length);
        payload.CopyTo(framed, 4);

        await pipe.WriteAsync(framed);
        await pipe.FlushAsync();
    }

    private static async Task Fill(Stream pipe, byte[] destination)
    {
        int filled = 0;

        while (filled < destination.Length)
        {
            int read = await pipe.ReadAsync(destination.AsMemory(filled));

            if (read <= 0)
            {
                throw new IOException("the client closed the pipe mid-message");
            }

            filled += read;
        }
    }

    private static void WriteUInt32(List<byte> destination, uint value)
    {
        Span<byte> four = stackalloc byte[4];

        BinaryPrimitives.WriteUInt32BigEndian(four, value);
        destination.AddRange(four);
    }

    private static void WriteString(List<byte> destination, ReadOnlySpan<byte> value)
    {
        WriteUInt32(destination, (uint)value.Length);
        destination.AddRange(value);
    }

    private static byte[] ReadString(ReadOnlySpan<byte> source, ref int at)
    {
        uint length = BinaryPrimitives.ReadUInt32BigEndian(source[at..]);

        at += 4;

        byte[] value = source.Slice(at, (int)length).ToArray();

        at += (int)length;

        return value;
    }

    /// <summary>
    /// A real RSA identity: a real key pair, a real public blob, and real signatures.
    ///
    /// <para>RSA rather than ed25519 because .NET has no ed25519 — and it turns out to be the better
    /// choice anyway: RSA is the one type where the agent protocol carries flags, so signing through
    /// this exercises the part of the client most likely to be wrong. A server that has refused
    /// SHA-1 signatures for years will reject anything signed under the legacy name.</para>
    ///
    /// <para>The key is generated per agent and never written anywhere, which is also what makes it
    /// a reasonable stand-in for a token: nothing outside this object can read it.</para>
    /// </summary>
    private sealed class RsaIdentity : IDisposable
    {
        private readonly RSA _key = RSA.Create(3072);

        internal RsaIdentity(string comment)
        {
            Comment = comment;

            RSAParameters parameters = _key.ExportParameters(includePrivateParameters: false);
            List<byte> blob = [];

            WriteString(blob, "ssh-rsa"u8);
            WriteMpint(blob, parameters.Exponent!);
            WriteMpint(blob, parameters.Modulus!);

            Blob = [.. blob];
        }

        internal string Comment { get; }

        internal byte[] Blob { get; }

        /// <summary>The line an <c>authorized_keys</c> wants, so a real server can trust this key.</summary>
        internal string AuthorizedKey => $"ssh-rsa {Convert.ToBase64String(Blob)} {Comment}";

        /// <summary>
        /// A real signature under whichever hash the flags asked for, wrapped the way the protocol
        /// carries one.
        /// </summary>
        internal byte[] Sign(byte[] data, uint flags)
        {
            (string name, HashAlgorithmName hash) = flags switch
            {
                4 => ("rsa-sha2-512", HashAlgorithmName.SHA512),
                2 => ("rsa-sha2-256", HashAlgorithmName.SHA256),
                _ => ("ssh-rsa", HashAlgorithmName.SHA1),
            };

            List<byte> wrapped = [];

            WriteString(wrapped, Encoding.ASCII.GetBytes(name));
            WriteString(wrapped, _key.SignData(data, hash, RSASignaturePadding.Pkcs1));

            return [.. wrapped];
        }

        /// <summary>
        /// An SSH <c>mpint</c>: big-endian, minimal, and with a leading zero where the top bit is
        /// set — without which a modulus reads as a negative number and no server accepts the key.
        /// </summary>
        private static void WriteMpint(List<byte> destination, byte[] value)
        {
            int first = 0;

            while (first < value.Length - 1 && value[first] == 0)
            {
                first++;
            }

            ReadOnlySpan<byte> trimmed = value.AsSpan(first);

            if ((trimmed[0] & 0x80) != 0)
            {
                WriteUInt32(destination, (uint)trimmed.Length + 1);
                destination.Add(0);
                destination.AddRange(trimmed);

                return;
            }

            WriteString(destination, trimmed);
        }

        public void Dispose() => _key.Dispose();
    }
}
