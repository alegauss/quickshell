using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Quickshell.Transport;

/// <summary>
/// A shell on this machine, behind the same four members an SSH channel will be behind.
///
/// <para><b>This exists before SSH on purpose.</b> It gives the emulator a real producer of VT bytes
/// with no network latency and no authentication, which makes a rendering fault, a parsing fault and
/// a transport fault three distinguishable things from the first day rather than one confusing thing
/// three months in. Every conformance run and every benchmark has something real to run against.</para>
///
/// <para><b>It is also a feature.</b> A local shell in a terminal this fast is the cheapest thing
/// this client will ever ship, and users of the incumbent already expect one to be there.</para>
///
/// <para><b>Handle lifetime is the whole difficulty, and it is not subtle in its consequences.</b>
/// Four handles exist for two pipes; two of them are handed to the pseudo-console, which duplicates
/// them, and <em>this</em> side must let go of its copies at once. Keeping the write end of the input
/// pipe is the classic version: the program on the other end never sees end of file, so a shell that
/// has been told to exit waits instead, and the session hangs on close rather than closing. The
/// order in <see cref="DisposeAsync"/> is the answer to that and is not arbitrary.</para>
///
/// <para><b>The pipes are named, not anonymous.</b> An anonymous pipe on Windows cannot do overlapped
/// I/O, so reading from one means a thread blocked for the life of the session — a thread per open
/// tab, doing nothing. A named pipe with a unique name costs one GUID and gives real asynchronous
/// reads.</para>
/// </summary>
public sealed class ConPtyChannel : IPtyChannel
{
    /// <summary>How long a program gets to leave after being told its console has gone.</summary>
    public static readonly TimeSpan GraceOnClose = TimeSpan.FromSeconds(2);

    private readonly NamedPipeServerStream _toChild;
    private readonly NamedPipeServerStream _fromChild;
    private readonly TaskCompletionSource<PtyExit> _closed =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly SemaphoreSlim _writing = new(1, 1);
    private readonly Lock _resizing = new();

    private nint _console;
    private nint _process;
    private RegisteredWaitHandle? _wait;
    private ManualResetEvent? _exited;
    private int _columns;
    private int _rows;
    private bool _disposed;

    private ConPtyChannel(
        NamedPipeServerStream toChild,
        NamedPipeServerStream fromChild,
        int columns,
        int rows)
    {
        _toChild = toChild;
        _fromChild = fromChild;
        _columns = columns;
        _rows = rows;
    }

    /// <inheritdoc/>
    public (int Columns, int Rows) Size
    {
        get
        {
            lock (_resizing)
            {
                return (_columns, _rows);
            }
        }
    }

    /// <inheritdoc/>
    public Task<PtyExit> Closed => _closed.Task;

    /// <summary>The process identifier of the program on the other end, for diagnostics.</summary>
    public int ProcessId { get; private set; }

    /// <summary>Whether the program on the other end is still running.</summary>
    public bool IsRunning => !_closed.Task.IsCompleted;

