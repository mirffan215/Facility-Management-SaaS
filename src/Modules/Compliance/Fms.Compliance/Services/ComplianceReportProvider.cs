using Fms.BuildingBlocks.Reporting;
using Fms.BuildingBlocks.Security;
using Fms.Compliance.Domain;
using Fms.Compliance.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Fms.Compliance.Services;

/// <summary>Where the obligations stand today and how permits were used in the period. Filtered by place when a site is chosen.</summary>
public sealed class ComplianceReportProvider(ComplianceDbContext db, TimeProvider clock) : IReportProvider
{
    public string Group => "compliance";
    public string Permission => Permissions.ComplianceRead;

    public async Task<ReportSection> BuildAsync(ReportFilter f, CancellationToken ct = default)
    {
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        var obligations = db.Obligations.AsNoTracking().Where(o => !o.IsArchived);
        var permits = db.Permits.AsNoTracking().AsQueryable();
        if (f.LocationIds is { } places)
        {
            obligations = obligations.Where(o => o.LocationId != null && places.Contains(o.LocationId.Value));
            permits = permits.Where(p => places.Contains(p.LocationId));
        }

        var statuses = (await obligations.Take(2000).ToListAsync(ct)).Select(o => ComplianceRules.StatusOf(o, today)).ToList();
        int N(ComplianceStatus s) => statuses.Count(x => x == s);
        var measurable = statuses.Count - N(ComplianceStatus.Scheduled);
        var inPeriod = await permits.Where(p => p.RequestedAt >= f.FromAt && p.RequestedAt < f.ToAt).Select(p => new { p.Status, p.RequestedAt }).ToListAsync(ct);
        var now = clock.GetUtcNow();
        var overdue = await permits.CountAsync(p => (p.Status == PermitStatus.Issued || p.Status == PermitStatus.Started) && p.PlannedEnd < now, ct);
        var inForce = await permits.CountAsync(p => p.Status == PermitStatus.Issued || p.Status == PermitStatus.Started, ct);
        return new ReportSection(
        [
            new("compliance.percentCompliant", Group, ReportMath.Percent(N(ComplianceStatus.Compliant) + N(ComplianceStatus.DueSoon), measurable), KpiUnit.Percent, "obligations with a valid certificate"),
            new("compliance.overdue", Group, N(ComplianceStatus.Overdue), KpiUnit.Count),
            new("compliance.failed", Group, N(ComplianceStatus.Failed), KpiUnit.Count),
            new("compliance.dueSoon", Group, N(ComplianceStatus.DueSoon), KpiUnit.Count),
            new("compliance.permitsInForce", Group, inForce, KpiUnit.Count),
            new("compliance.permitsOverdue", Group, overdue, KpiUnit.Count, "issued or started and past the planned end"),
            new("compliance.permitsRequested", Group, inPeriod.Count, KpiUnit.Count),
        ],
        [
            new("compliance.permitsByWeek", Group, KpiUnit.Count, ReportMath.Weekly(inPeriod.Select(p => DateOnly.FromDateTime(p.RequestedAt.UtcDateTime)), f.From, f.To)),
            new("compliance.byStatus", Group, KpiUnit.Count, statuses.GroupBy(s => s).OrderBy(g => g.Key).Select(g => new SeriesPoint(g.Key.ToString(), g.Count())).ToList()),
        ]);
    }
}
