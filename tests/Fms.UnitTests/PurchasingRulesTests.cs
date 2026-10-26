using Fms.BuildingBlocks.Security;
using Fms.Purchasing.Domain;

namespace Fms.UnitTests;

public class PurchasingRulesTests
{
    [Theory]
    [InlineData(0.01, 1)]
    [InlineData(5000, 1)]       // exactly at the first limit is still level 1
    [InlineData(5000.01, 2)]
    [InlineData(50000, 2)]
    [InlineData(50000.01, 3)]
    [InlineData(2_000_000, 3)]
    public void The_total_decides_the_approval_level(double total, int level) =>
        Assert.Equal(level, PurchasingRules.RequiredLevel((decimal)total, 5000m, 50000m));

    [Fact]
    public void A_tenant_with_its_own_limits_gets_its_own_levels() => Assert.Equal(2, PurchasingRules.RequiredLevel(1500m, 1000m, 10000m));

    private static IReadOnlySet<string> Perms(params string[] p) => new HashSet<string>(p);

    [Fact]
    public void The_highest_approval_permission_sets_the_level()
    {
        Assert.Equal(0, PurchasingRules.LevelHeld(Perms(Permissions.PurchasingRequest)));
        Assert.Equal(1, PurchasingRules.LevelHeld(Perms(Permissions.PurchasingApprove1)));
        Assert.Equal(2, PurchasingRules.LevelHeld(Perms(Permissions.PurchasingApprove1, Permissions.PurchasingApprove2)));
        Assert.Equal(3, PurchasingRules.LevelHeld(Perms(Permissions.PurchasingApprove3)));
        Assert.Equal(2, PurchasingRules.LevelHeld(Perms(Permissions.PurchasingApprove2))); // each level includes those below it
    }

    [Theory]
    [InlineData(1, 1, true)]
    [InlineData(2, 1, true)]
    [InlineData(1, 2, false)]
    [InlineData(0, 1, false)]
    [InlineData(3, 3, true)]
    public void An_approver_needs_enough_level(int held, int required, bool allowed) =>
        Assert.Equal(allowed, PurchasingRules.CanApprove(held, required, Guid.NewGuid(), Guid.NewGuid()));

    [Fact]
    public void Nobody_approves_their_own_request_whatever_their_level()
    {
        var me = Guid.NewGuid();
        Assert.False(PurchasingRules.CanApprove(3, 1, me, me));
    }

    [Theory]
    [InlineData(1000, 1000, 10, true)]
    [InlineData(1100, 1000, 10, true)]     // exactly at the tolerance
    [InlineData(1100.01, 1000, 10, false)]
    [InlineData(1000.01, 1000, 0, false)]  // no tolerance at all
    [InlineData(500, 1000, 10, true)]      // cheaper is always fine
    public void An_order_may_exceed_the_approved_total_only_within_the_tolerance(double order, double approved, double tolerance, bool ok) =>
        Assert.True(ok == PurchasingRules.WithinTolerance((decimal)order, (decimal)approved, (decimal)tolerance));

    [Fact]
    public void Line_totals_are_rounded_to_cents() => Assert.Equal(3.33m, PurchasingRules.LineTotal(3m, 1.11111m));

    [Fact]
    public void An_order_is_received_when_every_line_is_complete()
    {
        Assert.Equal(OrderStatus.PartiallyReceived, PurchasingRules.StatusAfterReceiving([(10m, 10m), (5m, 0m)]));
        Assert.Equal(OrderStatus.Received, PurchasingRules.StatusAfterReceiving([(10m, 10m), (5m, 5m)]));
        Assert.Equal(OrderStatus.PartiallyReceived, PurchasingRules.StatusAfterReceiving([(10m, 9.9999m)]));
    }

    [Fact]
    public void Numbers_read_as_pr_and_po()
    {
        Assert.Equal("PR-000042", PurchasingRules.DisplayRequest(42));
        Assert.Equal("PO-000007", PurchasingRules.DisplayOrder(7));
    }
}
