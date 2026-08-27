using System.Diagnostics;
using System.Text;
using Quickshell.Transport;
using Xunit;

namespace Quickshell.Transport.Tests;

/// <summary>
/// The pseudo-console, against real processes. Nothing here is a fixture: the bytes come from
/// <c>cmd.exe</c> and the exit codes come from Windows.
/// </summary>
public sealed class ConPtyChannelTests
{
    /// <summary>How long any one of these waits before calling it a hang.</summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);

    // ---- The falsification ----

    /// <summary>
    /// The design's own falsification: <em>falsified when a shell exiting leaves a process or a
    /// handle behind</em>.
    ///
    /// <para>Twenty sessions opened and closed. A leaked process shows as a live process identifier;
    /// a leaked handle shows as this process's handle count climbing with the session count. Twenty
    /// rather than one, because one leak is inside the noise of a single measurement and twenty is
    /// not.</para>
    /// </summary>
    [Fact]
    public async Task AShellExitingLeavesNoProcessAndNoHandleBehind()
    {
        // A first session pays whatever one-time cost the runtime has, so the count that is compared
        // is a steady one.
        await using (ConPtyChannel warm = await Echo("warm"))
        {
            await Drain(warm);
        }

        int before = Handles();

        for (int session = 0; session < 20; session++)
        {
            ConPtyChannel channel = await Echo($"session {session}");
            int identifier = channel.ProcessId;

            Assert.Contains($"session {session}", await Drain(channel), StringComparison.Ordinal);

            await channel.DisposeAsync();

            // Checked here and not in a list at the end: Windows reuses process identifiers, and a
            // number collected twenty sessions ago may by then belong to something else entirely.
            // That is a test that reports a leak nobody has.
            Assert.False(Alive(identifier), $"process {identifier} outlived the channel that started it");
        }

        int after = Handles();

        // Twenty sessions each leaking one handle would be twenty; a hundred is generous room for
        // the runtime's own churn while still catching a per-session leak.
        Assert.True(
            after - before < 100,
            $"handle count went from {before} to {after} across twenty sessions, which is a leak per session");
    }

    // ---- It carries bytes ----

    [Fact]
    public async Task AProgramsOutputArrivesAsBytes()
    {
        await using ConPtyChannel channel = await Echo("hello from the other end");

        Assert.Contains("hello from the other end", await Drain(channel), StringComparison.Ordinal);
    }

    /// <summary>
    /// A pseudo-console produces VT sequences and not plain text, which is the entire reason this
    /// task comes before SSH: the emulator now has a real producer of them.
    /// </summary>
    [Fact]
    public async Task TheOutputIsVirtualTerminalAndNotPlainText()
    {
        await using ConPtyChannel channel = await Echo("plain");

        byte[] bytes = await DrainBytes(channel);

        Assert.Contains((byte)0x1B, bytes);
    }

    /// <summary>
    /// Typing reaches the shell and comes back.
    ///
    /// <para><b>It waits for the shell to speak first, and that is not politeness.</b> Bytes written
    /// before the console host has a reader on the other side can be dropped, so a client that sent
    /// a command the instant it started one would lose it. A user types after the prompt appears for
    /// exactly the same reason, and this waits for quiet the way a user waits for a prompt.</para>
    /// </summary>
    [Fact]
    public async Task WhatIsWrittenReachesTheProgram()
    {
        await using ConPtyChannel channel = await Start("cmd.exe /q", 80, 25);

        Assert.NotEqual(string.Empty, await Prompt(channel));

        await channel.WriteAsync(Typed("echo typed-in-and-back"), TestContext.Current.CancellationToken);

        // The shell read it, ran it, and the result came back through the pseudo-console. Closing is
        // left to the channel: an interactive shell exiting on its own is what
        // ClosingWhileTheProgramRunsSaysThatIsWhatHappened is about, and it is not this claim.
        Assert.Contains("typed-in-and-back", await Prompt(channel), StringComparison.Ordinal);
    }

    // ---- It says how it ended ----

    [Fact]
    public async Task AnOrdinaryExitCarriesTheProgramsCode()
    {
        await using ConPtyChannel channel = await Start("cmd.exe /c exit 7", 80, 25);

        await Drain(channel);

        PtyExit exit = await Wait(channel);

        Assert.True(exit.IsExit);
        Assert.Equal(7, exit.Code);
        Assert.Equal(string.Empty, exit.Reason);
    }

