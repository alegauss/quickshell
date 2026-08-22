using System.Text;
using Quickshell.Terminal;
using Xunit;

namespace Quickshell.Terminal.Tests;

/// <summary>
/// The decoder and the segmenter, against streams whose boundaries were chosen to hurt.
/// </summary>
public sealed class DecodingTests
{
    /// <summary>
    /// The falsification this design names, and the only test here that matters on its own: a
    /// stream fed one byte at a time must produce what that stream fed whole produces. Every way of
    /// getting the pending tail wrong shows up here and almost nowhere else.
    /// </summary>
    [Theory]
    [InlineData("plain ascii only")]
    [InlineData("中文字 and 日本語")]
    [InlineData("café naïve über")]
    [InlineData("é decomposed")]
    [InlineData("\U0001F600\U0001F680 emoji")]
    [InlineData("\U0001F1E7\U0001F1F7 flag")]
    [InlineData("\U0001F469‍\U0001F4BB zwj sequence")]
    [InlineData("mixed é 中 \U0001F534 Ж all at once")]
    public void OneByteAtATimeIsTheSameAsAllAtOnce(string original)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(original);

        StreamDecoder whole = new();
        string atOnce = new(whole.Decode(bytes));
        atOnce += new string(whole.Flush());

        StreamDecoder trickle = new();
        StringBuilder oneAtATime = new();

        foreach (byte value in bytes)
        {
            oneAtATime.Append(trickle.Decode([value]));
        }

        oneAtATime.Append(trickle.Flush());

