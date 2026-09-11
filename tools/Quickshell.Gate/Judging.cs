using System.Globalization;
using System.Text.RegularExpressions;

namespace Quickshell.Gate;

/// <summary>Which way a figure improves.</summary>
public enum Better
{
    /// <summary>More is better: throughput.</summary>
    Higher,

    /// <summary>Less is better: time.</summary>
    Lower,
}

/// <summary>One figure the gate holds: its name in a commit trailer, its unit, and which way is good.</summary>
/// <param name="Name">The one word a commit names it by.</param>
/// <param name="Unit">What its numbers are in.</param>
/// <param name="Better">Which way it improves.</param>
/// <param name="What">Where it comes from, in a sentence.</param>
public sealed record Figure(string Name, string Unit, Better Better, string What);

/// <summary>What a baseline says about one figure: where it stood, and how far it may move.</summary>
/// <param name="Median">The baseline's own median.</param>
/// <param name="Threshold">The fraction it may worsen by before the gate refuses it.</param>
/// <param name="Samples">Every sample the baseline took, kept so the threshold can be read back.</param>
public sealed record Standing(double Median, double Threshold, IReadOnlyList<double> Samples);

/// <summary>A commit that said, in a trailer, that a figure was moved on purpose.</summary>
/// <param name="Commit">The commit that said so.</param>
/// <param name="Figure">The figure it named.</param>
/// <param name="Why">What it said the trade was.</param>
public sealed record Move(string Commit, string Figure, string Why);

/// <summary>How a check went for one figure.</summary>
public enum Verdict
{
    /// <summary>Within the threshold of the baseline.</summary>
    Held,

    /// <summary>Better than the baseline by more than the threshold: worth a new baseline.</summary>
    Better,

    /// <summary>Worse by more than the threshold, and nothing said it was meant.</summary>
    Worse,

    /// <summary>Worse by more than the threshold, and a commit said so and why.</summary>
    Accepted,
}

/// <summary>One figure, judged.</summary>
/// <param name="Figure">Which.</param>
/// <param name="Was">The baseline's median.</param>
/// <param name="Now">This check's median.</param>
/// <param name="Worse">How much worse, as a fraction of the baseline; negative where it is better.</param>
/// <param name="Threshold">How much worse it was allowed to be.</param>
/// <param name="Verdict">What that makes it.</param>
/// <param name="Because">The commit that accepted it, where one did.</param>
public sealed record Judgement(Figure Figure, double Was, double Now, double Worse, double Threshold,
                               Verdict Verdict, Move? Because);

/// <summary>
/// QS79: a measurement that fails a build instead of reporting to whoever reads it.
///
/// <para><b>The threshold is derived, not chosen.</b> A baseline takes several samples of every
/// figure, and the threshold is the larger of two things read off them: three robust standard
/// deviations — the median absolute deviation, scaled — and the whole spread the baseline itself
/// saw. So every sample the baseline took would pass its own gate, and a figure that is noisy on
/// this desk is allowed that noise and no more. It is written into the baseline file beside the
/// samples it came from.</para>
///
/// <para><b>A regression can be meant.</b> A commit that trades a figure for something says so in
/// a trailer — <c>Performance-Moved: start - the window builds its find bar up front</c> — and the
/// gate lets that figure through, naming the commit and the reason. Silence is what is refused, not
/// the regression. The excuse is one-shot: the gate then asks for a new baseline before a release,
/// because a trailer left in range would go on excusing that figure by any amount.</para>
/// </summary>
public static partial class Gate
{
    /// <summary>The figures the gate holds today, in the order it reports them.</summary>
    public static readonly IReadOnlyList<Figure> Figures =
    [
        new("parse", "MB/s", Better.Higher, "the replay harness's parse arm on the 32 MB cat-log stream, figure 2"),
        new("emulate", "MB/s", Better.Higher, "the replay harness's emulate arm on the same stream, what a session costs"),
        new("start", "ms", Better.Lower, "the release publish's warm start to its first interactive frame, figure 5"),
    ];

    /// <summary>The scale that turns a median absolute deviation into a standard deviation.</summary>
    private const double Consistency = 1.4826;

    /// <summary>
    /// How far a figure may worsen, as a fraction of its median, derived from the samples a
    /// baseline took of it.
    /// </summary>
    public static double Threshold(IReadOnlyList<double> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);

