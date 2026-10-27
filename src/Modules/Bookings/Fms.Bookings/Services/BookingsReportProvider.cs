using Fms.BuildingBlocks.Reporting;
using Fms.BuildingBlocks.Security;
using Fms.Bookings.Domain;
using Fms.Bookings.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Fms.Bookings.Services;

/// <summary>Use of bookable resources in the period. Filtered by site through the resource's space when a site is chosen.</summary>
public sealed class BookingsReportProvider(BookingsDbContext db) : IReportProvider
{
    public string Group => "bookings";
    public string Permission => Permissions.BookingsRead;

    public async Task<ReportSection> BuildAsync(ReportFilter f, CancellationToken ct = default)
    {
        var resources = db.Resources.AsNoTracking().AsQueryable();
        if (f.LocationIds is { } places) resources = resources.Where(r => r.SpaceId != null && places.Contains(r.SpaceId.Value));
        var scope = resources.Select(r => r.Id);
        var rows = await db.Bookings.AsNoTracking().Where(b => scope.Contains(b.ResourceId) && b.StartsAt >= f.FromAt && b.StartsAt < f.ToAt)
            .Select(b => new { b.ResourceId, b.Status, b.StartsAt, b.EndsAt }).ToListAsync(ct);
        var names = await resources.ToDictionaryAsync(r => r.Id, r => r.Name, ct);
        var used = rows.Where(b => b.Status is BookingStatus.Confirmed or BookingStatus.CheckedIn).ToList();
        var noShows = rows.Count(b => b.Status == BookingStatus.NoShow);
        var hours = used.Sum(b => (decimal)(b.EndsAt - b.StartsAt).TotalHours);
        var waiting = await db.Bookings.AsNoTracking().CountAsync(b => scope.Contains(b.ResourceId) && b.Status == BookingStatus.Pending, ct);
        return new ReportSection(
        [
            new("bookings.count", Group, used.Count, KpiUnit.Count),
            new("bookings.hours", Group, Math.Round(hours, 1), KpiUnit.Hours),
            new("bookings.noShowRate", Group, ReportMath.Percent(noShows, used.Count + noShows), KpiUnit.Percent, "of bookings that needed a check-in"),
            new("bookings.cancelled", Group, rows.Count(b => b.Status == BookingStatus.Cancelled), KpiUnit.Count),
            new("bookings.waiting", Group, waiting, KpiUnit.Count),
        ],
        [
            new("bookings.byWeek", Group, KpiUnit.Count, ReportMath.Weekly(used.Select(b => DateOnly.FromDateTime(b.StartsAt.UtcDateTime)), f.From, f.To)),
            new("bookings.hoursByResource", Group, KpiUnit.Hours, used.GroupBy(b => b.ResourceId).Select(g => new SeriesPoint(names.GetValueOrDefault(g.Key) ?? "?", Math.Round(g.Sum(b => (decimal)(b.EndsAt - b.StartsAt).TotalHours), 1)))
                .OrderByDescending(p => p.Value).Take(10).ToList()),
        ]);
    }
}
