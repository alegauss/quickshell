using System.Globalization;
using System.Text.Json.Serialization;

namespace Quickshell.Transport;

/// <summary>Which way a forward carries.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ForwardKind>))]
public enum ForwardKind
{
    /// <summary>A port here reaching a host and port on the remote network: OpenSSH's <c>-L</c>.</summary>
    Local,

    /// <summary>A port on the server reaching a host and port from here: OpenSSH's <c>-R</c>.</summary>
    Remote,

    /// <summary>A SOCKS proxy here into the whole remote network: OpenSSH's <c>-D</c>.</summary>
    Dynamic,
}

/// <summary>
/// One forward as a session keeps it: what to listen on and where it goes (QS69).
///
/// <para>Saved with the session rather than made by hand each time, so it travels with the store
/// and starts when the session does.</para>
/// </summary>
/// <param name="Kind">Which way it carries.</param>
/// <param name="ListenPort">The port listened on — here for local and dynamic, on the server for
/// remote. Zero lets whichever side listens choose.</param>
/// <param name="TargetHost">Where it goes; unused for a dynamic forward.</param>
/// <param name="TargetPort">The port there; unused for a dynamic forward.</param>
public sealed record ForwardSpec(ForwardKind Kind, int ListenPort, string? TargetHost = null, int TargetPort = 0)
{
    /// <summary>The address a local or dynamic forward accepts on; loopback where it says nothing.</summary>
    public string? Bind { get; init; }

    /// <summary>Written as OpenSSH's command line writes it, which is how a user recognises it.</summary>
    public override string ToString() => Kind switch
    {
        ForwardKind.Local => string.Create(CultureInfo.InvariantCulture, $"-L {ListenPort}:{TargetHost}:{TargetPort}"),
        ForwardKind.Remote => string.Create(CultureInfo.InvariantCulture, $"-R {ListenPort}:{TargetHost}:{TargetPort}"),
        _ => string.Create(CultureInfo.InvariantCulture, $"-D {ListenPort}"),
    };
}

/// <summary>A forward that started, and the port it actually holds.</summary>
/// <param name="Spec">What was asked for.</param>
/// <param name="BoundPort">The port listening, which is the chosen one where zero was asked for.</param>
public readonly record struct StartedForward(ForwardSpec Spec, int BoundPort);

/// <summary>
/// One running forward as the forwards view shows it (QS70): what it is, where it listens, and how
/// much it is doing.
/// </summary>
/// <param name="Spec">What was asked for.</param>
/// <param name="BoundPort">The port listening.</param>
/// <param name="Carrying">Connections carried right now, or null where the library keeps that to itself (a remote forward).</param>
/// <param name="Connections">Connections carried since it started.</param>
public readonly record struct ForwardActivity(ForwardSpec Spec, int BoundPort, int? Carrying, long Connections)
{
    /// <summary>
    /// What to paste into the tool that uses it: this machine's address for a local or SOCKS
    /// forward, the server's port for a remote one.
    /// </summary>
    public string Address => Spec.Kind == ForwardKind.Remote
        ? string.Create(CultureInfo.InvariantCulture, $"server port {BoundPort}")
        : string.Create(CultureInfo.InvariantCulture, $"{(Spec.Bind is { Length: > 0 } bind ? bind : "127.0.0.1")}:{BoundPort}");
}

/// <summary>A forward that did not start, and why in the words a user is shown.</summary>
/// <param name="Spec">What was asked for.</param>
/// <param name="Reason">What happened.</param>
/// <param name="Remedy">What would fix it.</param>
public readonly record struct FailedForward(ForwardSpec Spec, string Reason, string Remedy);

/// <summary>
/// Every forward a session keeps, started together and closed together (QS69).
///
/// <para><b>One that fails does not stop the rest, or the session.</b> The terminal is the primary
/// thing, and a port somebody else holds must never cost a user their shell — so each is started on
/// its own, and what did not start is reported beside what did rather than thrown.</para>
///
/// <para><b>Closed with what opened it.</b> Every listener here goes when this does and when the
/// connection does: a listener that outlives its session is what makes the next start fail.</para>
///
/// <para>Each can be stopped and started again on its own, which is what somebody debugging a port
/// conflict needs and nothing else.</para>
/// </summary>
public sealed class SessionForwards : IAsyncDisposable
{
    private readonly SshNetTransport _over;
    private readonly Dictionary<ForwardSpec, IAsyncDisposable> _running = [];
    private readonly Dictionary<ForwardSpec, StartedForward> _started = [];
    private readonly Dictionary<ForwardSpec, FailedForward> _failed = [];
    private readonly Lock _guard = new();

    private SessionForwards(SshNetTransport over) => _over = over;

    /// <summary>What is running, in the order asked for.</summary>
    public IReadOnlyList<StartedForward> Started
    {
        get
        {
            lock (_guard)
            {
                return [.. _started.Values];
            }
        }
    }

