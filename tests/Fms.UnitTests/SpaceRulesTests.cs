using Fms.Spaces.Domain;

namespace Fms.UnitTests;

public sealed class SpaceRulesTests
{
    private static DateOnly D(int day) => new DateOnly(2027, 1, 1).AddDays(day - 1);

    [Theory]
    [InlineData(1, 5, 6, 9, false)]
    [InlineData(1, 5, 5, 9, true)]
    [InlineData(1, 5, 3, 4, true)]
    [InlineData(6, 9, 1, 5, false)]
    public void Periods_overlap_when_they_share_a_day(int a1, int a2, int b1, int b2, bool expected) =>
        Assert.Equal(expected, SpaceRules.Overlap(D(a1), D(a2), D(b1), D(b2)));

    [Fact]
    public void An_open_ended_period_overlaps_everything_after_its_start()
    {
        Assert.True(SpaceRules.Overlap(D(10), null, D(500), D(501)));
        Assert.False(SpaceRules.Overlap(D(10), null, D(1), D(9)));
        Assert.True(SpaceRules.Overlap(D(10), null, D(1), null));
    }

    [Fact]
    public void The_peak_is_what_is_in_the_space_on_the_busiest_day()
    {
        var existing = new (DateOnly, DateOnly?, int)[] { (D(1), D(10), 2), (D(5), D(20), 3), (D(30), null, 1) };
        Assert.Equal(5, SpaceRules.Peak(existing, D(1), D(40)));
        Assert.Equal(2, SpaceRules.Peak(existing, D(1), D(4)));
        Assert.Equal(3, SpaceRules.Peak(existing, D(11), D(25)));
        Assert.Equal(0, SpaceRules.Peak(existing, D(21), D(29))); // nobody between the 21st and 29th
        Assert.Equal(1, SpaceRules.Peak(existing, D(100), null));
    }

    [Fact]
    public void Back_to_back_allocations_do_not_add_up()
    {
        var existing = new (DateOnly, DateOnly?, int)[] { (D(1), D(10), 4), (D(11), D(20), 4) };
        Assert.Equal(4, SpaceRules.Peak(existing, D(1), D(20)));
    }

    [Fact]
    public void A_period_starting_inside_a_busy_stretch_sees_it()
    {
        var existing = new (DateOnly, DateOnly?, int)[] { (D(1), D(10), 4) };
        Assert.Equal(4, SpaceRules.Peak(existing, D(8), D(30)));
        Assert.Equal(0, SpaceRules.Peak(existing, D(11), D(30)));
    }

    [Fact]
    public void Nothing_allocated_is_a_peak_of_zero() => Assert.Equal(0, SpaceRules.Peak([], D(1), null));

    [Theory]
    [InlineData(0.0, true)]
    [InlineData(1.0, true)]
    [InlineData(0.5, true)]
    [InlineData(-0.01, false)]
    [InlineData(1.01, false)]
    [InlineData(double.NaN, false)]
    [InlineData(double.PositiveInfinity, false)]
    public void Pin_positions_are_fractions_of_the_plan(double v, bool ok) => Assert.Equal(ok, SpaceRules.IsFraction(v));

    [Theory]
    [InlineData(0, 0, null)]
    [InlineData(10, 5, 50.0)]
    [InlineData(3, 1, 33.3)]
    [InlineData(4, 5, 125.0)]
    public void Utilisation_is_a_percentage_of_capacity_or_unknown(int capacity, int allocated, double? expected) =>
        Assert.Equal(expected is null ? null : (decimal)expected, SpaceRules.UtilisationPercent(capacity, allocated));

    [Fact]
    public void A_day_is_active_inside_its_period_only()
    {
        Assert.True(SpaceRules.IsActiveOn(D(5), D(10), D(5)));
        Assert.True(SpaceRules.IsActiveOn(D(5), D(10), D(10)));
        Assert.False(SpaceRules.IsActiveOn(D(5), D(10), D(11)));
        Assert.True(SpaceRules.IsActiveOn(D(5), null, D(4000)));
    }
}
