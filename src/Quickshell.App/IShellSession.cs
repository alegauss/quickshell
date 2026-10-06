using Quickshell.Terminal;

namespace Quickshell.App;

/// <summary>
/// Whatever is behind a pane: a shell on this machine or one on another, carried into the same
/// model by the same pipeline (QS126).
///
/// <para>The pipeline takes an <see cref="Quickshell.Transport.IPtyChannel"/> and does not care which
/// side of a network it is, so a pane does not either. What differs is how the channel is opened
/// and what has to be closed after it, and that is all this hides.</para>
/// </summary>
public interface IShellSession : IAsyncDisposable
{
    /// <summary>The three stages carrying output into the model and keystrokes back out.</summary>
    SessionPipeline Pipeline { get; }
}

/// <summary>Opens what a pane will run, once the pane knows its grid.</summary>
/// <param name="emulator">The model its output is parsed into.</param>
/// <param name="damage">The signal the pane's render loop is asleep on.</param>
/// <param name="columns">The grid the pane settled on.</param>
/// <param name="rows">Its rows.</param>
/// <param name="cancellationToken">Gives up.</param>
public delegate Task<IShellSession> ShellOpener(Emulator emulator, DamageSignal damage, int columns,
                                                 int rows, CancellationToken cancellationToken);
