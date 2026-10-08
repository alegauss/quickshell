using Quickshell.Terminal;
using Quickshell.Transport;

namespace Quickshell.App;

/// <summary>
/// Whatever is behind a pane: a shell on this machine or one on another, carried into the same
/// model by the same pipeline (QS126).
///
/// <para>The pipeline takes an <see cref="IPtyChannel"/> and does not care which side of a network
/// it is, so a pane does not either. What differs is how the channel is opened and what has to be
/// closed after it, and that is all this hides.</para>
///
/// <para><b>What a pane asks of it, and not the pipeline itself (QS220).</b> A session that
/// reconnects has a new pipeline for every connection, so a pane holding the first would type into
/// a channel that has gone. These are the four things a pane does with a session, each answered by
/// whichever pipeline is live.</para>
/// </summary>
public interface IShellSession : IAsyncDisposable
{
    /// <summary>Sends what was typed to the shell that is there now; nothing where none is.</summary>
    ValueTask TypeAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default);

    /// <summary>Tells the model and the far end the grid changed.</summary>
    void Resize(int columns, int rows);

    /// <summary>How many lines of history the model keeps, kept across every reconnect.</summary>
    void KeepScrollback(int lines);

    /// <summary>
    /// Completes when the session is over for good — the shell exited, the attempts ran out, or the
    /// link went with nothing set to bring it back — carrying how it ended.
    /// </summary>
    Task<PtyExit> Ended { get; }
}

/// <summary>Opens what a pane will run, once the pane knows its grid.</summary>
/// <param name="emulator">The model its output is parsed into.</param>
/// <param name="damage">The signal the pane's render loop is asleep on.</param>
/// <param name="columns">The grid the pane settled on.</param>
/// <param name="rows">Its rows.</param>
/// <param name="cancellationToken">Gives up.</param>
public delegate Task<IShellSession> ShellOpener(Emulator emulator, DamageSignal damage, int columns,
                                                 int rows, CancellationToken cancellationToken);
