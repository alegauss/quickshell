using Quickshell.Gate;
using Xunit;

namespace Quickshell.Gate.Tests;

/// <summary>
/// QS79: the gate's judgement, which is where a measurement becomes a verdict.
///
/// <para>The measuring is a desk's and runs on the machine the figures are defined for. What is
/// checked here is everything between the samples and the exit code: that a threshold comes from the
/// samples and lets every one of them through, that a worsening past it fails and one short of it
/// does not, which way is worse for each figure, and that only a commit's own trailer excuses one.</para>
/// </summary>
public sealed class GateTests
{
    /// <summary>
    /// A threshold is read off the samples: it is never below their whole spread, so every sample a
    /// baseline took passes its own gate.
    /// </summary>
    [Fact]
    public void AThresholdLetsThroughEverySampleItWasTakenFrom()
    {
        double[] samples = [100, 104, 98, 101, 99, 103, 97];

        double threshold = Gate.Threshold(samples);
        double median = Gate.Median(samples);

        Assert.Equal(100, median);
        Assert.True(threshold >= (104 - 97) / 100.0, $"a threshold of {threshold:P1} would refuse a sample the baseline took");
        Assert.All(samples, sample => Assert.True((median - sample) / median <= threshold));
    }

    /// <summary>A quiet figure gets a tight threshold and a noisy one a wide one: the noise is the desk's, and so is the allowance.</summary>
    [Fact]
    public void ANoisierFigureIsAllowedMore()
    {
        double quiet = Gate.Threshold([100, 100.5, 99.5, 100.2, 99.8, 100.1, 99.9]);
        double noisy = Gate.Threshold([100, 110, 90, 105, 95, 108, 92]);

        Assert.True(quiet < 0.02, $"a figure that moved half a per cent was allowed {quiet:P1}");
        Assert.True(noisy > quiet * 5, $"a figure ten times noisier was allowed {noisy:P1} against {quiet:P1}");
    }

    /// <summary>One sample says nothing about noise, so no threshold is derived from it.</summary>
    [Fact]
    public void OneSampleIsNotABaseline() =>
        Assert.Throws<ArgumentException>(() => Gate.Threshold([100]));

    /// <summary>
    /// Throughput worsens downward and time upward: the same five per cent is a failure on the way
    /// down for one and on the way up for the other.
    /// </summary>
    [Theory]
    [InlineData("parse", 1000, 940, Verdict.Worse)]
    [InlineData("parse", 1000, 1060, Verdict.Better)]
    [InlineData("parse", 1000, 970, Verdict.Held)]
    [InlineData("start", 600, 636, Verdict.Worse)]
    [InlineData("start", 600, 564, Verdict.Better)]
    [InlineData("start", 600, 612, Verdict.Held)]
    public void WorseIsTheWayEachFigureWorsens(string figure, double was, double now, Verdict expected)
    {
        Dictionary<string, Standing> baseline = new() { [figure] = new(was, 0.05, [was]) };

        Judgement judged = Assert.Single(Gate.Judge(baseline, new Dictionary<string, double> { [figure] = now }, []));

        Assert.Equal(expected, judged.Verdict);
    }

    /// <summary>
    /// A worsening a commit named in its trailer is accepted, and says which commit and why; one it did
    /// not name still fails beside it.
    /// </summary>
    [Fact]
    public void OnlyTheFigureACommitNamedIsExcused()
    {
        Dictionary<string, Standing> baseline = new()
        {
            ["start"] = new(600, 0.05, [600]),
            ["parse"] = new(1000, 0.05, [1000]),
        };

        Dictionary<string, double> now = new() { ["start"] = 700, ["parse"] = 800 };

        Move meant = new("abcdef1234567", "start", "the window builds its find bar up front");

        IReadOnlyList<Judgement> judged = Gate.Judge(baseline, now, [meant]);

        Judgement start = judged.Single(one => one.Figure.Name == "start");
        Judgement parse = judged.Single(one => one.Figure.Name == "parse");

        Assert.Equal(Verdict.Accepted, start.Verdict);
        Assert.Equal(meant, start.Because);
        Assert.Equal(Verdict.Worse, parse.Verdict);
        Assert.Null(parse.Because);
    }

    /// <summary>
    /// Where two commits traded the same figure, the verdict names the newer: git prints the stretch
    /// newest first, and the trade nearest the check is the one the figure now reflects.
    /// </summary>
    [Fact]
    public void TheNewestTradeForAFigureIsTheOneNamed()
    {
        Dictionary<string, Standing> baseline = new() { ["start"] = new(600, 0.05, [600]) };

        Move newer = new("3333333333", "start", "the tab strip is built up front");
        Move older = new("1111111111", "start", "the find bar is built up front");

        Judgement judged = Assert.Single(Gate.Judge(baseline, new Dictionary<string, double> { ["start"] = 700 }, [newer, older]));

        Assert.Equal(newer, judged.Because);
    }

    /// <summary>
    /// The trailer is read off the log git prints for the stretch since the baseline: one per line it
    /// starts, with the commit that carried it, and not where a sentence merely quotes it.
    /// </summary>
    [Fact]
    public void AMoveIsATrailerAtTheStartOfALine()
    {
        string log =
            "1111111111\0fix: something\n\nPerformance-Moved: start - the find bar is built up front\n"
            + "Co-Authored-By: somebody\n\u0001"
            + "2222222222\0docs: explain the gate\n\nA commit says Performance-Moved: parse - quoted, not declared\n\u0001"
            + "3333333333\0perf: trade\n\nPerformance-Moved: Emulate: scrollback grew to what users asked for\n\u0001";

        IReadOnlyList<Move> moves = Gate.Moves(log);

        Assert.Equal(
            [
                new Move("1111111111", "start", "the find bar is built up front"),
                new Move("3333333333", "emulate", "scrollback grew to what users asked for"),
            ],
            moves);
    }

    /// <summary>
    /// Every figure the gate holds says which way it improves, and each name is one word a trailer
    /// can carry.
    /// </summary>
    [Fact]
    public void EveryFigureIsNamedByOneWord() =>
        Assert.All(Gate.Figures, figure => Assert.Matches("^[a-z]+$", figure.Name));
}
