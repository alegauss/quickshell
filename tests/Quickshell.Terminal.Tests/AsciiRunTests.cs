using System.Text;
using Quickshell.Terminal;
using Xunit;

namespace Quickshell.Terminal.Tests;

/// <summary>
/// QS258: printable ASCII written a run at a time lands exactly where a character at a time would.
///
/// <para>The same stream, in the same reads, is fed to two emulators, one with the fast path switched
/// off, and must leave the same screen, scrollback, cursor and owed wrap - across the edges where
/// the two paths could differ: the right edge, the right margin, wrapping off, wide characters, marks
/// that extend the last character of a run, and a designated line-drawing set. Fed whole and seven
/// bytes at a time, so runs also begin and end at a read's edge. The reads are the same on both
/// sides on purpose: where a read ends changes what a later mark joins, on either path (QS261).</para>
/// </summary>
public sealed class AsciiRunTests
{
    public static TheoryData<string, string> Streams => new()
    {
        { "lines longer than the screen", string.Concat(Enumerable.Repeat("the quick brown fox jumps over the lazy dog ", 12)) + "\r\n" + new string('x', 200) },
        { "exactly the width, then more", new string('a', 40) + "b\r\n" + new string('c', 40) + "\r\n" },
        { "wrapping off", "\x1b[?7l" + new string('w', 95) + "\x1b[?7h" + new string('v', 50) },
        { "a left and right margin", "\x1b[?69h\x1b[5;30s\x1b[3;8H" + string.Concat(Enumerable.Repeat("margin text ", 10)) },
        { "a run ending in a combining mark", "plain caf" + "é" + " more text and été" },
        { "a keycap after a run", "press #️⃣ now and 1️⃣ too" },
        { "wide characters between runs", "abc中文def\U0001F600ghi" + new string('z', 30) },
        { "line drawing designated", "\x1b(0lqqqqk\x1b(B and back to ascii text" },
        { "colours changing inside a line", "red \x1b[31mRED\x1b[0m bold \x1b[1mBOLD\x1b[22m done" },
        { "a repeat of the last printed", "abc\x1b[5b and then more" },
        { "insert mode", "0123456789\x1b[5G\x1b[4hINS\x1b[4l tail" },
    };

    [Theory]
    [MemberData(nameof(Streams))]
    public void ARunLandsWhereCharactersWould(string name, string stream)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(stream);

        foreach (int read in (int[])[bytes.Length, 7])
        {
            Emulator fast = new(40, 6, scrollback: 50);
            Emulator slow = new(40, 6, scrollback: 50) { AsciiRuns = false };

            for (int at = 0; at < bytes.Length; at += read)
            {
                ReadOnlySpan<byte> chunk = bytes.AsSpan(at, Math.Min(read, bytes.Length - at));

                fast.Feed(chunk);
                slow.Feed(chunk);
            }

            Compare($"{name}, reads of {read}", fast, slow);
        }
    }

    /// <summary>
    /// QS261: where the network splits a stream does not decide what the screen holds. Each stream
    /// fed whole and fed one byte at a time — every boundary a read can have — leaves the same
    /// screen, on the path a session takes.
    /// </summary>
    [Theory]
    [MemberData(nameof(Streams))]
    public void AStreamSplitAnywhereLeavesTheScreenItLeavesWhole(string name, string stream)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(stream);

        Emulator whole = new(40, 6, scrollback: 50);
        Emulator split = new(40, 6, scrollback: 50);

        whole.Feed(bytes);

        for (int at = 0; at < bytes.Length; at++)
        {
            split.Feed(bytes.AsSpan(at, 1));
        }

        Compare($"{name}, whole against a byte at a time", whole, split);
    }

    /// <summary>
    /// QS261 at the edge: a keycap whose mark arrives after its base filled the last column wraps as
    /// the same keycap arriving whole does, and a mark joining a base at the edge joins that base and
    /// not the one before it.
    /// </summary>
    [Theory]
    [InlineData("edge keycap", "#️⃣ after")]
    [InlineData("edge accent", "é after")]
    [InlineData("edge keycap, wrapping off", "\x1b[?7l#️⃣")]
    public void AMarkJoiningTheLastColumnLandsAsTheWholeClusterDoes(string name, string tail)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(new string('a', 39) + tail);

        Emulator whole = new(40, 6, scrollback: 50);
        Emulator split = new(40, 6, scrollback: 50);

        whole.Feed(bytes);

        for (int at = 0; at < bytes.Length; at++)
        {
            split.Feed(bytes.AsSpan(at, 1));
        }

        Compare($"{name}, whole against a byte at a time", whole, split);
    }

    private static void Compare(string name, Emulator fast, Emulator slow)
    {
        TerminalBuffer a = fast.Buffer;
        TerminalBuffer b = slow.Buffer;

        Assert.True(a.LineCount == b.LineCount, $"{name}: {a.LineCount} lines against {b.LineCount}");
        Assert.True((a.CursorRow, a.CursorColumn) == (b.CursorRow, b.CursorColumn),
                    $"{name}: cursor at {a.CursorRow},{a.CursorColumn} against {b.CursorRow},{b.CursorColumn}");
        Assert.True(fast.PendingWrap == slow.PendingWrap, $"{name}: owed wrap differs");

        for (int line = 0; line < a.LineCount; line++)
        {
            Assert.True(a.IsWrapped(line) == b.IsWrapped(line), $"{name}: line {line} wraps differently");

            ReadOnlySpan<Cell> left = a.Line(line);
            ReadOnlySpan<Cell> right = b.Line(line);

            for (int column = 0; column < left.Length; column++)
            {
                Assert.True(Same(a, left[column], b, right[column]),
                            $"{name}: line {line} column {column} is '{Text(a, left[column])}' run-wise and '{Text(b, right[column])}' a character at a time");
            }
        }
    }

    /// <summary>Two cells agree: the same text, and the same everything else once the text is set aside.</summary>
    private static bool Same(TerminalBuffer a, Cell left, TerminalBuffer b, Cell right) =>
        Text(a, left) == Text(b, right) && left.WithCodepoint(0).Equals(right.WithCodepoint(0));

    private static string Text(TerminalBuffer buffer, Cell cell)
    {
        Span<char> text = stackalloc char[GraphemeSegmenter.MaximumCluster];
        int written = buffer.TextOf(cell, text);

        return written < 0 ? "?" : new string(text[..written]);
    }
}
