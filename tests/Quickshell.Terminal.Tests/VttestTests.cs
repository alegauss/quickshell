using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Quickshell.Terminal;
using Xunit;

namespace Quickshell.Terminal.Tests;

/// <summary>
/// vttest, judged by xterm rather than by a person (QS33).
///
/// <para><b>Why there is an oracle at all.</b> vttest draws a screen and asks whoever is looking
/// whether it is right. Its verdict is a person, so it cannot be a test until something else gives
/// the answer. <c>Vttest/oracle</c> is a container that drives vttest on a real 80x24 pty, keeps
/// every byte, cuts the stream wherever vttest stops and waits for RETURN, and then has a real
/// xterm draw each cut and print its own screen with Media Copy. xterm is the terminal vttest's
/// screens were written against; a screen this emulator draws as xterm does is a screen it
/// passed.</para>
///
/// <para><b>What it compares is the text of every row.</b> Attributes are not in xterm's printout,
/// so a rendition test is judged on where its words land and not on whether they are bold. That
/// is said in the report rather than implied by a pass.</para>
///
/// <para>Regenerating the oracle is <c>docker build -t qs-vttest tests/Quickshell.Terminal.Tests/Vttest/oracle</c>
/// and <c>docker run --rm -v &lt;this folder&gt;:/out qs-vttest</c>; the cuts and xterm's screens are
/// committed, so the suite needs neither.</para>
/// </summary>
public sealed class VttestTests
{
    private const int Columns = 80;
    private const int Rows = 24;

    /// <summary>
    /// The cuts this emulator draws differently from xterm, each with why. A cut not in this list
    /// that disagrees is a regression; a cut in it that now agrees is a list that has gone stale.
    /// </summary>
    private static readonly Dictionary<int, string> Known = new()
    {
        [10] = "QS208: 132 columns asked for with CSI ? 3 h; xterm obeys, this keeps 80",
        [12] = "QS208: the same 132-column screen, light background",
        [25] = "QS207: insert mode (CSI 4 h) overwrites instead of pushing the B right",
        [26] = "QS207: inherits cut 025's row, so its AB is AA",
        [30] = "QS208: the VT102 test's 132-column pass",
    };

    [Fact]
    public void EveryVttestScreenIsDrawnAsXtermDrawsIt()
    {
        string folder = Path.Combine(Repository.Root, "tests", "Quickshell.Terminal.Tests", "Vttest");
        byte[] stream = Unzipped(Path.Combine(folder, "vttest.raw.gz"));
        using JsonDocument listed = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "cuts.json")));
        Cut[] cuts = [.. listed.RootElement.EnumerateArray().Select(
            cut => new Cut(cut.GetProperty("name").GetString()!, cut.GetProperty("offset").GetInt32()))];

        List<string> regressions = [];
        List<string> stale = [];
        int agreed = 0;

        for (int number = 0; number < cuts.Length; number++)
        {
            string[] xterm = Screen(File.ReadAllLines(Path.Combine(folder, "xterm", $"{number:000}.txt")));
            string[] ours = Drawn(stream.AsSpan(0, cuts[number].Offset));

            int row = Enumerable.Range(0, Rows).FirstOrDefault(index => xterm[index] != ours[index], -1);

            if (row < 0)
            {
                agreed++;

                if (Known.ContainsKey(number))
                {
                    stale.Add($"{number:000} now agrees with xterm; drop it from Known");
                }

                continue;
            }

            if (!Known.ContainsKey(number))
            {
                regressions.Add($"{number:000} ({cuts[number].Name}) row {row + 1}:\n"
                                + $"  xterm: [{xterm[row]}]\n  ours:  [{ours[row]}]");
            }
        }

        Assert.True(regressions.Count == 0 && stale.Count == 0,
            $"{agreed} of {cuts.Length} vttest screens agree with xterm.\n"
            + string.Join("\n", regressions.Concat(stale)));
    }

    private static string[] Drawn(ReadOnlySpan<byte> prefix)
    {
        Emulator emulator = new(Columns, Rows, scrollback: 0);
        emulator.Feed(prefix);

        string[] rows = new string[Rows];

        for (int row = 0; row < Rows; row++)
        {
            StringBuilder text = new();

            foreach (Cell cell in emulator.Buffer.Screen(row))
            {
                if (cell.Width != 0)
                {
                    text.Append(emulator.Buffer.TextOf(cell));
                }
            }

            rows[row] = text.ToString().TrimEnd();
        }

        return rows;
    }

    private static string[] Screen(string[] printed) =>
        [.. Enumerable.Range(0, Rows).Select(row => row < printed.Length ? printed[row].TrimEnd() : string.Empty)];

    private static byte[] Unzipped(string path)
    {
        using FileStream file = File.OpenRead(path);
        using GZipStream unzip = new(file, CompressionMode.Decompress);
        using MemoryStream bytes = new();
        unzip.CopyTo(bytes);

        return bytes.ToArray();
    }

    private sealed record Cut(string Name, int Offset);
}
