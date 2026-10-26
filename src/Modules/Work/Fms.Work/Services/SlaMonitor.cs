using Fms.BuildingBlocks.Messaging;
using Fms.BuildingBlocks.Tenancy;
using Fms.Work.Domain;
using Fms.Work.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Fms.Work.Services;

/// <summary>
/// Looks for SLA clocks that have run out (or are about to) and raises one alert for each, once. Run it every minute or so from the
/// worker. Each tenant is processed in its own scope, so one tenant's data is never touched under another's identity.
/// </summary>
public sealed class SlaMonitor(IServiceScopeFactory scopes, TimeProvider clock, ILogger<SlaMonitor> log)
{
    /// <summary>Returns how many alerts were raised.</summary>
    public async Task<int> RunOnceAsync(CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();

        List<Guid> tenants;
        await using (var scope = scopes.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<TenantContext>().BeginPlatformScope();
            var db = scope.ServiceProvider.GetRequiredService<WorkDbContext>();
            // Cheap pre-filter: anything that could need an alert. The exact decision is made per tenant below.
            tenants = await db.WorkOrders.IgnoreQueryFilters()
                .Where(w => w.SlaStartedAt != null && w.CompletedAt == null && w.Status != WorkOrderStatus.Closed && w.Status != WorkOrderStatus.Cancelled
                            && ((w.ResponseDueAt != null && w.RespondedAt == null && w.ResponseBreachNotifiedAt == null && w.ResponseDueAt < now)
                                || (w.SlaPausedAt == null && w.ResolutionBreachNotifiedAt == null && w.ResolutionDueAt != null && w.ResolutionDueAt < now)
                                || (w.SlaPausedAt == null && w.ResolutionWarnedAt == null && w.ResolutionDueAt != null)))
                .Select(w => w.TenantId).Distinct().ToListAsync(ct);
        }

        var raised = 0;
        foreach (var tenant in tenants)
        {
            try { raised += await RunTenantAsync(tenant, now, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "SLA monitoring failed for a tenant; the others continue");
            }
        }

        return raised;
    }

    private async Task<int> RunTenantAsync(Guid tenant, DateTimeOffset now, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().SetTenant(tenant);
        var db = scope.ServiceProvider.GetRequiredService<WorkDbContext>();
        var outbox = scope.ServiceProvider.GetRequiredService<IOutbox>();

        var candidates = await db.WorkOrders
            .Where(w => w.SlaStartedAt != null && w.CompletedAt == null && w.Status != WorkOrderStatus.Closed && w.Status != WorkOrderStatus.Cancelled)
            .ToListAsync(ct);

        var raised = 0;
        foreach (var wo in candidates)
        {
            if (wo.RespondedAt is null && wo.ResponseDueAt < now && wo.ResponseBreachNotifiedAt is null)
            {
                wo.ResponseBreachNotifiedAt = now;
                outbox.Enqueue(WorkMessages.Sla, new SlaAlertMessage(wo.Id, SlaAlert.ResponseBreached));
                raised++;
            }

            if (wo.SlaPausedAt is not null) continue;
            if (wo.ResolutionDueAt < now && wo.ResolutionBreachNotifiedAt is null)
            {
                wo.ResolutionBreachNotifiedAt = now;
                wo.ResolutionWarnedAt ??= now; // no "at risk" notice after the breach one
                outbox.Enqueue(WorkMessages.Sla, new SlaAlertMessage(wo.Id, SlaAlert.ResolutionBreached));
                raised++;
            }
            else if (SlaRules.ResolutionAtRisk(wo, now) && wo.ResolutionWarnedAt is null)
            {
                wo.ResolutionWarnedAt = now;
                outbox.Enqueue(WorkMessages.Sla, new SlaAlertMessage(wo.Id, SlaAlert.ResolutionAtRisk));
                raised++;
            }
        }

        if (raised > 0) await db.SaveChangesAsync(ct);
        return raised;
    }
}
