using System.Text.Json;
using Fms.BuildingBlocks.Identity;
using Fms.BuildingBlocks.Results;
using Fms.BuildingBlocks.Security;
using Fms.Inventory.Domain;
using Fms.Inventory.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Fms.Inventory.Services;

/// <summary>Quantities: what is where, receiving, transfers, adjustments, reorder levels and the movement ledger.</summary>
public sealed class StockService(InventoryDbContext db, StockLedger ledger, IAuditPort audit, IUserDirectory users)
{
    // ---------------- reading ----------------

    public async Task<Page<StockLevelDto>> LevelsAsync(Guid? storeId, Guid? itemId, string? q, bool lowOnly, bool includeZero, int? page, int? pageSize, CancellationToken ct = default)
    {
        var (number, size) = Page<StockLevelDto>.Normalize(page, pageSize);
        var query = db.Levels.AsNoTracking().AsQueryable();
        if (storeId is { } s) query = query.Where(l => l.StoreId == s);
        if (itemId is { } i) query = query.Where(l => l.ItemId == i);
        if (lowOnly) query = query.Where(l => l.MinQty != null && l.OnHand - l.Reserved <= l.MinQty);
        else if (!includeZero) query = query.Where(l => l.OnHand > 0 || l.MinQty != null);
        if (Common.Clean(q) is { } text)
            query = query.Where(l => db.Items.Any(x => x.Id == l.ItemId && (x.Code.Contains(text) || x.Name.Contains(text) || (x.NameAr != null && x.NameAr.Contains(text)) || x.Barcode == text)));

        var total = await query.CountAsync(ct);
        var levels = await query.OrderBy(l => db.Items.Where(x => x.Id == l.ItemId).Select(x => x.Code).FirstOrDefault()).ThenBy(l => l.StoreId).Skip((number - 1) * size).Take(size).ToListAsync(ct);
        return new Page<StockLevelDto>(await ToDtosAsync(levels, ct), total, number, size);
    }

    public async Task<Result<IReadOnlyList<StockLevelDto>>> LevelsOfItemAsync(Guid itemId, CancellationToken ct = default)
    {
        if (!await db.Items.AnyAsync(i => i.Id == itemId, ct)) return Result<IReadOnlyList<StockLevelDto>>.NotFound();
        return Result<IReadOnlyList<StockLevelDto>>.Ok(await ToDtosAsync(await db.Levels.AsNoTracking().Where(l => l.ItemId == itemId).ToListAsync(ct), ct));
    }

    public async Task<Page<MovementDto>> MovementsAsync(Guid? storeId, Guid? itemId, Guid? workOrderId, MovementType? type, int? page, int? pageSize, CancellationToken ct = default)
    {
        var (number, size) = Page<MovementDto>.Normalize(page, pageSize);
        var query = db.Movements.AsNoTracking().AsQueryable();
        if (storeId is { } s) query = query.Where(m => m.StoreId == s);
        if (itemId is { } i) query = query.Where(m => m.ItemId == i);
        if (workOrderId is { } w) query = query.Where(m => m.WorkOrderId == w);
        if (type is { } t) query = query.Where(m => m.Type == t);
        var total = await query.CountAsync(ct);
        var rows = await query.OrderByDescending(m => m.At).ThenByDescending(m => m.BalanceAfter).Skip((number - 1) * size).Take(size).ToListAsync(ct);
        var stores = await db.Stores.AsNoTracking().Where(x => rows.Select(r => r.StoreId).Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.Name, ct);
        var items = await db.Items.AsNoTracking().Where(x => rows.Select(r => r.ItemId).Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct);
        var people = await users.GetAsync(rows.Where(r => r.By != null).Select(r => r.By!.Value), ct);
        return new Page<MovementDto>(rows.Select(m => new MovementDto(
            m.Id, m.StoreId, stores.GetValueOrDefault(m.StoreId), m.ItemId, items.GetValueOrDefault(m.ItemId)?.Code, items.GetValueOrDefault(m.ItemId)?.Name, m.Type, m.Quantity, m.UnitCost, m.BalanceAfter,
            m.At, m.By, m.By is { } u && people.TryGetValue(u, out var p) ? p.DisplayName ?? p.Email : null, m.WorkOrderId, m.Reference, m.Reason)).ToList(), total, number, size);
    }

    // ---------------- receiving, adjusting, moving ----------------

