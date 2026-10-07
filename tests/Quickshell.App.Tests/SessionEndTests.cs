using System.Text;
using Quickshell.App;
using Quickshell.Terminal;
using Quickshell.Transport;
using Xunit;

namespace Quickshell.App.Tests;

/// <summary>
/// QS152: a session that ends says so, in the pane, and a window that has stopped responding is no
/// longer what it looks like.
///
/// <para>Through a real leaf and a real pipeline, over a channel whose ending is known exactly, so
/// what is asserted is what the pane holds once the shell has gone.</para>
/// </summary>
public sealed class SessionEndTests
{
    private static readonly TerminalShare Shared = new();

    /// <summary>
    /// The falsification: typing <c>exit</c> leaves a pane that says the shell exited and with
    /// what, rather than the last frame with a cursor blinking at nothing.
    /// </summary>
    [Fact]
    public void AShellThatExitsSaysSoInThePane()
    {
        (string screen, string? ended, bool live, bool cursor) = OnSta(() =>
        {
            TerminalLeaf leaf = TerminalLeaf.Open(Settings.Default, "cmd.exe", Shared);
            PtyStub far = new();

            leaf.ConnectAsync(Opening(far)).GetAwaiter().GetResult();

            far.Produce(Encoding.ASCII.GetBytes("C:\\> exit"));
            far.End(new PtyExit(3, string.Empty));

            Until(() => leaf.Ended is not null);

            return (Text(leaf.Emulator), leaf.Ended, leaf.IsLive, leaf.Emulator.CursorVisible);
        });

        Assert.Equal("the shell exited with code 3", ended);
        Assert.Contains("[cmd.exe: the shell exited with code 3. Nothing typed here goes anywhere now.]",
                        screen, StringComparison.Ordinal);

        // What the shell printed last is still there above it: the sentence is added, not swapped in.
        Assert.Contains("C:\\> exit", screen, StringComparison.Ordinal);
        Assert.False(live);
        Assert.False(cursor);
    }

    /// <summary>A link that went is told apart from a program that exited, as a remote session words it.</summary>
    [Fact]
    public void ALinkThatWentIsNotAProgramThatExited()
    {
        Assert.Equal("the shell exited with code 0", TerminalLeaf.Ending(new PtyExit(0, string.Empty)));
        Assert.Equal("the connection ended: the network went away",
                     TerminalLeaf.Ending(new PtyExit(null, "the network went away")));
        Assert.Equal("the connection ended", TerminalLeaf.Ending(new PtyExit(null, string.Empty)));
    }

    /// <summary>A tab being closed is not a session ending, and writes nothing into a pane on its way out.</summary>
    [Fact]
    public void ClosingTheTabIsNotTheSessionEnding()
    {
        (string? ended, string screen) = OnSta(() =>
        {
            TerminalLeaf leaf = TerminalLeaf.Open(Settings.Default, "cmd.exe", Shared);
            PtyStub far = new();

            leaf.ConnectAsync(Opening(far)).GetAwaiter().GetResult();

            leaf.DisposeAsync().AsTask().GetAwaiter().GetResult();
            Thread.Sleep(200);

            return (leaf.Ended, Text(leaf.Emulator));
        });

        Assert.Null(ended);
        Assert.DoesNotContain("Nothing typed here", screen, StringComparison.Ordinal);
    }

    // ---- plumbing ----

    private static ShellOpener Opening(PtyStub far) =>
        (emulator, damage, _, _, _) =>
            Task.FromResult<IShellSession>(new Stubbed(SessionPipeline.Start(far, emulator, damage: damage)));

    private sealed class Stubbed(SessionPipeline pipeline) : IShellSession
    {
        public SessionPipeline Pipeline => pipeline;

        public ValueTask DisposeAsync() => pipeline.DisposeAsync();
    }

    private static string Text(Emulator emulator)
    {
        TerminalBuffer buffer = emulator.Buffer;
        StringBuilder text = new();
        Span<char> cell = stackalloc char[8];

        for (int line = 0; line < buffer.LineCount; line++)
        {
            foreach (Cell glyph in buffer.Line(line))
            {
                text.Append(cell[..buffer.TextOf(glyph, cell)]);
            }

            text.Append('\n');
        }

        return text.ToString();
    }

    private static void Until(Func<bool> ready)
    {
        for (int wait = 0; wait < 500 && !ready(); wait++)
        {
            Thread.Sleep(10);
        }

        Assert.True(ready(), "the pane never reached the state this was waiting for");
    }

    private static T OnSta<T>(Func<T> work)
    {
        T result = default!;
        Exception? failed = null;

        Thread sta = new(() =>
        {
            try
            {
                result = work();
            }
            catch (Exception caught)
            {
                failed = caught;
            }
        });

        sta.SetApartmentState(ApartmentState.STA);
        sta.Start();
        sta.Join();

        if (failed is not null)
        {
            throw new InvalidOperationException("the STA work failed", failed);
        }

        return result;
    }
}
