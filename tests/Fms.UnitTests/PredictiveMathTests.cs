using Fms.BuildingBlocks.Identity;
using Fms.Work.Domain;

namespace Fms.UnitTests;

public sealed class PredictiveMathTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 30, 12, 0, 0, TimeSpan.Zero);

    private static List<DateTimeOffset> At(params double[] daysAgo) => daysAgo.Select(d => Now.AddDays(-d)).ToList();

    [Fact]
    public void Gaps_that_have_shrunk_to_half_or_less_are_a_hint_with_the_numbers_and_an_expected_date()
    {
        // Gaps: 60, 60, 20, 10 -> earlier median 60, last two average 15.
        var trend = PredictiveMath.Failures(At(150, 90, 30, 10, 0));
        Assert.NotNull(trend);
        Assert.Equal(5, trend!.Events);
        Assert.Equal(60, trend.EarlierMedianDays);
        Assert.Equal(15, trend.RecentMeanDays);
        Assert.Equal(Now, trend.LastAt);
        Assert.Equal(Now.AddDays(15), trend.ExpectedBy);
    }

    [Fact]
    public void Steady_gaps_or_too_few_repairs_give_no_hint()
    {
        Assert.Null(PredictiveMath.Failures(At(120, 90, 60, 30, 0)));
        Assert.Null(PredictiveMath.Failures(At(60, 20, 10))); // only three repairs
        Assert.Null(PredictiveMath.Failures(At(100, 99, 98, 97, 96))); // the "earlier" gap is a single day: nothing to compare against
        Assert.Null(PredictiveMath.Failures(At(145, 95, 55, 25, 0))); // gaps 50, 40, 30, 25: recent 27.5 against median 45 is only 0.61
    }

    [Fact]
    public void The_boundary_is_inclusive_and_order_of_input_does_not_matter()
    {
        // Gaps 40, 40, 20, 20 -> median 40, recent mean 20 = exactly half.
        Assert.NotNull(PredictiveMath.Failures(At(120, 80, 40, 20, 0)));
        Assert.NotNull(PredictiveMath.Failures(At(0, 40, 120, 20, 80)));
        // Gaps 40, 40, 21, 21: just above half.
        Assert.Null(PredictiveMath.Failures(At(122, 82, 42, 21, 0)));
    }

    private static MeterValue R(double daysAgo, decimal value) => new(value, Now.AddDays(-daysAgo));

    [Fact]
    public void A_meter_climbing_at_least_one_and_a_half_times_faster_than_the_month_before_is_a_hint()
    {
        // Prior window (14..44 days ago): 10 per day. Recent (last 14 days): 20 per day.
        var readings = new List<MeterValue> { R(44, 0), R(14.5, 295), R(10, 1000), R(0, 1200) };
        // prior: 0 -> 295 over 29.5 days = 10/day. recent: 1000 -> 1200 over 10 days = 20/day.
        var pace = PredictiveMath.Pace(readings, Now);
        Assert.NotNull(pace);
        Assert.Equal(20, pace!.RecentPerDay);
        Assert.Equal(10, pace.PriorPerDay);
        Assert.Equal(2, pace.Ratio);
    }

    [Fact]
    public void Similar_pace_too_little_data_and_resets_do_not_make_a_hint()
    {
        Assert.Null(PredictiveMath.Pace([R(44, 0), R(14.5, 300), R(10, 300), R(0, 400)], Now)); // 10/day then 10/day
        Assert.Null(PredictiveMath.Pace([R(40, 0), R(20, 200), R(1, 400)], Now)); // only one reading in the last 14 days
        Assert.Null(PredictiveMath.Pace([R(13, 0), R(12, 100)], Now)); // less than three days of readings
        Assert.Null(PredictiveMath.Pace([R(30, 100), R(25, 150), R(5, 160), R(0, 170)], Now)); // prior covers five days only
    }

    [Fact]
    public void A_replaced_meter_that_restarts_lower_is_skipped_not_counted_as_a_fall_or_a_rise()
    {
        // Prior: 0 -> 100 -> 200 over 30 days (about 6.7/day). Recent: 9000 -> 20 (reset) -> 160: only the 140 rise counts, over 10 days = 14/day.
        var readings = new List<MeterValue> { R(44, 0), R(30, 100), R(15, 200), R(10, 9000), R(5, 20), R(0, 160) };
        var pace = PredictiveMath.Pace(readings, Now);
        Assert.NotNull(pace);
        Assert.Equal(14, pace!.RecentPerDay);
    }

    [Fact]
    public void Median_of_an_even_and_odd_list()
    {
        Assert.Equal(2, PredictiveMath.Median([3, 1, 2]));
        Assert.Equal(2.5, PredictiveMath.Median([4, 1, 2, 3]));
    }
}
