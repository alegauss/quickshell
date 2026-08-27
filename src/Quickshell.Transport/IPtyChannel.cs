namespace Quickshell.Transport;

/// <summary>
/// Why a channel ended: an exit code where the program on the other end exited, and a sentence
/// where it did not get that far.
/// </summary>
/// <param name="Code">The program's exit code, or null where there was never a program.</param>
/// <param name="Reason">
/// What happened, in words a user can read. Empty where <paramref name="Code"/> says it all.
/// </param>
public readonly record struct PtyExit(int? Code, string Reason)
{
    /// <summary>A program that exited, with the code it gave.</summary>
    public static PtyExit Exited(int code) => new(code, string.Empty);

    /// <summary>A channel that ended without a program's exit code, and why.</summary>
    public static PtyExit Failed(string reason) => new(null, reason);

    /// <summary>Whether this is an ordinary exit rather than a failure.</summary>
    public bool IsExit => Code is not null;
}

/// <summary>
/// The one thing everything above it knows about the far end: four members, and no hint of how the
/// bytes get there.
///
/// <para><b>This interface is the whole reason SSH is a later implementation rather than a later
/// rewrite.</b> The parser, the buffer, the renderer and the input map are written against these
/// four members; a channel that reaches a remote machine and a channel that reaches a shell on this
/// one are the same shape, so nothing above has to learn the difference.</para>
///
/// <para><b>Reading is asynchronous and never spins.</b> A channel that polled would burn a core to
/// discover that a shell is idle, which is the opposite of the footprint this client exists to
/// argue for.</para>
/// </summary>
public interface IPtyChannel : IAsyncDisposable, IDisposable
{
    /// <summary>How many columns and rows the far end currently believes it has.</summary>
    (int Columns, int Rows) Size { get; }

    /// <summary>
    /// Completes when the far end is gone, carrying its exit code or the reason there is none.
    ///
    /// <para>A task rather than an event, because a session's shutdown is a thing to await once and
    /// not a thing to subscribe to: an event with no subscriber at the moment it fires is how a
    /// closed session stays on screen.</para>
    ///
    /// <para><b>This says the program is gone, not that its output has all arrived.</b> The two are
    /// different moments and a session loop needs both: keep reading until
    /// <see cref="ReadAsync"/> answers zero, and read this to find out what the program's exit meant.
    /// A loop that stopped reading the instant this completed would drop the last thing the program
    /// printed.</para>
    /// </summary>
    Task<PtyExit> Closed { get; }

    /// <summary>
    /// Reads whatever has arrived, waiting where nothing has.
    /// </summary>
    /// <returns>How many bytes landed in the buffer, or zero at end of stream.</returns>
    ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default);

    /// <summary>Writes what the user typed, or what the terminal owes the host as a reply.</summary>
    ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default);

    /// <summary>
    /// Tells the far end the window changed size, which is what makes a full-screen program redraw.
    /// </summary>
    void Resize(int columns, int rows);
}