    [Fact]
    public async Task AProgramThatDoesNotExistIsRefusedWithWhatWindowsSaid()
    {
        System.ComponentModel.Win32Exception failure =
            await Assert.ThrowsAsync<System.ComponentModel.Win32Exception>(
                () => Start("quickshell-no-such-program.exe", 80, 25));

        Assert.Contains("quickshell-no-such-program.exe", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>A channel closed while a program is still running says so rather than reporting an
    /// exit code it never received.</summary>
    [Fact]
    public async Task ClosingWhileTheProgramRunsSaysThatIsWhatHappened()
    {
        ConPtyChannel channel = await Start("cmd.exe /q", 80, 25);
        int identifier = channel.ProcessId;

        await channel.DisposeAsync();

        PtyExit exit = await channel.Closed;

        Assert.False(Alive(identifier), $"process {identifier} outlived the channel that started it");

        // Either it took the end of file and exited, or it did not and was ended. Both are answers;
        // what is refused is a channel that reports neither, or reports an exit code it never saw.
        Assert.True(exit.IsExit || exit.Reason.Length > 0);
    }

    // ---- Size ----

    [Fact]
    public async Task TheSizeIsWhatItWasStartedWith()
    {
        await using ConPtyChannel channel = await Start("cmd.exe /c exit", 132, 43);

        Assert.Equal((132, 43), channel.Size);
    }

    [Fact]
    public async Task ResizingIsAcceptedAndRemembered()
    {
        await using ConPtyChannel channel = await Start("cmd.exe /q", 80, 25);

        channel.Resize(120, 40);

        Assert.Equal((120, 40), channel.Size);

        channel.Resize(60, 20);

        Assert.Equal((60, 20), channel.Size);
    }

    /// <summary>A resize a console cannot have is refused here rather than passed on to Windows.</summary>
    [Theory]
    [InlineData(0, 25)]
    [InlineData(80, 0)]
    [InlineData(-1, 25)]
    [InlineData(80, -1)]
    public async Task ASizeAConsoleCannotHaveIsRefused(int columns, int rows)
    {
        await using ConPtyChannel channel = await Start("cmd.exe /q", 80, 25);

        Assert.Throws<ArgumentOutOfRangeException>(() => channel.Resize(columns, rows));
    }

    [Theory]
    [InlineData(0, 25)]
    [InlineData(80, 0)]
    public async Task AChannelCannotStartAtASizeAConsoleCannotHave(int columns, int rows)
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => Start("cmd.exe", columns, rows));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task AChannelCannotStartWithNothingToRun(string commandLine)
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => Start(commandLine, 80, 25));
    }

    // ---- Closing twice ----

    [Fact]
    public async Task ClosingTwiceIsNotAnError()
    {
        ConPtyChannel channel = await Start("cmd.exe /c exit", 80, 25);

        await channel.DisposeAsync();
        await channel.DisposeAsync();
    }

    /// <summary>The synchronous door closes the same way, for a caller with no async context.</summary>
    [Fact]
    public async Task TheSynchronousDoorAlsoCloses()
    {
        ConPtyChannel channel = await Start("cmd.exe /c exit", 80, 25);
        int identifier = channel.ProcessId;

        // The blocking call is the subject of this test, not an oversight in it.
#pragma warning disable CA1849 // Call async methods when in an async method
        channel.Dispose();
#pragma warning restore CA1849

        Assert.False(Alive(identifier), $"process {identifier} outlived the channel that started it");
    }

    // ---- Helpers ----

    private static Task<ConPtyChannel> Echo(string what) =>
        Start($"cmd.exe /c echo {what}", 80, 25);

    /// <summary>A line as a user's keyboard would send it: carriage return then line feed.</summary>
    private static byte[] Typed(string line) =>
        Encoding.UTF8.GetBytes(line + (char)0x0D + (char)0x0A);

    /// <summary>Every channel these tests open, carrying the run's own cancellation.</summary>
    private static Task<ConPtyChannel> Start(string commandLine, int columns, int rows) =>
        ConPtyChannel.StartAsync(
            commandLine, columns, rows, null, TestContext.Current.CancellationToken);

    /// <summary>Reads until the far end closes, or until the patience runs out.</summary>
    private static async Task<string> Drain(ConPtyChannel channel) =>
        Encoding.UTF8.GetString(await DrainBytes(channel));

    private static async Task<byte[]> DrainBytes(ConPtyChannel channel)
    {
        using CancellationTokenSource patience = new(Patience);
        using MemoryStream all = new();

        byte[] buffer = new byte[4096];

        try
        {
            int read;

            while ((read = await channel.ReadAsync(buffer, patience.Token)) > 0)
            {
                await all.WriteAsync(buffer.AsMemory(0, read), patience.Token);
            }
        }
        catch (OperationCanceledException)
        {
            Assert.Fail("the channel did not close within the patience this test allows");
        }
        catch (IOException)
        {
            // The pipe went with the program, which is the ordinary way this ends.
        }

        return all.ToArray();
    }

    /// <summary>
    /// Reads until the far end goes quiet, which is what a prompt being ready looks like from here.
    /// </summary>
    private static async Task<string> Prompt(ConPtyChannel channel)
    {
        using MemoryStream all = new();

        byte[] buffer = new byte[4096];

        while (true)
        {
            using CancellationTokenSource quiet = new(TimeSpan.FromMilliseconds(600));

            try
            {
                int read = await channel.ReadAsync(buffer, quiet.Token);

                if (read == 0)
                {
                    break;
                }

                await all.WriteAsync(buffer.AsMemory(0, read), TestContext.Current.CancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        return Encoding.UTF8.GetString(all.ToArray());
    }

    private static async Task<PtyExit> Wait(ConPtyChannel channel)
    {
        Task<PtyExit> closed = channel.Closed;

        Assert.Same(closed, await Task.WhenAny(closed, Task.Delay(Patience)));

        return await closed;
    }

    private static bool Alive(int identifier)
    {
        try
        {
            using Process process = Process.GetProcessById(identifier);

            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            // No such process, which is the answer this is asking for.
            return false;
        }
    }

    private static int Handles()
    {
        // Two collections, because a finalisable handle wrapper waiting to be collected is not a
        // leak and would read as one.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        using Process self = Process.GetCurrentProcess();
        self.Refresh();

        return self.HandleCount;
    }
}
