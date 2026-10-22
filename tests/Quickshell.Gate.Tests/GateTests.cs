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

    /// <summary>
    /// QS253's falsification: a desk that runs a seventh slower on one day than another fails
    /// nothing, because the baseline's code ran beside the tree and slowed with it. The rounds are
    /// the 2026-10-10 shape: every figure about 14 per cent worse than the baseline's numbers, and
    /// the baseline's own commit just as slow.
    /// </summary>
    [Fact]
    public void ADeskThatSlowedEverythingFailsNothing()
    {
        Dictionary<string, Paired> rounds = new()
        {
            ["parse"] = new([992, 1005, 980, 1010], [967, 1012, 990, 1001]),
            ["emulate"] = new([46.2, 45.3, 47.0, 45.8], [48.6, 44.9, 46.1, 46.5]),
            ["start"] = new([558, 542, 550, 561], [552, 549, 560, 555]),
        };

        IReadOnlyList<Judgement> judged = Gate.JudgeSideBySide(rounds, []);

        Assert.Equal(3, judged.Count);
        Assert.All(judged, judgement => Assert.Equal(Verdict.Held, judgement.Verdict));
    }

    /// <summary>
    /// And a change in the code still shows through a desk that drifts while it is measured: emulate
    /// a tenth slower in every round, with the desk itself wandering by more than that between rounds.
    /// </summary>
    [Fact]
    public void ACodeRegressionShowsThroughADriftingDesk()
    {
        double[] desk = [1.00, 0.86, 0.93, 0.80];
        double[] reference = [.. desk.Select(speed => 54 * speed)];
        double[] now = [.. reference.Select(rate => rate * 0.90)];

        Judgement judged = Assert.Single(Gate.JudgeSideBySide(
            new Dictionary<string, Paired> { ["emulate"] = new(now, reference) }, []));

        Assert.Equal(Verdict.Worse, judged.Verdict);
        Assert.InRange(judged.Worse, 0.099, 0.101);
    }

    /// <summary>Rounds pair one to one, and one round says nothing about noise.</summary>
    [Fact]
    public void RoundsThatDoNotPairAreRefused()
    {
        Assert.Throws<ArgumentException>(() => Gate.JudgeSideBySide(
            new Dictionary<string, Paired> { ["parse"] = new([1000, 990], [1000]) }, []));

        Assert.Throws<ArgumentException>(() => Gate.JudgeSideBySide(
            new Dictionary<string, Paired> { ["parse"] = new([1000], [1000]) }, []));
    }

    /// <summary>
    /// A sitting too noisy to see a tenth is not a pass. The rounds are 2026-10-10's, taken while
    /// other projects ran on the desk: the allowance they derive is over forty per cent, so a hold
    /// there would have let a regression that size through.
    /// </summary>
    [Fact]
    public void ASittingTooNoisyToSeeATenthJudgesNothing()
    {
        Judgement judged = Assert.Single(Gate.JudgeSideBySide(
            new Dictionary<string, Paired> { ["parse"] = new([700, 900, 760, 610], [720, 640, 900, 700]) }, []));

        Assert.Equal(Verdict.Noisy, judged.Verdict);
    }

    /// <summary>
    /// QS375: a sitting quieter than the baseline allows the baseline's noise and no less. The rounds are
    /// 2026-10-22's at 1fa8c98, parse 0.7 % worse in every round with 0.6 % of spread, which refused a
    /// release where the baseline had measured 1.46 %. A worsening past the floor still fails.
    /// </summary>
    [Fact]
    public void AQuietSittingAllowsTheBaselinesOwnNoise()
    {
        Dictionary<string, Standing> floors = new() { ["parse"] = new(1155, 0.0146, [1155, 1150]) };
        double[] reference = [1192, 1190, 1195, 1191];

        Judgement within = Assert.Single(Gate.JudgeSideBySide(
            new Dictionary<string, Paired> { ["parse"] = new([1184, 1180, 1185, 1183], reference) }, [], floors));
        Judgement past = Assert.Single(Gate.JudgeSideBySide(
            new Dictionary<string, Paired> { ["parse"] = new([.. reference.Select(rate => rate * 0.97)], reference) }, [], floors));

        Assert.Equal(Verdict.Held, within.Verdict);
        Assert.Equal(0.0146, within.Threshold, 4);
        Assert.Equal(Verdict.Worse, past.Verdict);
    }

    /// <summary>And a floor above the noisy bound does not make every sitting too noisy: emulate's is 17.7 %.</summary>
    [Fact]
    public void AFloorAboveTheNoisyBoundStillJudges()
    {
        Dictionary<string, Standing> floors = new() { ["emulate"] = new(53.6, 0.177, [53.6, 51.2]) };

        Judgement judged = Assert.Single(Gate.JudgeSideBySide(
            new Dictionary<string, Paired> { ["emulate"] = new([246, 247, 245, 246], [56.2, 56.0, 56.4, 56.1]) }, [], floors));

        Assert.Equal(Verdict.Better, judged.Verdict);
    }
}
