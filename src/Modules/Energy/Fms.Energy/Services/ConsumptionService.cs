using Fms.BuildingBlocks.Results;
using Fms.Energy.Domain;
using Fms.Energy.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Fms.Energy.Services;

/// <summary>Use and cost per meter over a period, from the readings that have not been voided.</summary>
public sealed class ConsumptionService(EnergyDbContext db, TimeProvider clock)
{
    public const int MaxDays = 400;
    public const int MaxMeters = 500;

    public async Task<Result<ConsumptionDto>> ComputeAsync(DateOnly? from, DateOnly? to, EnergyKind? kind, Guid? locationId, Guid? meterId, CancellationToken ct = default)
    {
        var end = to ?? Common.Today(clock);
        var start = from ?? end.AddDays(-29);
        if (start > end) return Result<ConsumptionDto>.Invalid("from", "The start must not be after the end.");
        if (end.DayNumber - start.DayNumber >= MaxDays) return Result<ConsumptionDto>.Invalid("from", $"Choose at most {MaxDays} days.");

        var query = db.Meters.AsNoTracking().Where(m => !m.IsArchived);
        if (kind is { } k) query = query.Where(m => m.Kind == k);
        if (locationId is { } l) query = query.Where(m => m.LocationId == l);
        if (meterId is { } id) query = query.Where(m => m.Id == id);
        var meters = await query.OrderBy(m => m.Code).Take(MaxMeters).ToListAsync(ct);
        var tariffs = await db.Tariffs.AsNoTracking().ToListAsync(ct);

        var spanFrom = new DateTimeOffset(start.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var spanTo = new DateTimeOffset(end.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var ids = meters.Select(m => m.Id).ToList();
        var rows = await db.Readings.AsNoTracking().Where(r => ids.Contains(r.MeterId) && r.At < spanTo && !db.Voids.Any(v => v.ReadingId == r.Id)).Select(r => new { r.MeterId, r.At, r.Value }).ToListAsync(ct);
        // The first reading after the period closes the last interval; without it the final day could never be counted.
        var ahead = await db.Readings.AsNoTracking().Where(r => ids.Contains(r.MeterId) && r.At >= spanTo && !db.Voids.Any(v => v.ReadingId == r.Id))
            .GroupBy(r => r.MeterId).Select(g => g.OrderBy(r => r.At).Select(r => new { r.MeterId, r.At, r.Value }).First()).ToListAsync(ct);
        var byMeter = rows.Concat(ahead).GroupBy(r => r.MeterId).ToDictionary(g => g.Key, g => g.OrderBy(r => r.At).ToList());

        var result = new List<MeterUseDto>();
        foreach (var m in meters)
        {
            var all = byMeter.GetValueOrDefault(m.Id) ?? [];

            // Keep the last reading from before the period (it starts the first interval) and everything inside it; older ones cannot matter.
            var lead = all.LastOrDefault(r => r.At < spanFrom);
            var inside = all.Where(r => r.At >= spanFrom).ToList(); // includes the closing reading after the period, if there is one
            var used = (lead is null ? inside : new[] { lead }.Concat(inside)).Select(r => (r.At, r.Value)).ToList();
            var usage = EnergyMath.UsageFrom(used, m.IsCumulative, m.RolloverAt);
            var days = EnergyMath.ByDay(usage.Intervals).Where(d => d.Key >= start && d.Key <= end).ToList();

            decimal total = 0;
            decimal? cost = null;
            string? currency = null;
            foreach (var (day, amount) in days)
            {
                total += amount;
                if (EnergyMath.TariffOn(tariffs.Where(t => t.Kind == m.Kind), day) is { } tariff)
                {
                    cost = (cost ?? 0) + amount * tariff.PricePerUnit;
                    currency ??= tariff.Currency;
                }
            }

            result.Add(new MeterUseDto(m.Id, m.Code, m.Name, m.Kind, m.Unit, Math.Round(total, 3), cost is null ? null : Math.Round(cost.Value, 2), currency, usage.Resets, days.Select(d => new DayUse(d.Key, Math.Round(d.Value, 3))).ToList()));
        }

        var totals = result.GroupBy(r => r.Kind).OrderBy(g => g.Key).Select(g =>
        {
            var units = g.Select(x => x.Unit).Distinct().ToList();
            var currencies = g.Where(x => x.Currency != null).Select(x => x.Currency).Distinct().ToList();
            return new KindTotalDto(g.Key, units.Count == 1 ? units[0] : "", units.Count == 1 ? g.Sum(x => x.Total) : 0, g.Any(x => x.Cost != null) && currencies.Count == 1 ? g.Sum(x => x.Cost ?? 0) : null, currencies.Count == 1 ? currencies[0] : null, units.Count > 1);
        }).ToList();
        return Result<ConsumptionDto>.Ok(new ConsumptionDto(start, end, result, totals));
    }
}