        if (samples.Count < 2)
        {
            throw new ArgumentException("a threshold is derived from at least two samples", nameof(samples));
        }

        double median = Median(samples);

        if (median <= 0)
        {
            throw new ArgumentException("a figure whose median is not positive has no scale to move on", nameof(samples));
        }

        double deviation = Median([.. samples.Select(sample => Math.Abs(sample - median))]);
        double spread = (samples.Max() - samples.Min()) / median;

        return Math.Max(3 * Consistency * deviation / median, spread);
    }

    /// <summary>The middle of a set of samples.</summary>
    public static double Median(IReadOnlyList<double> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);

        double[] sorted = [.. samples.Order()];

        return sorted.Length % 2 == 1
            ? sorted[sorted.Length / 2]
            : (sorted[(sorted.Length / 2) - 1] + sorted[sorted.Length / 2]) / 2;
    }

    /// <summary>
    /// Judges each figure a check measured against where the baseline had it.
    /// </summary>
    /// <param name="baseline">The standings, by figure name.</param>
    /// <param name="now">This check's median, by figure name.</param>
    /// <param name="moves">What commits since the baseline said they moved on purpose.</param>
    public static IReadOnlyList<Judgement> Judge(IReadOnlyDictionary<string, Standing> baseline,
                                                 IReadOnlyDictionary<string, double> now,
                                                 IReadOnlyList<Move> moves)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(now);
        ArgumentNullException.ThrowIfNull(moves);

        List<Judgement> judged = [];

        foreach (Figure figure in Figures)
        {
            if (!baseline.TryGetValue(figure.Name, out Standing? was) || !now.TryGetValue(figure.Name, out double measured))
            {
                continue;
            }

            double worse = figure.Better == Better.Higher
                ? (was.Median - measured) / was.Median
                : (measured - was.Median) / was.Median;

            // The newest trade for this figure: git lists the stretch newest first, and the trade
            // nearest the check is the one the figure now reflects.
            Move? because = moves.FirstOrDefault(move => string.Equals(move.Figure, figure.Name, StringComparison.OrdinalIgnoreCase));

            Verdict verdict = Weigh(worse, was.Threshold, because);

            judged.Add(new Judgement(figure, was.Median, measured, worse, was.Threshold, verdict,
                                     verdict == Verdict.Accepted ? because : null));
        }

        return judged;
    }

    /// <summary>What a worsening of this size makes a figure, given its allowance and any trade that named it.</summary>
    private static Verdict Weigh(double worse, double threshold, Move? because)
    {
        if (worse > threshold)
        {
            return because is null ? Verdict.Worse : Verdict.Accepted;
        }

        return worse < -threshold ? Verdict.Better : Verdict.Held;
    }

    /// <summary>
    /// The moves a stretch of history declares, read from <c>git log --format=%H%x00%B%x01</c>:
    /// every <c>Performance-Moved: &lt;figure&gt; - &lt;why&gt;</c> trailer, with the commit that
    /// carried it.
    /// </summary>
    public static IReadOnlyList<Move> Moves(string log)
    {
        ArgumentNullException.ThrowIfNull(log);

        List<Move> moves = [];

        foreach (string entry in log.Split('\u0001', StringSplitOptions.RemoveEmptyEntries))
        {
            int split = entry.IndexOf('\0', StringComparison.Ordinal);

            if (split < 0)
            {
                continue;
            }

            string commit = entry[..split].Trim();

            foreach (Match declared in Trailer.Matches(entry[(split + 1)..]))
            {
                moves.Add(new Move(commit, declared.Groups["figure"].Value.ToLowerInvariant(),
                                   declared.Groups["why"].Value.Trim()));
            }
        }

        return moves;
    }

    /// <summary>A number in a figure's unit, the way the report prints it.</summary>
    public static string Show(double value, Figure figure)
    {
        ArgumentNullException.ThrowIfNull(figure);

        return value.ToString(value >= 100 ? "0" : "0.0", CultureInfo.InvariantCulture) + " " + figure.Unit;
    }

    /// <summary>The trailer, anchored at a line's start so a sentence quoting it is not one.</summary>
    [GeneratedRegex(@"^Performance-Moved:[ \t]*(?<figure>[A-Za-z]+)[ \t]*[-:–—][ \t]*(?<why>[^\r\n]+)$",
                    RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex Trailer { get; }
}