    /// <summary>How many are running, read without building a list: what the window's title counts (QS70).</summary>
    public int Count
    {
        get
        {
            lock (_guard)
            {
                return _started.Count;
            }
        }
    }

    /// <summary>What could not start, and why.</summary>
    public IReadOnlyList<FailedForward> Failed
    {
        get
        {
            lock (_guard)
            {
                return [.. _failed.Values];
            }
        }
    }

    /// <summary>Every running forward with what it is carrying, read now (QS70).</summary>
    public IReadOnlyList<ForwardActivity> Activity()
    {
        lock (_guard)
        {
            return [.. _started.Values.Select(started => _running[started.Spec] switch
            {
                LocalForward local => new ForwardActivity(started.Spec, started.BoundPort, local.Carrying, local.Connections),
                DynamicForward socks => new ForwardActivity(started.Spec, started.BoundPort, socks.Carrying, socks.Connections),
                RemoteForward remote => new ForwardActivity(started.Spec, started.BoundPort, null, remote.Connections),
                _ => new ForwardActivity(started.Spec, started.BoundPort, null, 0),
            })];
        }
    }

    /// <summary>Starts every forward over a connected session, each on its own.</summary>
    /// <param name="over">The connection, direct or through a chain.</param>
    /// <param name="forwards">What the session keeps.</param>
    /// <exception cref="SshException">There is no connection to carry anything.</exception>
    public static SessionForwards Start(ISshTransport over, IEnumerable<ForwardSpec> forwards)
    {
        ArgumentNullException.ThrowIfNull(over);
        ArgumentNullException.ThrowIfNull(forwards);

        SshNetTransport carrier = Carrier(over)
            ?? throw new SshException(SshFailureKind.Dropped, "There is no connection to carry a forward.",
                                      "The session is not open.");

        SessionForwards set = new(carrier);

        foreach (ForwardSpec forward in forwards)
        {
            set.StartOne(forward);
        }

        return set;
    }

    /// <summary>Starts one again — after it was stopped, or after it failed and its port came free.</summary>
    /// <returns>Whether it is running now.</returns>
    public async ValueTask<bool> StartAsync(ForwardSpec forward)
    {
        ArgumentNullException.ThrowIfNull(forward);

        await StopAsync(forward).ConfigureAwait(false);

        return StartOne(forward);
    }

    /// <summary>Stops one, leaving the others and the session alone.</summary>
    public async ValueTask StopAsync(ForwardSpec forward)
    {
        ArgumentNullException.ThrowIfNull(forward);

        IAsyncDisposable? running;

        lock (_guard)
        {
            _running.Remove(forward, out running);
            _started.Remove(forward);
            _failed.Remove(forward);
        }

        if (running is not null)
        {
            await running.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Closes every listener.</summary>
    public async ValueTask DisposeAsync()
    {
        IAsyncDisposable[] all;

        lock (_guard)
        {
            all = [.. _running.Values];
            _running.Clear();
            _started.Clear();
        }

        foreach (IAsyncDisposable forward in all)
        {
            await forward.DisposeAsync().ConfigureAwait(false);
        }
    }

    private bool StartOne(ForwardSpec forward)
    {
        ForwardBinding binding = forward.Bind is { Length: > 0 } bind ? ForwardBinding.To(bind) : ForwardBinding.Loopback;

        try
        {
            (IAsyncDisposable running, int bound) = forward.Kind switch
            {
                ForwardKind.Local => Bound(LocalForward.Open(_over, forward.TargetHost!, forward.TargetPort,
                                                             forward.ListenPort, binding)),
                ForwardKind.Remote => Bound(RemoteForward.Open(_over, forward.ListenPort, forward.TargetHost!,
                                                               forward.TargetPort)),
                _ => Bound(DynamicForward.Open(_over, forward.ListenPort, binding)),
            };

            lock (_guard)
            {
                _running[forward] = running;
                _started[forward] = new StartedForward(forward, bound);
            }

            return true;
        }
        catch (SshException refused)
        {
            lock (_guard)
            {
                _failed[forward] = new FailedForward(forward, refused.Message, refused.Remedy);
            }

            return false;
        }
    }

    private static (IAsyncDisposable, int) Bound(LocalForward forward) => (forward, forward.BoundPort);

    private static (IAsyncDisposable, int) Bound(RemoteForward forward) => (forward, forward.BoundPort);

    private static (IAsyncDisposable, int) Bound(DynamicForward forward) => (forward, forward.BoundPort);

    /// <summary>The connection a forward rides on: the transport itself, or the last hop of a chain.</summary>
    internal static SshNetTransport? Carrier(ISshTransport over) => over switch
    {
        SshNetTransport direct => direct,
        SshChain chain => chain.Carrier,
        _ => null,
    };
}
