using Quickshell.Terminal;
using Quickshell.Transport;

namespace Quickshell.App;

/// <summary>
/// A shell on this machine, behind the window's terminal.
///
/// <para><b>This is what makes the window show something.</b> Every other piece of QS116 joins two
/// parts that were already built; this one supplies the bytes they were built to carry. A client with
/// a swapchain, a loop and a keyboard and no producer at all is a black rectangle that repaints
/// correctly.</para>
///
/// <para><b>Local rather than remote, and not as a placeholder.</b> A pseudo-console has no network
/// to be slow, no host key to trust and no password to get wrong, so a fault in what a user sees is a
/// rendering or a parsing fault and never a transport one. QS126 is where the same three lines below
/// are handed an SSH channel instead — the pipeline takes an <see cref="IPtyChannel"/> and does not
/// care which side of a network it is.</para>
///
/// <para><b>The command line is <c>COMSPEC</c> and there is no setting for it.</b> That variable is
/// Windows' own answer to which command processor this user has, so a key here would be a second
/// answer to a question already answered. QS50 is where the settings surface exists and where one
/// belongs if it turns out to be wanted.</para>
/// </summary>
public sealed class LocalSession : IAsyncDisposable
{
    private readonly ConPtyChannel _channel;

    private LocalSession(ConPtyChannel channel, SessionPipeline pipeline)
    {
        _channel = channel;

        Pipeline = pipeline;
    }

    /// <summary>
    /// What a session with nothing else asked for runs: this user's command processor.
    /// </summary>
    public static string Shell =>
        Environment.GetEnvironmentVariable("COMSPEC") is { Length: > 0 } processor
            ? processor
            : "cmd.exe";

    /// <summary>The three stages carrying its output into the model and keystrokes back out.</summary>
    public SessionPipeline Pipeline { get; }

    /// <summary>The identifier of the program on the other end, for a crash report to name.</summary>
    public int ProcessId => _channel.ProcessId;

    /// <summary>
    /// Starts a shell and puts the pipeline over it.
    /// </summary>
    /// <param name="emulator">The model its output is parsed into.</param>
    /// <param name="damage">The signal the pane's render loop is already asleep on.</param>
    /// <param name="columns">The grid the pane settled on.</param>
    /// <param name="rows">Its rows.</param>
    /// <param name="commandLine">What to run, or null for <see cref="Shell"/>.</param>
    /// <param name="cancellationToken">Gives up on the pseudo-console's pipes connecting.</param>
    public static async Task<LocalSession> OpenAsync(
        Emulator emulator,
        DamageSignal damage,
        int columns,
        int rows,
        string? commandLine = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(emulator);
        ArgumentNullException.ThrowIfNull(damage);

        // Clamped rather than refused: a window narrower than one cell is a window a user can make,
        // and a session that declined to start over it would be a terminal that failed to open
        // because somebody dragged an edge too far.
        ConPtyChannel channel = await ConPtyChannel
            .StartAsync(commandLine ?? Shell, Math.Max(1, columns), Math.Max(1, rows),
                        cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return new LocalSession(channel, SessionPipeline.Start(channel, emulator, damage: damage));
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        // The pipeline first: its loops are the things reading and writing the channel, and a channel
        // closed under them turns an ordinary shutdown into three caught exceptions.
        await Pipeline.DisposeAsync().ConfigureAwait(false);
        await _channel.DisposeAsync().ConfigureAwait(false);
    }
}
