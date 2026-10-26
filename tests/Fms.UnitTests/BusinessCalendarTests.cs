using Fms.Work.Domain;

namespace Fms.UnitTests;

public sealed class BusinessCalendarTests
{
    // Riyadh is UTC+3 all year. Saudi week: Sunday to Thursday, 08:00-17:00 local.
    private static BusinessCalendar Riyadh(params DateOnly[] holidays) => new("Asia/Riyadh", SaudiWeek(), holidays.ToHashSet());

    private static Dictionary<DayOfWeek, WorkingDay?> SaudiWeek() => new()
    {
        [DayOfWeek.Sunday] = new(TimeSpan.FromHours(8), TimeSpan.FromHours(17)), [DayOfWeek.Monday] = new(TimeSpan.FromHours(8), TimeSpan.FromHours(17)),
        [DayOfWeek.Tuesday] = new(TimeSpan.FromHours(8), TimeSpan.FromHours(17)), [DayOfWeek.Wednesday] = new(TimeSpan.FromHours(8), TimeSpan.FromHours(17)),
        [DayOfWeek.Thursday] = new(TimeSpan.FromHours(8), TimeSpan.FromHours(17)), [DayOfWeek.Friday] = null, [DayOfWeek.Saturday] = null,
    };

    private static DateTimeOffset Local(int y, int m, int d, int h, int min = 0) => new(y, m, d, h, min, 0, TimeSpan.FromHours(3));

    // 2026-11-01 is a Sunday.
    [Fact]
    public void Inside_hours_the_clock_runs_normally() =>
        Assert.Equal(Local(2026, 11, 1, 12), Riyadh().AddBusinessMinutes(Local(2026, 11, 1, 10), 120));

    [Fact]
    public void Time_outside_opening_hours_does_not_count()
    {
        // Sunday 16:00 + 2h: one hour left today, one hour on Monday morning.
        Assert.Equal(Local(2026, 11, 2, 9), Riyadh().AddBusinessMinutes(Local(2026, 11, 1, 16), 120));
        // Started before opening: the clock starts at 08:00.
        Assert.Equal(Local(2026, 11, 1, 9), Riyadh().AddBusinessMinutes(Local(2026, 11, 1, 5), 60));
    }

    [Fact]
    public void The_weekend_is_skipped()
    {
        // Thursday 16:00 + 2h skips Friday and Saturday: one hour left, one hour on Sunday.
        Assert.Equal(Local(2026, 11, 8, 9), Riyadh().AddBusinessMinutes(Local(2026, 11, 5, 16), 120));
        // Friday midday + 30 minutes lands on Sunday morning.
        Assert.Equal(Local(2026, 11, 8, 8, 30), Riyadh().AddBusinessMinutes(Local(2026, 11, 6, 12), 30));
    }

    [Fact]
    public void Holidays_are_skipped()
    {
        var calendar = Riyadh(new DateOnly(2026, 11, 2));
        Assert.Equal(Local(2026, 11, 3, 9), calendar.AddBusinessMinutes(Local(2026, 11, 1, 16), 120));
    }

    [Fact]
    public void Exactly_filling_the_day_ends_at_closing_and_zero_minutes_changes_nothing()
    {
        Assert.Equal(Local(2026, 11, 1, 17), Riyadh().AddBusinessMinutes(Local(2026, 11, 1, 8), 540));
        var t = Local(2026, 11, 6, 3);
        Assert.Equal(t, Riyadh().AddBusinessMinutes(t, 0));
    }

    [Fact]
    public void Counting_is_the_inverse_of_adding()
    {
        var calendar = Riyadh(new DateOnly(2026, 11, 3));
        foreach (var minutes in new[] { 1, 59, 60, 540, 541, 2000, 5000 })
        {
            var start = Local(2026, 11, 1, 10, 15);
            var end = calendar.AddBusinessMinutes(start, minutes);
            Assert.Equal(minutes, calendar.BusinessMinutesBetween(start, end));
        }
    }

    [Fact]
    public void Counting_ignores_closed_time()
    {
        // Thursday 15:00 to Sunday 10:00 = 2h Thursday + 2h Sunday.
        Assert.Equal(240, Riyadh().BusinessMinutesBetween(Local(2026, 11, 5, 15), Local(2026, 11, 8, 10)));
        Assert.Equal(0, Riyadh().BusinessMinutesBetween(Local(2026, 11, 6, 9), Local(2026, 11, 7, 20)));
        Assert.Equal(0, Riyadh().BusinessMinutesBetween(Local(2026, 11, 1, 12), Local(2026, 11, 1, 10))); // backwards
    }

    [Fact]
    public void Always_open_counts_every_minute_including_weekends() =>
        Assert.Equal(Local(2026, 11, 7, 12).AddMinutes(0), BusinessCalendar.AlwaysOpen.AddBusinessMinutes(Local(2026, 11, 6, 12), 24 * 60));

    [Fact]
    public void Daylight_saving_neither_adds_nor_loses_working_minutes()
    {
        // New York springs forward on 2026-03-08 (02:00 -> 03:00). Open 00:00-24:00 on Sunday: that day has 23 real hours.
        var open = Enum.GetValues<DayOfWeek>().ToDictionary(d => d, _ => (WorkingDay?)new WorkingDay(TimeSpan.Zero, TimeSpan.FromHours(24)));
        var calendar = new BusinessCalendar("America/New_York", open);
        var start = new DateTimeOffset(2026, 3, 7, 12, 0, 0, TimeSpan.FromHours(-5)); // Saturday noon EST
        var end = calendar.AddBusinessMinutes(start, 24 * 60);
        Assert.Equal(start.AddHours(24), end); // exactly 24 elapsed hours, whatever the wall clock says
        Assert.Equal(24 * 60, calendar.BusinessMinutesBetween(start, end));
    }

    [Fact]
    public void Hours_round_trip_and_bad_hours_are_explained()
    {
        var json = WorkingHours.Serialize(SaudiWeek());
        var parsed = WorkingHours.TryParse(json, out var error);
        Assert.Null(error);
        Assert.Equal(new WorkingDay(TimeSpan.FromHours(8), TimeSpan.FromHours(17)), parsed![DayOfWeek.Sunday]);
        Assert.Null(parsed[DayOfWeek.Friday]);

        Assert.Null(WorkingHours.TryParse("not json", out var e1)); Assert.NotNull(e1);
        Assert.Null(WorkingHours.TryParse("""{"Sunday":{"start":"17:00","end":"08:00"}}""", out var e2)); Assert.Contains("before closing", e2);
        Assert.Null(WorkingHours.TryParse("""{"Sunday":{"start":"8am","end":"17:00"}}""", out _));
        Assert.Null(WorkingHours.TryParse("""{"Sunday":null}""", out var e3)); Assert.Contains("At least one day", e3);
        Assert.NotNull(WorkingHours.TryParse("""{"sunday":{"start":"00:00","end":"24:00"}}""", out _));
    }
}
