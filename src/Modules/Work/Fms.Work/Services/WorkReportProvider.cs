using Fms.BuildingBlocks.Reporting;
using Fms.BuildingBlocks.Security;
using Fms.Work.Domain;
using Fms.Work.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Fms.Work.Services;

/// <summary>Work order, SLA, preventive maintenance and cost figures. Filtered by the chosen site (through the work order's place) when one is chosen.</summary>
public sealed class WorkReportProvider(WorkDbContext db, TimeProvider clock) : IReportProvider
{
    public string Group => "work";
    public string Permission => Permissions.WorkOrdersRead;

    public async Task<ReportSection> BuildAsync(ReportFilter f, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var orders = db.WorkOrders.AsNoTracking().AsQueryable();
        if (f.LocationIds is { } places) orders = orders.Where(w => places.Contains(w.LocationId));

        var created = await orders.Where(w => w.RequestedAt >= f.FromAt && w.RequestedAt < f.ToAt).Select(w => new { w.RequestedAt, w.Priority, w.Type }).ToListAsync(ct);
        var completed = await orders.Where(w => w.CompletedAt != null && w.CompletedAt >= f.FromAt && w.CompletedAt < f.ToAt).Select(w => new { w.RequestedAt, w.CompletedAt, w.Type }).ToListAsync(ct);
        var open = await orders.Where(w => w.Status != WorkOrderStatus.Completed && w.Status != WorkOrderStatus.Verified && w.Status != WorkOrderStatus.Closed && w.Status != WorkOrderStatus.Cancelled).Select(w => new { w.Status, w.Priority, w.DueAt }).ToListAsync(ct);
        var overdue = open.Count(w => w.DueAt < now); // "open" already excludes work that is done

        var repairs = completed.Where(c => c.Type != WorkOrderType.Planned).Select(c => (decimal)(c.CompletedAt!.Value - c.RequestedAt).TotalHours).ToList();

        // Service levels: only orders whose due moment fell in the period and which are decided (met, or missed because it is past due).
        var response = await orders.Where(w => w.ResponseDueAt != null && w.ResponseDueAt >= f.FromAt && w.ResponseDueAt < f.ToAt).Select(w => new { w.ResponseDueAt, w.RespondedAt }).ToListAsync(ct);
        var responseDecided = response.Where(w => w.RespondedAt != null || w.ResponseDueAt < now).ToList();
        var resolution = await orders.Where(w => w.ResolutionDueAt != null && w.ResolutionDueAt >= f.FromAt && w.ResolutionDueAt < f.ToAt).Select(w => new { w.ResolutionDueAt, w.CompletedAt }).ToListAsync(ct);
        var resolutionDecided = resolution.Where(w => w.CompletedAt != null || w.ResolutionDueAt < now).ToList();

        // Cost recorded in the period on the orders in scope (stock parts are in the inventory group; labour is time x the rate frozen on the entry).
        var scope = orders.Select(w => w.Id);
        var voidedCosts = db.Voids.Where(v => v.EntryType == LedgerEntryType.Cost).Select(v => v.EntryId);
        var voidedTime = db.Voids.Where(v => v.EntryType == LedgerEntryType.Time).Select(v => v.EntryId);
        var costRows = await db.CostLines.AsNoTracking().Where(c => scope.Contains(c.WorkOrderId) && c.RecordedAt >= f.FromAt && c.RecordedAt < f.ToAt && !voidedCosts.Contains(c.Id))
            .Select(c => new { c.Kind, Amount = c.Quantity * c.UnitCost }).ToListAsync(ct);
        var labour = await db.TimeEntries.AsNoTracking().Where(t => scope.Contains(t.WorkOrderId) && t.WorkedOn >= f.From && t.WorkedOn <= f.To && !voidedTime.Contains(t.Id))
            .Select(t => t.Minutes * t.HourlyRate / 60m).ToListAsync(ct);
        var material = costRows.Where(c => c.Kind == CostKind.Material).Sum(c => c.Amount);
        var contractor = costRows.Where(c => c.Kind == CostKind.Contractor).Sum(c => c.Amount);
        var other = costRows.Where(c => c.Kind == CostKind.Other).Sum(c => c.Amount);
        var labourTotal = labour.Sum();

        // Preventive maintenance: how much planned work was done on time (same definition as the compliance screen).
        var pm = await db.PmOccurrences.AsNoTracking().Where(o => o.DueOn >= f.From && o.DueOn <= f.To).Select(o => new { o.Outcome, o.OnTime, o.DueOn }).ToListAsync(ct);
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var pmDue = pm.Count(o => o.Outcome != PmOutcome.Open || o.DueOn < today);
        var pmOnTime = pm.Count(o => o.Outcome == PmOutcome.Completed && o.OnTime == true);

        var kpis = new List<Kpi>
        {
            new("work.created", Group, created.Count, KpiUnit.Count),
            new("work.completed", Group, completed.Count, KpiUnit.Count),
            new("work.open", Group, open.Count, KpiUnit.Count),
            new("work.overdue", Group, overdue, KpiUnit.Count),
            new("work.mttr", Group, ReportMath.Average(repairs), KpiUnit.Hours, "request to completion, repairs and emergencies"),
            new("work.slaResponse", Group, ReportMath.Percent(responseDecided.Count(w => w.RespondedAt != null && w.RespondedAt <= w.ResponseDueAt), responseDecided.Count), KpiUnit.Percent, $"{responseDecided.Count}"),
            new("work.slaResolution", Group, ReportMath.Percent(resolutionDecided.Count(w => w.CompletedAt != null && w.CompletedAt <= w.ResolutionDueAt), resolutionDecided.Count), KpiUnit.Percent, $"{resolutionDecided.Count}"),
            new("work.pmCompliance", Group, ReportMath.Percent(pmOnTime, pmDue), KpiUnit.Percent, $"{pmDue}"),
            new("work.cost", Group, Math.Round(material + contractor + other + labourTotal, 2), KpiUnit.Money),
        };
        var series = new List<Series>
        {
            new("work.createdByWeek", Group, KpiUnit.Count, ReportMath.Weekly(created.Select(c => DateOnly.FromDateTime(c.RequestedAt.UtcDateTime)), f.From, f.To)),
            new("work.completedByWeek", Group, KpiUnit.Count, ReportMath.Weekly(completed.Select(c => DateOnly.FromDateTime(c.CompletedAt!.Value.UtcDateTime)), f.From, f.To)),
            new("work.openByStatus", Group, KpiUnit.Count, open.GroupBy(o => o.Status).OrderBy(g => g.Key).Select(g => new SeriesPoint(g.Key.ToString(), g.Count())).ToList()),
            new("work.openByPriority", Group, KpiUnit.Count, open.GroupBy(o => o.Priority).OrderByDescending(g => g.Key).Select(g => new SeriesPoint(g.Key.ToString(), g.Count())).ToList()),
            new("work.costByKind", Group, KpiUnit.Money, [new("Labour", Math.Round(labourTotal, 2)), new("Material", Math.Round(material, 2)), new("Contractor", Math.Round(contractor, 2)), new("Other", Math.Round(other, 2))]),
        };
        return new ReportSection(kpis, series);
    }
}
