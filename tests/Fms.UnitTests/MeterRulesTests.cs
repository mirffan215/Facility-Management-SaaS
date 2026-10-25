using Fms.Facilities.Domain;

namespace Fms.UnitTests;

public sealed class MeterRulesTests
{
    [Theory]
    [InlineData(100, false, null, null, true)]            // first reading
    [InlineData(100, false, 90, null, true)]              // increasing
    [InlineData(100, false, 100, null, true)]             // unchanged
    [InlineData(80, false, 90, null, false)]              // decreasing
    [InlineData(80, true, 90, null, true)]                // reset allows lower
    public void Against_the_previous_reading(int value, bool reset, int? previous, int? next, bool ok) =>
        Assert.Equal(ok, MeterRules.CheckCumulative(value, reset, previous, next is null ? null : (next.Value, false)) is null);

    [Fact]
    public void A_backdated_value_must_not_exceed_the_next_reading()
    {
        Assert.Null(MeterRules.CheckCumulative(95, false, 90, (100m, false)));
        Assert.NotNull(MeterRules.CheckCumulative(105, false, 90, (100m, false)));
    }

    [Fact]
    public void A_later_reset_restarts_the_sequence_so_it_does_not_cap_earlier_values()
    {
        Assert.Null(MeterRules.CheckCumulative(5000, false, 4000, (10m, true)));
    }

    [Theory]
    [InlineData("kWh", true)]
    [InlineData("°C", true)]
    [InlineData("م³", true)]
    [InlineData("", false)]
    [InlineData("this unit label is far too long", false)]
    public void Units(string unit, bool valid) => Assert.Equal(valid, MeterRules.IsValidUnit(unit));
}
