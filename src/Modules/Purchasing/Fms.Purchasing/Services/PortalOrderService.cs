using System.Text.Json;
using Fms.BuildingBlocks.Identity;
using Fms.BuildingBlocks.Results;
using Fms.BuildingBlocks.Security;
using Fms.Purchasing.Domain;
using Fms.Purchasing.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Fms.Purchasing.Services;

public sealed record PortalOrderLineDto(int Position, string Description, string Unit, decimal Quantity, decimal UnitPrice, decimal Total, decimal ReceivedQty);

/// <summary>An order as its vendor sees it: what was asked for and on what terms. Nothing internal (who requested it, the approval, the stock store, the accounting reference).</summary>
public sealed record PortalOrderDto(
    Guid Id, string Display, OrderStatus Status, DateTimeOffset IssuedAt, DateOnly? ExpectedOn, string CurrencyCode, decimal Total, string? Notes, DateTimeOffset? AcknowledgedAt,
    string? CancelReason, IReadOnlyList<PortalOrderLineDto>? Lines);

/// <summary>The vendor portal's view of purchase orders. Every query is filtered by the signed-in person's vendor; an order of another vendor looks exactly like one that does not exist.</summary>
public sealed class PortalOrderService(PurchasingDbContext db, IVendorScope scope, IAuditPort audit, ICurrentUser user, TimeProvider clock)
{
    public async Task<Result<IReadOnlyList<PortalOrderDto>>> ListAsync(CancellationToken ct = default)
    {
        if (await scope.CurrentVendorIdAsync(ct) is not { } vendorId) return Result<IReadOnlyList<PortalOrderDto>>.Forbidden("You do not have access to the vendor portal.");
        var rows = await db.Orders.AsNoTracking().Where(o => o.VendorId == vendorId).OrderByDescending(o => o.Number).Take(100).ToListAsync(ct);
        return Result<IReadOnlyList<PortalOrderDto>>.Ok(rows.Select(o => ToDto(o, null)).ToList());
    }

    public async Task<Result<PortalOrderDto>> GetAsync(Guid id, CancellationToken ct = default)
    {
        if (await scope.CurrentVendorIdAsync(ct) is not { } vendorId) return Result<PortalOrderDto>.Forbidden("You do not have access to the vendor portal.");
        var o = await db.Orders.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && x.VendorId == vendorId, ct);
        if (o is null) return Result<PortalOrderDto>.NotFound();
        var lines = await db.OrderLines.AsNoTracking().Where(l => l.OrderId == id).OrderBy(l => l.Position).ToListAsync(ct);
        return Result<PortalOrderDto>.Ok(ToDto(o, lines));
    }

    /// <summary>The vendor confirms it has the order. Only its own, and only while it is newly issued.</summary>
    public async Task<Result<PortalOrderDto>> AcknowledgeAsync(Guid id, CancellationToken ct = default)
    {
        if (await scope.CurrentVendorIdAsync(ct) is not { } vendorId) return Result<PortalOrderDto>.Forbidden("You do not have access to the vendor portal.");
        var o = await db.Orders.FirstOrDefaultAsync(x => x.Id == id && x.VendorId == vendorId, ct);
        if (o is null) return Result<PortalOrderDto>.NotFound();
        if (o.Status != OrderStatus.Issued) return Result<PortalOrderDto>.Conflict("This order cannot be acknowledged now.");
        o.Status = OrderStatus.Acknowledged;
        o.AcknowledgedAt = clock.GetUtcNow();
        o.AcknowledgedBy = user.UserId;
        audit.Record("purchaseorder.acknowledged", nameof(PurchaseOrder), id.ToString(), JsonSerializer.Serialize(new { byVendor = true }));
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            return Result<PortalOrderDto>.Conflict("This order changed a moment ago. Reload to see where it stands.");
        }

        return await GetAsync(id, ct);
    }

    private static PortalOrderDto ToDto(PurchaseOrder o, List<PurchaseOrderLine>? lines) => new(
        o.Id, PurchasingRules.DisplayOrder(o.Number), o.Status, o.IssuedAt, o.ExpectedOn, o.CurrencyCode, o.Total, o.Notes, o.AcknowledgedAt, o.CancelReason,
        lines?.Select(l => new PortalOrderLineDto(l.Position, l.Description, l.Unit, l.Quantity, l.UnitPrice, PurchasingRules.LineTotal(l.Quantity, l.UnitPrice), l.ReceivedQty)).ToList());
}
