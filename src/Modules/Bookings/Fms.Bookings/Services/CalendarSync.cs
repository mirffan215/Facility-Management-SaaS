using System.Collections.Concurrent;
using Fms.BuildingBlocks.Identity;
using Fms.BuildingBlocks.Integration;
using Fms.BuildingBlocks.Messaging;
using Fms.BuildingBlocks.Results;
using Fms.BuildingBlocks.Security;
using Fms.Bookings.Domain;
using Fms.Bookings.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Fms.Bookings.Services;

public sealed class CalendarOptions
{
    public const string SectionName = "Calendar";

    /// <summary>Lets the in-memory test adapter be chosen. Never turn this on in production: it forgets everything on restart.</summary>
    public bool AllowFakeAdapter { get; set; }
}

/// <summary>Remembers events in memory, per process. For tests, demos and trying the screens before a real calendar is connected.</summary>
public sealed class FakeCalendarAdapter : ICalendarAdapter
{
    public static readonly ConcurrentDictionary<string, CalendarEvent> Events = new();

    public string Name => "fake";

    public Task<string> UpsertAsync(CalendarEvent calendarEvent, string? externalId, CancellationToken ct = default)
    {
        var id = externalId ?? "FAKE-EVT-" + calendarEvent.BookingId.ToString("N")[..12];
        Events[id] = calendarEvent;
        return Task.FromResult(id);
    }

    public Task DeleteAsync(string externalId, CancellationToken ct = default)
    {
        Events.TryRemove(externalId, out _);
        return Task.CompletedTask;
    }
}

public sealed class CalendarSyncService(BookingsDbContext db, IEnumerable<ICalendarAdapter> adapters, IOptions<CalendarOptions> options, IAuditPort audit, TimeProvider clock)
{
    public IReadOnlyList<string> Available() =>
        adapters.Select(a => a.Name).Where(n => n != "fake" || options.Value.AllowFakeAdapter).Order().ToList();

    public async Task<CalendarSyncDto> GetAsync(CancellationToken ct = default)
    {
        var s = await db.SyncSettings.AsNoTracking().FirstOrDefaultAsync(ct);
        return new CalendarSyncDto(s?.Enabled ?? false, s?.Adapter ?? "", Available(), s is null ? null : Common.Version(s.RowVersion));
    }

    public async Task<Result<CalendarSyncDto>> SaveAsync(SaveCalendarSyncRequest r, CancellationToken ct = default)
    {
        var adapter = Common.Clean(r.Adapter) ?? "";
        var enabled = r.Enabled ?? false;
        if (enabled && !Available().Contains(adapter)) return Result<CalendarSyncDto>.Invalid("adapter", "Choose one of the available calendar connections.");
        var s = await db.SyncSettings.FirstOrDefaultAsync(ct);
        if (s is null)
        {
            s = new CalendarSyncSettings();
            db.SyncSettings.Add(s);
        }
        else
        {
            if (!Common.TryVersion(r.RowVersion, out var version)) return Result<CalendarSyncDto>.Invalid("rowVersion", "Reload and try again.");
            db.Entry(s).Property(x => x.RowVersion).OriginalValue = version;
        }

        s.Enabled = enabled;
        s.Adapter = adapter;
        s.UpdatedAt = clock.GetUtcNow();
        audit.Record("calendar.sync_settings_saved", nameof(CalendarSyncSettings), s.Id.ToString(), System.Text.Json.JsonSerializer.Serialize(new { enabled, adapter }));
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            return Result<CalendarSyncDto>.Conflict("Someone else changed this. Reload and try again.");
        }

        return Result<CalendarSyncDto>.Ok(await GetAsync(ct));
    }
}

/// <summary>
/// Sends a changed booking to the connected calendar: created or updated while it holds the slot, removed once it does not. At least once and safe to repeat (the reference
/// returned the first time is reused). A failure is thrown so the outbox retries it later; bookings themselves never wait for the calendar.
/// </summary>
public sealed class CalendarSyncHandler(BookingsDbContext db, IEnumerable<ICalendarAdapter> adapters, IOptions<CalendarOptions> options, IUserDirectory users, ILogger<CalendarSyncHandler> log) : IOutboxHandler
{
    public string MessageType => CalendarMessages.Sync;

    public async Task HandleAsync(OutboxMessageContext message, CancellationToken ct)
    {
        var settings = await db.SyncSettings.AsNoTracking().FirstOrDefaultAsync(ct);
        if (settings is not { Enabled: true }) return; // not connected: nothing to send and nothing kept for later
        var adapter = adapters.FirstOrDefault(a => a.Name == settings.Adapter && (a.Name != "fake" || options.Value.AllowFakeAdapter))
            ?? throw new InvalidOperationException($"The calendar connection '{settings.Adapter}' is not available on this server.");

        var bookingId = message.Payload<CalendarSyncMessage>().BookingId;
        var booking = await db.Bookings.AsNoTracking().FirstOrDefaultAsync(b => b.Id == bookingId, ct);
        if (booking is null) return;
        var link = await db.CalendarLinks.FirstOrDefaultAsync(l => l.BookingId == bookingId, ct);

        if (!BookingRules.IsHolding(booking.Status))
        {
            if (link is not null)
            {
                await adapter.DeleteAsync(link.ExternalEventId, ct);
                db.CalendarLinks.Remove(link);
                await db.SaveChangesAsync(ct);
            }

            return;
        }

        var resource = await db.Resources.AsNoTracking().FirstAsync(r => r.Id == booking.ResourceId, ct);
        var organizer = (await users.GetAsync([booking.OrganizerId], ct)).GetValueOrDefault(booking.OrganizerId);
        var id = await adapter.UpsertAsync(new CalendarEvent(booking.Id, organizer?.Email, booking.Title, resource.Name, null, booking.StartsAt, booking.EndsAt, booking.Status == BookingStatus.Pending), link?.ExternalEventId, ct);
        if (link is null)
        {
            db.CalendarLinks.Add(new CalendarLink { BookingId = bookingId, ExternalEventId = id });
            await db.SaveChangesAsync(ct);
        }
        else if (link.ExternalEventId != id)
        {
            link.ExternalEventId = id;
            await db.SaveChangesAsync(ct);
        }

        log.LogDebug("Booking {BookingId} sent to the calendar", booking.Id);
    }
}