    /// <summary>
    /// Starts a program with a pseudo-console of a given size.
    /// </summary>
    /// <param name="commandLine">What to run, as a command line. Not a shell string: it is passed to
    /// <c>CreateProcess</c> as written.</param>
    /// <param name="columns">Columns the program is told it has.</param>
    /// <param name="rows">Rows.</param>
    /// <param name="workingDirectory">Where to start it, or null for this process's directory.</param>
    /// <param name="cancellationToken">Gives up on the pipes connecting.</param>
    /// <exception cref="Win32Exception">Where Windows refused, carrying what it said.</exception>
    /// <remarks>
    /// <para><b>Asynchronous, and it has to be.</b> Two named pipes need connecting, and the
    /// connection completes on the thread pool. A synchronous factory blocked the calling thread
    /// until that happened — which works for one channel and deadlocks for fifteen at once, because
    /// the threads waiting for the connection are the threads that would complete it. A test host
    /// running its cases in parallel found it immediately; a user opening a window of tabs would have
    /// found it later.</para>
    /// </remarks>
    public static async Task<ConPtyChannel> StartAsync(
        string commandLine,
        int columns,
        int rows,
        string? workingDirectory = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandLine);
        ArgumentOutOfRangeException.ThrowIfLessThan(columns, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(rows, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(columns, short.MaxValue);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(rows, short.MaxValue);

        string name = $"quickshell-{Guid.NewGuid():N}";

        // Asynchronous on this side, because this side is the one that waits. The child's ends are
        // ordinary handles: the console host does its own I/O on them.
        NamedPipeServerStream toChild = new(
            name + "-in", PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        NamedPipeServerStream fromChild = new(
            name + "-out", PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

        ConPtyChannel channel = new(toChild, fromChild, columns, rows);

        try
        {
            await channel.OpenAsync(name, commandLine, workingDirectory, cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            await channel.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        return channel;
    }

    /// <inheritdoc/>
    public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        _fromChild.ReadAsync(buffer, cancellationToken);

    /// <inheritdoc/>
    public async ValueTask WriteAsync(
        ReadOnlyMemory<byte> bytes,
        CancellationToken cancellationToken = default)
    {
        if (bytes.IsEmpty)
        {
            return;
        }

        // One writer at a time, because a keystroke and a terminal's reply to the host are two
        // producers and an interleaved escape sequence is a sequence neither of them sent.
        await _writing.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await _toChild.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await _toChild.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writing.Release();
        }
    }

    /// <inheritdoc/>
    public void Resize(int columns, int rows)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(columns, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(rows, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(columns, short.MaxValue);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(rows, short.MaxValue);

        lock (_resizing)
        {
            if (_console == 0)
            {
                return;
            }

            int result = PtyNative.ResizePseudoConsole(
                _console, new PtyNative.Coord { X = (short)columns, Y = (short)rows });

            if (result != 0)
            {
                throw new Win32Exception(result, "the pseudo-console refused the new size");
            }

            _columns = columns;
            _rows = rows;
        }
    }

    /// <summary>
    /// Closes, for a caller with no asynchronous context to close from.
    ///
    /// <para>Written out rather than wrapping <see cref="DisposeAsync"/>: waiting on that from a
    /// synchronous method is the shape that deadlocks under a saturated thread pool, and a shutdown
    /// path is the last place to put one.</para>
    /// </summary>
    public void Dispose()
    {
        if (!Begin())
        {
            return;
        }

        CloseQuietly(_toChild);
        CloseQuietly(_fromChild);
        ReleaseConsole();

        if (_process != 0 && !_closed.Task.Wait(GraceOnClose))
        {
            End();
        }

        Release();
    }

    /// <summary>
    /// Closes in the one order that does not hang.
    ///
    /// <para>The input pipe goes first, because that is the end of file a shell is waiting for. The
    /// pseudo-console goes second and blocks until the console host has flushed. The output pipe goes
    /// after that, so nothing the program wrote on its way out is thrown away. And a program that has
    /// not left by then is ended rather than left behind — a channel that leaks a process is the
    /// failure this task is read against.</para>
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (!Begin())
        {
            return;
        }

        // End of file for the program's own input. Without this a shell told to exit waits here.
        await CloseQuietlyAsync(_toChild).ConfigureAwait(false);

        // The output goes before the console, and only on this path. Releasing the console makes the
        // console host flush what it still owes into that pipe, and a caller that has reached Dispose
        // has stopped reading it — so flushing into it could block on a pipe nobody will drain. On
        // the ordinary path the program exits first and the flush happens in ReleaseConsole with the
        // reader still there, which is what makes a read loop see the program's last words.
        await CloseQuietlyAsync(_fromChild).ConfigureAwait(false);

        ReleaseConsole();

        if (_process != 0)
        {
            await Settle().ConfigureAwait(false);
        }

        Release();
    }

    /// <summary>Whether this call is the one that closes, which only one of them is.</summary>
    private bool Begin()
    {
        if (_disposed)
        {
            return false;
        }

        _disposed = true;
        return true;
    }

    /// <summary>What both shutdown paths let go of once the program is settled.</summary>
    private void Release()
    {
        _wait?.Unregister(null);
        _wait = null;
        _exited?.Dispose();
        _exited = null;

        if (_process != 0)
        {
            PtyNative.CloseHandle(_process);
            _process = 0;
        }

        _writing.Dispose();

        _closed.TrySetResult(PtyExit.Failed("the channel was closed before the program exited"));
    }

    /// <summary>
    /// Ends a program that would not leave. The lesser of two wrongs: the alternative is a process
    /// nobody can see and nobody will close.
    /// </summary>
    private void End()
    {
        PtyNative.TerminateProcess(_process, 1);
        _closed.TrySetResult(PtyExit.Failed("the program did not exit when its console closed, and was ended"));
    }

    /// <summary>
    /// Everything that can fail, in the order Windows requires it.
    /// </summary>
    private async Task OpenAsync(
        string name,
        string commandLine,
        string? workingDirectory,
        CancellationToken cancellationToken)
    {
        // The child's ends. Opened before the pseudo-console because it is handed them, and closed
        // immediately afterwards because it has duplicated them by then.
        using NamedPipeClientStream childInput = new(
            ".", name + "-in", PipeDirection.In, PipeOptions.Asynchronous);
        using NamedPipeClientStream childOutput = new(
            ".", name + "-out", PipeDirection.Out, PipeOptions.Asynchronous);

        await Task.WhenAll(
                _toChild.WaitForConnectionAsync(cancellationToken),
                _fromChild.WaitForConnectionAsync(cancellationToken),
                childInput.ConnectAsync(cancellationToken),
                childOutput.ConnectAsync(cancellationToken))
            .ConfigureAwait(false);

        PtyNative.Coord size = new() { X = (short)_columns, Y = (short)_rows };

        int created = PtyNative.CreatePseudoConsole(
            size,
            childInput.SafePipeHandle.DangerousGetHandle(),
            childOutput.SafePipeHandle.DangerousGetHandle(),
            0,
            out _console);

        if (created != 0)
        {
            throw new Win32Exception(created, "the pseudo-console could not be created");
        }

        StartChild(commandLine, workingDirectory);
    }

    private void StartChild(string commandLine, string? workingDirectory)
    {
        nint size = 0;
        PtyNative.InitializeProcThreadAttributeList(0, 1, 0, ref size);

        nint list = Marshal.AllocHGlobal(size);

        try
        {
            if (!PtyNative.InitializeProcThreadAttributeList(list, 1, 0, ref size))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "the attribute list could not be made");
            }

            nint console = _console;

            if (!PtyNative.UpdateProcThreadAttribute(
                    list, 0, PtyNative.AttributePseudoConsole, console, IntPtr.Size, 0, 0))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(), "the pseudo-console could not be attached to the process");
            }

            PtyNative.StartupInfoEx startup = default;
            startup.StartupInfo.Size = Marshal.SizeOf<PtyNative.StartupInfoEx>();
            startup.AttributeList = list;

            // The line that took the longest to find, and the platform's documentation says not to
            // write it: STARTF_USESTDHANDLES is described as mutually exclusive with a pseudo-console.
            // Measured, it is the opposite of optional.
            //
            // Windows copies this process's standard handles into the child whatever
            // bInheritHandles says. The child attaches to the pseudo-console correctly - it reports
            // the pseudo-console's own size when asked - and then writes its output to the inherited
            // handle instead of to the console it is attached to. So a shell run from a parent whose
            // output is redirected, which is every test host and every build agent, sends its output
            // straight past the terminal: the pseudo-console emits its own setup sequences and
            // nothing else, and the shell's text appears in the log of whatever launched the client.
            //
            // With the flag set and all three handles left null, the child finds none to inherit and
            // opens CONIN$ and CONOUT$ itself - which are the pseudo-console's.
            startup.StartupInfo.Flags = PtyNative.UseStandardHandles;

            // CreateProcess writes into the command line it is given, so it gets a copy that may be
            // written into. A string literal handed to it is memory the runtime shares.
            char[] mutable = new char[commandLine.Length + 1];
            commandLine.CopyTo(mutable);

            bool started = PtyNative.CreateProcess(
                null,
                ref mutable[0],
                0,
                0,
                false,
                PtyNative.ExtendedStartupInfoPresent,
                0,
                workingDirectory,
                ref startup,
                out PtyNative.ProcessInformation process);

            if (!started)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"'{commandLine}' could not be started");
            }

