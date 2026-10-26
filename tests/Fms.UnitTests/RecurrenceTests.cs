using Fms.Work.Domain;

namespace Fms.UnitTests;

public sealed class RecurrenceTests
{
    private static DateOnly D(int y, int m, int d) => new(y, m, d);

    [Fact]
    public void Monthly_fixed_dates_do_not_drift_after_a_short_month()
    {
        var start = D(2026, 1, 31);
        var dates = Enumerable.Range(0, 5).Select(n => Recurrence.Nth(start, PmIntervalUnit.Month, 1, n)).ToList();
        Assert.Equal([D(2026, 1, 31), D(2026, 2, 28), D(2026, 3, 31), D(2026, 4, 30), D(2026, 5, 31)], dates);
    }

    [Theory]
    [InlineData(PmIntervalUnit.Day, 10, "2026-01-01", "2026-01-25", "2026-01-31")]
    [InlineData(PmIntervalUnit.Week, 2, "2026-01-05", "2026-01-19", "2026-02-02")]
    [InlineData(PmIntervalUnit.Month, 3, "2026-01-15", "2026-04-15", "2026-07-15")]
    [InlineData(PmIntervalUnit.Year, 1, "2024-02-29", "2026-02-28", "2027-02-28")]
    public void The_next_fixed_date_is_strictly_after(PmIntervalUnit unit, int interval, string start, string after, string expected) =>
        Assert.Equal(DateOnly.Parse(expected), Recurrence.FixedAfter(DateOnly.Parse(start), unit, interval, DateOnly.Parse(after)));

    [Fact]
    public void A_start_in_the_future_is_the_first_date() => Assert.Equal(D(2027, 1, 1), Recurrence.FixedAfter(D(2027, 1, 1), PmIntervalUnit.Month, 1, D(2026, 6, 1)));

    [Fact]
    public void Jumping_far_ahead_lands_on_the_right_date()
    {
        var start = D(2020, 1, 1);
        var next = Recurrence.FixedAfter(start, PmIntervalUnit.Day, 3, D(2026, 11, 10));
        Assert.True(next > D(2026, 11, 10) && next <= D(2026, 11, 13));
        Assert.Equal(0, (next.DayNumber - start.DayNumber) % 3);
    }

    [Fact]
    public void A_new_fixed_plan_starts_on_the_first_date_from_today()
    {
        Assert.Equal(D(2026, 12, 1), Recurrence.First(PmTimeBasis.Fixed, D(2026, 1, 1), PmIntervalUnit.Month, 1, D(2026, 11, 10)));
        Assert.Equal(D(2026, 11, 1), Recurrence.First(PmTimeBasis.Fixed, D(2026, 1, 1), PmIntervalUnit.Month, 1, D(2026, 11, 1))); // today counts
        Assert.Equal(D(2027, 3, 5), Recurrence.First(PmTimeBasis.Fixed, D(2027, 3, 5), PmIntervalUnit.Month, 1, D(2026, 11, 10)));
    }

    [Fact]
    public void A_new_floating_plan_is_never_due_in_the_past()
    {
        Assert.Equal(D(2026, 11, 10), Recurrence.First(PmTimeBasis.Floating, D(2026, 1, 1), PmIntervalUnit.Month, 1, D(2026, 11, 10)));
        Assert.Equal(D(2027, 1, 1), Recurrence.First(PmTimeBasis.Floating, D(2027, 1, 1), PmIntervalUnit.Month, 1, D(2026, 11, 10)));
    }

    [Fact]
    public void A_fixed_schedule_keeps_its_dates_whether_the_job_is_early_or_late()
    {
        var start = D(2026, 1, 1);
        Assert.Equal(D(2026, 5, 1), Recurrence.Next(PmTimeBasis.Fixed, start, PmIntervalUnit.Month, 1, D(2026, 4, 1), D(2026, 3, 25))); // early
        Assert.Equal(D(2026, 5, 1), Recurrence.Next(PmTimeBasis.Fixed, start, PmIntervalUnit.Month, 1, D(2026, 4, 1), D(2026, 4, 20))); // late
        // Very late: two dates were missed; the next one is the first still ahead, not an instantly overdue one.
        Assert.Equal(D(2026, 7, 1), Recurrence.Next(PmTimeBasis.Fixed, start, PmIntervalUnit.Month, 1, D(2026, 4, 1), D(2026, 6, 20)));
    }

    [Fact]
    public void A_floating_schedule_counts_from_the_finish()
    {
        Assert.Equal(D(2026, 7, 20), Recurrence.Next(PmTimeBasis.Floating, D(2026, 1, 1), PmIntervalUnit.Month, 3, D(2026, 4, 1), D(2026, 4, 20)));
        Assert.Equal(D(2026, 5, 25), Recurrence.Next(PmTimeBasis.Floating, D(2026, 1, 1), PmIntervalUnit.Day, 90, D(2026, 4, 1), D(2026, 2, 24))); // done early, so earlier next time
    }

    [Fact]
    public void Projection_lists_the_dates_in_range_and_stops()
    {
        var dates = Recurrence.Project(PmTimeBasis.Fixed, D(2026, 1, 1), PmIntervalUnit.Month, 1, D(2026, 11, 1), D(2027, 2, 15)).ToList();
        Assert.Equal([D(2026, 11, 1), D(2026, 12, 1), D(2027, 1, 1), D(2027, 2, 1)], dates);
        Assert.Empty(Recurrence.Project(PmTimeBasis.Fixed, D(2026, 1, 1), PmIntervalUnit.Month, 1, D(2027, 6, 1), D(2027, 2, 15)));
        Assert.Equal(400, Recurrence.Project(PmTimeBasis.Floating, D(2026, 1, 1), PmIntervalUnit.Day, 1, D(2026, 1, 1), D(2040, 1, 1)).Count()); // bounded
    }
}
