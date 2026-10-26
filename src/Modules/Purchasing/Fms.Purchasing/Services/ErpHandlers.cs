using Fms.BuildingBlocks.Integration;
using Fms.BuildingBlocks.Messaging;
using Fms.Purchasing.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Fms.Purchasing.Services;

/// <summary>The accounting system accepted an order: keep its reference on the order (shown, never typed by hand). Safe to repeat.</summary>
public sealed class OrderSyncedHandler(PurchasingDbContext db) : IOutboxHandler
{
    public string MessageType => ErpMessages.OrderSynced;

    public async Task HandleAsync(OutboxMessageContext message, CancellationToken ct)
    {
        var m = message.Payload<ErpReferenceMessage>();
        var order = await db.Orders.FirstOrDefaultAsync(o => o.Id == m.EntityId, ct);
        if (order is null || order.ErpReference == m.ErpReference) return;
        order.ErpReference = m.ErpReference.Length > 50 ? m.ErpReference[..50] : m.ErpReference;
        await db.SaveChangesAsync(ct);
    }
}
