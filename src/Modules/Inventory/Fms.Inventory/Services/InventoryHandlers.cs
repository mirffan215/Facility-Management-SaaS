using Fms.BuildingBlocks.Identity;
using Fms.BuildingBlocks.Messaging;
using Fms.BuildingBlocks.Notifications;
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
