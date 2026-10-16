using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;

using Quickshell.Transport;

namespace Quickshell.App;

/// <summary>
/// What an agent does through the gateway, written as terminal output a person can watch (QS335): each
/// call as a prompt line with the session and the command, then what came back, then how it ended.
///
/// <para><b>A file, because the gateway and the window are two processes</b> (QS321) and the gateway
/// opens no port. The gateway appends to <c>agent-feeds/&lt;run&gt;.vt</c> in quickshell's data folder;
/// the window tails it into a tab of its own through <see cref="FeedChannel"/>, drawn by the renderer
/// every pane uses, so it costs the drawing path nothing new. With no window open nothing changes,
/// and the audit log is the record.</para>
///
/// <para>Everything shown is redacted as the agent's answers are (QS330): a person watching over the
/// agent's shoulder sees what the agent sees.</para>
/// </summary>
public sealed class AgentFeed
{
    private const string Escape = "\u001b";

    private readonly string _file;
    private readonly Lock _writing = new();

    private AgentFeed(string file) => _file = file;

    /// <summary>The file the window tails.</summary>
    public string File => _file;

    /// <summary>A feed for a gateway run, in <paramref name="folder"/>, opened with the run's title.</summary>
    public static AgentFeed Start(string folder, string run)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        ArgumentException.ThrowIfNullOrWhiteSpace(run);

        Directory.CreateDirectory(folder);

        AgentFeed feed = new(Path.Combine(folder, run + ".vt"));

        feed.Append($"{Escape}]0;agent {run}\u0007{Escape}[2mAn agent is working through quickshell's gateway (run {run}). "
                    + $"This tab only shows what it does; Ctrl+Shift+F12 stops every agent.{Escape}[0m\r\n");

        return feed;
    }

    /// <summary>The prompt line for a call about to run.</summary>
    public void Begin(string tool, JsonObject arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        string host = arguments["host"] is JsonValue named && named.TryGetValue(out string? path) ? path : "-";
        string what = tool switch
        {
            "exec" or "job_start" => string.Join(' ', (arguments["argv"] as JsonArray ?? []).Select(word => Argv.Word(word?.ToString() ?? string.Empty))),
            "read_file" or "edit_file" => arguments["path"]?.ToString() ?? string.Empty,
            _ => arguments.Count == 0 ? string.Empty : arguments.ToJsonString(),
        };

        (string shown, _) = SecretRedactor.Redact(what);

        Append($"\r\n{Escape}[1;35magent{Escape}[0m {Escape}[1m{host}{Escape}[0m {Escape}[36m{tool}{Escape}[0m {shown}\r\n");
    }

    /// <summary>What came back, as the agent was given it, and how the call ended.</summary>
    public void End(JsonObject answer, bool failed)
    {
        ArgumentNullException.ThrowIfNull(answer);

        StringBuilder said = new();

        if (failed)
        {
            said.Append($"{Escape}[31mrefused: {Lines(answer["error"]?.ToString())}{Escape}[0m");
        }
        else
        {
            foreach (string field in (string[])["stdout", "text", "diff"])
            {
                if (answer[field]?.ToString() is { Length: > 0 } text)
                {
                    said.Append(Lines(text));
                }
            }

            if (answer["stderr"]?.ToString() is { Length: > 0 } errors)
            {
                said.Append($"{Escape}[31m{Lines(errors)}{Escape}[0m");
            }
        }

        string ended = answer["exitCode"] is JsonValue code && code.TryGetValue(out int exit)
            ? string.Create(CultureInfo.InvariantCulture, $"exit {exit}")
            : failed ? "refused" : "done";

        said.Append($"{Escape}[2m[{ended}]{Escape}[0m\r\n");
        Append(said.ToString());
    }

    /// <summary>A line from the gateway itself, in the warning colour: a taint, a stop.</summary>
    public void Note(string text) => Append($"{Escape}[33m{Escape}[1m! {text}{Escape}[0m\r\n");

    /// <summary>Text with its line ends as a terminal wants them, ending on one.</summary>
    private static string Lines(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        string lines = text.ReplaceLineEndings("\n").Replace("\n", "\r\n", StringComparison.Ordinal);

        return lines.EndsWith("\r\n", StringComparison.Ordinal) ? lines : lines + "\r\n";
    }

    private void Append(string text)
    {
        lock (_writing)
        {
            using FileStream stream = new(_file, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            byte[] bytes = Encoding.UTF8.GetBytes(text);

            stream.Write(bytes);
        }
    }
}

/// <summary>
/// A pane's channel that reads a growing file and writes nowhere: the agent's tab (QS335), which is
/// read-only for the person so their keys never interleave with the agent's.
/// </summary>
public sealed class FeedChannel : IPtyChannel
{
    private readonly FileStream _file;
    private readonly TaskCompletionSource<PtyExit> _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Opens <paramref name="path"/> to tail from its start.</summary>
    public FeedChannel(string path, int columns, int rows)
    {
        _file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        Size = (columns, rows);
    }

    /// <inheritdoc/>
    public (int Columns, int Rows) Size { get; private set; }

    /// <inheritdoc/>
    public Task<PtyExit> Closed => _closed.Task;

    /// <inheritdoc/>
    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        // A tail: what is there, then a wait for more, until the pane goes.
        while (!_closed.Task.IsCompleted)
        {
            int read = await _file.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);

            if (read > 0)
            {
                return read;
            }

            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }

        return 0;
    }

    /// <inheritdoc/>
    public ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

    /// <inheritdoc/>
    public void Resize(int columns, int rows) => Size = (columns, rows);

    /// <inheritdoc/>
    public void Dispose()
    {
        _closed.TrySetResult(PtyExit.Failed(string.Empty));
        _file.Dispose();
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        Dispose();

        return ValueTask.CompletedTask;
    }
}

/// <summary>The agent's tab's session: the feed tailed through the ordinary pipeline, with keys going nowhere.</summary>
public sealed class FeedSession : IShellSession
{
    private readonly FeedChannel _channel;
    private readonly SessionPipeline _pipeline;

    private FeedSession(FeedChannel channel, SessionPipeline pipeline)
    {
        _channel = channel;
        _pipeline = pipeline;
    }

    /// <summary>Starts tailing <paramref name="path"/> into <paramref name="emulator"/>.</summary>
    public static FeedSession Open(string path, Quickshell.Terminal.Emulator emulator, DamageSignal damage, int columns, int rows)
    {
        FeedChannel channel = new(path, Math.Max(1, columns), Math.Max(1, rows));

        return new FeedSession(channel, SessionPipeline.Start(channel, emulator, damage: damage));
    }

    /// <inheritdoc/>
    public ValueTask TypeAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

    /// <inheritdoc/>
    public void Resize(int columns, int rows) => _pipeline.Resize(columns, rows);

    /// <inheritdoc/>
    public void KeepScrollback(int lines) => _pipeline.KeepScrollback(lines);

    /// <inheritdoc/>
    public Task<PtyExit> Ended => LocalSession.EndedAsync(_pipeline);

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        await _pipeline.DisposeAsync().ConfigureAwait(false);
        await _channel.DisposeAsync().ConfigureAwait(false);
    }
}