        Assert.Equal(original, atOnce);
        Assert.Equal(original, oneAtATime.ToString());
    }

    /// <summary>The same claim for clusters, which can straddle far more reads than a character can.</summary>
    [Theory]
    [InlineData("\U0001F469‍\U0001F4BB", 1)]
    [InlineData("\U0001F1E7\U0001F1F7", 1)]
    [InlineData("é", 1)]
    [InlineData("á̧̈", 1)]
    // Devanagari na, virama, na. Two clusters and not one: UAX #29's default algorithm does not
    // join across a virama, and the tailored rules that would are not what a host counts columns
    // with either. Expecting one here was wrong about Unicode, not about this code.
    [InlineData("न्न", 2)]
    [InlineData("ab", 2)]
    [InlineData("\U0001F600\U0001F600", 2)]
    public void AClusterSurvivesBeingSplitAcrossEveryBoundary(string original, int expected)
    {
        Assert.Equal(expected, GraphemeSegmenter.Clusters(original).Count());

        for (int split = 0; split <= original.Length; split++)
        {
            GraphemeSegmenter segmenter = new();
            List<string> clusters = [.. segmenter.Feed(original.AsSpan(0, split))];
            clusters.AddRange(segmenter.Feed(original.AsSpan(split)));
            clusters.AddRange(segmenter.Flush());

            Assert.True(expected == clusters.Count,
                $"split at {split} of {original.Length} gave {clusters.Count} clusters " +
                $"({string.Join("|", clusters.Select(c => string.Join(",", c.Select(ch => ((int)ch).ToString("X4")))))}) " +
                $"but the whole string gives {expected}");
            Assert.Equal(original, string.Concat(clusters));
        }
    }

    [Fact]
    public void ACharacterSplitAcrossTwoReadsIsOneCharacterAndNotTwoBrokenOnes()
    {
        byte[] bytes = Encoding.UTF8.GetBytes("中");
        Assert.Equal(3, bytes.Length);

        StreamDecoder decoder = new();

        // The first two bytes complete nothing at all.
        Assert.Empty(new string(decoder.Decode(bytes.AsSpan(0, 2))));
        Assert.True(decoder.HasPending);

        Assert.Equal("中", new string(decoder.Decode(bytes.AsSpan(2))));
        Assert.False(decoder.HasPending);
    }

    // ---- Nothing a host can send may end the session ----

    [Theory]
    [InlineData(new byte[] { 0xFF })]
    [InlineData(new byte[] { 0xC0, 0x80 })]                     // overlong NUL
    [InlineData(new byte[] { 0xE0, 0x80, 0x80 })]               // overlong
    [InlineData(new byte[] { 0xED, 0xA0, 0x80 })]               // a surrogate, which UTF-8 forbids
    [InlineData(new byte[] { 0xF5, 0x80, 0x80, 0x80 })]         // beyond U+10FFFF
    [InlineData(new byte[] { 0x41, 0xC2, 0x41 })]               // a truncated sequence mid-text
    [InlineData(new byte[] { 0xE2, 0x28, 0xA1 })]               // an invalid continuation
    public void ArbitraryBytesProduceReplacementsAndNeverAnException(byte[] hostile)
    {
        StreamDecoder decoder = new();

        string text = new string(decoder.Decode(hostile)) + new string(decoder.Flush());

        Assert.Contains('�', text);
    }

    /// <summary>
    /// One replacement per maximal subpart, which is the rule that keeps a stream of rubbish from
    /// becoming a stream of one replacement per byte.
    /// </summary>
    [Fact]
    public void AnInvalidSequenceIsOneReplacementPerMaximalSubpart()
    {
        StreamDecoder decoder = new();

        // E1 80 is the start of a three-byte sequence; E2 F5 breaks after one byte. The Unicode
        // rules give one replacement for the truncated E1 80, then one for the lone E2.
        string text = new string(decoder.Decode([0xE1, 0x80, 0xE2, 0x41]));

        Assert.Equal("��A", text);
    }

    [Fact]
    public void ADroppedConnectionMidCharacterFlushesOneReplacement()
    {
        StreamDecoder decoder = new();

        Assert.Empty(new string(decoder.Decode([0xE4, 0xB8])));
        Assert.Equal("�", new string(decoder.Flush()));
        Assert.False(decoder.HasPending);
    }

    [Fact]
    public void AResetForgetsThePendingTail()
    {
        StreamDecoder decoder = new();

        decoder.Decode([0xE4, 0xB8]);
        decoder.Reset();

        Assert.False(decoder.HasPending);
        Assert.Equal("A", new string(decoder.Decode([0x41])));
    }

    /// <summary>The encoding is a session setting, so a stream that is not UTF-8 decodes too.</summary>
    [Fact]
    public void AStreamInAnotherEncodingIsDecodedByTheOneItWasToldAbout()
    {
        Encoding latin1 = Encoding.Latin1;
        StreamDecoder decoder = new(latin1);

        Assert.Equal("café", new string(decoder.Decode(latin1.GetBytes("café"))));
        Assert.Same(latin1, decoder.Encoding);
    }

    // ---- What a cell holds ----

    [Fact]
    public void TheSegmenterHoldsBackTheLastClusterUntilItKnowsWhatFollows()
    {
        GraphemeSegmenter segmenter = new();

        // 'e' alone might still be the base of a cluster, so nothing is emitted yet.
        Assert.Empty(segmenter.Feed("e"));
        Assert.Equal(1, segmenter.Pending);

        // The acute proves it was, and still nothing is emitted: another mark could follow.
        Assert.Empty(segmenter.Feed("́"));

        // 'x' starts a new cluster, which is what finishes the one before it.
        Assert.Equal(["é"], segmenter.Feed("x"));
        Assert.Equal(["x"], segmenter.Flush());
    }

    [Theory]
    [InlineData("a", 1)]
    [InlineData("中", 2)]
    [InlineData("\U0001F600", 2)]
    [InlineData("é", 1)]
    [InlineData("\U0001F469‍\U0001F4BB", 2)]
    [InlineData("\U0001F1E7\U0001F1F7", 2)]
    public void AClusterIsAsWideAsItsBase(string cluster, int cells)
    {
        Assert.Equal(cells, CharacterWidth.OfCluster(cluster));
    }

    // ---- The generated table ----

    [Fact]
    public void TheWidthTableRecordsTheUnicodeItCameFrom()
    {
        Assert.Matches(@"^\d+\.\d+\.\d+$", CharacterWidth.UnicodeVersion);
    }

    [Theory]
    [InlineData('A', 1)]
    [InlineData(0x4E2D, 2)]      // CJK
    [InlineData(0xFF21, 2)]      // fullwidth A
    [InlineData(0xAC00, 2)]      // Hangul syllable
    [InlineData(0x1F600, 2)]     // emoji presentation
    [InlineData(0x0301, 0)]      // combining acute
    [InlineData(0x200D, 0)]      // zero-width joiner
    [InlineData(0xFE0F, 0)]      // variation selector 16
    [InlineData(0x0416, 1)]      // Cyrillic Zhe
    [InlineData(0x2764, 1)]      // heavy black heart: text presentation by default, so one cell
    public void TheGeneratedTableAgreesWithWhatAHostWouldHaveDecided(int codepoint, int cells)
    {
        Assert.Equal(cells, CharacterWidth.Of(codepoint));
    }

    [Fact]
    public void AMixedLineTotalsTheColumnsTheHostWouldHave()
    {
        Assert.Equal(1 + 1 + 2 + 0 + 2, CharacterWidth.Of("ab中́\U0001F600"));
    }
}
