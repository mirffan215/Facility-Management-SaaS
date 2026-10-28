using Fms.BuildingBlocks.Identity;
using Fms.Purchasing.Domain;
using Fms.Purchasing.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Fms.Purchasing.Services;

/// <summary>What the Projects module may ask about purchase orders: what was ordered and what has arrived, at the order's prices.</summary>
public sealed class PurchaseOrderLookup(PurchasingDbContext db) : IPurchaseOrderLookup
{
    public async Task<IReadOnlyDictionary<Guid, PurchaseOrderInfo>> GetAsync(IEnumerable<Guid> ids, CancellationToken ct = default)
    {
        var list = ids.Distinct().Take(500).ToList();
        if (list.Count == 0) return new Dictionary<Guid, PurchaseOrderInfo>();
        var orders = await db.Orders.AsNoTracking().Where(o => list.Contains(o.Id)).ToListAsync(ct);
        var lines = await db.OrderLines.AsNoTracking().Where(l => list.Contains(l.OrderId)).Select(l => new { l.OrderId, l.Quantity, l.UnitPrice, l.ReceivedQty }).ToListAsync(ct);
        return orders.ToDictionary(o => o.Id, o => new PurchaseOrderInfo(
            o.Id, PurchasingRules.DisplayOrder(o.Number), o.VendorName, o.Status.ToString(), o.Total, Math.Round(lines.Where(l => l.OrderId == o.Id).Sum(l => l.ReceivedQty * l.UnitPrice), 2), o.CurrencyCode, o.Status == OrderStatus.Cancelled));
    }

    public async Task<PurchaseOrderInfo?> FindByNumberAsync(string number, CancellationToken ct = default)
    {
        var digits = new string(number.Where(char.IsAsciiDigit).ToArray());
        if (digits.Length is 0 or > 9 || !int.TryParse(digits, out var n)) return null;
        var id = await db.Orders.AsNoTracking().Where(o => o.Number == n).Select(o => (Guid?)o.Id).FirstOrDefaultAsync(ct);
        return id is null ? null : (await GetAsync([id.Value], ct)).GetValueOrDefault(id.Value);
    }
}
