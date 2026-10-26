using Fms.BuildingBlocks.Integration;
using Fms.BuildingBlocks.Messaging;
using Fms.Vendors.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Fms.Vendors.Services;

/// <summary>The accounting system accepted a vendor: keep its account reference on the vendor (shown, never typed by hand). Safe to repeat.</summary>
public sealed class VendorSyncedHandler(VendorsDbContext db) : IOutboxHandler
{
    public string MessageType => ErpMessages.VendorSynced;

    public async Task HandleAsync(OutboxMessageContext message, CancellationToken ct)
    {
        var m = message.Payload<ErpReferenceMessage>();
        var vendor = await db.Vendors.FirstOrDefaultAsync(v => v.Id == m.EntityId, ct);
        if (vendor is null || vendor.ErpAccount == m.ErpReference) return;
        vendor.ErpAccount = m.ErpReference.Length > 50 ? m.ErpReference[..50] : m.ErpReference;
        await db.SaveChangesAsync(ct);
    }
}
