using Fms.Vendors.Domain;

namespace Fms.UnitTests;

public class ContractRulesTests
{
    private static readonly DateOnly Today = new(2026, 6, 15);

    private static Contract Make(int startOffset, int endOffset, int notice = 60, bool terminated = false) => new()
    {
        StartOn = Today.AddDays(startOffset), EndOn = Today.AddDays(endOffset), RenewalNoticeDays = notice, TerminatedAt = terminated ? DateTimeOffset.UtcNow : null,
    };

    [Theory]
    [InlineData(1, 300, ContractStatus.Upcoming)]       // starts tomorrow
    [InlineData(0, 300, ContractStatus.Active)]         // starts today
    [InlineData(-100, 61, ContractStatus.Active)]       // just outside the notice window
    [InlineData(-100, 60, ContractStatus.Expiring)]     // exactly on the notice day
    [InlineData(-100, 0, ContractStatus.Expiring)]      // the last day still counts
    [InlineData(-100, -1, ContractStatus.Expired)]      // the day after
    public void Status_follows_the_dates(int start, int end, ContractStatus expected) => Assert.Equal(expected, ContractRules.StatusOn(Make(start, end), Today));

    [Fact]
    public void Terminated_wins_over_any_date() => Assert.Equal(ContractStatus.Terminated, ContractRules.StatusOn(Make(-10, 300, terminated: true), Today));

    [Theory]
    [InlineData(1, 300, false)]
    [InlineData(0, 300, true)]
    [InlineData(-100, 0, true)]
    [InlineData(-100, -1, false)]
    public void Only_started_and_not_ended_contracts_are_in_force(int start, int end, bool inForce) => Assert.Equal(inForce, ContractRules.InForce(Make(start, end), Today));

    [Fact]
    public void A_terminated_contract_is_never_in_force() => Assert.False(ContractRules.InForce(Make(-10, 300, terminated: true), Today));

    [Fact]
    public void A_zero_day_notice_only_expires_after_the_end() => Assert.Equal(ContractStatus.Active, ContractRules.StatusOn(Make(-10, 1, notice: 0), Today));

    [Fact]
    public void Days_left_counts_to_the_end_date() => Assert.Equal(40, ContractRules.DaysLeft(Make(-10, 40), Today));
}
