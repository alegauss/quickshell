using System.Buffers;
using System.Text;
using Quickshell.App;
using Quickshell.Terminal;
using Quickshell.Transport;

namespace Quickshell.Soak;

/// <summary>What one soaked session is doing, because a leak hides in the role nobody ran.</summary>
internal enum Role
{
    /// <summary>Connected, a shell open, nothing happening. Keepalive is the only traffic.</summary>
    Idle,

    /// <summary>A host printing continuously, parsed into a model the whole time.</summary>
    Printing,

    /// <summary>Opening and closing on a loop, which is where a per-session leak shows.</summary>
    Churning,

    /// <summary>A local forward with connections crossing it.</summary>
    Forwarding,

    /// <summary>Dropped and reconnected on a timer, which is the reconnect path under repetition.</summary>
    Flapping,

    /// <summary>A full-screen program redrawing on its own clock.</summary>
    FullScreen,
}

/// <summary>
/// One session under soak: it connects, does its role until told to stop, and counts what it did.
///
/// <para><b>Every role holds its own connection to the fixture.</b> Twenty of these is twenty real
/// SSH sessions against a real sshd, which is the only arrangement in which a leak that scales with
/// sessions can appear at all.</para>
///
/// <para><b>A failure is recorded and the role carries on.</b> Three days is long enough that a
/// transient refusal will happen, and a soak that stopped at the first one would measure the
/// network. What must not be forgiven is a failure that repeats, so the count is reported.</para>
/// </summary>
internal sealed class Soaked(Role role, int number, string host, int port, string user, string key,
                             bool parse = true, bool pipeline = true)
    : IAsyncDisposable
{
    private const int ReadSize = 64 * 1024;

    private readonly CancellationTokenSource _stopping = new();

    private SshNetTransport? _transport;
    private IPtyChannel? _shell;
    private SessionPipeline? _pipeline;
    private LocalForward? _forward;
    private Task _running = Task.CompletedTask;
    private long _pipelined;
    private long _read;

    /// <summary>Which role this is.</summary>
    public Role Role { get; } = role;

    /// <summary>Its number among the sessions, for the log.</summary>
    public int Number { get; } = number;

    /// <summary>The model its output is parsed into, where its role parses any.</summary>
    public Emulator? Emulator { get; private set; }

    /// <summary>How many connections this role has made. A churning role makes many.</summary>
    public long Connections { get; private set; }

    /// <summary>
    /// How many bytes the host has sent it, whichever way it is reading.
    ///
    /// <para>The pipeline counts its own, and a session that has been reconnected has to keep what
    /// earlier connections moved — otherwise a churning role reports only its last few seconds.</para>
    /// </summary>
    public long Bytes =>
        _pipelined + (_pipeline?.Work.Bytes ?? 0) + _read;

    /// <summary>How many failures it has swallowed and carried on from.</summary>
    public long Failures { get; private set; }

    /// <summary>
    /// What the last failure said.
    ///
    /// <para><b>A count on its own was not enough, and the run that proved it reported "824 failures
    /// swallowed" against a fixture that had stopped — three days of nothing, and no clue in the
    /// report as to why.</b> A tool that carries on past failures has to say what it carried on
    /// past.</para>
    /// </summary>
    public string LastFailure { get; private set; } = string.Empty;

    /// <summary>Starts the role.</summary>
    public void Start() => _running = Task.Run(() => RunAsync(_stopping.Token));

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync().ConfigureAwait(false);

        try
        {
            await _running.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Stopping is how this ends.
        }

        await Close().ConfigureAwait(false);

        _stopping.Dispose();
    }

    private async Task RunAsync(CancellationToken stopping)
    {
        while (!stopping.IsCancellationRequested)
        {
            try
            {
                await OnceAsync(stopping).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception failure)
            {
                Failures++;
                LastFailure = $"{failure.GetType().Name}: {failure.Message}";

                // Long enough not to hammer a server that is genuinely refusing, short enough that
                // a three-day run is not spent waiting.
                await Task.Delay(TimeSpan.FromSeconds(5), stopping).ConfigureAwait(false);
            }
        }
    }

    /// <summary>One pass of the role. Roles that live forever return only when stopped.</summary>
    private async Task OnceAsync(CancellationToken stopping)
    {
        await Open(stopping).ConfigureAwait(false);

        switch (Role)
        {
            case Role.Idle:
                // Nothing but keepalive, which is the point: an idle session is the one whose cost
                // a user attributes to having the client open at all.
                await Task.Delay(Timeout.InfiniteTimeSpan, stopping).ConfigureAwait(false);

                break;

            case Role.Churning:
                await Task.Delay(TimeSpan.FromSeconds(20), stopping).ConfigureAwait(false);
                await Close().ConfigureAwait(false);

                break;

            case Role.Flapping:
                await Task.Delay(TimeSpan.FromSeconds(90), stopping).ConfigureAwait(false);

                // Disposed under the reader, which is what a dropped link looks like from here.
                await Close().ConfigureAwait(false);

                break;

            case Role.Forwarding:
                await ForwardAsync(stopping).ConfigureAwait(false);

                break;

            default:
                await ReadAsync(stopping).ConfigureAwait(false);

                break;
        }
    }

    private async Task Open(CancellationToken stopping)
    {
        await Close().ConfigureAwait(false);

        SshNetTransport transport = new()
        {
            // On for every role, so keepalive traffic is part of what is being soaked rather than
            // something the run quietly avoided.
            KeepAlive = TimeSpan.FromSeconds(30),
            Timeout = TimeSpan.FromSeconds(20),
        };

        SshCredential.PrivateKey credential = new(key);

        await transport.ConnectAsync(SshEndpoint.For(host, user, port), [credential], Trusting,
                                     stopping).ConfigureAwait(false);

        _transport = transport;
        Connections++;

        if (Role is Role.Printing or Role.FullScreen or Role.Idle)
        {
            Emulator ??= new Emulator(200, 50, scrollback: 2_000);

            _shell = await transport.OpenShellAsync(200, 50, stopping).ConfigureAwait(false);

            string command = Role switch
            {
                // A real curses application repainting on its own clock, which is the case the
                // parser and the damage model are least exercised by anything else.
                Role.FullScreen => "TERM=xterm-256color top -b -d 1\n",

                // Continuous output of real bytes rather than a generated pattern.
                Role.Printing => "while :; do cat /srv/big.txt; done\n",

                _ => string.Empty,
            };

            if (command.Length > 0)
            {
                await _shell.WriteAsync(Encoding.ASCII.GetBytes(command), stopping)
                            .ConfigureAwait(false);
            }

            if (pipeline)
            {
                // The arrangement a real client reads through: a bounded queue of 64 reads, and a
                // reader that waits when it is full so the wait reaches the far end's flow control.
                _pipeline = SessionPipeline.Start(_shell, Emulator);
            }
        }
        else if (Role == Role.Forwarding)
        {
            // Onto the fixture's own sshd, which is a service that is certainly listening and
            // answers with a banner, so traffic really crosses.
            _forward = LocalForward.Open(transport, "127.0.0.1", 22);
        }
    }

    /// <summary>Reads whatever the host is sending, into the model, until stopped.</summary>
    private async Task ReadAsync(CancellationToken stopping)
    {
        if (_pipeline is not null)
        {
            // The pipeline owns the reading. Waiting on it is the whole of this role's work, and
            // what it moved is counted by the pipeline itself.
            await _pipeline.Completed.WaitAsync(stopping).ConfigureAwait(false);

            return;
        }

        if (_shell is null)
        {
            return;
        }

        byte[] buffer = ArrayPool<byte>.Shared.Rent(ReadSize);

        try
        {
            while (!stopping.IsCancellationRequested)
            {
                int read = await _shell.ReadAsync(buffer, stopping).ConfigureAwait(false);

                if (read == 0)
                {
                    return;
                }

                // Read and dropped where parsing is off, which is what isolates a layer: the bytes
                // still cross the network and the channel, and nothing above the transport sees
                // them. A counter that rises in both arrangements is not the parser's.
                if (parse)
                {
                    Emulator?.Feed(buffer.AsSpan(0, read));
                }

                // The reply the terminal owes is drained rather than left to accumulate, which is
                // itself a place a long run would grow.
                if (parse && Emulator is { Reply.IsEmpty: false })
                {
                    byte[] reply = Emulator.Reply.ToArray();

                    Emulator.ClearReply();

                    await _shell.WriteAsync(reply, stopping).ConfigureAwait(false);
                }

                _read += read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>Opens a connection through the forward every few seconds and reads the banner.</summary>
    private async Task ForwardAsync(CancellationToken stopping)
    {
        while (!stopping.IsCancellationRequested && _forward is { IsOpen: true })
        {
            using System.Net.Sockets.TcpClient client = new();

            await client.ConnectAsync("127.0.0.1", _forward.BoundPort, stopping)
                        .ConfigureAwait(false);

            byte[] banner = new byte[256];

            int read = await client.GetStream().ReadAsync(banner, stopping).ConfigureAwait(false);

            _read += read;

            await Task.Delay(TimeSpan.FromSeconds(5), stopping).ConfigureAwait(false);
        }
    }

    private async ValueTask Close()
    {
        if (_pipeline is not null)
        {
            // Carried across, so a churning role's total is what it has ever moved rather than what
            // its last connection did.
            _pipelined += _pipeline.Work.Bytes;

            await _pipeline.DisposeAsync().ConfigureAwait(false);
            _pipeline = null;
        }

        if (_forward is not null)
        {
            await _forward.DisposeAsync().ConfigureAwait(false);
            _forward = null;
        }

        if (_shell is not null)
        {
            await _shell.DisposeAsync().ConfigureAwait(false);
            _shell = null;
        }

        if (_transport is not null)
        {
            await _transport.DisposeAsync().ConfigureAwait(false);
            _transport = null;
        }
    }

    /// <summary>
    /// The fixture's key is known and this is a soak, so every key is accepted.
    ///
    /// <para>Said out loud because it would be the wrong answer anywhere else: what is being measured
    /// here is what three days of connecting costs, not whether the host-key store works — that is
    /// Block B's, tested against a changed fingerprint rather than against a soak.</para>
    /// </summary>
    private static ValueTask<SshHostKeyVerdict> Trusting(SshEndpoint _, SshHostKey __,
                                                         CancellationToken ___) =>
        ValueTask.FromResult(SshHostKeyVerdict.Accept);
}
