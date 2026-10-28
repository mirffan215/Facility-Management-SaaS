using Fms.Leases.Domain;

namespace Fms.UnitTests;

public sealed class LeaseMathTests
{
    private static readonly DateOnly Today = new(2026, 6, 1);

    private static Lease Lease(Action<Lease>? edit = null)
    {
        var l = new Lease
        {
            Status = LeaseStatus.Active, StartOn = new DateOnly(2026, 1, 1), EndOn = new DateOnly(2026, 12, 31), BaseRent = 1200, Frequency = RentFrequency.Monthly, NoticeDays = 60, AlertLeadDays = 90,
        };
        edit?.Invoke(l);
        return l;
    }

    [Theory]
    [InlineData(LeaseStatus.Draft, "2026-06-01", LeaseState.Draft)]
    [InlineData(LeaseStatus.Terminated, "2026-06-01", LeaseState.Terminated)]
    [InlineData(LeaseStatus.Active, "2026-12-31", LeaseState.Active)]
    [InlineData(LeaseStatus.Active, "2027-01-01", LeaseState.Expired)]
    public void State_comes_from_the_stored_status_and_the_dates(LeaseStatus status, string today, LeaseState expected) =>
        Assert.Equal(expected, LeaseMath.StateOf(Lease(l => l.Status = status), DateOnly.Parse(today)));

    [Fact]
    public void Deadlines_are_the_notice_date_the_break_notice_date_and_the_end_in_date_order_without_the_ones_passed()
    {
        var l = Lease(x => { x.BreakOn = new DateOnly(2026, 9, 1); x.BreakNoticeDays = 30; });
        var d = LeaseMath.Upcoming(l, Today);
        Assert.Equal([DeadlineKind.Break, DeadlineKind.Notice, DeadlineKind.Expiry], d.Select(x => x.Kind));
        Assert.Equal(new DateOnly(2026, 8, 2), d[0].On);
        Assert.Equal(new DateOnly(2026, 11, 1), d[1].On);

        Assert.Equal([DeadlineKind.Expiry], LeaseMath.Upcoming(l, new DateOnly(2026, 11, 2)).Select(x => x.Kind));
        Assert.Empty(LeaseMath.Upcoming(l, new DateOnly(2027, 1, 1)));
        Assert.DoesNotContain(LeaseMath.Upcoming(Lease(x => x.NoticeDays = 0), Today), x => x.Kind == DeadlineKind.Notice);
    }

    [Theory]
    [InlineData("2026-07-01", null)] // the first deadline (notice on 2026-11-01) is more than 90 days away
    [InlineData("2026-08-03", "notice:2026-11-01")]
    [InlineData("2026-11-01", "notice:2026-11-01")]
    [InlineData("2026-11-02", "expiry:2026-12-31")]
    [InlineData("2027-01-05", "expired:2026-12-31")]
    public void The_alert_key_names_the_deadline_in_range_or_the_expiry(string today, string? expected) =>
        Assert.Equal(expected, LeaseMath.AlertKey(Lease(), DateOnly.Parse(today)));

    [Fact]
    public void Only_active_leases_are_warned_about_and_a_longer_lead_warns_earlier()
    {
        Assert.Null(LeaseMath.AlertKey(Lease(l => l.Status = LeaseStatus.Draft), new DateOnly(2027, 1, 5)));
        Assert.Null(LeaseMath.AlertKey(Lease(l => l.Status = LeaseStatus.Terminated), new DateOnly(2026, 11, 2)));
        Assert.Equal("notice:2026-11-01", LeaseMath.AlertKey(Lease(l => l.AlertLeadDays = 200), new DateOnly(2026, 5, 1)));
    }

    [Fact]
    public void Extending_changes_the_key_so_warnings_start_afresh()
    {
        var l = Lease();
        var before = LeaseMath.AlertKey(l, new DateOnly(2026, 11, 2));
        l.EndOn = new DateOnly(2027, 12, 31);
        Assert.NotEqual(before, LeaseMath.AlertKey(l, new DateOnly(2026, 11, 2)));
    }

    [Fact]
    public void A_flat_schedule_has_one_instalment_per_period()
    {
        var s = LeaseMath.Schedule(Lease());
        Assert.Equal(12, s.Count);
        Assert.All(s, i => Assert.Equal(1200m, i.Amount));
        Assert.Equal(new DateOnly(2026, 12, 1), s[^1].DueOn);
        Assert.Equal(new DateOnly(2026, 12, 31), s[^1].PeriodEnd);
        Assert.False(s[^1].IsPartial);
        Assert.Equal(1, s[0].Number);
    }

