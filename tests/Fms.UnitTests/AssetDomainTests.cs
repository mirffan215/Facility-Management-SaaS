using Fms.Facilities.Domain;

namespace Fms.UnitTests;

public sealed class AssetDomainTests
{
    [Fact]
    public void New_tags_are_ten_crockford_characters_and_distinct()
    {
        var tags = Enumerable.Range(0, 1000).Select(_ => AssetTag.New()).ToList();

        Assert.All(tags, t =>
        {
            Assert.Equal(10, t.Length);
            Assert.DoesNotContain(t, c => "ILOU".Contains(c));
            Assert.Equal(t, AssetTag.Normalize(t));
        });
        Assert.Equal(1000, tags.Distinct().Count());
    }

    [Theory]
    [InlineData("abcde-fghjk", "ABCDEFGHJK")]
    [InlineData(" ABCDE FGHJK ", "ABCDEFGHJK")]
    [InlineData("0O0O0-1I1L1", "0000011111")]
    [InlineData("abcdefghjk", "ABCDEFGHJK")]
    public void Scanner_and_typing_variants_normalise(string input, string expected) => Assert.Equal(expected, AssetTag.Normalize(input));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("SHORT")]
    [InlineData("ABCDEFGHJKM")]
    [InlineData("ABCDE-FGHJ!")]
    [InlineData("UUUUUUUUUU")]
    public void Malformed_tags_do_not_normalise(string? input) => Assert.Null(AssetTag.Normalize(input));

    [Fact]
    public void Tags_are_grouped_for_printing() => Assert.Equal("ABCDE-FGHJK", AssetTag.Format("ABCDEFGHJK"));

    [Theory]
    [InlineData(AssetStatus.Planned, AssetStatus.Active, true)]
    [InlineData(AssetStatus.Planned, AssetStatus.Retired, true)]
    [InlineData(AssetStatus.Planned, AssetStatus.UnderMaintenance, false)]
    [InlineData(AssetStatus.Active, AssetStatus.UnderMaintenance, true)]
    [InlineData(AssetStatus.Active, AssetStatus.Planned, false)]
    [InlineData(AssetStatus.UnderMaintenance, AssetStatus.Active, true)]
    [InlineData(AssetStatus.OutOfService, AssetStatus.Active, true)]
    [InlineData(AssetStatus.Retired, AssetStatus.Active, false)]
    [InlineData(AssetStatus.Retired, AssetStatus.Retired, false)]
    [InlineData(AssetStatus.Active, AssetStatus.Active, false)]
    public void Status_transitions(AssetStatus from, AssetStatus to, bool allowed) => Assert.Equal(allowed, AssetRules.CanTransition(from, to));

    [Fact]
    public void Retired_is_final()
    {
        foreach (var to in Enum.GetValues<AssetStatus>()) Assert.False(AssetRules.CanTransition(AssetStatus.Retired, to));
    }

    [Theory]
    [InlineData(AssetStatus.OutOfService, true)]
    [InlineData(AssetStatus.Retired, true)]
    [InlineData(AssetStatus.Active, false)]
    [InlineData(AssetStatus.UnderMaintenance, false)]
    public void Only_out_of_service_and_retired_need_a_reason(AssetStatus to, bool needs) => Assert.Equal(needs, AssetRules.NeedsReason(to));

    [Fact]
    public void Warranty_states_around_today()
    {
        var today = new DateOnly(2026, 10, 25);
        Assert.Equal(WarrantyState.None, AssetRules.WarrantyOf(null, today));
        Assert.Equal(WarrantyState.Expired, AssetRules.WarrantyOf(today.AddDays(-1), today));
        Assert.Equal(WarrantyState.ExpiringSoon, AssetRules.WarrantyOf(today, today));
        Assert.Equal(WarrantyState.ExpiringSoon, AssetRules.WarrantyOf(today.AddDays(30), today));
        Assert.Equal(WarrantyState.Active, AssetRules.WarrantyOf(today.AddDays(31), today));
    }
}
