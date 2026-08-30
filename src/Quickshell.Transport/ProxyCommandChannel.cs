using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Quickshell.Transport;

/// <summary>
/// A hop reached by running a program and talking to it, rather than by opening a socket.
///
/// <para><b>This is the escape hatch, and it is here because the alternative is a client people
/// cannot use.</b> The <c>ssh_config</c> files in the wild are full of <c>ProxyCommand</c>: a
/// corporate SSO helper, a cloud provider's session manager, somebody's shell script that knows how
/// to reach a machine no library ever will. A client that honours only its own jump path is a client
/// that fails on the hosts a user most needs, in a way they cannot work around.</para>
///
/// <para><b>The program's standard streams are the connection.</b> It is handed a target on the
/// command line, and whatever it writes is what the server said. SSH.NET has no way to be given a
/// stream, so this listens on a local port, accepts exactly one connection, and copies bytes between
/// that socket and the program in both directions — the same shape QS57's forwarded port takes, and
/// with the same exposure, which QS119 carries.</para>
///
/// <para><b>What the program printed on stderr is kept.</b> A proxy command that fails does so by
/// exiting, and the connection then looks to SSH.NET like a link that dropped for no reason. The
/// message the program printed is the only thing that says why, so it is captured and handed back
/// rather than discarded to a console nobody is watching.</para>
/// </summary>
public sealed class ProxyCommandChannel : IAsyncDisposable
{
    private readonly Process _process;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stopping = new();
    private readonly StringBuilder _printed = new();
    private readonly Lock _guard = new();

    private Task _serving = Task.CompletedTask;
    private bool _disposed;

    private ProxyCommandChannel(Process process, TcpListener listener, string command,
                                SshEndpoint target)
    {
        _process = process;
        _listener = listener;
        Command = command;

        // The target's own user, carried through: what changes is where the session connects, not
        // who it logs in as.
        Reachable = SshEndpoint.For("127.0.0.1", target.User,
                                    ((IPEndPoint)listener.LocalEndpoint).Port);
    }

    /// <summary>The command line as it was run, with its tokens already expanded.</summary>
    public string Command { get; }

    /// <summary>Where the next hop connects to reach what this program is carrying.</summary>
    public SshEndpoint Reachable { get; }

