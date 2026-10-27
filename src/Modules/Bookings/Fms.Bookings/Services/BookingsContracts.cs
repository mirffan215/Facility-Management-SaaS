using Fms.Bookings.Domain;

namespace Fms.Bookings.Services;

public sealed record ResourceDto(
    Guid Id, ResourceKind Kind, string Code, string Name, string? NameAr, Guid? SpaceId, string? SpaceName, int Capacity, string TimeZone, string OpensAt, string ClosesAt, IReadOnlyList<DayOfWeek> Days,
    int MinMinutes, int MaxMinutes, int MaxAdvanceDays, int BufferMinutes, bool RequiresApproval, bool CheckInRequired, int CheckInGraceMinutes, string? Notes, bool IsArchived, string RowVersion);

public sealed record SaveResourceRequest(
    ResourceKind? Kind, string? Code, string? Name, string? NameAr, Guid? SpaceId, int? Capacity, string? TimeZone, string? OpensAt, string? ClosesAt, IReadOnlyList<DayOfWeek>? Days,
    int? MinMinutes, int? MaxMinutes, int? MaxAdvanceDays, int? BufferMinutes, bool? RequiresApproval, bool? CheckInRequired, int? CheckInGraceMinutes, string? Notes, string? RowVersion);

public sealed record BookingDto(
    Guid Id, Guid ResourceId, string ResourceCode, string ResourceName, ResourceKind ResourceKind, Guid OrganizerId, string? OrganizerName, string Title, DateTimeOffset StartsAt, DateTimeOffset EndsAt, int Attendees,
    BookingStatus Status, Guid? SeriesId, string? Notes, string? DecisionNote, string? CancelReason, DateTimeOffset? CheckedInAt, bool CanCheckInNow, bool Mine, string RowVersion);

public sealed record CreateBookingRequest(Guid? ResourceId, string? Title, DateTimeOffset? StartsAt, DateTimeOffset? EndsAt, int? Attendees, string? Notes, int? RepeatWeeks);

public sealed record CreatedBookingsDto(IReadOnlyList<BookingDto> Bookings);

public sealed record DecideBookingRequest(string? Note, string? RowVersion);

public sealed record CancelBookingRequest(string? Reason, string? RowVersion);

/// <summary>A taken slot on a resource, without who took it (anyone who can book may look at availability).</summary>
public sealed record BusySlotDto(DateTimeOffset StartsAt, DateTimeOffset EndsAt, bool Pending, bool Mine);

public sealed record AvailabilityDto(Guid ResourceId, DateTimeOffset From, DateTimeOffset To, IReadOnlyList<BusySlotDto> Busy);

public sealed record CalendarFeedDto(string Path, DateTimeOffset CreatedAt);

public sealed record CalendarFeedStatusDto(bool Exists, DateTimeOffset? CreatedAt, DateTimeOffset? LastUsedAt);

public sealed record CalendarSyncDto(bool Enabled, string Adapter, IReadOnlyList<string> AvailableAdapters, string? RowVersion);

public sealed record SaveCalendarSyncRequest(bool? Enabled, string? Adapter, string? RowVersion);
