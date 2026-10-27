using Fms.BuildingBlocks.Messaging;
using Fms.BuildingBlocks.Notifications;
using Fms.BuildingBlocks.Persistence;
using Fms.BuildingBlocks.Security;
using Fms.BuildingBlocks.Tenancy;
using Fms.Visitors.Domain;
using Fms.Visitors.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Fms.Visitors.Services;

/// <summary>Tells a host that their visitor has arrived.</summary>
public sealed class VisitorArrivedHandler(VisitorsDbContext db, INotificationSink sink) : IOutboxHandler
{
    public string MessageType => VisitorMessages.Arrived;

    public async Task HandleAsync(OutboxMessageContext message, CancellationToken ct)
    {
        var m = message.Payload<VisitNoticeMessage>();
        var v = await db.Visits.AsNoTracking().FirstOrDefaultAsync(x => x.Id == m.VisitId, ct);
        if (v is null || v.Status != VisitStatus.CheckedIn) return;
        var company = string.IsNullOrWhiteSpace(v.VisitorCompany) ? "" : $" ({v.VisitorCompany})";
        var subject = $"Your visitor {v.VisitorName} has arrived";
        var body = $"{v.VisitorName}{company} has arrived at {v.SiteName} and is at reception.\n\nوصل زائرك {v.VisitorName}{company} إلى {v.SiteName} وهو في الاستقبال.";
        await sink.NotifyAsync(message.MessageId, [new NotificationRequest(v.HostId, "visitor.arrived", new { visitId = v.Id, visitor = v.VisitorName, site = v.SiteName }, "/visitors?tab=mine", subject, body)], ct);
    }
}

/// <summary>Marks expected visits that never arrived, and removes visitors' personal details when the retention period is over. Called by the worker; each tenant in its own scope.</summary>
public sealed class VisitMonitor(IServiceScopeFactory scopes, ILogger<VisitMonitor> log)
{
    public async Task<int> RunOnceAsync(CancellationToken ct = default)
    {
        List<Guid> tenants;
        await using (var scope = scopes.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<TenantContext>().BeginPlatformScope();
            var now = scope.ServiceProvider.GetRequiredService<TimeProvider>().GetUtcNow();
            tenants = await scope.ServiceProvider.GetRequiredService<VisitorsDbContext>().Visits.IgnoreQueryFilters()
                .Where(v => (v.Status == VisitStatus.Expected && v.ExpectedAt < now.AddDays(-1)) || (v.AnonymisedAt == null && v.ExpectedAt < now.AddDays(-30)))
                .Select(v => v.TenantId).Distinct().ToListAsync(ct);
        }

        var changed = 0;
        foreach (var tenant in tenants)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                scope.ServiceProvider.GetRequiredService<TenantContext>().SetTenant(tenant);
                changed += await scope.ServiceProvider.GetRequiredService<VisitHousekeeping>().RunAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "Visitor housekeeping failed for a tenant; the others continue");
            }
        }

        return changed;
    }
}

public sealed class VisitHousekeeping(VisitorsDbContext db, IAuditPort audit, TimeProvider clock)
{
    public async Task<int> RunAsync(CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var missed = 0;
        foreach (var v in await db.Visits.Where(x => x.Status == VisitStatus.Expected && x.ExpectedAt < now.AddDays(-1)).ToListAsync(ct))
        {
            if (!VisitorRules.IsNoShow(v.Status, v.ExpectedAt, v.ExpectedUntil, now)) continue;
            v.Status = VisitStatus.NoShow;
            missed++;
        }

        var days = (await db.Settings.AsNoTracking().FirstOrDefaultAsync(ct))?.RetentionDays ?? 365;
        var removed = 0;
        foreach (var v in await db.Visits.Where(x => x.AnonymisedAt == null && x.ExpectedAt < now.AddDays(-days) && x.Status != VisitStatus.Expected && x.Status != VisitStatus.CheckedIn).Take(500).ToListAsync(ct))
        {
            v.VisitorName = "—";
            v.VisitorCompany = null;
            v.VisitorEmail = null;
            v.VisitorPhone = null;
            v.Purpose = null;
            v.PassHash = Common.Hash(Common.NewCode()); // the old pass can never be shown again
            v.AnonymisedAt = now;
            removed++;
        }

        if (removed > 0) audit.Record("visits.anonymised", nameof(Visit), null, System.Text.Json.JsonSerializer.Serialize(new { count = removed, days }));
        if (missed + removed > 0) await db.SaveChangesAsync(ct);
        return missed + removed;
    }
}
