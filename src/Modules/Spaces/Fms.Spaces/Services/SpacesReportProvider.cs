using Fms.BuildingBlocks.Reporting;
using Fms.BuildingBlocks.Security;
using Fms.Spaces.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Fms.Spaces.Services;

/// <summary>How full the spaces are on the last day of the period, by kind. Filtered by site through the spaces under it.</summary>
public sealed class SpacesReportProvider(SpacesDbContext db) : IReportProvider
{
    public string Group => "spaces";
    public string Permission => Permissions.SpacesRead;

    public async Task<ReportSection> BuildAsync(ReportFilter f, CancellationToken ct = default)
    {
        var profiles = db.Profiles.AsNoTracking().Where(p => p.Capacity > 0);
        if (f.LocationIds is { } places) profiles = profiles.Where(p => places.Contains(p.LocationId));
        var spaces = await profiles.Select(p => new { p.LocationId, p.Kind, p.Capacity }).ToListAsync(ct);
        var day = f.To;
        var ids = spaces.Select(s => s.LocationId).ToList();
        var held = await db.Allocations.AsNoTracking().Where(a => ids.Contains(a.SpaceId) && a.CancelledAt == null && a.StartOn <= day && (a.EndOn == null || a.EndOn >= day))
            .GroupBy(a => a.SpaceId).Select(g => new { g.Key, N = g.Sum(a => a.Headcount) }).ToDictionaryAsync(x => x.Key, x => x.N, ct);

        var capacity = spaces.Sum(s => s.Capacity);
        var allocated = spaces.Sum(s => held.GetValueOrDefault(s.LocationId));
        var full = spaces.Count(s => held.GetValueOrDefault(s.LocationId) >= s.Capacity);
        var empty = spaces.Count(s => held.GetValueOrDefault(s.LocationId) == 0);
        return new ReportSection(
        [
            new("spaces.capacity", Group, capacity, KpiUnit.Count),
            new("spaces.allocated", Group, allocated, KpiUnit.Count),
            new("spaces.utilisation", Group, ReportMath.Percent(allocated, capacity), KpiUnit.Percent, $"on {day:yyyy-MM-dd}"),
            new("spaces.full", Group, full, KpiUnit.Count),
            new("spaces.empty", Group, empty, KpiUnit.Count),
        ],
        [
            new("spaces.utilisationByKind", Group, KpiUnit.Percent, spaces.GroupBy(s => s.Kind).OrderBy(g => g.Key)
                .Select(g => new SeriesPoint(g.Key.ToString(), ReportMath.Percent(g.Sum(s => held.GetValueOrDefault(s.LocationId)), g.Sum(s => s.Capacity)) ?? 0)).ToList()),
        ]);
    }
}