    public Task<Result<StockLevelDto>> ReceiveAsync(ReceiveRequest r, CancellationToken ct = default) => ledger.RunAsync(async () =>
    {
        var errors = new ValidationErrors();
        var (store, item) = await CheckStoreAndItemAsync(r.StoreId, r.ItemId, errors, ct);
        if (r.Quantity is not { } qty || !StockRules.IsValidQuantity(qty)) errors.Add("quantity", "Quantity must be greater than zero (up to 4 decimals).");
        if (r.UnitCost is not { } cost || !StockRules.IsValidCost(cost)) errors.Add("unitCost", "Unit cost must be zero or more (up to 4 decimals).");
        if (r.Reference is { Length: > 100 }) errors.Add("reference", "Reference is at most 100 characters.");
        if (r.Note is { Length: > 500 }) errors.Add("note", "Note is at most 500 characters.");
        if (errors.Any) return errors.ToResult<StockLevelDto>();

        var level = await ledger.LevelAsync(store!.Id, item!.Id, ct);
        ledger.Post(level, MovementType.Receipt, r.Quantity!.Value, r.UnitCost!.Value, m => { m.Reference = Common.Clean(r.Reference); m.Reason = Common.Clean(r.Note); });
        audit.Record("inventory.received", nameof(StockLevel), level.Id.ToString(), JsonSerializer.Serialize(new { store.Code, ItemCode = item.Code, r.Quantity, r.UnitCost, r.Reference }));
        await db.SaveChangesAsync(ct);
        return Result<StockLevelDto>.Ok(await DtoAsync(level, store, item, ct));
    });

    public Task<Result<StockLevelDto>> AdjustAsync(AdjustRequest r, CancellationToken ct = default) => ledger.RunAsync(async () =>
    {
        var errors = new ValidationErrors();
        var (store, item) = await CheckStoreAndItemAsync(r.StoreId, r.ItemId, errors, ct);
        if (r.Delta is not { } delta || delta == 0 || Math.Abs(delta) > StockRules.MaxQuantity || decimal.Round(delta, StockRules.QuantityDecimals) != delta) errors.Add("delta", "Say how much to add (positive) or remove (negative); up to 4 decimals.");
        if (string.IsNullOrWhiteSpace(r.Reason) || r.Reason.Trim().Length > 500) errors.Add("reason", "A reason is required (max 500 characters).");
        if (errors.Any) return errors.ToResult<StockLevelDto>();

        var level = await ledger.LevelAsync(store!.Id, item!.Id, ct);
        if (level.OnHand + r.Delta!.Value < level.Reserved)
            return Result<StockLevelDto>.Invalid("delta", level.Reserved > 0 ? $"That would leave less than the {level.Reserved} held for work orders. Release reservations first." : "There is not that much in stock.");

        ledger.Post(level, MovementType.Adjustment, r.Delta.Value, level.AverageCost, m => m.Reason = r.Reason!.Trim());
        audit.Record("inventory.adjusted", nameof(StockLevel), level.Id.ToString(), JsonSerializer.Serialize(new { store.Code, ItemCode = item.Code, r.Delta, reason = r.Reason!.Trim() }));
        await db.SaveChangesAsync(ct);
        return Result<StockLevelDto>.Ok(await DtoAsync(level, store, item, ct));
    });

    public Task<Result<IReadOnlyList<StockLevelDto>>> TransferAsync(TransferRequest r, CancellationToken ct = default) => ledger.RunAsync(async () =>
    {
        var errors = new ValidationErrors();
        var (from, item) = await CheckStoreAndItemAsync(r.FromStoreId, r.ItemId, errors, ct, "fromStoreId");
        var (to, _) = await CheckStoreAndItemAsync(r.ToStoreId, r.ItemId, errors, ct, "toStoreId");
        if (r.FromStoreId is not null && r.FromStoreId == r.ToStoreId) errors.Add("toStoreId", "Choose a different store.");
        if (r.Quantity is not { } qty || !StockRules.IsValidQuantity(qty)) errors.Add("quantity", "Quantity must be greater than zero (up to 4 decimals).");
        if (r.Note is { Length: > 500 }) errors.Add("note", "Note is at most 500 characters.");
        if (errors.Any) return errors.ToResult<IReadOnlyList<StockLevelDto>>();

        var source = await ledger.FindLevelAsync(from!.Id, item!.Id, ct);
        var available = source is null ? 0 : StockRules.Available(source.OnHand, source.Reserved);
        if (source is null || available < r.Quantity!.Value) return Result<IReadOnlyList<StockLevelDto>>.Invalid("quantity", $"Only {available} available in {from.Name} (the rest is held for work orders).");

        var target = await ledger.LevelAsync(to!.Id, item.Id, ct);
        var transferId = Guid.NewGuid();
        var cost = source.AverageCost;
        ledger.Post(source, MovementType.TransferOut, -r.Quantity.Value, cost, m => { m.TransferId = transferId; m.Reason = Common.Clean(r.Note); });
        ledger.Post(target, MovementType.TransferIn, r.Quantity.Value, cost, m => { m.TransferId = transferId; m.Reason = Common.Clean(r.Note); });
        audit.Record("inventory.transferred", nameof(Item), item.Id.ToString(), JsonSerializer.Serialize(new { from = from.Code, to = to.Code, ItemCode = item.Code, r.Quantity, transferId }));
        await db.SaveChangesAsync(ct);
        return Result<IReadOnlyList<StockLevelDto>>.Ok([await DtoAsync(source, from, item, ct), await DtoAsync(target, to, item, ct)]);
    });

