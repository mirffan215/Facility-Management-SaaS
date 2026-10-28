using Fms.Energy.Domain;

namespace Fms.UnitTests;

public sealed class EnergyMathTests
{
    private static readonly DateTimeOffset D0 = new(2027, 3, 1, 0, 0, 0, TimeSpan.Zero);

    private static (DateTimeOffset, decimal) R(int hours, decimal v) => (D0.AddHours(hours), v);

    [Fact]
    public void A_counting_meter_uses_the_difference_between_consecutive_readings()
    {
        var u = EnergyMath.UsageFrom([R(0, 100), R(24, 130), R(48, 190)], cumulative: true, rolloverAt: null);
        Assert.Equal([30m, 60m], u.Intervals.Select(i => i.Amount));
        Assert.Equal(0, u.Resets);
    }

    [Fact]
    public void Readings_are_ordered_by_time_not_by_arrival() =>
        Assert.Equal([30m, 60m], EnergyMath.UsageFrom([R(48, 190), R(0, 100), R(24, 130)], true, null).Intervals.Select(i => i.Amount));

    [Fact]
    public void One_reading_gives_no_use_for_a_counting_meter() =>
        Assert.Empty(EnergyMath.UsageFrom([R(0, 100)], true, null).Intervals);

    [Fact]
    public void A_lower_reading_without_a_wrap_value_is_a_reset_counted_and_left_out()
    {
        var u = EnergyMath.UsageFrom([R(0, 900), R(24, 950), R(48, 20), R(72, 70)], true, null);
        Assert.Equal([50m, 50m], u.Intervals.Select(i => i.Amount));
        Assert.Equal(1, u.Resets);
    }

    [Fact]
    public void A_lower_reading_on_a_meter_that_wraps_is_the_wrap_around()
    {
        var u = EnergyMath.UsageFrom([R(0, 99_900), R(24, 50)], true, 100_000);
        Assert.Equal(150m, Assert.Single(u.Intervals).Amount);
        Assert.Equal(0, u.Resets);
    }

    [Fact]
    public void An_amount_meter_counts_each_reading_as_the_use_of_its_period()
    {
        var u = EnergyMath.UsageFrom([R(0, 5), R(1, 7), R(2, 3)], cumulative: false, rolloverAt: null);
        Assert.Equal([5m, 7m, 3m], u.Intervals.Select(i => i.Amount));
    }

    [Fact]
    public void Use_is_spread_evenly_over_the_days_an_interval_covers()
    {
        var days = EnergyMath.ByDay([new Usage(D0, D0.AddDays(3), 300m)]);
        Assert.Equal(3, days.Count);
        Assert.All(days.Values, v => Assert.Equal(100m, v));
    }

    [Fact]
    public void An_interval_across_midnight_is_split_by_time()
    {
        var days = EnergyMath.ByDay([new Usage(D0.AddHours(18), D0.AddHours(30), 120m)]); // 6 h on day one, 6 h on day two
        Assert.Equal(60m, days[new DateOnly(2027, 3, 1)]);
        Assert.Equal(60m, days[new DateOnly(2027, 3, 2)]);
    }

    [Fact]
    public void Several_intervals_in_one_day_add_up_and_the_total_is_never_lost()
    {
        var intervals = new[] { new Usage(D0.AddHours(1), D0.AddHours(5), 10m), new Usage(D0.AddHours(5), D0.AddHours(29), 24m), new Usage(D0.AddHours(29), D0.AddHours(29), 4m) };
        var days = EnergyMath.ByDay(intervals);
        Assert.Equal(38m, days.Values.Sum());
        Assert.Equal(10m + 24m * 19 / 24, days[new DateOnly(2027, 3, 1)]);
    }

    [Fact]
    public void An_instant_lands_on_its_own_day() =>
        Assert.Equal(7m, EnergyMath.ByDay([new Usage(D0.AddHours(30), D0.AddHours(30), 7m)])[new DateOnly(2027, 3, 2)]);

    [Fact]
    public void The_newest_tariff_started_by_the_day_applies_and_none_before_the_first_does()
    {
        var tariffs = new[]
        {
            new Tariff { Kind = EnergyKind.Electricity, EffectiveFrom = new DateOnly(2027, 1, 1), PricePerUnit = 0.18m },
            new Tariff { Kind = EnergyKind.Electricity, EffectiveFrom = new DateOnly(2027, 3, 1), PricePerUnit = 0.20m },
        };
        Assert.Null(EnergyMath.TariffOn(tariffs, new DateOnly(2026, 12, 31)));
        Assert.Equal(0.18m, EnergyMath.TariffOn(tariffs, new DateOnly(2027, 2, 28))!.PricePerUnit);
        Assert.Equal(0.20m, EnergyMath.TariffOn(tariffs, new DateOnly(2027, 3, 1))!.PricePerUnit);
        Assert.Equal(0.20m, EnergyMath.TariffOn(tariffs, new DateOnly(2030, 1, 1))!.PricePerUnit);
    }
}
