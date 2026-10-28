using Fms.Compliance.Domain;

namespace Fms.UnitTests;

public sealed class ComplianceRulesTests
{
    private static readonly DateOnly Today = new(2027, 6, 15);

    private static Obligation O(Action<Obligation>? tweak = null)
    {
        var o = new Obligation { Title = "x", LeadDays = 30 };
        tweak?.Invoke(o);
        return o;
    }

    [Theory]
    [InlineData(-1, ComplianceStatus.Overdue)]
    [InlineData(0, ComplianceStatus.DueSoon)]
    [InlineData(30, ComplianceStatus.DueSoon)]
    [InlineData(31, ComplianceStatus.Compliant)]
    [InlineData(400, ComplianceStatus.Compliant)]
    public void A_valid_certificate_is_judged_by_how_close_its_end_is(int daysUntilEnd, ComplianceStatus expected) =>
        Assert.Equal(expected, ComplianceRules.StatusOf(O(o => { o.LastCompletedOn = Today.AddDays(-100); o.ValidUntil = Today.AddDays(daysUntilEnd); o.LastResult = CompletionResult.Passed; }), Today));

    [Theory]
    [InlineData(-5, ComplianceStatus.Overdue)]
    [InlineData(10, ComplianceStatus.DueSoon)]
    [InlineData(90, ComplianceStatus.Scheduled)]
    public void Something_never_done_is_judged_by_its_first_due_date(int daysUntilFirstDue, ComplianceStatus expected) =>
        Assert.Equal(expected, ComplianceRules.StatusOf(O(o => o.FirstDueOn = Today.AddDays(daysUntilFirstDue)), Today));

    [Fact]
    public void A_failure_outranks_dates() =>
        Assert.Equal(ComplianceStatus.Failed, ComplianceRules.StatusOf(O(o => { o.LastCompletedOn = Today; o.ValidUntil = Today.AddDays(300); o.LastResult = CompletionResult.Failed; }), Today));

    [Fact]
    public void A_one_off_that_was_done_stays_compliant_and_one_without_a_date_is_only_scheduled()
    {
        Assert.Equal(ComplianceStatus.Compliant, ComplianceRules.StatusOf(O(o => { o.LastCompletedOn = Today.AddYears(-3); o.LastResult = CompletionResult.Passed; }), Today));
        Assert.Equal(ComplianceStatus.Scheduled, ComplianceRules.StatusOf(O(), Today));
    }

    [Fact]
    public void The_first_due_date_stops_mattering_once_something_has_been_done()
    {
        var o = O(x => { x.FirstDueOn = Today.AddDays(-100); x.LastCompletedOn = Today.AddDays(-10); x.ValidUntil = Today.AddDays(355); x.LastResult = CompletionResult.Passed; });
        Assert.Equal(ComplianceStatus.Compliant, ComplianceRules.StatusOf(o, Today));
        Assert.Equal(Today.AddDays(355), ComplianceRules.DueOn(o));
    }

    [Theory]
    [InlineData(12, "2028-01-31", "2027-01-31")]
    [InlineData(1, "2027-03-31", "2027-02-28")]
    [InlineData(6, "2027-08-31", "2027-02-28")]
    public void Validity_is_the_performed_date_plus_the_interval_in_calendar_months(int months, string performed, string _)
    {
        var p = DateOnly.Parse(performed).AddMonths(-months);
        Assert.Equal(p.AddMonths(months), ComplianceRules.DefaultValidUntil(p, months));
    }

    [Fact]
    public void A_one_off_has_no_default_end() => Assert.Null(ComplianceRules.DefaultValidUntil(Today, null));

    [Fact]
    public void Month_end_dates_do_not_overflow() => Assert.Equal(new DateOnly(2027, 2, 28), ComplianceRules.DefaultValidUntil(new DateOnly(2027, 1, 31), 1));

    [Theory]
    [InlineData(PermitStatus.Issued, true)]
    [InlineData(PermitStatus.Started, true)]
    [InlineData(PermitStatus.Closed, false)]
    [InlineData(PermitStatus.Requested, false)]
    [InlineData(PermitStatus.Cancelled, false)]
    public void Only_permits_in_force_can_be_overdue(PermitStatus status, bool expected) =>
        Assert.Equal(expected, ComplianceRules.IsOverdue(status, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow));

    [Fact]
    public void A_permit_in_force_is_not_overdue_until_its_end_has_passed() =>
        Assert.False(ComplianceRules.IsOverdue(PermitStatus.Issued, DateTimeOffset.UtcNow.AddMinutes(1), DateTimeOffset.UtcNow));

    [Fact]
    public void Windows_overlap_only_when_they_share_time_and_touching_ends_do_not()
    {
        var t = new DateTimeOffset(2027, 1, 1, 8, 0, 0, TimeSpan.Zero);
        Assert.True(ComplianceRules.Overlaps(t, t.AddHours(4), t.AddHours(2), t.AddHours(6)));
        Assert.True(ComplianceRules.Overlaps(t, t.AddHours(4), t.AddHours(1), t.AddHours(2)));
        Assert.False(ComplianceRules.Overlaps(t, t.AddHours(4), t.AddHours(4), t.AddHours(6)));
        Assert.False(ComplianceRules.Overlaps(t.AddHours(4), t.AddHours(6), t, t.AddHours(4)));
    }
}
