using System.Text.Json;
using Fms.BuildingBlocks.Identity;
using Fms.BuildingBlocks.Results;
using Fms.BuildingBlocks.Security;
using Fms.Inventory.Domain;
using Fms.Inventory.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Fms.Inventory.Services;

/// <summary>
/// Counting a store. The count sheet freezes what the system thought was there when counting started; people enter what they found; a different person
/// approves, and only then are the differences posted as adjustments (as differences, so stock moved during the count is not undone).
/// </summary>
public sealed class StockTakeService(InventoryDbContext db, StockLedger ledger, IAuditPort audit, ICurrentUser user, IUserDirectory users, TimeProvider clock)
{
    // ---------------- reading ----------------

    public async Task<IReadOnlyList<StockTakeDto>> ListAsync(Guid? storeId, StockTakeStatus? status, CancellationToken ct = default)
    {
        var query = db.StockTakes.AsNoTracking().AsQueryable();
        if (storeId is { } s) query = query.Where(t => t.StoreId == s);
        if (status is { } st) query = query.Where(t => t.Status == st);
        var takes = await query.OrderByDescending(t => t.StartedAt).Take(100).ToListAsync(ct);
        return await ToDtosAsync(takes, withDetail: false, ct);
    }

    public async Task<Result<StockTakeDto>> GetAsync(Guid id, CancellationToken ct = default)
    {
        var take = await db.StockTakes.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id, ct);
        return take is null ? Result<StockTakeDto>.NotFound() : Result<StockTakeDto>.Ok((await ToDtosAsync([take], withDetail: true, ct)).Single());
    }

    // ---------------- counting ----------------

    public async Task<Result<StockTakeDto>> StartAsync(StartStockTakeRequest r, CancellationToken ct = default)
    {
        if (r.StoreId is not { } storeId || await db.Stores.AsNoTracking().FirstOrDefaultAsync(s => s.Id == storeId, ct) is not { IsArchived: false })
            return Result<StockTakeDto>.Invalid("storeId", "Store not found or archived.");
        if (r.Note is { Length: > 500 }) return Result<StockTakeDto>.Invalid("note", "Note is at most 500 characters.");
        if (user.UserId is not { } me) return Result<StockTakeDto>.Forbidden("Sign in to start a stock take.");
        if (await db.StockTakes.AnyAsync(t => t.StoreId == storeId && (t.Status == StockTakeStatus.Counting || t.Status == StockTakeStatus.Submitted), ct))
            return Result<StockTakeDto>.Conflict("This store already has a stock take in progress.");

        var take = new StockTake { StoreId = storeId, Status = StockTakeStatus.Counting, Note = Common.Clean(r.Note), StartedAt = clock.GetUtcNow(), StartedBy = me };
        db.StockTakes.Add(take);
        foreach (var level in await db.Levels.AsNoTracking().Where(l => l.StoreId == storeId && l.OnHand > 0).ToListAsync(ct))
            db.StockTakeLines.Add(new StockTakeLine { StockTakeId = take.Id, ItemId = level.ItemId, SystemQty = level.OnHand, UnitCost = level.AverageCost });
        audit.Record("inventory.stocktake.started", nameof(StockTake), take.Id.ToString(), JsonSerializer.Serialize(new { storeId }));
        await db.SaveChangesAsync(ct);
        return await GetAsync(take.Id, ct);
    }

    public async Task<Result<StockTakeDto>> SaveCountsAsync(Guid id, SaveCountsRequest r, CancellationToken ct = default)
    {
        var take = await db.StockTakes.FirstOrDefaultAsync(t => t.Id == id, ct);
        if (take is null) return Result<StockTakeDto>.NotFound();
        if (take.Status != StockTakeStatus.Counting) return Result<StockTakeDto>.Conflict("Counts can only be changed while counting.");
        var counts = r.Counts ?? [];
        var errors = new ValidationErrors();
        if (counts.Count == 0 || counts.Count > 2000) errors.Add("counts", "Send between 1 and 2000 counts.");
        if (counts.Any(c => c.ItemId is null || (c.CountedQty is { } q && (q < 0 || q > StockRules.MaxQuantity || decimal.Round(q, StockRules.QuantityDecimals) != q)))) errors.Add("counts", "Every count needs an item and a quantity of zero or more (up to 4 decimals).");
        if (counts.GroupBy(c => c.ItemId).Any(g => g.Count() > 1)) errors.Add("counts", "An item can be counted once per save.");
        if (errors.Any) return errors.ToResult<StockTakeDto>();

        var lines = await db.StockTakeLines.Where(l => l.StockTakeId == id).ToDictionaryAsync(l => l.ItemId, ct);
        foreach (var c in counts)
        {
            if (lines.TryGetValue(c.ItemId!.Value, out var line)) { line.CountedQty = c.CountedQty; continue; }

            // Something found that the system did not expect in this store: it counts as a new line from zero.
            if (!await db.Items.AnyAsync(i => i.Id == c.ItemId && !i.IsArchived, ct)) return Result<StockTakeDto>.Invalid("counts", "An item was not found or is archived.");
            var level = await db.Levels.AsNoTracking().FirstOrDefaultAsync(l => l.StoreId == take.StoreId && l.ItemId == c.ItemId, ct);
            line = new StockTakeLine { StockTakeId = id, ItemId = c.ItemId.Value, SystemQty = 0, UnitCost = level?.AverageCost ?? 0, CountedQty = c.CountedQty };
            lines[line.ItemId] = line;
            db.StockTakeLines.Add(line);
        }

        await db.SaveChangesAsync(ct);
        return await GetAsync(id, ct);
    }

    public async Task<Result<StockTakeDto>> SubmitAsync(Guid id, CancellationToken ct = default)
    {
        var take = await db.StockTakes.FirstOrDefaultAsync(t => t.Id == id, ct);
        if (take is null) return Result<StockTakeDto>.NotFound();
        if (take.Status != StockTakeStatus.Counting) return Result<StockTakeDto>.Conflict("Only a stock take that is being counted can be submitted.");
        var missing = await db.StockTakeLines.CountAsync(l => l.StockTakeId == id && l.CountedQty == null, ct);
        if (missing > 0) return Result<StockTakeDto>.Invalid("counts", $"{missing} line(s) have not been counted. Enter a count (zero if none is left).");

        take.Status = StockTakeStatus.Submitted;
        take.SubmittedAt = clock.GetUtcNow();
        take.SubmittedBy = user.UserId;
        audit.Record("inventory.stocktake.submitted", nameof(StockTake), id.ToString());
        return await SaveAsync(take, ct);
    }

    /// <summary>The approver sends the sheet back to be recounted, or the starter gives up on it.</summary>
    public async Task<Result<StockTakeDto>> ReopenAsync(Guid id, string? reason, CancellationToken ct = default)
    {
        var take = await db.StockTakes.FirstOrDefaultAsync(t => t.Id == id, ct);
        if (take is null) return Result<StockTakeDto>.NotFound();
        if (take.Status != StockTakeStatus.Submitted) return Result<StockTakeDto>.Conflict("Only a submitted stock take can be sent back.");
        if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length > 500) return Result<StockTakeDto>.Invalid("reason", "Say why it is sent back (max 500 characters).");
        take.Status = StockTakeStatus.Counting;
        take.SubmittedAt = null;
        take.SubmittedBy = null;
        audit.Record("inventory.stocktake.sent_back", nameof(StockTake), id.ToString(), JsonSerializer.Serialize(new { reason = reason.Trim() }));
        return await SaveAsync(take, ct);
    }

    public async Task<Result<StockTakeDto>> CancelAsync(Guid id, string? reason, CancellationToken ct = default)
    {
        var take = await db.StockTakes.FirstOrDefaultAsync(t => t.Id == id, ct);
        if (take is null) return Result<StockTakeDto>.NotFound();
        if (take.Status is not (StockTakeStatus.Counting or StockTakeStatus.Submitted)) return Result<StockTakeDto>.Conflict("This stock take is already finished.");
        if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length > 500) return Result<StockTakeDto>.Invalid("reason", "Say why it is cancelled (max 500 characters).");
        take.Status = StockTakeStatus.Cancelled;
        take.DecidedAt = clock.GetUtcNow();
        take.DecidedBy = user.UserId;
        audit.Record("inventory.stocktake.cancelled", nameof(StockTake), id.ToString(), JsonSerializer.Serialize(new { reason = reason.Trim() }));
        return await SaveAsync(take, ct);
    }

    // ---------------- approval ----------------

    public Task<Result<StockTakeDto>> ApproveAsync(Guid id, CancellationToken ct = default) => ledger.RunAsync(async () =>
    {
        var take = await db.StockTakes.FirstOrDefaultAsync(t => t.Id == id, ct);
        if (take is null) return Result<StockTakeDto>.NotFound();
        if (take.Status != StockTakeStatus.Submitted) return Result<StockTakeDto>.Conflict("Only a submitted stock take can be approved.");
        if (user.UserId is not { } me) return Result<StockTakeDto>.Forbidden("Sign in to approve.");
        if (me == take.SubmittedBy) return Result<StockTakeDto>.Forbidden("A stock take must be approved by someone other than the person who submitted it.");

        var lines = await db.StockTakeLines.Where(l => l.StockTakeId == id).ToListAsync(ct);
        var posted = new List<object>();
        foreach (var line in lines.Where(l => l.CountedQty is { } c && c != l.SystemQty))
        {
            var delta = line.CountedQty!.Value - line.SystemQty; // a difference, so stock moved since counting began is kept
            var level = await ledger.LevelAsync(take.StoreId, line.ItemId, ct);
            if (level.OnHand + delta < level.Reserved || level.OnHand + delta < 0)
            {
                var code = await db.Items.AsNoTracking().Where(i => i.Id == line.ItemId).Select(i => i.Code).FirstOrDefaultAsync(ct);
                db.ChangeTracker.Clear();
                return Result<StockTakeDto>.Invalid("counts", $"Item {code}: posting {delta} would leave less than is held for work orders ({level.Reserved}). Release reservations or send the count back.");
            }

            ledger.Post(level, MovementType.CountAdjustment, delta, delta > 0 && level.AverageCost == 0 ? line.UnitCost : level.AverageCost, m => { m.StockTakeId = id; m.Reason = "Stock take"; });
            posted.Add(new { line.ItemId, line.SystemQty, counted = line.CountedQty, delta });
        }

        take.Status = StockTakeStatus.Approved;
        take.DecidedAt = clock.GetUtcNow();
        take.DecidedBy = me;
        audit.Record("inventory.stocktake.approved", nameof(StockTake), id.ToString(), JsonSerializer.Serialize(new { adjustments = posted }));
        await db.SaveChangesAsync(ct);
        return await GetAsync(id, ct);
    });

    // ---------------- helpers ----------------

    private async Task<Result<StockTakeDto>> SaveAsync(StockTake take, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            return Result<StockTakeDto>.Conflict("This stock take was changed by someone else. Reload and try again.");
        }

        return await GetAsync(take.Id, ct);
    }

    private async Task<IReadOnlyList<StockTakeDto>> ToDtosAsync(List<StockTake> takes, bool withDetail, CancellationToken ct)
    {
        var ids = takes.Select(t => t.Id).ToList();
        var storeIds = takes.Select(t => t.StoreId).Distinct().ToList();
        var stores = await db.Stores.AsNoTracking().Where(s => storeIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => s.Name, ct);
        var lines = await db.StockTakeLines.AsNoTracking().Where(l => ids.Contains(l.StockTakeId)).ToListAsync(ct);
        var itemIds = lines.Select(l => l.ItemId).Distinct().ToList();
        var items = withDetail ? await db.Items.AsNoTracking().Where(i => itemIds.Contains(i.Id)).ToDictionaryAsync(i => i.Id, ct) : [];
        var people = await users.GetAsync(takes.SelectMany(t => new[] { (Guid?)t.StartedBy, t.SubmittedBy, t.DecidedBy }).OfType<Guid>(), ct);
        string? Name(Guid? id) => id is { } g && people.TryGetValue(g, out var p) ? p.DisplayName ?? p.Email : null;

        return takes.Select(t =>
        {
            var mine = lines.Where(l => l.StockTakeId == t.Id).ToList();
            var diffs = mine.Where(l => l.CountedQty is { } c && c != l.SystemQty).ToList();
            var detail = withDetail
                ? mine.Where(l => items.ContainsKey(l.ItemId)).OrderBy(l => items[l.ItemId].Code).Select(l => new StockTakeLineDto(
                    l.ItemId, items[l.ItemId].Code, items[l.ItemId].Name, items[l.ItemId].Unit, l.SystemQty, l.CountedQty, l.CountedQty - l.SystemQty, l.UnitCost,
                    l.CountedQty is { } c ? Math.Round((c - l.SystemQty) * l.UnitCost, 2) : null)).ToList()
                : null;
            return new StockTakeDto(t.Id, t.StoreId, stores.GetValueOrDefault(t.StoreId) ?? "", t.Status, t.Note, t.StartedAt, Name(t.StartedBy), t.SubmittedAt, Name(t.SubmittedBy), t.DecidedAt, Name(t.DecidedBy),
                mine.Count, mine.Count(l => l.CountedQty != null), diffs.Count, Math.Round(diffs.Sum(l => (l.CountedQty!.Value - l.SystemQty) * l.UnitCost), 2), detail, Common.Version(t.RowVersion));
        }).ToList();
    }
}
