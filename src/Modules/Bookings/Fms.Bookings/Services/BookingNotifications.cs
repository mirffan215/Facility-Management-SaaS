using Fms.BuildingBlocks.Identity;
using Fms.BuildingBlocks.Messaging;
using Fms.BuildingBlocks.Notifications;
using Fms.BuildingBlocks.Persistence;
using Fms.BuildingBlocks.Security;
using Fms.BuildingBlocks.Tenancy;
using Fms.Bookings.Domain;
using Fms.Bookings.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Fms.Bookings.Services;

/// <summary>Tells the people who approve bookings that one is waiting (never the person who made it).</summary>
public sealed class BookingPendingHandler(BookingsDbContext db, IUserDirectory users, INotificationSink sink) : IOutboxHandler
{
    public string MessageType => BookingMessages.Pending;

    public async Task HandleAsync(OutboxMessageContext message, CancellationToken ct)
    {
        var m = message.Payload<BookingNoticeMessage>();
        var b = await db.Bookings.AsNoTracking().FirstOrDefaultAsync(x => x.Id == m.BookingId, ct);
        if (b is null || b.Status != BookingStatus.Pending) return;
        var resource = await db.Resources.AsNoTracking().FirstAsync(r => r.Id == b.ResourceId, ct);
        var organizer = (await users.GetAsync([b.OrganizerId], ct)).GetValueOrDefault(b.OrganizerId);
        var who = organizer?.DisplayName ?? organizer?.Email ?? "Someone";
        var more = m.Count > 1 ? $" (and {m.Count - 1} more in a weekly series)" : "";
        var moreAr = m.Count > 1 ? $" (و{m.Count - 1} أخرى في سلسلة أسبوعية)" : "";
        var subject = $"Booking waiting for approval: {resource.Name}";
        var body = $"{who} asked to book {resource.Name} on {b.StartsAt:yyyy-MM-dd HH:mm} UTC{more}. Title: {b.Title}.\n\nطلب {who} حجز {resource.Name} بتاريخ {b.StartsAt:yyyy-MM-dd HH:mm} UTC{moreAr}. العنوان: {b.Title}.";
        var approvers = (await users.WithPermissionAsync(Permissions.BookingsApprove, ct)).Where(u => u.Id != b.OrganizerId).ToList();
        await sink.NotifyAsync(message.MessageId, approvers.Select(u => new NotificationRequest(
            u.Id, "booking.pending", new { bookingId = b.Id, resource = resource.Name, title = b.Title }, "/bookings?tab=approvals", subject, body)).ToList(), ct);
    }
}

/// <summary>Tells the organizer that their booking was approved, rejected, or cancelled by someone else.</summary>
public sealed class BookingDecidedHandler(BookingsDbContext db, INotificationSink sink) : IOutboxHandler
{
    public string MessageType => BookingMessages.Decided;

    public async Task HandleAsync(OutboxMessageContext message, CancellationToken ct)
    {
        var m = message.Payload<BookingNoticeMessage>();
        var b = await db.Bookings.AsNoTracking().FirstOrDefaultAsync(x => x.Id == m.BookingId, ct);
        if (b is null) return;
        var resource = await db.Resources.AsNoTracking().FirstAsync(r => r.Id == b.ResourceId, ct);
        var (en, ar, note) = b.Status switch
        {
            BookingStatus.Confirmed => ("approved", "تمت الموافقة على", b.DecisionNote),
            BookingStatus.Rejected => ("rejected", "تم رفض", b.DecisionNote),
            BookingStatus.Cancelled => ("cancelled", "تم إلغاء", b.CancelReason),
            _ => ("", "", null),
        };
        if (en == "") return;
        var subject = $"Your booking of {resource.Name} was {en}";
        var body = $"{b.Title} · {resource.Name} · {b.StartsAt:yyyy-MM-dd HH:mm} UTC was {en}.{(note is null ? "" : $" Note: {note}")}\n\n{ar} حجزك {b.Title} · {resource.Name} · {b.StartsAt:yyyy-MM-dd HH:mm} UTC.{(note is null ? "" : $" ملاحظة: {note}")}";
        await sink.NotifyAsync(message.MessageId, [new NotificationRequest(b.OrganizerId, "booking." + en, new { bookingId = b.Id, resource = resource.Name, title = b.Title, note }, "/bookings?tab=mine", subject, body)], ct);
    }
}

/// <summary>Frees the slot of a booking that needed a check-in and did not get one. Called by the worker; each tenant in its own scope.</summary>
public sealed class BookingMonitor(IServiceScopeFactory scopes, ILogger<BookingMonitor> log)
{
    public async Task<int> RunOnceAsync(CancellationToken ct = default)
    {
        List<Guid> tenants;
        await using (var scope = scopes.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<TenantContext>().BeginPlatformScope();
            var now = scope.ServiceProvider.GetRequiredService<TimeProvider>().GetUtcNow();
            tenants = await scope.ServiceProvider.GetRequiredService<BookingsDbContext>().Bookings.IgnoreQueryFilters()
                .Where(b => b.Status == BookingStatus.Confirmed && b.StartsAt < now).Select(b => b.TenantId).Distinct().ToListAsync(ct);
        }

        var freed = 0;
        foreach (var tenant in tenants)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                scope.ServiceProvider.GetRequiredService<TenantContext>().SetTenant(tenant);
                freed += await scope.ServiceProvider.GetRequiredService<NoShows>().ReleaseAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "Releasing no-show bookings failed for a tenant; the others continue");
            }
        }

        return freed;
    }
}

public sealed class NoShows(BookingsDbContext db, IAuditPort audit, Fms.BuildingBlocks.Messaging.IOutbox outbox, TimeProvider clock)
{
    public async Task<int> ReleaseAsync(CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var due = await db.Bookings.Where(b => b.Status == BookingStatus.Confirmed && b.StartsAt < now).ToListAsync(ct);
        if (due.Count == 0) return 0;
        var resources = await db.Resources.AsNoTracking().Where(r => r.CheckInRequired).ToDictionaryAsync(r => r.Id, ct);
        var freed = 0;
        foreach (var b in due)
        {
            if (!resources.TryGetValue(b.ResourceId, out var r) || !BookingRules.IsNoShow(b.Status, r.CheckInRequired, b.StartsAt, r.CheckInGraceMinutes, now)) continue;
            b.Status = BookingStatus.NoShow;
            b.CancelledAt = now;
            b.CancelReason = "Not checked in";
            outbox.Enqueue(Fms.BuildingBlocks.Integration.CalendarMessages.Sync, new Fms.BuildingBlocks.Integration.CalendarSyncMessage(b.Id));
            audit.Record("booking.no_show", nameof(Booking), b.Id.ToString(), System.Text.Json.JsonSerializer.Serialize(new { b.ResourceId }));
            freed++;
        }

        if (freed > 0) await db.SaveChangesAsync(ct);
        return freed;
    }
}
