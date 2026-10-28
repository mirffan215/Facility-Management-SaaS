using Fms.BuildingBlocks.Reporting;
using Fms.BuildingBlocks.Security;
using Fms.Leases.Domain;
using Fms.Leases.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Fms.Leases.Services;

/// <summary>Where the leases stand today. Rent is added up only when all active leases of a kind are in one currency, never across currencies.</summary>
public sealed class LeasesReportProvider(LeasesDbContext db, TimeProvider clock) : IReportProvider
{
    public string Group => "leases";
    public string Permission => Permissions.LeasesRead;

    public async Task<ReportSection> BuildAsync(ReportFilter f, CancellationToken ct = default)
    {
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        var query = db.Leases.AsNoTracking().AsQueryable();
        if (f.LocationIds is { } places) query = query.Where(l => l.LocationId != null && places.Contains(l.LocationId.Value));
        var all = await query.Take(LeaseService.MaxLeases).ToListAsync(ct);
        var states = all.Select(l => (Lease: l, State: LeaseMath.StateOf(l, today))).ToList();
        var active = states.Where(x => x.State == LeaseState.Active).Select(x => x.Lease).ToList();
        decimal? Rent(LeaseKind kind)
        {
            var of = active.Where(l => l.Kind == kind).ToList();
            return of.Select(l => l.CurrencyCode).Distinct().Count() > 1 ? null : of.Sum(l => LeaseMath.NextTwelveMonths(l, today));
        }

        string? Currency(LeaseKind kind) => active.Where(l => l.Kind == kind).Select(l => l.CurrencyCode).Distinct().Count() == 1 ? active.First(l => l.Kind == kind).CurrencyCode : null;
        return new ReportSection(
        [
            new("leases.active", Group, active.Count, KpiUnit.Count),
            new("leases.expiringSoon", Group, active.Count(l => l.EndOn.DayNumber - today.DayNumber <= LeaseService.SoonDays), KpiUnit.Count, "ending within 90 days"),
            new("leases.expired", Group, states.Count(x => x.State == LeaseState.Expired), KpiUnit.Count, "past their end and not extended or terminated"),
            new("leases.rentPayable", Group, Rent(LeaseKind.Occupied), KpiUnit.Money, Currency(LeaseKind.Occupied)),
            new("leases.rentReceivable", Group, Rent(LeaseKind.LetOut), KpiUnit.Money, Currency(LeaseKind.LetOut)),
        ],
        [
            new("leases.byState", Group, KpiUnit.Count, states.GroupBy(x => x.State).OrderBy(g => g.Key).Select(g => new SeriesPoint(g.Key.ToString(), g.Count())).ToList()),
        ]);
    }
}
