using System.IO;
using System.IO.Compression;
using System.Text;

using Quickshell.Terminal;
using Quickshell.Transport;

using Xunit;

namespace Quickshell.App.Tests;

/// <summary>
/// QS350: what a bash at its prompt sends when a split narrows its pane, and what this client then shows,
/// traced against the Debian fixture's real bash over SSH.
///
/// <para>What the trace found (bash 5.2.21): bash answers the window change by redrawing its prompt where
/// it is, <c>CR ESC[K CR</c> and the prompt, with no line feed, so bash adds no line. What moves the prompt
/// down is this client's reflow: every line above it wider than the new width wraps, and each wrap is a row
/// more above the prompt, the cursor going with it. A terminal that reflows does that, and the prompt is
/// drawn once.</para>
/// </summary>
public sealed class NarrowedPromptTests : IDisposable
{
    private const string Prompt = "probe@qs-sshd-target:~$";

    private readonly string _here = Path.Combine(Path.GetTempPath(), "qs-narrow-" + Guid.NewGuid().ToString("N"));

    public NarrowedPromptTests() => Directory.CreateDirectory(_here);

    private static CancellationToken Stop => TestContext.Current.CancellationToken;

    public void Dispose() => Directory.Delete(_here, recursive: true);

    /// <summary>
    /// Narrowed from 80 columns to 40 at its prompt, bash redraws the prompt in place, and the screen holds it
    /// once, on the cursor's row, below the lines the reflow wrapped, with nothing after it.
    /// </summary>
    [Fact]
    public async Task ANarrowedBashPromptIsRedrawnInPlaceAndShownOnce()
    {
        SshFixture.SkipWithoutIt();

        string probeKey = Path.Combine(Repository.Root, "prototypes", "SshProbe", "fixture", "keys", "probe_ed25519");
        ResolvedSession session = SessionTree.Of(new SessionNode
        {
            Children = [new SessionNode { Name = "db", Host = "127.0.0.1", Settings = new SessionSettings { User = "probe", Port = 2222, Key = probeKey } }],
        }).Session("db")!;
        TrustOnFirstUse trust = new(KnownHosts.ReadFrom(Path.Combine(_here, "known_hosts")), (_, _) => ValueTask.FromResult(SshHostKeyVerdict.Accept));

        Emulator emulator = new(80, 25);
        SessionRecording recording = SessionRecording.Start(_here, "narrow");
        int wrapped;
        int before;
        string[] after;
        (int Row, int Column) cursor;

        await using (RemoteShell shell = await RemoteShell.OpenAsync(session, trust, emulator, new DamageSignal(), 80, 25, Stop, recording: recording))
        {
            // A line wider than forty columns above the prompt, so the narrowing has something to wrap.
            await shell.TypeAsync(Encoding.ASCII.GetBytes("echo MARK-$((6*7)) ........................................\r"), Stop);
            await Until(() => Rows(emulator.Buffer).Any(row => row.StartsWith("MARK-42", StringComparison.Ordinal))
                              && Rows(emulator.Buffer)[emulator.Buffer.CursorRow] == Prompt);

            before = emulator.Buffer.CursorRow;

            int wrappedAlready = Wraps(emulator.Buffer);

            // As a split does it: the model first, then the window change to the server. What the reflow
            // adds is the rows wrapped now that were not at eighty columns.
            emulator.Resize(40, 25);
            wrapped = Wraps(emulator.Buffer) - wrappedAlready;
            shell.Resize(40, 25);

            await Task.Delay(TimeSpan.FromSeconds(2), Stop);

            after = Rows(emulator.Buffer);
            cursor = (emulator.Buffer.CursorRow, emulator.Buffer.CursorColumn);
        }

        await recording.DisposeAsync();

        string answered = await AfterTheLastPrompt(recording.Path);

        // bash's whole answer to the window change: back to the line's start, cleared, the prompt again.
        Assert.StartsWith("\r\x1b[K\r", answered, StringComparison.Ordinal);
        Assert.DoesNotContain('\n', answered);

        Assert.True(wrapped > 0, "nothing above the prompt was wide enough to wrap, so the trace showed nothing");
        Assert.Equal((before + wrapped, Prompt.Length + 1), cursor);
        Assert.Equal(1, after.Count(row => row == Prompt));
        Assert.Equal(Prompt, after[before + wrapped]);
        Assert.All(after.Skip(before + wrapped + 1), row => Assert.Equal(string.Empty, row));
    }

    /// <summary>What the host sent after the prompt that followed MARK-42: its answer to the window change.</summary>
    private static async Task<string> AfterTheLastPrompt(string recorded)
    {
        await using GZipStream unzip = new(File.OpenRead(recorded), CompressionMode.Decompress);
        using MemoryStream raw = new();

        await unzip.CopyToAsync(raw, Stop);

        string sent = Encoding.UTF8.GetString(raw.ToArray());
        int mark = sent.LastIndexOf("MARK-42", StringComparison.Ordinal);
        int prompt = sent.IndexOf("$ ", mark, StringComparison.Ordinal);

        return sent[(prompt + 2)..];
    }

    private static int Wraps(TerminalBuffer buffer) => Enumerable.Range(0, buffer.Rows).Count(buffer.IsScreenWrapped);

    private static string[] Rows(TerminalBuffer buffer) =>
        [.. Enumerable.Range(0, buffer.Rows).Select(row =>
        {
            StringBuilder line = new();

            foreach (Cell cell in buffer.Screen(row))
            {
                if (cell.Width != 0)
                {
                    line.Append(cell.Codepoint == 0 ? " " : buffer.TextOf(cell));
                }
            }

            return line.ToString().TrimEnd();
        })];

    private static async Task Until(Func<bool> condition)
    {
        for (DateTime giveUp = DateTime.UtcNow.AddSeconds(20); DateTime.UtcNow < giveUp; await Task.Delay(50, Stop))
        {
            if (condition())
            {
                return;
            }
        }

        throw new TimeoutException("the condition never held");
    }
}
