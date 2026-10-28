using Fms.BuildingBlocks.Identity;
using Fms.Work.Domain;
using Fms.Work.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Fms.Work.Services;

public sealed record RepeatProblemDto(Guid AssetId, string? AssetCode, string? AssetName, int Count, int Emergencies, DateTimeOffset FirstAt, DateTimeOffset LastAt, string LastTitle, double? AverageDaysBetween);

public static class RepeatMath
{
    /// <summary>The average number of days between the first and last of <paramref name="count"/> problems, or null when there are fewer than two.</summary>
    public static double? AverageDaysBetween(DateTimeOffset first, DateTimeOffset last, int count) =>
        count < 2 ? null : Math.Round((last - first).TotalDays / (count - 1), 1);
}

/// <summary>
/// Assets that keep going wrong: three or more repair requests in the last 90 days (both numbers can be changed), worked out from the work orders alone.
/// Plain counting, no model: it works whether or not AI is switched on, and says exactly why an asset is listed.
/// </summary>
public sealed class RepeatProblemService(WorkDbContext db, IFacilityLookup facilities, TimeProvider clock)
{
    public const int MaxListed = 50;

    public async Task<IReadOnlyList<RepeatProblemDto>> ListAsync(int? days, int? minimum, CancellationToken ct = default)
    {
        var window = Math.Clamp(days ?? 90, 7, 365);
        var min = Math.Clamp(minimum ?? 3, 2, 20);
        var since = clock.GetUtcNow().AddDays(-window);
        var rows = await db.WorkOrders.AsNoTracking()
            .Where(w => w.AssetId != null && w.RequestedAt >= since && w.Type != WorkOrderType.Planned && w.Status != WorkOrderStatus.Cancelled
                        && (w.ClosureCode == null || (w.ClosureCode != ClosureCode.Duplicate && w.ClosureCode != ClosureCode.NotRequired)))
            .Select(w => new { AssetId = w.AssetId!.Value, w.RequestedAt, w.Title, w.Type }).ToListAsync(ct);
        var groups = rows.GroupBy(r => r.AssetId).Where(g => g.Count() >= min).OrderByDescending(g => g.Count()).ThenByDescending(g => g.Max(x => x.RequestedAt)).Take(MaxListed).ToList();
        var result = new List<RepeatProblemDto>();
        foreach (var g in groups)
        {
            var asset = await facilities.GetAssetAsync(g.Key, ct);
            var ordered = g.OrderBy(x => x.RequestedAt).ToList();
            result.Add(new RepeatProblemDto(g.Key, asset?.Code, asset?.Name, ordered.Count, ordered.Count(x => x.Type == WorkOrderType.Emergency), ordered[0].RequestedAt, ordered[^1].RequestedAt, ordered[^1].Title,
                RepeatMath.AverageDaysBetween(ordered[0].RequestedAt, ordered[^1].RequestedAt, ordered.Count)));
        }

        return result;
    }
}