    /// <summary>
    /// Whether the program is still running. False once this has been disposed, which is the honest
    /// answer and not an incidental one: asking a disposed <see cref="Process"/> whether it has
    /// exited throws, and a property that threw where the answer is plainly "no" would be a trap
    /// laid for the caller checking whether cleanup worked.
    /// </summary>
    public bool IsRunning
    {
        get
        {
            if (_disposed)
            {
                return false;
            }

            try
            {
                return !_process.HasExited;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }
    }

    /// <summary>
    /// The program's process id while it is running, and null once it is not. What it is for is
    /// letting a caller check with the operating system that it really is gone.
    /// </summary>
    public int? Id
    {
        get
        {
            try
            {
                return _disposed ? null : _process.Id;
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }
    }

    /// <summary>
    /// Whatever the program printed on its error stream, which is the only account of a failure it
    /// gives. Empty where it printed nothing.
    /// </summary>
    public string Printed
    {
        get
        {
            lock (_guard)
            {
                return _printed.ToString().Trim();
            }
        }
    }

    /// <summary>
    /// Runs a proxy command for one target and returns where the session can now connect.
    /// </summary>
    /// <param name="command">
    /// The command line, with OpenSSH's tokens in it: <c>%h</c> the host, <c>%p</c> the port,
    /// <c>%r</c> the user, and <c>%%</c> a literal percent.
    /// </param>
    /// <param name="target">The machine the command is being asked to reach.</param>
    /// <param name="cancellationToken">Abandons the attempt.</param>
    /// <exception cref="SshException">The program could not be started.</exception>
    public static ValueTask<ProxyCommandChannel> StartAsync(string command, SshEndpoint target,
                                                            CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);

        cancellationToken.ThrowIfCancellationRequested();

        string expanded = Expand(command, target);
        (string program, string arguments) = Split(expanded);

        TcpListener listener = new(IPAddress.Loopback, 0);

        // One at a time: this carries exactly one session, and a backlog would be a second caller
        // waiting on a program that will never serve it.
        listener.Start(1);

        Process process = new()
        {
            StartInfo = new ProcessStartInfo(program, arguments)
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };

        ProxyCommandChannel channel = new(process, listener, expanded, target);

        process.ErrorDataReceived += channel.Keep;

        try
        {
            process.Start();
        }
        catch (Exception refused) when (refused is System.ComponentModel.Win32Exception
                                                 or InvalidOperationException)
        {
            listener.Stop();
            process.Dispose();

            throw new SshException(
                SshFailureKind.Unrecognised,
                $"The proxy command could not be run: {program}",
                "The program never started, so nothing was attempted against the server.",
                "Check the ProxyCommand in your ssh config: the program must be on PATH or named "
                + "by a full path.",
                refused.Message);
        }

        process.BeginErrorReadLine();

        channel._serving = Task.Run(() => channel.ServeAsync(), CancellationToken.None);

        return ValueTask.FromResult(channel);
    }

    /// <summary>
    /// OpenSSH's tokens, expanded the way OpenSSH expands them.
    ///
    /// <para><c>%%</c> is handled in the same pass as the rest, so a command containing a literal
    /// percent before an <c>h</c> is not silently turned into the host name.</para>
    /// </summary>
    /// <param name="command">The command line as the config file spells it.</param>
    /// <param name="target">The machine whose details the tokens stand for.</param>
    /// <returns>The command line with nothing left to substitute.</returns>
    public static string Expand(string command, SshEndpoint target)
    {
        StringBuilder expanded = new(command.Length);
        int at = 0;

        while (at < command.Length)
        {
            if (command[at] != '%' || at + 1 == command.Length)
            {
                expanded.Append(command[at]);
                at++;

                continue;
            }

            char token = command[at + 1];

            _ = token switch
            {
                'h' => expanded.Append(target.Host),
                'p' => expanded.Append(target.Port.ToString(CultureInfo.InvariantCulture)),
                'r' => expanded.Append(target.User),
                '%' => expanded.Append('%'),
                _ => expanded.Append(command[at]).Append(token),
            };

            at += 2;
        }

        return expanded.ToString();
    }

    /// <summary>
    /// The program, and everything after it left exactly as written.
    ///
    /// <para>The remainder is not re-quoted. Whatever quoting the user put in their config is what
    /// the program's own argument parsing sees, which is the only way a command line that worked
    /// under <c>ssh</c> keeps working here.</para>
    /// </summary>
    /// <param name="command">An expanded command line.</param>
    /// <returns>The program to run, and the rest of the line as written.</returns>
    public static (string Program, string Arguments) Split(string command)
    {
        string trimmed = command.TrimStart();

        if (trimmed.StartsWith('"'))
        {
            int closing = trimmed.IndexOf('"', 1);

            if (closing > 0)
            {
                return (trimmed[1..closing], trimmed[(closing + 1)..].TrimStart());
            }
        }

        int space = trimmed.IndexOf(' ', StringComparison.Ordinal);

        return space < 0 ? (trimmed, string.Empty) : (trimmed[..space], trimmed[(space + 1)..].TrimStart());
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        await _stopping.CancelAsync().ConfigureAwait(false);

        _listener.Stop();

        try
        {
            if (!_process.HasExited)
            {
                // The whole tree: a proxy command is very often a shell line that started something
                // else, and killing only the shell leaves the thing that holds the connection.
                _process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception gone) when (gone is InvalidOperationException
                                             or System.ComponentModel.Win32Exception
                                             or NotSupportedException)
        {
            // It ended on its own between the check and the kill, which is the outcome anyway.
        }

        try
        {
            await _serving.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The pump ends by its socket being closed underneath it, which is how it is meant to
            // end. Whatever it threw on the way out is not news.
        }

        _process.ErrorDataReceived -= Keep;
        _process.Dispose();
        _stopping.Dispose();
    }

    private void Keep(object sender, DataReceivedEventArgs line)
    {
        if (line.Data is null)
        {
            return;
        }

        lock (_guard)
        {
            _printed.AppendLine(line.Data);
        }
    }

    /// <summary>
    /// Accepts the one connection and copies bytes until either end stops.
    ///
    /// <para>Both directions run at once and either one finishing ends the other, because a proxy
    /// command that exits and a session that closes are the same event seen from two sides.</para>
    /// </summary>
    private async Task ServeAsync()
    {
        try
        {
            using TcpClient client = await _listener.AcceptTcpClientAsync(_stopping.Token)
                                                    .ConfigureAwait(false);

            using NetworkStream socket = client.GetStream();

            Task upstream = socket.CopyToAsync(_process.StandardInput.BaseStream, _stopping.Token);
            Task downstream = _process.StandardOutput.BaseStream.CopyToAsync(socket, _stopping.Token);

            await Task.WhenAny(upstream, downstream).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Every way this ends — cancelled, socket closed, program gone — is the connection being
            // over, and the caller learns that from the session rather than from here.
        }
        finally
        {
            _listener.Stop();
        }
    }
}
