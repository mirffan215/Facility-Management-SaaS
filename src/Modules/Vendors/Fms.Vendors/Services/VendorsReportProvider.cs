using Fms.BuildingBlocks.Reporting;
using Fms.BuildingBlocks.Security;
using Fms.Vendors.Domain;
using Fms.Vendors.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Fms.Vendors.Services;

/// <summary>Vendors and contracts as they stand today (not by period, not by site).</summary>
public sealed class VendorsReportProvider(VendorsDbContext db, TimeProvider clock) : IReportProvider
{
    public string Group => "vendors";
    public string Permission => Permissions.VendorsRead;

    public async Task<ReportSection> BuildAsync(ReportFilter f, CancellationToken ct = default)
    {
        var today = Common.Today(clock);
        var vendors = await db.Vendors.AsNoTracking().Where(v => !v.IsArchived).Select(v => v.Status).ToListAsync(ct);
        var contracts = await db.Contracts.AsNoTracking().Where(c => c.TerminatedAt == null).Select(c => new { c.StartOn, c.EndOn, c.RenewalNoticeDays, c.Value }).ToListAsync(ct);
        var inForce = contracts.Count(c => c.StartOn <= today && c.EndOn >= today);
        var expiring = contracts.Count(c => c.StartOn <= today && c.EndOn >= today && c.EndOn.DayNumber - today.DayNumber <= Math.Max(c.RenewalNoticeDays, 0));
        var expired = contracts.Count(c => c.EndOn < today);
        return new ReportSection(
        [
            new("vendors.active", Group, vendors.Count(s => s == VendorStatus.Active), KpiUnit.Count),
            new("vendors.contractsInForce", Group, inForce, KpiUnit.Count),
            new("vendors.contractsExpiring", Group, expiring, KpiUnit.Count, "inside their renewal notice"),
            new("vendors.contractsExpired", Group, expired, KpiUnit.Count, "ended and not terminated"),
            new("vendors.contractValue", Group, Math.Round(contracts.Where(c => c.StartOn <= today && c.EndOn >= today).Sum(c => c.Value ?? 0), 2), KpiUnit.Money, "in force"),
        ],
        [
            new("vendors.byStatus", Group, KpiUnit.Count, vendors.GroupBy(s => s).OrderBy(g => g.Key).Select(g => new SeriesPoint(g.Key.ToString(), g.Count())).ToList()),
        ]);
    }
}