    public Task<Result<StockLevelDto>> SetLevelAsync(SetLevelRequest r, CancellationToken ct = default) => ledger.RunAsync(async () =>
    {
        var errors = new ValidationErrors();
        var (store, item) = await CheckStoreAndItemAsync(r.StoreId, r.ItemId, errors, ct);
        if (r.MinQty is { } min && (min < 0 || min > StockRules.MaxQuantity || decimal.Round(min, StockRules.QuantityDecimals) != min)) errors.Add("minQty", "Minimum must be zero or more (up to 4 decimals).");
        if (r.MaxQty is { } max && (max < 0 || max > StockRules.MaxQuantity || decimal.Round(max, StockRules.QuantityDecimals) != max)) errors.Add("maxQty", "Maximum must be zero or more (up to 4 decimals).");
        if (r.MinQty is { } a && r.MaxQty is { } b && b < a) errors.Add("maxQty", "Maximum cannot be below the minimum.");
        if (errors.Any) return errors.ToResult<StockLevelDto>();

        var level = await ledger.FindLevelAsync(store!.Id, item!.Id, ct);
        if (level is null) level = await ledger.LevelAsync(store.Id, item.Id, ct);
        else
        {
            if (!Common.TryVersion(r.RowVersion, out var version)) return Result<StockLevelDto>.Invalid("rowVersion", "rowVersion is required.");
            db.Entry(level).Property(x => x.RowVersion).OriginalValue = version;
        }

        level.MinQty = r.MinQty;
        level.MaxQty = r.MaxQty;
        ledger.Refresh(level);
        audit.Record("inventory.reorder_set", nameof(StockLevel), level.Id.ToString(), JsonSerializer.Serialize(new { store.Code, ItemCode = item.Code, r.MinQty, r.MaxQty }));
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException) when (Common.TryVersion(r.RowVersion, out _))
        {
            db.ChangeTracker.Clear();
            return Result<StockLevelDto>.Conflict("This stock record was changed by someone else. Reload and try again.");
        }

        return Result<StockLevelDto>.Ok(await DtoAsync(level, store, item, ct));
    });

    // ---------------- helpers ----------------

    private async Task<(Store? Store, Item? Item)> CheckStoreAndItemAsync(Guid? storeId, Guid? itemId, ValidationErrors errors, CancellationToken ct, string storeField = "storeId")
    {
        Store? store = null;
        Item? item = null;
        if (storeId is not { } sid || (store = await db.Stores.AsNoTracking().FirstOrDefaultAsync(s => s.Id == sid, ct)) is null) errors.Add(storeField, "Store not found.");
        else if (store.IsArchived) errors.Add(storeField, "This store is archived.");
        if (itemId is not { } iid || (item = await db.Items.AsNoTracking().FirstOrDefaultAsync(i => i.Id == iid, ct)) is null) errors.Add("itemId", "Item not found.");
        else if (item.IsArchived) errors.Add("itemId", "This item is archived.");
        return (store, item);
    }

    private Task<StockLevelDto> DtoAsync(StockLevel level, Store store, Item item, CancellationToken ct) =>
        Task.FromResult(ToDto(level, store, item));

    internal async Task<IReadOnlyList<StockLevelDto>> ToDtosAsync(IReadOnlyList<StockLevel> levels, CancellationToken ct)
    {
        var storeIds = levels.Select(l => l.StoreId).Distinct().ToList();
        var itemIds = levels.Select(l => l.ItemId).Distinct().ToList();
        var stores = await db.Stores.AsNoTracking().Where(s => storeIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, ct);
        var items = await db.Items.AsNoTracking().Where(i => itemIds.Contains(i.Id)).ToDictionaryAsync(i => i.Id, ct);
        return levels.Where(l => stores.ContainsKey(l.StoreId) && items.ContainsKey(l.ItemId)).Select(l => ToDto(l, stores[l.StoreId], items[l.ItemId])).ToList();
    }

    internal static StockLevelDto ToDto(StockLevel l, Store s, Item i) => new(
        s.Id, s.Code, s.Name, i.Id, i.Code, i.Name, i.Unit, l.OnHand, l.Reserved, StockRules.Available(l.OnHand, l.Reserved), l.AverageCost, Math.Round(l.OnHand * l.AverageCost, 2),
        l.MinQty, l.MaxQty, StockRules.IsLow(l.OnHand, l.Reserved, l.MinQty), StockRules.SuggestedOrder(l.OnHand, l.Reserved, l.MinQty, l.MaxQty), Common.Version(l.RowVersion));
}
