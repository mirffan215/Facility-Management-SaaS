using Fms.Bookings.Domain;
using Fms.Bookings.Services;

namespace Fms.UnitTests;

public sealed class BookingRulesTests
{
    // 2027-03-01 is a Monday. Riyadh is UTC+3 all year, so 09:00 local is 06:00 UTC.
    private static readonly DateTimeOffset Now = new(2027, 3, 1, 0, 0, 0, TimeSpan.Zero);

    private static BookableResource Room(string zone = "Asia/Riyadh") => new() { TimeZone = zone, OpensAt = new TimeOnly(8, 0), ClosesAt = new TimeOnly(18, 0), MinMinutes = 15, MaxMinutes = 240, MaxAdvanceDays = 60 };

    private static DateTimeOffset Local(int day, int hour, int minute = 0) => new DateTimeOffset(2027, 3, day, hour, minute, 0, TimeSpan.FromHours(3)).ToUniversalTime();

    [Fact]
    public void A_booking_inside_opening_hours_on_an_open_day_is_fine() => Assert.Null(BookingRules.Check(Room(), Local(2, 9), Local(2, 10), Now));

    [Theory]
    [InlineData(7, 0, 9, 0)] // starts before opening
    [InlineData(17, 0, 18, 30)] // ends after closing
    [InlineData(17, 30, 18, 15)]
    public void A_booking_outside_opening_hours_is_refused(int sh, int sm, int eh, int em)
    {
        var problem = BookingRules.Check(Room(), Local(2, sh, sm), Local(2, eh, em), Now);
        Assert.NotNull(problem);
        Assert.Contains("08:00", problem!.Value.Message);
    }

    [Fact]
    public void The_edges_of_opening_hours_are_inside_them() => Assert.Null(BookingRules.Check(Room(), Local(2, 8), Local(2, 12), Now));

    [Fact]
    public void Opening_hours_are_judged_in_the_resources_time_zone_not_UTC()
    {
        // 06:00 UTC is 09:00 in Riyadh (open); 04:00 UTC is 07:00 there (closed), though both are "daytime" in UTC.
        Assert.Null(BookingRules.Check(Room(), new DateTimeOffset(2027, 3, 2, 6, 0, 0, TimeSpan.Zero), new DateTimeOffset(2027, 3, 2, 7, 0, 0, TimeSpan.Zero), Now));
        Assert.NotNull(BookingRules.Check(Room(), new DateTimeOffset(2027, 3, 2, 4, 0, 0, TimeSpan.Zero), new DateTimeOffset(2027, 3, 2, 5, 0, 0, TimeSpan.Zero), Now));
    }

    [Fact]
    public void Days_that_are_not_open_are_refused()
    {
        var r = Room();
        r.DaysMask = BookingRules.DayBit(DayOfWeek.Sunday) | BookingRules.DayBit(DayOfWeek.Monday); // 2027-03-02 is a Tuesday
        var problem = BookingRules.Check(r, Local(2, 9), Local(2, 10), Now);
        Assert.Contains("Tuesday", problem!.Value.Message);
        Assert.Null(BookingRules.Check(r, Local(1, 9), Local(1, 10), Now));
    }

    [Theory]
    [InlineData(10, false)] // shorter than 15
    [InlineData(15, true)]
    [InlineData(240, true)]
    [InlineData(245, false)]
    public void Length_is_between_the_shortest_and_the_longest(int minutes, bool ok)
    {
        var start = Local(2, 9);
        Assert.Equal(ok, BookingRules.Check(Room(), start, start.AddMinutes(minutes), Now) is null);
    }

    [Fact]
    public void Times_are_in_steps_of_five_minutes() => Assert.NotNull(BookingRules.Check(Room(), Local(2, 9, 3), Local(2, 10, 3), Now));

    [Fact]
    public void The_end_must_follow_the_start() => Assert.NotNull(BookingRules.Check(Room(), Local(2, 10), Local(2, 9), Now));