    [Theory]
    [InlineData(RentFrequency.Quarterly, 4)]
    [InlineData(RentFrequency.HalfYearly, 2)]
    [InlineData(RentFrequency.Annual, 1)]
    public void The_frequency_sets_the_number_of_instalments(RentFrequency f, int count) => Assert.Equal(count, LeaseMath.Schedule(Lease(l => l.Frequency = f)).Count);

    [Fact]
    public void Escalation_compounds_and_rounds_to_cents_at_every_step()
    {
        var l = Lease(x => { x.EndOn = new DateOnly(2028, 12, 31); x.BaseRent = 1000.55m; x.EscalationPercent = 3; x.EscalationEveryMonths = 12; });
        var s = LeaseMath.Schedule(l);
        Assert.Equal(36, s.Count);
        Assert.Equal(1000.55m, s[0].Amount);
        Assert.Equal(1030.57m, s[12].Amount); // 1000.55 * 1.03 = 1030.5665
        Assert.Equal(1061.49m, s[24].Amount); // 1030.57 * 1.03 = 1061.4871
        Assert.Equal([0, 1, 2], new[] { s[0], s[12], s[24] }.Select(i => i.EscalationStep));
        Assert.Equal(1000.55m, LeaseMath.RentAtStep(l, 0));
    }

    [Fact]
    public void Escalation_every_six_months_steps_twice_a_year()
    {
        var s = LeaseMath.Schedule(Lease(l => { l.EscalationPercent = 10; l.EscalationEveryMonths = 6; }));
        Assert.Equal(1200m, s[5].Amount);
        Assert.Equal(1320m, s[6].Amount);
    }

    [Fact]
    public void Rent_free_months_cost_nothing_and_a_percent_of_zero_never_escalates()
    {
        var s = LeaseMath.Schedule(Lease(l => { l.RentFreeMonths = 3; l.EscalationPercent = 0; l.EscalationEveryMonths = 1; }));
        Assert.Equal(3, s.Count(i => i.IsRentFree));
        Assert.All(s.Take(3), i => Assert.Equal(0m, i.Amount));
        Assert.All(s.Skip(3), i => Assert.Equal(1200m, i.Amount));
        Assert.All(s, i => Assert.Equal(0, i.EscalationStep));
    }

    [Fact]
    public void A_rent_free_period_that_starts_inside_the_free_months_is_free_for_the_whole_quarter()
    {
        var s = LeaseMath.Schedule(Lease(l => { l.Frequency = RentFrequency.Quarterly; l.RentFreeMonths = 2; }));
        Assert.True(s[0].IsRentFree);
        Assert.False(s[1].IsRentFree);
    }

    [Fact]
    public void A_term_that_does_not_divide_evenly_pro_rates_the_last_period_by_days()
    {
        var s = LeaseMath.Schedule(Lease(l => { l.StartOn = new DateOnly(2026, 1, 1); l.EndOn = new DateOnly(2026, 2, 14); l.BaseRent = 2800; }));
        Assert.Equal(2, s.Count);
        Assert.True(s[1].IsPartial);
        Assert.Equal(new DateOnly(2026, 2, 14), s[1].PeriodEnd);
        Assert.Equal(1400m, s[1].Amount); // 14 of 28 days of February
    }

    [Fact]
    public void A_start_on_the_31st_does_not_drift_through_short_months()
    {
        var s = LeaseMath.Schedule(Lease(l => { l.StartOn = new DateOnly(2026, 1, 31); l.EndOn = new DateOnly(2026, 6, 30); }));
        Assert.Equal([new DateOnly(2026, 1, 31), new DateOnly(2026, 2, 28), new DateOnly(2026, 3, 31), new DateOnly(2026, 4, 30), new DateOnly(2026, 5, 31)], s.Take(5).Select(i => i.DueOn));
    }

    [Fact]
    public void The_next_twelve_months_adds_only_instalments_due_in_the_window()
    {
        var l = Lease(x => { x.EndOn = new DateOnly(2027, 12, 31); x.EscalationPercent = 5; });
        Assert.Equal(12 * 1200m, LeaseMath.NextTwelveMonths(l, new DateOnly(2026, 1, 1)));
        Assert.Equal(6 * 1200m + 6 * 1260m, LeaseMath.NextTwelveMonths(l, new DateOnly(2026, 7, 1)));
        Assert.Equal(0m, LeaseMath.NextTwelveMonths(l, new DateOnly(2028, 1, 1)));
    }

    [Fact]
    public void Term_months_counts_calendar_months()
    {
        Assert.Equal(11, LeaseMath.TermMonths(Lease()));
        Assert.Equal(600, LeaseMath.TermMonths(new Lease { StartOn = new DateOnly(2026, 1, 1), EndOn = new DateOnly(2076, 1, 1) }));
    }
}
