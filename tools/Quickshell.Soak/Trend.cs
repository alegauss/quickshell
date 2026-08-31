using System.Globalization;

namespace Quickshell.Soak;

/// <summary>
/// One watched counter, and whether it is flat.
///
/// <para><b>Flat is the criterion rather than merely bounded</b>, and that distinction is the whole
/// reason this class exists. A counter that rises slowly and stays under a limit for three days is a
/// leak that reaches the limit in three weeks — and three weeks is an ordinary uptime for a terminal
/// client. A bound cannot tell those apart. A slope can.</para>
///
/// <para><b>Least squares over the samples after warm-up.</b> The first minutes of any process are
/// caches filling and JIT happening, which is a rise that is not a leak; counting them would fail
/// every run. What is reported is the slope per hour and what that slope reaches in twenty-one days,
/// because the extrapolation is the number a reader can argue with.</para>
/// </summary>
internal sealed class Trend(string name, string unit, double tolerance)
{
    private readonly List<(double Hours, double Value)> _samples = [];

    /// <summary>What is being watched.</summary>
    public string Name { get; } = name;

    /// <summary>What its numbers are in.</summary>
    public string Unit { get; } = unit;

    /// <summary>
    /// How much growth over twenty-one days is not called a leak, in <see cref="Unit"/>.
    ///
    /// <para>Not zero: a sampled counter on a real machine has noise, and a tolerance of zero would
    /// report a leak on measurement jitter. It is a stated number so that raising it is a decision
    /// somebody makes in a diff.</para>
    /// </summary>
    public double Tolerance { get; } = tolerance;

    /// <summary>How many samples are behind the verdict.</summary>
    public int Samples => _samples.Count;

    /// <summary>The first value taken, for the reader's sense of scale.</summary>
    public double First => _samples.Count == 0 ? double.NaN : _samples[0].Value;

    /// <summary>The last value taken.</summary>
    public double Last => _samples.Count == 0 ? double.NaN : _samples[^1].Value;

    /// <summary>The highest value seen.</summary>
    public double Peak => _samples.Count == 0 ? double.NaN : _samples.Max(sample => sample.Value);

    /// <summary>Takes one reading.</summary>
    public void Took(TimeSpan since, double value) => _samples.Add((since.TotalHours, value));

    /// <summary>
    /// The least-squares slope, per hour. NaN until there are two readings at different times.
    /// </summary>
    public double PerHour
    {
        get
        {
            if (_samples.Count < 2)
            {
                return double.NaN;
            }

            double meanHours = _samples.Average(sample => sample.Hours);
            double meanValue = _samples.Average(sample => sample.Value);

            double covariance = _samples.Sum(sample => (sample.Hours - meanHours)
                                                       * (sample.Value - meanValue));
            double spread = _samples.Sum(sample => (sample.Hours - meanHours)
                                                    * (sample.Hours - meanHours));

            return spread <= 0 ? double.NaN : covariance / spread;
        }
    }

    /// <summary>What this slope reaches over an ordinary uptime, which is what makes it arguable.</summary>
    public double OverThreeWeeks => PerHour * 24 * 21;

    /// <summary>
    /// How long the samples span. A slope over a short span extrapolated over three weeks is a
    /// number with no information in it.
    /// </summary>
    public TimeSpan Span =>
        _samples.Count < 2
            ? TimeSpan.Zero
            : TimeSpan.FromHours(_samples[^1].Hours - _samples[0].Hours);

    /// <summary>
    /// Whether this counter is flat: its three-week extrapolation is within tolerance.
    ///
    /// <para>A falling counter is flat for this purpose. Memory being released is not a defect, and a
    /// verdict that failed on it would be measuring the garbage collector's schedule.</para>
    /// </summary>
    public bool Flat => double.IsNaN(PerHour) || OverThreeWeeks <= Tolerance;

    /// <summary>
    /// Whether there is enough of a run behind this to say anything at all.
    ///
    /// <para><b>The first validation run of this tool is why this exists.</b> Three minutes of a
    /// parser reaching full speed fitted a slope of 35,523 MB/h, which extrapolated to seventeen
    /// million megabytes over three weeks and reported a leak. The slope was arithmetically correct
    /// and told nobody anything: what it measured was warm-up, over an interval too short for a
    /// trend to mean a trend. A tool that reports a leak on every short run is a tool whose verdict
    /// everybody learns to skip — which is the same defect as a suite that prints "Passed" while
    /// skipping a hundred tests.</para>
    ///
    /// <para>So a verdict needs both: hours of span, and enough samples that one outlier cannot set
    /// the line. Below that the numbers are still printed — they are what a person watching the run
    /// wants — and the verdict column says it is too short instead of guessing.</para>
    /// </summary>
    public bool Judgeable => Span >= MinimumSpan && Samples >= MinimumSamples;

    /// <summary>The shortest run a verdict is issued for.</summary>
    public static TimeSpan MinimumSpan { get; } = TimeSpan.FromHours(2);

    /// <summary>The fewest samples a verdict is issued on.</summary>
    public static int MinimumSamples => 60;

    /// <summary>One row of the report.</summary>
    public string Row()
    {
        string verdict = !Judgeable
            ? "too short to judge"
            : Flat ? "flat" : "**RISING**";

        // The extrapolation is withheld where it would be nonsense rather than printed with a
        // caveat beside it: a number on the page is a number somebody quotes.
        string reach = Judgeable
            ? OverThreeWeeks.ToString("F1", CultureInfo.InvariantCulture)
            : "—";

        string slope = Judgeable
            ? PerHour.ToString("F3", CultureInfo.InvariantCulture)
            : "—";

        return string.Create(CultureInfo.InvariantCulture,
                             $"| {Name} | {First:F1} | {Last:F1} | {Peak:F1} | {slope} | "
                             + $"{reach} | {Tolerance:F1} | {verdict} |");
    }
}