    [Fact]
    public void The_past_and_the_far_future_are_refused()
    {
        Assert.NotNull(BookingRules.Check(Room(), Local(1, 9), Local(1, 10), Now.AddDays(1)));
        Assert.Null(BookingRules.Check(Room(), Local(2, 9), Local(2, 10), Local(2, 9).AddMinutes(4))); // just started: still allowed
        Assert.NotNull(BookingRules.Check(Room(), Local(2, 9).AddDays(70), Local(2, 10).AddDays(70), Now));
    }

    [Fact]
    public void A_booking_cannot_run_past_midnight_unless_the_resource_is_open_to_midnight()
    {
        var r = Room();
        r.OpensAt = new TimeOnly(0, 0);
        r.ClosesAt = new TimeOnly(0, 0); // open all day, to midnight
        r.MaxMinutes = 1440;
        Assert.Null(BookingRules.Check(r, Local(2, 23), Local(3, 0), Now));
        Assert.Null(BookingRules.Check(r, Local(2, 0), Local(2, 1), Now.AddDays(-1)));
        Assert.NotNull(BookingRules.Check(r, Local(2, 23), Local(3, 1), Now));
    }

    [Theory]
    [InlineData(0, 60, 0, 60, 0, true)]
    [InlineData(0, 60, 60, 120, 0, false)] // back to back is fine without a buffer
    [InlineData(0, 60, 60, 120, 15, true)] // but not with a 15 minute buffer
    [InlineData(0, 60, 75, 120, 15, false)]
    [InlineData(60, 120, 0, 60, 15, true)] // the buffer counts both ways
    [InlineData(0, 60, 30, 45, 0, true)]
    public void Bookings_clash_when_they_overlap_counting_the_buffer(int a1, int a2, int b1, int b2, int buffer, bool clash)
    {
        var t = new DateTimeOffset(2027, 3, 2, 0, 0, 0, TimeSpan.Zero);
        Assert.Equal(clash, BookingRules.Clash(t.AddMinutes(a1), t.AddMinutes(a2), t.AddMinutes(b1), t.AddMinutes(b2), buffer));
    }

    [Fact]
    public void Check_in_opens_before_the_start_and_closes_after_the_grace_period()
    {
        var start = new DateTimeOffset(2027, 3, 2, 9, 0, 0, TimeSpan.Zero);
        var end = start.AddHours(1);
        Assert.False(BookingRules.CanCheckIn(start, end, 15, start.AddMinutes(-16)));
        Assert.True(BookingRules.CanCheckIn(start, end, 15, start.AddMinutes(-15)));
        Assert.True(BookingRules.CanCheckIn(start, end, 15, start.AddMinutes(15)));
        Assert.False(BookingRules.CanCheckIn(start, end, 15, start.AddMinutes(16)));
        Assert.True(BookingRules.CanCheckIn(start, end, 0, start.AddMinutes(50))); // no grace limit: until it ends
    }

    [Theory]
    [InlineData(BookingStatus.Confirmed, true, 16, true)]
    [InlineData(BookingStatus.Confirmed, true, 10, false)]
    [InlineData(BookingStatus.Confirmed, false, 90, false)] // nothing needs a check-in
    [InlineData(BookingStatus.CheckedIn, true, 90, false)]
    [InlineData(BookingStatus.Pending, true, 90, false)]
    public void Only_a_confirmed_booking_that_needed_a_check_in_becomes_a_no_show(BookingStatus status, bool required, int minutesAfterStart, bool noShow)
    {
        var start = new DateTimeOffset(2027, 3, 2, 9, 0, 0, TimeSpan.Zero);
        Assert.Equal(noShow, BookingRules.IsNoShow(status, required, start, 15, start.AddMinutes(minutesAfterStart)));
    }

    [Fact]
    public void A_weekly_series_keeps_its_local_time_across_a_clock_change()
    {
        // New York changes its clocks on 2027-03-14. 10:00 local is 15:00 UTC before and 14:00 UTC after.
        var first = new DateTimeOffset(2027, 3, 6, 15, 0, 0, TimeSpan.Zero);
        var starts = BookingRules.WeeklyStarts(first, 3, "America/New_York");
        Assert.Equal([first, new DateTimeOffset(2027, 3, 13, 15, 0, 0, TimeSpan.Zero), new DateTimeOffset(2027, 3, 20, 14, 0, 0, TimeSpan.Zero)], starts);
    }

