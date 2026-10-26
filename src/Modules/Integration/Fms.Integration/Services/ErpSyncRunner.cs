using Fms.BuildingBlocks.Integration;
using Fms.BuildingBlocks.Messaging;
using Fms.BuildingBlocks.Tenancy;
using Fms.Integration.Domain;
using Fms.Integration.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Fms.Integration.Services;

/// <summary>
/// Sends queued items to the accounting system. Several workers may run: each claims a batch with a lease, so no item is sent by two at once, and an item whose worker died is
/// taken again when its lease ends. Each item is sent in its own tenant's scope with that tenant's adapter and company. A failure that might pass is retried with growing delays; the
/// rest, or too many tries, set the item aside for a person.
/// </summary>
public sealed class ErpSyncRunner(IServiceScopeFactory scopes, TimeProvider clock, IOptions<ErpOptions> options, ILogger<ErpSyncRunner> log)
{
    private sealed class ClaimedRow
    {
        public Guid Id { get; set; }
        public Guid TenantId { get; set; }
        public int Entity { get; set; }
        public Guid EntityId { get; set; }
        public string IdempotencyKey { get; set; } = "";
        public string PayloadJson { get; set; } = "";
        public int Attempts { get; set; }
    }

    /// <summary>Sends what is due. <paramref name="asOf"/> is for tests, which move "now" instead of waiting. Returns how many items were handed to an adapter.</summary>
    public async Task<int> RunOnceAsync(DateTimeOffset? asOf = null, CancellationToken ct = default)
    {
        var now = asOf ?? clock.GetUtcNow();
        var claimed = await ClaimAsync(now, ct);
        foreach (var item in claimed)
        {
            try
            {
                await SendOneAsync(item, now, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "Sending ERP item {ItemId} failed unexpectedly; it will be tried again when its lease ends", item.Id);
            }
        }

        return claimed.Count;
    }

    private async Task<List<ClaimedRow>> ClaimAsync(DateTimeOffset now, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().BeginPlatformScope();
        var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        var o = options.Value;
        var until = now + o.Lease;

        // One item per entity at a time, and never one the lease of which is still running.
        return await db.Database.SqlQuery<ClaimedRow>($"""
            WITH due AS (
                SELECT TOP ({o.BatchSize}) i.*
                FROM integration.ErpExportItems i WITH (UPDLOCK, READPAST, ROWLOCK)
                WHERE ((i.Status = 1 AND i.NextAttemptAt <= {now}) OR (i.Status = 2 AND i.LockedUntil < {now}))
                  AND NOT EXISTS (SELECT 1 FROM integration.ErpExportItems o
                                  WHERE o.TenantId = i.TenantId AND o.Entity = i.Entity AND o.EntityId = i.EntityId AND o.Id <> i.Id AND o.Status = 2 AND o.LockedUntil >= {now})
                ORDER BY i.NextAttemptAt, i.CreatedAt)
            UPDATE due SET Status = 2, LockedUntil = {until}, Attempts = Attempts + 1
            OUTPUT inserted.Id, inserted.TenantId, inserted.Entity, inserted.EntityId, inserted.IdempotencyKey, inserted.PayloadJson, inserted.Attempts
            """).ToListAsync(ct);
    }

    private async Task SendOneAsync(ClaimedRow claimed, DateTimeOffset now, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().SetTenant(claimed.TenantId);
        var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        var outbox = scope.ServiceProvider.GetRequiredService<IOutbox>();
        var o = options.Value;

        var item = await db.ErpItems.FirstOrDefaultAsync(i => i.Id == claimed.Id, ct);
        if (item is null) return;
        var settings = await db.ErpSettings.AsNoTracking().FirstOrDefaultAsync(ct);

        if (settings is not { Enabled: true })
        {
            // The connection was turned off after this was queued: hold it, without counting a try, until someone turns it back on.
            item.Status = ExportStatus.Pending;
            item.Attempts = Math.Max(0, item.Attempts - 1);
            item.NextAttemptAt = now.AddHours(1);
            item.LockedUntil = null;
            item.LastError = "The accounting connection is turned off.";
            await db.SaveChangesAsync(ct);
            return;
        }

        var adapter = scope.ServiceProvider.GetServices<IErpAdapter>().FirstOrDefault(a => a.Name == settings.Adapter);
        ErpResult result;
        if (adapter is null) result = new ErpResult(false, null, false, $"The adapter '{settings.Adapter}' is not installed.");
        else
        {
            try
            {
                result = await adapter.ExportAsync(new ErpExportCommand(settings.CompanyId, item.Entity, item.EntityId, item.IdempotencyKey, item.PayloadJson), ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                result = new ErpResult(false, null, true, ex.Message); // a timeout or a dropped connection: worth another try
            }
        }

        item.LockedUntil = null;
        if (result.Success)
        {
            item.Status = ExportStatus.Succeeded;
            item.SucceededAt = now;
            item.ErpReference = result.ErpReference;
            item.LastError = null;
            if (result.ErpReference is { } reference)
            {
                if (item.Entity == ErpEntity.Vendor) outbox.Enqueue(ErpMessages.VendorSynced, new ErpReferenceMessage(item.EntityId, reference));
                else if (item.Entity == ErpEntity.PurchaseOrder) outbox.Enqueue(ErpMessages.OrderSynced, new ErpReferenceMessage(item.EntityId, reference));
            }
        }
        else
        {
            var error = result.Error ?? "The accounting system refused the request.";
            item.LastError = error.Length > 2000 ? error[..2000] : error;
            if (!result.Retryable || ErpRetry.GiveUp(item.Attempts, o.MaxAttempts))
            {
                item.Status = ExportStatus.DeadLetter;
                log.LogError("ERP item {ItemId} ({Entity}) set aside after {Attempts} attempt(s): {Error}", item.Id, item.Entity, item.Attempts, item.LastError);
            }
            else
            {
                item.Status = ExportStatus.Pending;
                item.NextAttemptAt = now + ErpRetry.Delay(item.Attempts, o.BaseRetryDelay, o.MaxRetryDelay);
            }
        }

        await db.SaveChangesAsync(ct);
    }
}
