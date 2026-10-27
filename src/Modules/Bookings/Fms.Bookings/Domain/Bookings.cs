using System.ComponentModel.DataAnnotations;
using Fms.BuildingBlocks.Tenancy;

namespace Fms.Bookings.Domain;

public enum ResourceKind { Room = 1, Desk = 2, Parking = 3, Equipment = 4, Other = 5 }

/// <summary>Something people can book: a room, a desk, a parking bay, a piece of equipment, with the rules for booking it.</summary>
public sealed class BookableResource : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public ResourceKind Kind { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string? NameAr { get; set; }

    /// <summary>The space it is in (a Space-type location owned by Facilities), if any.</summary>
    public Guid? SpaceId { get; set; }

    public int Capacity { get; set; } = 1;

    /// <summary>IANA zone the opening hours are in.</summary>
    public string TimeZone { get; set; } = "UTC";

    public TimeOnly OpensAt { get; set; } = new(7, 0);
    public TimeOnly ClosesAt { get; set; } = new(19, 0);

    /// <summary>Days it can be booked, one bit per <see cref="DayOfWeek"/> (Sunday = bit 0).</summary>
    public int DaysMask { get; set; } = BookingRules.AllDays;

    public int MinMinutes { get; set; } = 15;
    public int MaxMinutes { get; set; } = 480;
    public int MaxAdvanceDays { get; set; } = 90;

    /// <summary>Gap kept free after each booking (cleaning, set-up).</summary>
    public int BufferMinutes { get; set; }

    public bool RequiresApproval { get; set; }
    public bool CheckInRequired { get; set; }
    public int CheckInGraceMinutes { get; set; } = 15;
    public string? Notes { get; set; }
    public bool IsArchived { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    [Timestamp]
    public byte[] RowVersion { get; set; } = [];
}

public enum BookingStatus { Pending = 1, Confirmed = 2, CheckedIn = 3, Rejected = 5, Cancelled = 6, NoShow = 7 }

public sealed class Booking : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid ResourceId { get; set; }
    public Guid OrganizerId { get; set; }
    public string Title { get; set; } = "";
    public DateTimeOffset StartsAt { get; set; }
    public DateTimeOffset EndsAt { get; set; }
    public int Attendees { get; set; } = 1;
    public BookingStatus Status { get; set; }

    /// <summary>Bookings made together as a weekly series share this.</summary>
    public Guid? SeriesId { get; set; }

    public string? Notes { get; set; }
    public Guid? DecidedBy { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
    public string? DecisionNote { get; set; }
    public DateTimeOffset? CancelledAt { get; set; }
    public Guid? CancelledBy { get; set; }
    public string? CancelReason { get; set; }
    public DateTimeOffset? CheckedInAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    [Timestamp]
    public byte[] RowVersion { get; set; } = [];
}

/// <summary>
/// The event a booking has in the connected calendar. Kept apart from the booking so that background syncing never changes the booking's row (and so never makes
/// a person's own cancel or check-in fail as "changed by someone else").
/// </summary>
public sealed class CalendarLink : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid BookingId { get; set; }
    public string ExternalEventId { get; set; } = "";
}

/// <summary>The secret address of one person's calendar feed. Only the hash is kept; the address is shown once.</summary>
public sealed class CalendarFeedToken : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid UserId { get; set; }
    public string TokenHash { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }
}

/// <summary>Whether bookings are sent to an external calendar, and through which adapter. One row per tenant.</summary>
public sealed class CalendarSyncSettings : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public bool Enabled { get; set; }
    public string Adapter { get; set; } = "";
    public DateTimeOffset UpdatedAt { get; set; }

    [Timestamp]
    public byte[] RowVersion { get; set; } = [];
}

/// <summary>The rules of booking, free of storage so they can be tested exhaustively.</summary>
public static class BookingRules
{
    public const int AllDays = 0b1111111;
    public const int CheckInOpensMinutesBefore = 15;
    public static readonly BookingStatus[] Holding = [BookingStatus.Pending, BookingStatus.Confirmed, BookingStatus.CheckedIn];

    public static int DayBit(DayOfWeek d) => 1 << (int)d;

    public static bool IsHolding(BookingStatus s) => s is BookingStatus.Pending or BookingStatus.Confirmed or BookingStatus.CheckedIn;

