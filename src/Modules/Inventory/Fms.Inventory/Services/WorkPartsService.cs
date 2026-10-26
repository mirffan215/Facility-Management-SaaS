using System.Text.Json;
using Fms.BuildingBlocks.Identity;
using Fms.BuildingBlocks.Results;
using Fms.BuildingBlocks.Security;
using Fms.Inventory.Domain;
using Fms.Inventory.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Fms.Inventory.Services;

/// <summary>
/// Parts on work orders. A planner <b>reserves</b> parts (they stay in the store but are promised), a technician <b>issues</b> them (they leave stock and
/// are costed at the store's average) and can <b>return</b> what was not used. Parts cost on the work order is read from here, so there is one record.
/// </summary>
public sealed class WorkPartsService(InventoryDbContext db, StockLedger ledger, IAuditPort audit, IWorkOrderLookup work, IFacilityLookup facilities, TimeProvider clock, ICurrentUser user)
    : IWorkPartsLookup, IPmPartsReserver
{
    // ---------------- reading ----------------

    public async Task<Result<WorkPartsDto>> PartsAsync(Guid workOrderId, CancellationToken ct = default)
    {
        var wo = await work.GetAsync(workOrderId, ct);
        if (wo is null) return Result<WorkPartsDto>.NotFound();

        var reservations = await db.Reservations.AsNoTracking().Where(r => r.WorkOrderId == workOrderId).ToListAsync(ct);
        var movements = await db.Movements.AsNoTracking().Where(m => m.WorkOrderId == workOrderId && (m.Type == MovementType.Issue || m.Type == MovementType.Return)).ToListAsync(ct);
        var keys = reservations.Select(r => (r.StoreId, r.ItemId)).Concat(movements.Select(m => (m.StoreId, m.ItemId))).Distinct().ToList();
        var storeIds = keys.Select(k => k.StoreId).Distinct().ToList();
        var itemIds = keys.Select(k => k.ItemId).Distinct().ToList();
        var stores = await db.Stores.AsNoTracking().Where(s => storeIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, ct);
        var items = await db.Items.AsNoTracking().Where(i => itemIds.Contains(i.Id)).ToDictionaryAsync(i => i.Id, ct);
        var levels = await db.Levels.AsNoTracking().Where(l => storeIds.Contains(l.StoreId) && itemIds.Contains(l.ItemId)).ToListAsync(ct);

        var lines = new List<WorkPartDto>();
        foreach (var (storeId, itemId) in keys)
        {
            if (!stores.TryGetValue(storeId, out var store) || !items.TryGetValue(itemId, out var item)) continue;
            var reserved = reservations.Where(r => r.StoreId == storeId && r.ItemId == itemId).Sum(r => r.Quantity);
            var moves = movements.Where(m => m.StoreId == storeId && m.ItemId == itemId).ToList();
            var issued = -moves.Sum(m => m.Quantity);
            var cost = Math.Round(-moves.Sum(m => m.Quantity * m.UnitCost), 2);
            if (reserved == 0 && issued == 0 && moves.Count == 0) continue;
            var level = levels.FirstOrDefault(l => l.StoreId == storeId && l.ItemId == itemId);
            lines.Add(new WorkPartDto(itemId, item.Code, item.Name, item.Unit, storeId, store.Name, reserved, issued, issued > 0 ? Math.Round(cost / issued, 4) : 0, cost,
                level is null ? 0 : StockRules.Available(level.OnHand, level.Reserved)));
        }

        lines = lines.OrderBy(l => l.ItemCode).ThenBy(l => l.StoreName).ToList();
        return Result<WorkPartsDto>.Ok(new WorkPartsDto(workOrderId, wo.AcceptsParts, lines, lines.Sum(l => l.Cost)));
    }

    public async Task<decimal> MaterialCostAsync(Guid workOrderId, CancellationToken ct = default)
    {
        var moves = await db.Movements.AsNoTracking().Where(m => m.WorkOrderId == workOrderId && (m.Type == MovementType.Issue || m.Type == MovementType.Return))
            .Select(m => new { m.Quantity, m.UnitCost }).ToListAsync(ct);
        return Math.Round(-moves.Sum(m => m.Quantity * m.UnitCost), 2);
    }

    // ---------------- reserve and release ----------------

    public Task<Result<WorkPartsDto>> ReserveAsync(Guid workOrderId, PartRequest r, CancellationToken ct = default) => ledger.RunAsync(async () =>
    {
        var (gate, wo) = await GateAsync(workOrderId, ct);
        if (gate is not null) return gate;
        var errors = new ValidationErrors();
        if (r.ItemId is null || !await db.Items.AnyAsync(i => i.Id == r.ItemId && !i.IsArchived, ct)) errors.Add("itemId", "Item not found or archived.");
        if (r.Quantity is not { } qty || !StockRules.IsValidQuantity(qty)) errors.Add("quantity", "Quantity must be greater than zero (up to 4 decimals).");
        if (r.StoreId is { } sid && !await db.Stores.AnyAsync(s => s.Id == sid && !s.IsArchived, ct)) errors.Add("storeId", "Store not found or archived.");
        if (errors.Any) return errors.ToResult<WorkPartsDto>();

        var storeId = r.StoreId ?? await ChooseStoreAsync(r.ItemId!.Value, r.Quantity!.Value, wo!, ct);
        if (storeId is null) return Result<WorkPartsDto>.Invalid("storeId", "No store has that much available. Receive stock, release another reservation, or choose a smaller quantity.");

        var outcome = await HoldAsync(wo!, storeId.Value, r.ItemId!.Value, r.Quantity!.Value, ReservationSource.Manual, ct);
        if (outcome is not null) return Result<WorkPartsDto>.Invalid("quantity", outcome);
        await db.SaveChangesAsync(ct);
        return await PartsAsync(workOrderId, ct);
    });

    public Task<Result<WorkPartsDto>> ReleaseAsync(Guid workOrderId, PartRequest r, CancellationToken ct = default) => ledger.RunAsync(async () =>
    {
        if (await work.GetAsync(workOrderId, ct) is null) return Result<WorkPartsDto>.NotFound();
        if (r.ItemId is null || r.StoreId is null) return Result<WorkPartsDto>.Invalid("itemId", "Say which part and store to release.");
        var reservation = await db.Reservations.FirstOrDefaultAsync(x => x.WorkOrderId == workOrderId && x.StoreId == r.StoreId && x.ItemId == r.ItemId, ct);
        if (reservation is null || reservation.Quantity == 0) return Result<WorkPartsDto>.NotFound("Nothing is reserved for that part.");

        var released = reservation.Quantity;
        await ReleaseOneAsync(reservation, ct);
        audit.Record("inventory.reservation_released", "WorkOrder", workOrderId.ToString(), JsonSerializer.Serialize(new { reservation.ItemId, reservation.StoreId, quantity = released }));
        await db.SaveChangesAsync(ct);
        return await PartsAsync(workOrderId, ct);
    });

    /// <summary>Lets go of everything still held for a work order (it was cancelled, verified or closed). Safe to repeat.</summary>
    public Task<Result<int>> ReleaseAllAsync(Guid workOrderId, CancellationToken ct = default) => ledger.RunAsync(async () =>
    {
        var held = await db.Reservations.Where(x => x.WorkOrderId == workOrderId && x.Quantity > 0).ToListAsync(ct);
        foreach (var reservation in held) await ReleaseOneAsync(reservation, ct);
        if (held.Count > 0)
        {
            audit.Record("inventory.reservations_released", "WorkOrder", workOrderId.ToString(), JsonSerializer.Serialize(new { lines = held.Count, reason = "work order ended" }));
            await db.SaveChangesAsync(ct);
        }

        return Result<int>.Ok(held.Count);
    });

    // ---------------- issue and return ----------------

    public Task<Result<WorkPartsDto>> IssueAsync(Guid workOrderId, PartRequest r, CancellationToken ct = default) => ledger.RunAsync(async () =>
    {
        var (gate, wo) = await GateAsync(workOrderId, ct);
        if (gate is not null) return gate;
        var errors = new ValidationErrors();
        if (r.ItemId is null || !await db.Items.AnyAsync(i => i.Id == r.ItemId, ct)) errors.Add("itemId", "Item not found.");
        if (r.Quantity is not { } qty || !StockRules.IsValidQuantity(qty)) errors.Add("quantity", "Quantity must be greater than zero (up to 4 decimals).");
        if (r.StoreId is { } sid && !await db.Stores.AnyAsync(s => s.Id == sid, ct)) errors.Add("storeId", "Store not found.");
        if (errors.Any) return errors.ToResult<WorkPartsDto>();

        var itemId = r.ItemId!.Value;
        var quantity = r.Quantity!.Value;
        var storeId = r.StoreId ?? await ChooseStoreAsync(itemId, quantity, wo!, ct);
        if (storeId is null) return Result<WorkPartsDto>.Invalid("storeId", "No store has that much available.");

        var level = await ledger.FindLevelAsync(storeId.Value, itemId, ct);
        var reservation = await db.Reservations.FirstOrDefaultAsync(x => x.WorkOrderId == workOrderId && x.StoreId == storeId && x.ItemId == itemId, ct);
        var own = reservation?.Quantity ?? 0;
        var usable = level is null ? 0 : level.OnHand - (level.Reserved - own); // what is on the shelf less what is promised to other work orders
        if (level is null || quantity > usable)
            return Result<WorkPartsDto>.Invalid("quantity", $"Only {Math.Max(0, usable)} can be issued from that store (the rest is held for other work orders).");

        var fromReservation = Math.Min(own, quantity);
        if (reservation is not null && fromReservation > 0)
        {
            reservation.Quantity -= fromReservation;
            level.Reserved -= fromReservation;
        }

        ledger.Post(level, MovementType.Issue, -quantity, level.AverageCost, m => m.WorkOrderId = workOrderId);
        audit.Record("inventory.issued", "WorkOrder", workOrderId.ToString(), JsonSerializer.Serialize(new { itemId, storeId, quantity, unitCost = level.AverageCost, fromReservation }));
        await db.SaveChangesAsync(ct);
        return await PartsAsync(workOrderId, ct);
    });

    public Task<Result<WorkPartsDto>> ReturnAsync(Guid workOrderId, PartRequest r, CancellationToken ct = default) => ledger.RunAsync(async () =>
    {
        var (gate, _) = await GateAsync(workOrderId, ct);
        if (gate is not null) return gate;
        var errors = new ValidationErrors();
        if (r.ItemId is null || r.StoreId is null) errors.Add("itemId", "Say which part and store it goes back to.");
        if (r.Quantity is not { } qty || !StockRules.IsValidQuantity(qty)) errors.Add("quantity", "Quantity must be greater than zero (up to 4 decimals).");
        if (r.Reason is { Length: > 500 }) errors.Add("reason", "Reason is at most 500 characters.");
        if (errors.Any) return errors.ToResult<WorkPartsDto>();

        var moves = await db.Movements.AsNoTracking().Where(m => m.WorkOrderId == workOrderId && m.StoreId == r.StoreId && m.ItemId == r.ItemId && (m.Type == MovementType.Issue || m.Type == MovementType.Return))
            .Select(m => new { m.Quantity, m.UnitCost }).ToListAsync(ct);
        var net = -moves.Sum(m => m.Quantity);
        if (r.Quantity!.Value > net) return Result<WorkPartsDto>.Invalid("quantity", net <= 0 ? "Nothing from this store is out on this work order." : $"Only {net} is out on this work order.");

        var cost = Math.Round(-moves.Sum(m => m.Quantity * m.UnitCost) / net, 4); // back in at what it cost going out
        var level = await ledger.FindLevelAsync(r.StoreId!.Value, r.ItemId!.Value, ct);
        if (level is null) return Result<WorkPartsDto>.NotFound();
        ledger.Post(level, MovementType.Return, r.Quantity.Value, cost, m => { m.WorkOrderId = workOrderId; m.Reason = Common.Clean(r.Reason); });
        audit.Record("inventory.returned", "WorkOrder", workOrderId.ToString(), JsonSerializer.Serialize(new { r.ItemId, r.StoreId, r.Quantity, unitCost = cost, reason = Common.Clean(r.Reason) }));
        await db.SaveChangesAsync(ct);
        return await PartsAsync(workOrderId, ct);
    });

    // ---------------- preventive maintenance ----------------

    public async Task ReserveForTemplateAsync(Guid workOrderId, Guid pmTemplateId, CancellationToken ct = default)
    {
        var parts = await db.PmTemplateParts.AsNoTracking().Where(p => p.PmTemplateId == pmTemplateId).ToListAsync(ct);
        if (parts.Count == 0) return;
        var wo = await work.GetAsync(workOrderId, ct);
        if (wo is null || !wo.AcceptsParts) return;

        var short_ = new List<object>();
        foreach (var part in parts)
        {
            var result = await ledger.RunAsync(async () =>
            {
                if (await db.Reservations.AnyAsync(r => r.WorkOrderId == workOrderId && r.ItemId == part.ItemId && r.Source == ReservationSource.PmTemplate, ct)) return Result<bool>.Ok(true); // already done (a retry)
                var storeId = part.StoreId ?? await ChooseStoreAsync(part.ItemId, part.Quantity, wo, ct);
                if (storeId is null) return Result<bool>.Invalid("storeId", "short");
                if (await HoldAsync(wo, storeId.Value, part.ItemId, part.Quantity, ReservationSource.PmTemplate, ct) is not null) return Result<bool>.Invalid("quantity", "short");
                await db.SaveChangesAsync(ct);
                return Result<bool>.Ok(true);
            });
            if (!result.IsOk) short_.Add(new { part.ItemId, part.Quantity, part.StoreId });
        }

        if (short_.Count > 0)
        {
            audit.Record("inventory.reservation_short", "WorkOrder", workOrderId.ToString(), JsonSerializer.Serialize(new { missing = short_ }));
            await db.SaveChangesAsync(ct);
        }
    }

    // ---------------- helpers ----------------

    private async Task<(Result<WorkPartsDto>? Failure, WorkOrderRef? Order)> GateAsync(Guid workOrderId, CancellationToken ct)
    {
        var wo = await work.GetAsync(workOrderId, ct);
        if (wo is null) return (Result<WorkPartsDto>.NotFound(), null);
        if (!wo.AcceptsParts) return (Result<WorkPartsDto>.Conflict("This work order no longer takes parts."), null);
        return (null, wo);
    }

    /// <summary>Holds stock for a work order. Returns a message when there is not enough available, otherwise null (nothing is saved here).</summary>
    private async Task<string?> HoldAsync(WorkOrderRef wo, Guid storeId, Guid itemId, decimal quantity, ReservationSource source, CancellationToken ct)
    {
        var level = await ledger.FindLevelAsync(storeId, itemId, ct);
        var available = level is null ? 0 : StockRules.Available(level.OnHand, level.Reserved);
        if (level is null || available < quantity) return $"Only {available} available in that store.";

        var reservation = await db.Reservations.FirstOrDefaultAsync(r => r.WorkOrderId == wo.Id && r.StoreId == storeId && r.ItemId == itemId, ct);
        if (reservation is null)
            db.Reservations.Add(new Reservation { WorkOrderId = wo.Id, StoreId = storeId, ItemId = itemId, Quantity = quantity, Source = source, CreatedAt = clock.GetUtcNow(), CreatedBy = user.UserId });
        else reservation.Quantity += quantity;
        level.Reserved += quantity;
        ledger.Refresh(level);
        audit.Record("inventory.reserved", "WorkOrder", wo.Id.ToString(), JsonSerializer.Serialize(new { itemId, storeId, quantity, source }));
        return null;
    }

    private async Task ReleaseOneAsync(Reservation reservation, CancellationToken ct)
    {
        var level = await ledger.FindLevelAsync(reservation.StoreId, reservation.ItemId, ct);
        if (level is not null)
        {
            level.Reserved = Math.Max(0, level.Reserved - reservation.Quantity);
            ledger.Refresh(level);
        }

        reservation.Quantity = 0;
    }

    /// <summary>The store to take from when none was named: one already holding this work order's reservation, else a store at the work order's site, else the one with most available.</summary>
    private async Task<Guid?> ChooseStoreAsync(Guid itemId, decimal quantity, WorkOrderRef wo, CancellationToken ct)
    {
        var held = await db.Reservations.AsNoTracking().Where(r => r.WorkOrderId == wo.Id && r.ItemId == itemId && r.Quantity > 0).Select(r => (Guid?)r.StoreId).FirstOrDefaultAsync(ct);
        if (held is not null) return held;

        var candidates = await db.Levels.AsNoTracking().Where(l => l.ItemId == itemId && l.OnHand - l.Reserved >= quantity)
            .Join(db.Stores.Where(s => !s.IsArchived), l => l.StoreId, s => s.Id, (l, s) => new { s.Id, s.SiteId, Available = l.OnHand - l.Reserved }).ToListAsync(ct);
        if (candidates.Count == 0) return null;
        var site = wo.LocationId is { } loc ? await facilities.SiteOfAsync(loc, ct) : null;
        return candidates.OrderByDescending(c => site != null && c.SiteId == site).ThenByDescending(c => c.Available).First().Id;
    }
}
