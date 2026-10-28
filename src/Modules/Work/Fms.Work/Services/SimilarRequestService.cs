using System.Text.RegularExpressions;
using Fms.Work.Domain;
using Fms.Work.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Fms.Work.Services;

public sealed record SimilarRequestDto(Guid Id, string Number, string Title, string Status, DateTimeOffset RequestedAt, int SharedWords);

public static partial class SimilarMath
{
    private static readonly HashSet<string> Stop = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "and", "for", "with", "not", "from", "this", "that", "are", "was", "has", "have", "need", "needs", "please", "again", "still", "very",
        "في", "من", "على", "الى", "إلى", "عن", "هذا", "هذه", "لا", "مع", "قد", "تم", "ان", "أن",
    };

    [GeneratedRegex(@"[\p{L}\p{Nd}]+", RegexOptions.CultureInvariant, 200)]
    private static partial Regex Word();

    /// <summary>The words of a title that carry meaning: letters and digits, at least three characters, not a common filler word, in lower case.</summary>
    public static HashSet<string> Words(string? text) =>
        Word().Matches(text ?? "").Select(m => m.Value.ToLowerInvariant()).Where(w => w.Length >= 3 && !Stop.Contains(w)).ToHashSet();

    public static int SharedWords(string? a, string? b)
    {
        var x = Words(a);
        x.IntersectWith(Words(b));
        return x.Count;
    }
}

/// <summary>
/// "Has someone already reported this?": open requests of the last 30 days about the same asset (or, with no asset, the same place) whose titles share a meaningful word with what is being typed.
/// Plain word matching, no model. It only suggests; the person decides whether theirs is a new problem.
/// </summary>
public sealed class SimilarRequestService(WorkDbContext db, TimeProvider clock)
{
    public const int MaxListed = 5;

    public async Task<IReadOnlyList<SimilarRequestDto>> FindAsync(Guid? locationId, Guid? assetId, string? title, CancellationToken ct = default)
    {
        if (SimilarMath.Words(title).Count == 0 || (locationId is null && assetId is null)) return [];
        var since = clock.GetUtcNow().AddDays(-30);
        var open = new[] { WorkOrderStatus.Requested, WorkOrderStatus.Approved, WorkOrderStatus.Assigned, WorkOrderStatus.InProgress, WorkOrderStatus.OnHold };
        var query = db.WorkOrders.AsNoTracking().Where(w => w.RequestedAt >= since && open.Contains(w.Status));
        query = assetId is { } a ? query.Where(w => w.AssetId == a) : query.Where(w => w.LocationId == locationId);
        var rows = await query.OrderByDescending(w => w.RequestedAt).Take(100).Select(w => new { w.Id, w.Number, w.Title, w.Status, w.RequestedAt }).ToListAsync(ct);
        return rows.Select(w => new SimilarRequestDto(w.Id, WorkOrderRules.DisplayNumber(w.Number), w.Title, w.Status.ToString(), w.RequestedAt, SimilarMath.SharedWords(title, w.Title)))
            .Where(w => w.SharedWords > 0).OrderByDescending(w => w.SharedWords).ThenByDescending(w => w.RequestedAt).Take(MaxListed).ToList();
    }
}
