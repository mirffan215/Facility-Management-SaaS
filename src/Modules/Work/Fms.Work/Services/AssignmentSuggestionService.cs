using Fms.BuildingBlocks.Identity;
using Fms.BuildingBlocks.Results;
using Fms.BuildingBlocks.Security;
using Fms.Work.Domain;
using Fms.Work.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Fms.Work.Services;

public sealed record AssigneeSuggestionDto(Guid UserId, string Name, int Score, int SameAsset, int SameCategory, int SamePlace, int OpenJobs);

public sealed record VendorSuggestionDto(Guid VendorId, string Name, Guid ContractId, string ContractNumber, int? ResolutionMinutes, int PastJobs);

public sealed record AssignmentSuggestionsDto(Guid WorkOrderId, IReadOnlyList<AssigneeSuggestionDto> Assignees, IReadOnlyList<VendorSuggestionDto> Vendors);

/// <summary>Suggests who could take an open work order, and which vendor under contract could, from history and contracts. Rules, not a model; the dispatcher decides.</summary>
public sealed class AssignmentSuggestionService(WorkDbContext db, IUserDirectory users, TimeProvider clock, IVendorLookup? vendors = null)
{
    public const int HistoryDays = 180;
    private static readonly WorkOrderStatus[] Open = [WorkOrderStatus.Requested, WorkOrderStatus.Approved, WorkOrderStatus.Assigned];
    private static readonly WorkOrderStatus[] Done = [WorkOrderStatus.Completed, WorkOrderStatus.Verified, WorkOrderStatus.Closed];
    private static readonly WorkOrderStatus[] OnTheirPlate = [WorkOrderStatus.Assigned, WorkOrderStatus.InProgress, WorkOrderStatus.OnHold];

    public async Task<Result<AssignmentSuggestionsDto>> SuggestAsync(Guid workOrderId, CancellationToken ct = default)
    {
        var wo = await db.WorkOrders.AsNoTracking().FirstOrDefaultAsync(w => w.Id == workOrderId, ct);
        if (wo is null) return Result<AssignmentSuggestionsDto>.NotFound();
        if (!Open.Contains(wo.Status) && wo.Status != WorkOrderStatus.InProgress && wo.Status != WorkOrderStatus.OnHold) return Result<AssignmentSuggestionsDto>.Conflict("Only a work order that is still open can be given suggestions.");

        var people = (await users.WithPermissionAsync(Permissions.WorkOrdersWork, ct)).ToList();
        var ids = people.Select(p => p.Id).ToList();
        var since = clock.GetUtcNow().AddDays(-HistoryDays);
        var history = await db.WorkOrders.AsNoTracking()
            .Where(w => w.AssignedTo != null && ids.Contains(w.AssignedTo.Value) && Done.Contains(w.Status) && w.RequestedAt >= since && w.Id != wo.Id
                        && (w.ClosureCode == null || (w.ClosureCode != ClosureCode.Duplicate && w.ClosureCode != ClosureCode.NotRequired)))
            .OrderByDescending(w => w.RequestedAt).Take(5000)
            .Select(w => new { User = w.AssignedTo!.Value, w.AssetId, w.CategoryId, w.LocationId }).ToListAsync(ct);
        var load = await db.WorkOrders.AsNoTracking().Where(w => w.AssignedTo != null && ids.Contains(w.AssignedTo.Value) && OnTheirPlate.Contains(w.Status) && w.Id != wo.Id)
            .GroupBy(w => w.AssignedTo!.Value).Select(g => new { User = g.Key, N = g.Count() }).ToDictionaryAsync(x => x.User, x => x.N, ct);

        var stats = people.Select(p =>
        {
            var mine = history.Where(h => h.User == p.Id).ToList();
            return new CandidateStats(p.Id, p.DisplayName ?? p.Email ?? "?",
                wo.AssetId is { } a ? mine.Count(h => h.AssetId == a) : 0,
                wo.CategoryId is { } c ? mine.Count(h => h.CategoryId == c) : 0,
                mine.Count(h => h.LocationId == wo.LocationId),
                load.GetValueOrDefault(p.Id));
        });
        var assignees = AssignmentRanker.Rank(stats).Select(r => new AssigneeSuggestionDto(r.Stats.UserId, r.Stats.Name, r.Score, r.Stats.SameAsset, r.Stats.SameCategory, r.Stats.SamePlace, r.Stats.OpenJobs)).ToList();

        var vendorSuggestions = new List<VendorSuggestionDto>();
        if (vendors is not null)
        {
            var offers = await vendors.OffersAsync(wo.AssetId, wo.LocationId, (int)wo.Priority, 20, ct);
            var past = offers.Count == 0 ? new Dictionary<Guid, int>() : await db.WorkOrders.AsNoTracking()
                .Where(w => w.AssignedVendorId != null && Done.Contains(w.Status) && w.RequestedAt >= since && (wo.CategoryId == null || w.CategoryId == wo.CategoryId))
                .GroupBy(w => w.AssignedVendorId!.Value).Select(g => new { Vendor = g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Vendor, x => x.N, ct);
            var ranked = AssignmentRanker.RankVendors(offers.Select(o => new VendorStats(o.Vendor.Id, o.Vendor.Name, o.Contract.Number, o.Sla?.ResolutionMinutes, past.GetValueOrDefault(o.Vendor.Id))));
            var byVendor = offers.GroupBy(o => o.Vendor.Id).ToDictionary(g => g.Key, g => g.First());
            vendorSuggestions = ranked.Select(v => new VendorSuggestionDto(v.VendorId, v.Name, byVendor[v.VendorId].Contract.Id, v.ContractNumber, v.ResolutionMinutes, v.PastJobs)).ToList();
        }

        return Result<AssignmentSuggestionsDto>.Ok(new AssignmentSuggestionsDto(wo.Id, assignees, vendorSuggestions));
    }
}
