using Fms.BuildingBlocks.Reporting;
using Fms.BuildingBlocks.Security;
using Fms.Inventory.Domain;
using Fms.Inventory.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Fms.Inventory.Services;

/// <summary>Stock value, low stock and what was used. Stock is kept by store, not by site, so the site filter does not apply.</summary>
public sealed class InventoryReportProvider(InventoryDbContext db) : IReportProvider
{
    public string Group => "inventory";
    public string Permission => Permissions.InventoryRead;

    public async Task<ReportSection> BuildAsync(ReportFilter f, CancellationToken ct = default)
    {
        var levels = await db.Levels.AsNoTracking().Select(l => new { l.OnHand, l.Reserved, l.AverageCost, l.MinQty }).ToListAsync(ct);
        var value = levels.Sum(l => l.OnHand * l.AverageCost);
        var low = levels.Count(l => l.MinQty != null && l.OnHand - l.Reserved <= l.MinQty);

        var moves = await db.Movements.AsNoTracking().Where(m => m.At >= f.FromAt && m.At < f.ToAt).Select(m => new { m.Type, m.At, m.Quantity, m.UnitCost }).ToListAsync(ct);
        var issued = moves.Where(m => m.Type == MovementType.Issue).Sum(m => Math.Abs(m.Quantity) * m.UnitCost);
        var returned = moves.Where(m => m.Type == MovementType.Return).Sum(m => Math.Abs(m.Quantity) * m.UnitCost);
        var received = moves.Where(m => m.Type == MovementType.Receipt).Sum(m => Math.Abs(m.Quantity) * m.UnitCost);

        return new ReportSection(
        [
            new("inventory.value", Group, Math.Round(value, 2), KpiUnit.Money),
            new("inventory.low", Group, low, KpiUnit.Count),
            new("inventory.partsUsed", Group, Math.Round(issued - returned, 2), KpiUnit.Money, "issued minus returned"),
            new("inventory.received", Group, Math.Round(received, 2), KpiUnit.Money),
        ],
        [
            new("inventory.usedByWeek", Group, KpiUnit.Money, ReportMath.WeekLabels(f.From, f.To).Select(l =>
            {
                var start = DateOnly.ParseExact(l, "yyyy-MM-dd");
                var sum = moves.Where(m => m.Type is MovementType.Issue or MovementType.Return && ReportMath.WeekLabel(DateOnly.FromDateTime(m.At.UtcDateTime)) == l)
                    .Sum(m => (m.Type == MovementType.Issue ? 1 : -1) * Math.Abs(m.Quantity) * m.UnitCost);
                return new SeriesPoint(start.ToString("yyyy-MM-dd"), Math.Round(sum, 2));
            }).ToList()),
        ]);
    }
}
