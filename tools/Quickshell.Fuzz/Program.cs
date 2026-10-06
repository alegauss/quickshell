using Quickshell.Terminal;
using SharpFuzz;

namespace Quickshell.Fuzz;

/// <summary>
/// One libFuzzer input, fed to a fresh emulator, and the model's invariants checked after it.
///
/// <para><b>What counts as a crash.</b> An exception out of <see cref="Emulator.Feed"/> — nothing a
/// host sends may end a session — and any of the bounds the suite's hostile-input tests hold the
/// model to, checked here as exceptions so libFuzzer records the input that broke one. The input is
/// fed in two reads split at its first byte's value, so a sequence or a character straddling a read
/// is part of what gets searched rather than something only the suite's fixed splits reach.</para>
///
/// <para>A crash libFuzzer finds lands in <c>artifacts/fuzz/findings</c>. The suite's own list of
/// pathological shapes in <c>HostileInputTests</c> is where it goes next, so the build fails on it
/// from then on (QS102).</para>
/// </summary>
public static class Program
{
    public static void Main()
    {
        Fuzzer.LibFuzzer.Run(input =>
        {
            Emulator emulator = new(80, 24, scrollback: 200);
            int split = input.IsEmpty ? 0 : input[0] % (input.Length + 1);

            emulator.Feed(input[..split]);
            emulator.Feed(input[split..]);

            Bounded(emulator);
        });
    }

    /// <summary>The same bounds <c>HostileInputTests.Bounded</c> asserts, as exceptions.</summary>
    private static void Bounded(Emulator emulator)
    {
        TerminalBuffer buffer = emulator.Buffer;

        Hold(emulator.Reply.Length <= Emulator.MaximumReplyLength, "the reply outgrew its bound");
        Hold(emulator.Title.Length <= Emulator.MaximumOscLength, "the title outgrew its bound");
        Hold(emulator.WorkingDirectory.Length <= Emulator.MaximumOscLength, "the directory outgrew its bound");
        Hold(buffer.ClusterCount <= TerminalBuffer.MaximumClusters, "the cluster table outgrew its bound");
        Hold(buffer.CursorRow >= 0 && buffer.CursorRow < buffer.Rows, "the cursor row left the screen");
        Hold(buffer.CursorColumn >= 0 && buffer.CursorColumn < buffer.Columns, "the cursor column left the screen");
        Hold(buffer.LineCount >= buffer.Rows && buffer.LineCount <= buffer.Capacity, "the line count left its range");
        Hold(emulator.MarginTop >= 0 && emulator.MarginBottom < buffer.Rows
             && emulator.MarginTop <= emulator.MarginBottom, "the scrolling region is not a region");
    }

    private static void Hold(bool condition, string broken)
    {
        if (!condition)
        {
            throw new InvalidOperationException(broken);
        }
    }
}
