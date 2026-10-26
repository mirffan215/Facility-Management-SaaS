using Fms.BuildingBlocks.Identity;
using Fms.BuildingBlocks.Messaging;
using Fms.BuildingBlocks.Notifications;
using Fms.BuildingBlocks.Results;
using Fms.BuildingBlocks.Security;
using Fms.Inventory.Domain;
using Fms.Inventory.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Fms.Inventory.Services;

/// <summary>A work order can take no more parts: let go of whatever it was holding.</summary>
public sealed class WorkOrderEndedHandler(WorkPartsService parts) : IOutboxHandler
{
    public string MessageType => WorkOrderEnded.MessageType;

    public async Task HandleAsync(OutboxMessageContext message, CancellationToken ct)
    {
        var m = message.Payload<WorkOrderEndedMessage>();
        var result = await parts.ReleaseAllAsync(m.WorkOrderId, ct);
        if (!result.IsOk) throw new InvalidOperationException(result.Message ?? "Could not release reservations."); // retried later
    }
}

/// <summary>Tells the people who look after stock that an item has run down to its minimum.</summary>
public sealed class LowStockHandler(InventoryDbContext db, IUserDirectory users, INotificationSink sink) : IOutboxHandler
{
    public string MessageType => InventoryMessages.LowStock;

    public async Task HandleAsync(OutboxMessageContext message, CancellationToken ct)
    {
        var m = message.Payload<LowStockMessage>();
        var level = await db.Levels.AsNoTracking().FirstOrDefaultAsync(l => l.StoreId == m.StoreId && l.ItemId == m.ItemId, ct);
        if (level is null || !StockRules.IsLow(level.OnHand, level.Reserved, level.MinQty)) return; // already recovered
        var store = await db.Stores.AsNoTracking().FirstOrDefaultAsync(s => s.Id == m.StoreId, ct);
        var item = await db.Items.AsNoTracking().FirstOrDefaultAsync(i => i.Id == m.ItemId, ct);
        if (store is null || item is null) return;

        var available = StockRules.Available(level.OnHand, level.Reserved);
        var who = await users.WithPermissionAsync(Permissions.InventoryManage, ct);
        var subject = $"Low stock: {item.Code} {item.Name} ({store.Name})";
        var body = $"{item.Code} {item.Name} in {store.Name}: {available} {item.Unit} available, minimum {level.MinQty}.\n\n"
                   + $"المخزون منخفض: {item.Code} {item.NameAr ?? item.Name} في {store.NameAr ?? store.Name}: المتاح {available} {item.Unit}، الحد الأدنى {level.MinQty}.";
        await sink.NotifyAsync(message.MessageId, who.Select(u => new NotificationRequest(
            u.Id, "inventory.low", new { number = item.Code, title = $"{item.Name} · {store.Name}", available, min = level.MinQty }, "/inventory?tab=stock&low=1", subject, body)).ToList(), ct);
    }
}

/// <summary>What Purchasing may ask about items and stores: whether the ones named on an order line are real and still in use.</summary>
public sealed class InventoryCatalog(InventoryDbContext db) : IInventoryCatalog
{
    public async Task<ItemRef?> GetItemAsync(Guid itemId, CancellationToken ct = default)
    {
        var i = await db.Items.AsNoTracking().FirstOrDefaultAsync(x => x.Id == itemId, ct);
        return i is null ? null : new ItemRef(i.Id, i.Code, i.Name, i.Unit, !i.IsArchived);
    }

    public async Task<StoreRef?> GetStoreAsync(Guid storeId, CancellationToken ct = default)
    {
        var s = await db.Stores.AsNoTracking().FirstOrDefaultAsync(x => x.Id == storeId, ct);
        return s is null ? null : new StoreRef(s.Id, s.Code, s.Name, !s.IsArchived);
    }

    public async Task<IReadOnlyDictionary<Guid, ItemRef>> ItemsAsync(IEnumerable<Guid> itemIds, CancellationToken ct = default)
    {
        var ids = itemIds.Distinct().ToList();
        if (ids.Count == 0) return new Dictionary<Guid, ItemRef>();
        return (await db.Items.AsNoTracking().Where(i => ids.Contains(i.Id)).ToListAsync(ct)).ToDictionary(i => i.Id, i => new ItemRef(i.Id, i.Code, i.Name, i.Unit, !i.IsArchived));
    }

    public async Task<IReadOnlyDictionary<Guid, StoreRef>> StoresAsync(IEnumerable<Guid> storeIds, CancellationToken ct = default)
    {
        var ids = storeIds.Distinct().ToList();
        if (ids.Count == 0) return new Dictionary<Guid, StoreRef>();
        return (await db.Stores.AsNoTracking().Where(s => ids.Contains(s.Id)).ToListAsync(ct)).ToDictionary(s => s.Id, s => new StoreRef(s.Id, s.Code, s.Name, !s.IsArchived));
    }
}

/// <summary>Puts goods that arrived against a purchase order into stock, once per receipt (the receipt id is the key), at the price paid.</summary>
public sealed class GoodsReceivedHandler(InventoryDbContext db, StockLedger ledger, IAuditPort audit) : IOutboxHandler
{
    public string MessageType => GoodsReceived.MessageType;

    public async Task HandleAsync(OutboxMessageContext message, CancellationToken ct)
    {
        var m = message.Payload<GoodsReceivedMessage>();
        var reference = $"GR:{m.ReceiptId}";
        var result = await ledger.RunAsync(async () =>
        {
            if (await db.Movements.AnyAsync(x => x.StoreId == m.StoreId && x.ItemId == m.ItemId && x.Reference == reference, ct)) return Result<bool>.Ok(true); // already put away
            if (!await db.Stores.AnyAsync(s => s.Id == m.StoreId, ct) || !await db.Items.AnyAsync(i => i.Id == m.ItemId, ct)) return Result<bool>.NotFound("The store or item of a received line no longer exists.");
            var level = await ledger.LevelAsync(m.StoreId, m.ItemId, ct);
            ledger.Post(level, MovementType.Receipt, m.Quantity, m.UnitCost, x => { x.Reference = reference; x.Reason = $"Received on {m.OrderNumber}"; });
            audit.Record("inventory.received_on_order", nameof(StockLevel), level.Id.ToString(), System.Text.Json.JsonSerializer.Serialize(new { m.OrderNumber, m.ReceiptId, m.Quantity, m.UnitCost }));
            await db.SaveChangesAsync(ct);
            return Result<bool>.Ok(true);
        });
        if (!result.IsOk) throw new InvalidOperationException(result.Message ?? "Could not put the received goods into stock."); // retried, then set aside for a person to see
    }
}
