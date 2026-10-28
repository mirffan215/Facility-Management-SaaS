using Fms.BuildingBlocks.Reporting;
using Fms.BuildingBlocks.Security;
using Fms.Energy.Domain;
using Fms.Energy.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Fms.Energy.Services;

/// <summary>Use and cost by kind of energy over the period, and what is wrong now (raised alarms, meters that have not reported).</summary>
public sealed class EnergyReportProvider(EnergyDbContext db, ConsumptionService consumption) : IReportProvider
{
    public string Group => "energy";
    public string Permission => Permissions.EnergyRead;

    public async Task<ReportSection> BuildAsync(ReportFilter f, CancellationToken ct = default)
    {
        var meters = db.Meters.AsNoTracking().Where(m => !m.IsArchived);
        var alarms = db.AlarmRules.AsNoTracking().Where(r => r.IsRaised);
        if (f.LocationIds is { } places)
        {
            meters = meters.Where(m => m.LocationId != null && places.Contains(m.LocationId.Value));
            var ids = await meters.Select(m => m.Id).ToListAsync(ct);
            alarms = alarms.Where(r => ids.Contains(r.MeterId));
        }

        var use = await consumption.ComputeAsync(f.From, f.To, null, null, null, ct);
        var rows = use.IsOk ? use.Value!.Meters.Where(m => f.LocationIds is null || meters.Any(x => x.Id == m.MeterId)).ToList() : [];
        var resets = rows.Sum(r => r.Resets);
        var costs = rows.Where(r => r.Cost != null).ToList();
        var currencies = costs.Select(c => c.Currency).Distinct().ToList();
        var byKind = rows.GroupBy(r => r.Kind).OrderBy(g => g.Key).Select(g => new SeriesPoint(g.Key.ToString(), g.Sum(x => x.Total))).ToList();
        return new ReportSection(
        [
            new("energy.meters", Group, await meters.CountAsync(ct), KpiUnit.Count),
            new("energy.activeAlarms", Group, await alarms.CountAsync(ct), KpiUnit.Count),
            new("energy.cost", Group, costs.Count == 0 || currencies.Count != 1 ? null : costs.Sum(c => c.Cost!.Value), KpiUnit.Money, currencies.Count == 1 ? currencies[0] : null),
            new("energy.resets", Group, resets, KpiUnit.Count, "readings lower than the one before, left out of the use"),
        ],
        [
            new("energy.useByKind", Group, KpiUnit.Count, byKind),
        ]);
    }
}