    /// <summary>Two bookings clash when they overlap, counting the resource's buffer after each.</summary>
    public static bool Clash(DateTimeOffset aStart, DateTimeOffset aEnd, DateTimeOffset bStart, DateTimeOffset bEnd, int bufferMinutes)
    {
        var buffer = TimeSpan.FromMinutes(bufferMinutes);
        return aStart < bEnd + buffer && bStart < aEnd + buffer;
    }

    /// <summary>
    /// Why a time cannot be booked, in plain words, or null when it can. Opening hours and days are judged in the resource's own time zone;
    /// a booking must start and end on the same local day inside opening hours.
    /// </summary>
    public static (string Field, string Message)? Check(BookableResource r, DateTimeOffset start, DateTimeOffset end, DateTimeOffset now)
    {
        if (end <= start) return ("endsAt", "The end must be after the start.");
        if (start.Second != 0 || end.Second != 0 || start.Millisecond != 0 || end.Millisecond != 0 || start.Minute % 5 != 0 || end.Minute % 5 != 0) return ("startsAt", "Times are in steps of 5 minutes.");
        var minutes = (int)(end - start).TotalMinutes;
        if (minutes < r.MinMinutes) return ("endsAt", $"The shortest booking is {r.MinMinutes} minutes.");
        if (minutes > r.MaxMinutes) return ("endsAt", $"The longest booking is {r.MaxMinutes} minutes.");
        if (start < now.AddMinutes(-5)) return ("startsAt", "That time has already passed.");
        if (start > now.AddDays(r.MaxAdvanceDays)) return ("startsAt", $"Bookings can be made at most {r.MaxAdvanceDays} days ahead.");

        var zone = ZoneOf(r.TimeZone);
        var localStart = TimeZoneInfo.ConvertTime(start, zone);
        var localEnd = TimeZoneInfo.ConvertTime(end, zone);
        if (localStart.Date != localEnd.Date && !(localEnd.TimeOfDay == TimeSpan.Zero && localEnd.Date == localStart.Date.AddDays(1) && r.ClosesAt == new TimeOnly(0, 0))) return ("endsAt", "A booking must start and end on the same day.");
        if ((r.DaysMask & DayBit(localStart.DayOfWeek)) == 0) return ("startsAt", $"It cannot be booked on a {localStart.DayOfWeek}.");
        var open = r.OpensAt.ToTimeSpan();
        var close = r.ClosesAt == new TimeOnly(0, 0) ? TimeSpan.FromHours(24) : r.ClosesAt.ToTimeSpan();
        var endOfDay = localEnd.Date == localStart.Date ? localEnd.TimeOfDay : TimeSpan.FromHours(24);
        if (localStart.TimeOfDay < open || endOfDay > close) return ("startsAt", $"It can be booked between {r.OpensAt:HH\\:mm} and {(r.ClosesAt == new TimeOnly(0, 0) ? "24:00" : r.ClosesAt.ToString("HH\\:mm"))}.");
        return null;
    }

    public static TimeZoneInfo ZoneOf(string id)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException) { return TimeZoneInfo.Utc; }
    }

    public static bool IsZone(string id)
    {
        try { TimeZoneInfo.FindSystemTimeZoneById(id); return true; }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException) { return false; }
    }

    /// <summary>Checking in opens a little before the start and closes when the grace period after the start ends.</summary>
    public static bool CanCheckIn(DateTimeOffset start, DateTimeOffset end, int graceMinutes, DateTimeOffset now) =>
        now >= start.AddMinutes(-CheckInOpensMinutesBefore) && now <= (graceMinutes > 0 ? start.AddMinutes(graceMinutes) : end);

    /// <summary>A booking that needed a check-in and did not get one in time.</summary>
    public static bool IsNoShow(BookingStatus status, bool checkInRequired, DateTimeOffset start, int graceMinutes, DateTimeOffset now) =>
        checkInRequired && status == BookingStatus.Confirmed && now > start.AddMinutes(graceMinutes);

    /// <summary>The starts of a weekly series: the first, then one every seven days.</summary>
    public static IReadOnlyList<DateTimeOffset> WeeklyStarts(DateTimeOffset first, int count, string timeZone)
    {
        // Step by calendar weeks in the resource's zone so a booking stays at the same local time across a clock change.
        var zone = ZoneOf(timeZone);
        var local = TimeZoneInfo.ConvertTime(first, zone);
        var list = new List<DateTimeOffset>(count);
        for (var i = 0; i < count; i++)
        {
            var d = local.DateTime.AddDays(7 * i);
            list.Add(new DateTimeOffset(d, zone.GetUtcOffset(d)).ToUniversalTime());
        }

        return list;
    }
}
