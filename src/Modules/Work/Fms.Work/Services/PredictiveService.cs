using Fms.BuildingBlocks.Identity;
using Fms.Work.Domain;
using Fms.Work.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Fms.Work.Services;

public static class HintKind
{
    public const string FailuresSpeedingUp = "FailuresSpeedingUp";
    public const string MeterRunningFaster = "MeterRunningFaster";
}

/// <summary>One hint with the numbers it was drawn from. Fields that do not belong to the kind are null.</summary>
public sealed record PredictiveHintDto(
    string Kind, Guid AssetId, string? AssetCode, string? AssetName,
    int? Events, double? EarlierGapDays, double? RecentGapDays, DateTimeOffset? LastAt, DateTimeOffset? ExpectedBy,
    string? MeterName, string? Unit, double? RecentPerDay, double? PriorPerDay, double? Ratio, int? DaysToService);

/// <summary>
/// Early warnings from two plain rules: repairs on an asset coming closer together, and a service meter climbing faster than before.
/// Neither is a model and neither needs AI switched on; a hint says what it saw and the person decides what to do.
/// </summary>
public sealed class PredictiveService(WorkDbContext db, IFacilityLookup facilities, TimeProvider clock)
{
    public const int FailureWindowDays = 365;
    public const int MaxPlans = 200;
    public const int MaxHints = 50;

    public async Task<IReadOnlyList<PredictiveHintDto>> ListAsync(Guid? assetId, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var hints = new List<PredictiveHintDto>();
        hints.AddRange(await FailureHintsAsync(assetId, now, ct));
        hints.AddRange(await MeterHintsAsync(assetId, now, ct));
        return hints.OrderBy(h => h.ExpectedBy ?? DateTimeOffset.MaxValue).ThenBy(h => h.DaysToService ?? int.MaxValue).Take(MaxHints).ToList();
    }

    private async Task<List<PredictiveHintDto>> FailureHintsAsync(Guid? assetId, DateTimeOffset now, CancellationToken ct)
    {
        var since = now.AddDays(-FailureWindowDays);
        var rows = await db.WorkOrders.AsNoTracking()
            .Where(w => w.AssetId != null && (assetId == null || w.AssetId == assetId) && w.RequestedAt >= since && w.Type != WorkOrderType.Planned && w.Status != WorkOrderStatus.Cancelled
                        && (w.ClosureCode == null || (w.ClosureCode != ClosureCode.Duplicate && w.ClosureCode != ClosureCode.NotRequired)))
            .OrderByDescending(w => w.RequestedAt).Take(5000)
            .Select(w => new { AssetId = w.AssetId!.Value, w.RequestedAt }).ToListAsync(ct);
        var found = rows.GroupBy(r => r.AssetId)
            .Select(g => (Asset: g.Key, Trend: PredictiveMath.Failures(g.Select(x => x.RequestedAt).ToList())))
            .Where(x => x.Trend is not null).Take(MaxHints).ToList();
        var assets = await facilities.AssetsAsync(found.Select(f => f.Asset), ct);
        return found.Select(f =>
        {
            assets.TryGetValue(f.Asset, out var a);
            var t = f.Trend!;
            return new PredictiveHintDto(HintKind.FailuresSpeedingUp, f.Asset, a?.Code, a?.Name, t.Events, t.EarlierMedianDays, t.RecentMeanDays, t.LastAt, t.ExpectedBy, null, null, null, null, null, null);
        }).ToList();
    }

    private async Task<List<PredictiveHintDto>> MeterHintsAsync(Guid? assetId, DateTimeOffset now, CancellationToken ct)
    {
        var plans = await db.PmPlans.AsNoTracking()
            .Where(p => p.IsActive && p.MeterId != null && (assetId == null || p.AssetId == assetId))
            .Join(db.PmSchedules.Where(s => !s.IsArchived && s.MeterInterval != null && s.MeterName != null), p => p.ScheduleId, s => s.Id,
                (p, s) => new { p.AssetId, p.MeterId, p.LastServiceMeterValue, Name = s.MeterName!, Interval = s.MeterInterval!.Value })
            .Take(MaxPlans).ToListAsync(ct);
        var hints = new List<PredictiveHintDto>();
        foreach (var plan in plans)
        {
            var meter = await facilities.FindMeterAsync(plan.AssetId, plan.Name, ct);
            if (meter is null || !meter.IsCumulative) continue;
            var readings = await facilities.ReadingsSinceAsync(meter.Id, now.AddDays(-(PredictiveMath.RecentDays + PredictiveMath.PriorDays)), ct);
            if (PredictiveMath.Pace(readings, now) is not { } pace) continue;
            int? days = null;
            if (plan.LastServiceMeterValue is { } baseline && readings.Count > 0)
            {
                var left = baseline + plan.Interval - readings[^1].Value;
                days = left <= 0 ? 0 : (int)Math.Ceiling((double)left / pace.RecentPerDay);
            }

            var asset = (await facilities.AssetsAsync([plan.AssetId], ct)).GetValueOrDefault(plan.AssetId);
            hints.Add(new PredictiveHintDto(HintKind.MeterRunningFaster, plan.AssetId, asset?.Code, asset?.Name, null, null, null, null, null,
                meter.Name, meter.Unit, pace.RecentPerDay, pace.PriorPerDay, pace.Ratio, days));
        }

        return hints;
    }
}
