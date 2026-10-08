using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using Quickshell.Transport;

/// <summary>
/// The far end of <c>pty-bridge.py</c>: a TCP socket to a Linux pty in WSL, with esctest on the
/// other side of it and nothing in between (QS211).
///
/// <para><b>Why not the pseudo-console.</b> Through <see cref="ConPtyChannel"/> the console host
/// answered esctest's queries itself and the emulator never received them, so the figure measured
/// conhost wherever conhost intercepts. A pty and a socket intercept nothing: every byte the suite
/// sends is a byte <see cref="ReadAsync"/> returns, and every reply goes back as written.</para>
///
/// <para><b>Closed is the bridge's exit</b>, which is the command's own status: the bridge exits
/// with it once the pty has given up its last byte.</para>
/// </summary>
internal sealed class BridgeChannel : IPtyChannel
{
    private readonly Process _bridge;
    private readonly TcpClient _client;
    private readonly NetworkStream _stream;

    private BridgeChannel(Process bridge, TcpClient client, int columns, int rows)
    {
        _bridge = bridge;
        _client = client;
        _stream = client.GetStream();
        Size = (columns, rows);
        Closed = Exit();
    }

    public (int Columns, int Rows) Size { get; private set; }

    public Task<PtyExit> Closed { get; }

    /// <summary>Starts the bridge in WSL with a command on its pty, and connects to it.</summary>
    /// <param name="command">The command and its arguments, as WSL's shell will receive them.</param>
    /// <param name="columns">The pty's width.</param>
    /// <param name="rows">The pty's height.</param>
    public static async Task<BridgeChannel> StartAsync(string[] command, int columns, int rows)
    {
        int port = FreePort();
        string script = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "pty-bridge.py"));

        ProcessStartInfo start = new("wsl.exe")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (string argument in (string[])["--", "python3", "-", $"{port}", $"{columns}", $"{rows}", .. command])
        {
            start.ArgumentList.Add(argument);
        }

        Process bridge = Process.Start(start) ?? throw new InvalidOperationException("wsl.exe did not start");

        // The script arrives on stdin, so nothing has to be copied into WSL first; closing stdin is
        // what tells python the script is whole.
        await bridge.StandardInput.WriteAsync(script);
        bridge.StandardInput.Close();

        string? said = await bridge.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30));

        if (said != "listening")
        {
            string error = await bridge.StandardError.ReadToEndAsync();

            throw new InvalidOperationException($"the bridge did not start: {said} {error}".Trim());
        }

        TcpClient client = new() { NoDelay = true };
        await client.ConnectAsync("127.0.0.1", port);

        // Set and then read back, as IPtyChannel asks of any channel with a socket under it.
        if (!client.NoDelay)
        {
            throw new InvalidOperationException("TCP_NODELAY did not take on the bridge's socket");
        }

        return new BridgeChannel(bridge, client, columns, rows);
    }

    public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        _stream.ReadAsync(buffer, cancellationToken);

    public ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default) =>
        _stream.WriteAsync(bytes, cancellationToken);

    /// <summary>
    /// Recorded and not sent: esctest never resizes its terminal, and a size message would be a
    /// protocol this bridge does not have, mixed into the bytes it copies unchanged.
    /// </summary>
    public void Resize(int columns, int rows) => Size = (columns, rows);

    public async ValueTask DisposeAsync()
    {
        Dispose();
        await Task.CompletedTask;
    }

    public void Dispose()
    {
        _stream.Dispose();
        _client.Dispose();

        if (!_bridge.HasExited)
        {
            _bridge.Kill(entireProcessTree: true);
        }

        _bridge.Dispose();
    }

    private async Task<PtyExit> Exit()
    {
        await _bridge.WaitForExitAsync();

        return PtyExit.Exited(_bridge.ExitCode);
    }

    /// <summary>A port nothing is listening on, found by asking the system for one and letting it go.</summary>
    private static int FreePort()
    {
        using TcpListener probe = new(System.Net.IPAddress.Loopback, 0);
        probe.Start();

        return ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
    }
}
