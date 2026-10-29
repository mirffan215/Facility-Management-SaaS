using Fms.BuildingBlocks.Identity;

namespace Fms.Work.Domain;

public sealed record FailureTrend(int Events, double EarlierMedianDays, double RecentMeanDays, DateTimeOffset LastAt, DateTimeOffset ExpectedBy);

public sealed record PaceChange(double RecentPerDay, double PriorPerDay, double Ratio);

/// <summary>
/// The arithmetic behind the predictive hints. Plain statistics on the numbers we hold, no model: every hint can be checked by hand.
/// </summary>
public static class PredictiveMath
{
    public const int MinFailures = 4;
    public const double FailureShrink = 0.5;
    public const int RecentDays = 14;
    public const int PriorDays = 30;
    public const double PaceRise = 1.5;
    private const int MinRecentSpanDays = 3;
    private const int MinPriorSpanDays = 7;

    /// <summary>
    /// Whether the gaps between repairs are getting shorter. The last two gaps are averaged and compared with the median of the earlier gaps; a mean at or below half of the median is a hint.
    /// Null when there are fewer than four repairs, or when the gaps are not shrinking.
    /// </summary>
    public static FailureTrend? Failures(IReadOnlyList<DateTimeOffset> times)
    {
        if (times.Count < MinFailures) return null;
        var sorted = times.OrderBy(t => t).ToList();
        var gaps = sorted.Zip(sorted.Skip(1), (a, b) => (b - a).TotalDays).ToList();
        var recent = gaps.Skip(gaps.Count - 2).Average();
        var earlier = Median(gaps.Take(gaps.Count - 2).ToList());
        if (earlier < 1 || recent > earlier * FailureShrink) return null;
        return new FailureTrend(sorted.Count, Math.Round(earlier, 1), Math.Round(recent, 1), sorted[^1], sorted[^1].AddDays(recent));
    }

    /// <summary>
    /// Whether a running total is climbing faster than it was. Rate is the sum of rises divided by the days covered; a fall (a replaced meter) is skipped, not counted.
    /// Needs three days of readings in the last two weeks and seven in the month before.
    /// </summary>
    public static PaceChange? Pace(IReadOnlyList<MeterValue> readings, DateTimeOffset now)
    {
        var split = now.AddDays(-RecentDays);
        var recent = Rate(readings.Where(r => r.ReadAt >= split && r.ReadAt <= now).OrderBy(r => r.ReadAt).ToList(), MinRecentSpanDays);
        var prior = Rate(readings.Where(r => r.ReadAt < split && r.ReadAt >= split.AddDays(-PriorDays)).OrderBy(r => r.ReadAt).ToList(), MinPriorSpanDays);
        if (recent is null || prior is not > 0) return null;
        var ratio = recent.Value / prior.Value;
        return ratio >= PaceRise ? new PaceChange(Math.Round(recent.Value, 2), Math.Round(prior.Value, 2), Math.Round(ratio, 2)) : null;
    }

    public static double? Rate(IReadOnlyList<MeterValue> ordered, int minSpanDays)
    {
        if (ordered.Count < 2) return null;
        var span = (ordered[^1].ReadAt - ordered[0].ReadAt).TotalDays;
        if (span < minSpanDays) return null;
        var rise = 0m;
        for (var i = 1; i < ordered.Count; i++)
            if (ordered[i].Value > ordered[i - 1].Value) rise += ordered[i].Value - ordered[i - 1].Value;
        return (double)rise / span;
    }

    public static double Median(IReadOnlyList<double> values)
    {
        var s = values.OrderBy(v => v).ToList();
        return s.Count % 2 == 1 ? s[s.Count / 2] : (s[s.Count / 2 - 1] + s[s.Count / 2]) / 2;
    }
}
