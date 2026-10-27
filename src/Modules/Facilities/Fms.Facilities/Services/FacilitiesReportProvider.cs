using Fms.BuildingBlocks.Reporting;
using Fms.BuildingBlocks.Security;
using Fms.Facilities.Domain;
using Fms.Facilities.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Fms.Facilities.Services;

/// <summary>The estate itself: assets by status and the floor area the cost per square metre is measured against.</summary>
public sealed class FacilitiesReportProvider(FacilitiesDbContext db) : IReportProvider
{
    public string Group => "facilities";
    public string Permission => Permissions.AssetsRead;

    public async Task<ReportSection> BuildAsync(ReportFilter f, CancellationToken ct = default)
    {
        var assets = db.Assets.AsNoTracking().AsQueryable();
        if (f.LocationIds is { } places) assets = assets.Where(a => places.Contains(a.LocationId));
        var byStatus = await assets.GroupBy(a => a.Status).Select(g => new { g.Key, N = g.Count() }).ToListAsync(ct);

        // Area: a building's own figure; a site without building figures counts its own. Each square metre is counted once.
        var locations = db.Locations.AsNoTracking().Where(l => !l.IsArchived && l.AreaSquareMetres != null && (l.Type == LocationType.Site || l.Type == LocationType.Building));
        if (f.LocationIds is { } within) locations = locations.Where(l => within.Contains(l.Id));
        var withArea = await locations.Select(l => new { l.Id, l.Type, l.Path, Area = l.AreaSquareMetres!.Value }).ToListAsync(ct);
        var buildings = withArea.Where(l => l.Type == LocationType.Building).ToList();
        decimal area = buildings.Sum(b => b.Area);
        foreach (var s in withArea.Where(l => l.Type == LocationType.Site))
            if (!buildings.Any(b => b.Path.StartsWith(s.Path))) area += s.Area;

        var total = byStatus.Sum(x => x.N);
        return new ReportSection(
        [
            new("facilities.assets", Group, total, KpiUnit.Count),
            new("facilities.outOfService", Group, byStatus.Where(x => x.Key == AssetStatus.OutOfService).Sum(x => x.N), KpiUnit.Count),
            new("facilities.area", Group, Math.Round(area, 0), KpiUnit.Area, "square metres"),
        ],
        [
            new("facilities.assetsByStatus", Group, KpiUnit.Count, byStatus.OrderBy(x => x.Key).Select(x => new SeriesPoint(x.Key.ToString(), x.N)).ToList()),
        ]);
    }
}
