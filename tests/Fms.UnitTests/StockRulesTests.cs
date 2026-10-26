using Fms.Inventory.Domain;

namespace Fms.UnitTests;

public class StockRulesTests
{
    [Fact]
    public void Available_is_what_is_on_hand_less_what_is_reserved() => Assert.Equal(6, StockRules.Available(10, 4));

    [Theory]
    [InlineData(0, 0, 10, 5, 5)]       // an empty store takes the new cost outright
    [InlineData(10, 5, 10, 7, 6)]      // equal quantities average evenly
    [InlineData(10, 5, 30, 9, 8)]      // weighted by quantity: (50 + 270) / 40
    [InlineData(3, 10, 1, 6, 9)]       // (30 + 6) / 4
    [InlineData(10, 5, 5, 5, 5)]       // same cost changes nothing
    public void Receipts_move_the_weighted_average(decimal onHand, decimal average, decimal quantity, decimal cost, decimal expected) =>
        Assert.Equal(expected, StockRules.AverageAfterReceipt(onHand, average, quantity, cost));

    [Fact]
    public void The_average_is_rounded_to_four_decimals() => Assert.Equal(3.3333m, StockRules.AverageAfterReceipt(2, 3, 1, 4));

    [Theory]
    [InlineData(10, 0, 5, false)]
    [InlineData(5, 0, 5, true)]        // at the minimum is low
    [InlineData(10, 6, 5, true)]       // 4 available against a minimum of 5
    [InlineData(10, 4, 5, false)]
    public void Low_means_available_is_at_or_below_the_minimum(decimal onHand, decimal reserved, decimal min, bool expected) =>
        Assert.Equal(expected, StockRules.IsLow(onHand, reserved, min));

    [Fact]
    public void Without_a_minimum_stock_is_never_low() => Assert.False(StockRules.IsLow(0, 0, null));

    [Theory]
    [InlineData(2, 0, 5, 20.0, 18)]      // up to the maximum
    [InlineData(2, 0, 5, null, 8)]     // no maximum: twice the minimum
    [InlineData(9, 0, 5, 20.0, 0)]       // not low: nothing to order
    public void The_suggested_order_gets_back_to_the_maximum(decimal onHand, decimal reserved, decimal min, double? max, decimal expected) =>
        Assert.Equal(expected, StockRules.SuggestedOrder(onHand, reserved, min, max is null ? null : (decimal)max));

    [Theory]
    [InlineData(1, true)]
    [InlineData(0.0001, true)]
    [InlineData(0, false)]
    [InlineData(-1, false)]
    [InlineData(0.00001, false)]       // more decimals than stored
    [InlineData(2_000_000_000, false)]
    public void Quantities_must_be_positive_and_fit(double quantity, bool valid) => Assert.Equal(valid, StockRules.IsValidQuantity((decimal)quantity));

    [Theory]
    [InlineData(0, true)]
    [InlineData(12.3456, true)]
    [InlineData(-0.01, false)]
    [InlineData(1.23456, false)]
    public void Costs_may_be_zero_but_not_negative(double cost, bool valid) => Assert.Equal(valid, StockRules.IsValidCost((decimal)cost));
}
