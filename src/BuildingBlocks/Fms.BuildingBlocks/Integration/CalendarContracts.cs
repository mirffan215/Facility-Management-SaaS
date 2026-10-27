namespace Fms.BuildingBlocks.Integration;

/// <summary>A booking as an external calendar should show it.</summary>
public sealed record CalendarEvent(Guid BookingId, string? OrganizerEmail, string Title, string ResourceName, string? Location, DateTimeOffset StartsAt, DateTimeOffset EndsAt, bool Tentative);

/// <summary>
/// The one place that knows how to talk to an external calendar (Microsoft 365, Google). Implementations must be idempotent: sending the same event again with the
/// reference they gave before updates it rather than creating a second. No real adapter exists yet; see ADR-0019.
/// </summary>
public interface ICalendarAdapter
{
    /// <summary>The name a tenant's settings use to pick this adapter.</summary>
    string Name { get; }

    /// <summary>Creates or updates the event and returns its reference in the external calendar.</summary>
    Task<string> UpsertAsync(CalendarEvent calendarEvent, string? externalId, CancellationToken ct = default);

    Task DeleteAsync(string externalId, CancellationToken ct = default);
}

public static class CalendarMessages
{
    /// <summary>Tells the calendar connection a booking changed (created, approved, cancelled...). Payload: <see cref="CalendarSyncMessage"/>.</summary>
    public const string Sync = "calendar.sync";
}

public sealed record CalendarSyncMessage(Guid BookingId);