            // The thread handle is of no use to anyone here, and holding it is a handle leaked for
            // the life of the session.
            PtyNative.CloseHandle(process.Thread);

            _process = process.Process;
            ProcessId = process.ProcessId;

            WatchForExit();
        }
        finally
        {
            PtyNative.DeleteProcThreadAttributeList(list);
            Marshal.FreeHGlobal(list);
        }
    }

    /// <summary>
    /// Waits for the program without a thread and without a poll.
    ///
    /// <para>The thread pool's own wait machinery watches the process handle; nothing here runs until
    /// the handle signals. A loop asking whether the shell has exited yet would be the footprint this
    /// client exists to argue against, in the one place nobody would look for it.</para>
    /// </summary>
    private void WatchForExit()
    {
        // Not owning the handle: this class closes it in DisposeAsync, and two owners is one
        // double close.
        _exited = new ManualResetEvent(false)
        {
            SafeWaitHandle = new SafeWaitHandle(_process, ownsHandle: false),
        };

        _wait = ThreadPool.RegisterWaitForSingleObject(
            _exited,
            (_, _) => Finish(),
            null,
            Timeout.Infinite,
            executeOnlyOnce: true);
    }

    /// <summary>
    /// The program has gone, so the console goes with it.
    ///
    /// <para><b>Releasing the console here is what lets a reader see end of file at all.</b> The
    /// output pipe's other end is held by the console host and not by the program, so the pipe stays
    /// open after the program exits — a read loop would wait for ever on a shell that finished
    /// seconds ago, and look exactly like a hung network. Releasing it makes the host flush its last
    /// bytes and let go, which is the end of file the loop is waiting for.</para>
    ///
    /// <para><b>The exit is published before the release, and the order is load-bearing.</b>
    /// Releasing blocks until the console host has flushed, and the host can only flush into a pipe
    /// somebody is draining. A caller that awaits <see cref="Closed"/> and only then stops reading —
    /// which is the obvious way to write a session loop — would be waiting for a flush that was
    /// waiting for it. So <see cref="Closed"/> means "the program is gone", the read reaching zero
    /// means "and its last words have arrived", and the two are separate answers to separate
    /// questions.</para>
    /// </summary>
    private void Finish()
    {
        PtyExit exit = _process != 0 && PtyNative.GetExitCodeProcess(_process, out int code)
            ? PtyExit.Exited(code)
            : PtyExit.Failed("the program ended and Windows would not say with what code");

        _closed.TrySetResult(exit);

        ReleaseConsole();
    }

    /// <summary>Lets go of the pseudo-console, once, whichever path gets here first.</summary>
    private void ReleaseConsole()
    {
        nint console;

        lock (_resizing)
        {
            console = _console;
            _console = 0;
        }

        if (console != 0)
        {
            // Outside the lock: this blocks until the console host has flushed, and a resize waiting
            // behind it would be a resize waiting on a shell's last line of output.
            PtyNative.ClosePseudoConsole(console);
        }
    }

    /// <summary>
    /// Gives the program its grace period, and ends it if it does not take it.
    /// </summary>
    private async Task Settle()
    {
        Task<PtyExit> exit = _closed.Task;

        if (exit.IsCompleted)
        {
            return;
        }

        if (await Task.WhenAny(exit, Task.Delay(GraceOnClose)).ConfigureAwait(false) == exit)
        {
            return;
        }

        // It had its console taken away and its input closed, and it is still here.
        End();
    }

    /// <summary>
    /// Closing a pipe whose other end has already gone throws, and there is nothing to do about it
    /// that is not this.
    /// </summary>
    private static async ValueTask CloseQuietlyAsync(Stream stream)
    {
        try
        {
            await stream.DisposeAsync().ConfigureAwait(false);
        }
        catch (IOException)
        {
            // The far end went first, which is the ordinary way a session ends.
        }
        catch (ObjectDisposedException)
        {
            // Already closed, which two paths through shutdown can both reach.
        }
    }

    private static void CloseQuietly(Stream stream)
    {
        try
        {
            stream.Dispose();
        }
        catch (IOException)
        {
            // The far end went first, which is the ordinary way a session ends.
        }
        catch (ObjectDisposedException)
        {
            // Already closed, which two paths through shutdown can both reach.
        }
    }
}