    [Fact]
    public void A_series_in_a_zone_without_clock_changes_is_exactly_a_week_apart()
    {
        var first = new DateTimeOffset(2027, 3, 6, 6, 0, 0, TimeSpan.Zero);
        Assert.Equal([first, first.AddDays(7), first.AddDays(14), first.AddDays(21)], BookingRules.WeeklyStarts(first, 4, "Asia/Riyadh"));
    }

    [Theory]
    [InlineData("Asia/Riyadh", true)]
    [InlineData("UTC", true)]
    [InlineData("Mars/Olympus", false)]
    public void Time_zone_names_are_checked(string zone, bool ok) => Assert.Equal(ok, BookingRules.IsZone(zone));

    [Fact]
    public void Held_statuses_are_exactly_the_ones_that_keep_the_slot()
    {
        foreach (var s in Enum.GetValues<BookingStatus>()) Assert.Equal(s is BookingStatus.Pending or BookingStatus.Confirmed or BookingStatus.CheckedIn, BookingRules.IsHolding(s));
    }
}

public sealed class IcsWriterTests
{
    private static readonly DateTimeOffset T = new(2027, 3, 2, 6, 0, 0, TimeSpan.Zero);

    private static string Feed(string summary, string? location = "Room 1", bool tentative = false) =>
        IcsWriter.Build("Bookings", [new IcsEvent("abc@fms", summary, location, null, T, T.AddHours(1), tentative, T)]);

    [Fact]
    public void A_feed_has_the_required_structure_and_uses_CRLF_and_UTC_times()
    {
        var ics = Feed("Standup");
        Assert.StartsWith("BEGIN:VCALENDAR\r\nVERSION:2.0\r\n", ics);
        Assert.EndsWith("END:VEVENT\r\nEND:VCALENDAR\r\n", ics);
        Assert.Contains("DTSTART:20270302T060000Z\r\n", ics);
        Assert.Contains("DTEND:20270302T070000Z\r\n", ics);
        Assert.Contains("STATUS:CONFIRMED\r\n", ics);
        Assert.DoesNotContain("\n", ics.Replace("\r\n", ""));
    }

    [Fact]
    public void A_pending_booking_is_tentative() => Assert.Contains("STATUS:TENTATIVE\r\n", Feed("Standup", tentative: true));

    [Fact]
    public void Special_characters_are_escaped_and_cannot_start_a_new_property()
    {
        var ics = Feed("Budget; Q1, plan\\v2\r\nATTENDEE:mailto:evil@example.com");
        Assert.Contains("SUMMARY:Budget\\; Q1\\, plan\\\\v2\\nATTENDEE:mailto:evil@example.com\r\n", ics);
        Assert.DoesNotContain("\r\nATTENDEE:", ics);
    }

    [Fact]
    public void Control_characters_are_dropped() => Assert.Contains("SUMMARY:AB\r\n", Feed("A\u0007B"));

    [Fact]
    public void Long_lines_are_folded_at_75_octets_including_multibyte_text()
    {
        var ics = Feed(new string('ا', 100)); // Arabic letters are two bytes each in UTF-8
        foreach (var line in ics.Split("\r\n"))
            Assert.True(System.Text.Encoding.UTF8.GetByteCount(line) <= 76, line); // 75 octets of content plus the leading space of a continuation
        Assert.Equal(new string('ا', 100), string.Concat(ics.Split("\r\n").Where(l => l.StartsWith("SUMMARY:") || l.StartsWith(' ')).Select(l => l.StartsWith("SUMMARY:") ? l["SUMMARY:".Length..] : l[1..])));
    }

    [Fact]
    public void A_missing_location_is_left_out() => Assert.DoesNotContain("LOCATION", Feed("x", location: null));
}
