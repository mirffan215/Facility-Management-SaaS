using System.Text.Json;
using Fms.BuildingBlocks.Integration;
using Fms.BuildingBlocks.Results;
using Fms.BuildingBlocks.Security;
using Fms.Integration.Domain;
using Fms.Integration.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Fms.Integration.Services;

/// <summary>What has been sent to the accounting system, what is stuck, and whether the system still agrees with us.</summary>
public sealed class ErpAdminService(IntegrationDbContext db, IAuditPort audit, IEnumerable<IErpAdapter> adapters, TimeProvider clock)
{
    private static readonly TimeSpan ReconcileAfter = TimeSpan.FromHours(1);

    public async Task<ErpSummaryDto> SummaryAsync(CancellationToken ct = default)
    {
        var counts = await db.ErpItems.AsNoTracking().GroupBy(i => i.Status).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);
        int N(ExportStatus s) => counts.GetValueOrDefault(s);
        var oldest = await db.ErpItems.AsNoTracking().Where(i => i.Status == ExportStatus.Pending || i.Status == ExportStatus.Sending).OrderBy(i => i.CreatedAt).Select(i => (DateTimeOffset?)i.CreatedAt).FirstOrDefaultAsync(ct);
        var mismatched = await db.ErpItems.AsNoTracking().CountAsync(i => i.Status == ExportStatus.Succeeded && (i.Reconcile == ReconcileState.Mismatch || i.Reconcile == ReconcileState.Missing), ct);
        var enabled = (await db.ErpSettings.AsNoTracking().FirstOrDefaultAsync(ct))?.Enabled ?? false;
        return new ErpSummaryDto(enabled, N(ExportStatus.Pending), N(ExportStatus.Sending), N(ExportStatus.Succeeded), N(ExportStatus.DeadLetter), N(ExportStatus.Superseded), mismatched, oldest);
    }

    public async Task<Page<ErpItemDto>> ListAsync(ExportStatus? status, ErpEntity? entity, bool problemsOnly, int? page, int? pageSize, CancellationToken ct = default)
    {
        var (number, size) = Page<ErpItemDto>.Normalize(page, pageSize);
        var query = db.ErpItems.AsNoTracking().AsQueryable();
        if (status is { } s) query = query.Where(i => i.Status == s);
        if (entity is { } e) query = query.Where(i => i.Entity == e);
        if (problemsOnly) query = query.Where(i => i.Status == ExportStatus.DeadLetter || (i.Status == ExportStatus.Pending && i.Attempts > 0) || i.Reconcile == ReconcileState.Mismatch || i.Reconcile == ReconcileState.Missing);
        var total = await query.CountAsync(ct);
        var rows = await query.OrderByDescending(i => i.CreatedAt).Skip((number - 1) * size).Take(size).ToListAsync(ct);
        return new Page<ErpItemDto>(rows.Select(ToDto).ToList(), total, number, size);
    }

    public async Task<Result<ErpItemDto>> RetryAsync(Guid id, CancellationToken ct = default)
    {
        var item = await db.ErpItems.FirstOrDefaultAsync(i => i.Id == id, ct);
        if (item is null) return Result<ErpItemDto>.NotFound();
        if (item.Status != ExportStatus.DeadLetter && !(item.Status == ExportStatus.Pending && item.Attempts > 0)) return Result<ErpItemDto>.Conflict("Only an item that failed can be retried.");
        if (await db.ErpItems.AnyAsync(i => i.Entity == item.Entity && i.EntityId == item.EntityId && i.Revision > item.Revision && i.Status != ExportStatus.Superseded, ct))
            return Result<ErpItemDto>.Conflict("A newer version of this has been queued; it will carry the change.");
        item.Status = ExportStatus.Pending;
        item.Attempts = 0;
        item.NextAttemptAt = clock.GetUtcNow();
        item.LockedUntil = null;
        audit.Record("erp.item_retried", nameof(ErpExportItem), id.ToString(), JsonSerializer.Serialize(new { item.Entity, item.EntityId, item.Revision }));
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            return Result<ErpItemDto>.Conflict("A worker picked this item up at the same moment. Reload to see where it stands.");
        }

        return Result<ErpItemDto>.Ok(ToDto(item));
    }

    /// <summary>Asks the accounting system whether it still holds what we last sent for each thing, and records the answer. Only the latest sent version of each thing is checked.</summary>
    public async Task<Result<ReconcileResultDto>> ReconcileAsync(CancellationToken ct = default)
    {
        var settings = await db.ErpSettings.AsNoTracking().FirstOrDefaultAsync(ct);
        if (settings is not { Enabled: true }) return Result<ReconcileResultDto>.Conflict("The accounting connection is turned off.");
        var adapter = adapters.FirstOrDefault(a => a.Name == settings.Adapter);
        if (adapter is null) return Result<ReconcileResultDto>.Conflict($"The adapter '{settings.Adapter}' is not installed.");

        var now = clock.GetUtcNow();
        var cutoff = now - ReconcileAfter;
        var candidates = await db.ErpItems.Where(i => i.Status == ExportStatus.Succeeded && (i.ReconciledAt == null || i.ReconciledAt < cutoff)
                && !db.ErpItems.Any(n => n.Entity == i.Entity && n.EntityId == i.EntityId && n.Revision > i.Revision && n.Status != ExportStatus.Superseded))
            .OrderBy(i => i.ReconciledAt).Take(100).ToListAsync(ct);

        int matched = 0, mismatched = 0, missing = 0;
        foreach (var item in candidates)
        {
            var v = await adapter.VerifyAsync(new ErpVerifyCommand(settings.CompanyId, item.Entity, item.EntityId, item.ErpReference, item.PayloadJson), ct);
            item.Reconcile = v.Match switch { ErpMatch.Matched => ReconcileState.Matched, ErpMatch.Mismatch => ReconcileState.Mismatch, _ => ReconcileState.Missing };
            item.ReconciledAt = now;
            item.ReconcileDetail = v.Detail;
            if (v.Match == ErpMatch.Matched) matched++; else if (v.Match == ErpMatch.Mismatch) mismatched++; else missing++;
        }

        audit.Record("erp.reconciled", nameof(ErpExportItem), null, JsonSerializer.Serialize(new { checkedItems = candidates.Count, matched, mismatched, missing }));
        await db.SaveChangesAsync(ct);
        return Result<ReconcileResultDto>.Ok(new ReconcileResultDto(candidates.Count, matched, mismatched, missing));
    }

    private static ErpItemDto ToDto(ErpExportItem i) => new(
        i.Id, i.Entity, i.EntityId, Label(i.PayloadJson), i.Revision, i.Status, i.Attempts, i.NextAttemptAt, i.LastError, i.ErpReference, i.CreatedAt, i.SucceededAt, i.Reconcile, i.ReconciledAt, i.ReconcileDetail);

    private static string? Label(string payload)
    {
        try { return JsonDocument.Parse(payload).RootElement.TryGetProperty("label", out var l) ? l.GetString() : null; }
        catch (JsonException) { return null; }
    }
}
