using Fms.BuildingBlocks.Reporting;
using Fms.BuildingBlocks.Security;
using Fms.Visitors.Domain;
using Fms.Visitors.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Fms.Visitors.Services;

/// <summary>Visits in the period and who is on site now. Filtered by the site visited. Visitors' names never appear in figures.</summary>
public sealed class VisitorsReportProvider(VisitorsDbContext db) : IReportProvider
{
    public string Group => "visitors";
    public string Permission => Permissions.VisitorsReception;

    public async Task<ReportSection> BuildAsync(ReportFilter f, CancellationToken ct = default)
    {
        var visits = db.Visits.AsNoTracking().AsQueryable();
        if (f.SiteId is { } site) visits = visits.Where(v => v.SiteId == site);
        var rows = await visits.Where(v => v.ExpectedAt >= f.FromAt && v.ExpectedAt < f.ToAt).Select(v => new { v.Status, v.Kind, v.ExpectedAt, v.CheckedInAt, v.CheckedOutAt, v.WatchlistMatch }).ToListAsync(ct);
        var onSite = await visits.CountAsync(v => v.Status == VisitStatus.CheckedIn, ct);
        var arrived = rows.Where(v => v.CheckedInAt != null).ToList();
        var stays = arrived.Where(v => v.CheckedOutAt != null).Select(v => (decimal)(v.CheckedOutAt!.Value - v.CheckedInAt!.Value).TotalMinutes).ToList();
        var noShow = rows.Count(v => v.Status == VisitStatus.NoShow);
        return new ReportSection(
        [
            new("visitors.visits", Group, arrived.Count, KpiUnit.Count, "checked in"),
            new("visitors.onSite", Group, onSite, KpiUnit.Count),
            new("visitors.noShowRate", Group, ReportMath.Percent(noShow, arrived.Count + noShow), KpiUnit.Percent),
            new("visitors.averageStay", Group, ReportMath.Average(stays, 0), KpiUnit.Minutes),
            new("visitors.overrides", Group, arrived.Count(v => v.WatchlistMatch), KpiUnit.Count, "watchlist matches let in by security"),
        ],
        [
            new("visitors.byWeek", Group, KpiUnit.Count, ReportMath.Weekly(arrived.Select(v => DateOnly.FromDateTime(v.CheckedInAt!.Value.UtcDateTime)), f.From, f.To)),
            new("visitors.byKind", Group, KpiUnit.Count, arrived.GroupBy(v => v.Kind).OrderBy(g => g.Key).Select(g => new SeriesPoint(g.Key.ToString(), g.Count())).ToList()),
        ]);
    }
}
