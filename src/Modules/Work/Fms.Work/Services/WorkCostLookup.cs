using Fms.BuildingBlocks.Identity;
using Fms.Work.Domain;
using Fms.Work.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Fms.Work.Services;

/// <summary>What the Projects module may ask about the cost of work orders. Voided time and costs are left out; parts issued from stock are added when Inventory is present.</summary>
public sealed class WorkCostLookup(WorkDbContext db, IWorkPartsLookup? parts = null) : IWorkCostLookup
{
    public async Task<IReadOnlyDictionary<Guid, WorkCostInfo>> GetAsync(IEnumerable<Guid> ids, CancellationToken ct = default)
    {
        var list = ids.Distinct().Take(500).ToList();
        if (list.Count == 0) return new Dictionary<Guid, WorkCostInfo>();
        var currency = (await db.Settings.AsNoTracking().FirstOrDefaultAsync(ct))?.CurrencyCode ?? "SAR";
        var orders = await db.WorkOrders.AsNoTracking().Where(w => list.Contains(w.Id)).Select(w => new { w.Id, w.Number, w.Title, w.Status }).ToListAsync(ct);
        var voided = db.Voids.AsNoTracking().Where(v => list.Contains(v.WorkOrderId)).Select(v => v.EntryId);
        var time = await db.TimeEntries.AsNoTracking().Where(t => list.Contains(t.WorkOrderId) && !voided.Contains(t.Id)).Select(t => new { t.WorkOrderId, t.Minutes, t.HourlyRate }).ToListAsync(ct);
        var costs = await db.CostLines.AsNoTracking().Where(c => list.Contains(c.WorkOrderId) && !voided.Contains(c.Id)).Select(c => new { c.WorkOrderId, c.Quantity, c.UnitCost }).ToListAsync(ct);

        var result = new Dictionary<Guid, WorkCostInfo>();
        foreach (var w in orders)
        {
            var total = time.Where(t => t.WorkOrderId == w.Id).Sum(t => Math.Round(t.Minutes / 60m * t.HourlyRate, 2)) + costs.Where(c => c.WorkOrderId == w.Id).Sum(c => Math.Round(c.Quantity * c.UnitCost, 2));
            if (parts is not null) total += await parts.MaterialCostAsync(w.Id, ct);
            result[w.Id] = new WorkCostInfo(w.Id, WorkOrderRules.DisplayNumber(w.Number), w.Title, w.Status.ToString(), total, currency);
        }

        return result;
    }

    public async Task<WorkCostInfo?> FindByNumberAsync(string number, CancellationToken ct = default)
    {
        var digits = new string(number.Where(char.IsAsciiDigit).ToArray());
        if (digits.Length is 0 or > 9 || !int.TryParse(digits, out var n)) return null;
        var id = await db.WorkOrders.AsNoTracking().Where(w => w.Number == n).Select(w => (Guid?)w.Id).FirstOrDefaultAsync(ct);
        return id is null ? null : (await GetAsync([id.Value], ct)).GetValueOrDefault(id.Value);
    }
}
